import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of, throwError } from 'rxjs';
import { AccessService, MemberAccess, MemberAccessState } from '../../access/access.service';
import { CAPABILITIES, Capability, capabilitySet } from '../../access/capabilities';
import { OptionVisibility } from '../../access/option-visibility';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { OptionVisibilityService } from '../../services/option-visibility.service';
import { PlatformDiagnosticsAccessService } from '../../services/platform-diagnostics-access.service';
import { AdminComponent } from './admin.component';

function granted(
  capabilities: readonly Capability[],
  optionVisibility?: OptionVisibility
): MemberAccessState {
  const access: MemberAccess = {
    role: 'Test',
    capabilities: capabilitySet([...capabilities]),
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney',
    optionVisibility
  };
  return { status: 'granted', access };
}

/** Every capability, which is what an Owner holds and what most of these tests assume by default. */
const OWNER = granted(CAPABILITIES);

const MANAGER_CAPABILITIES: readonly Capability[] = [
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate',
  'Suppliers.ManageGstDefaults',
  'Expenses.Manage',
  'Reports.View',
  'Imports.Run',
  'SiteCommissions.Manage'
];

const MANAGER = granted(MANAGER_CAPABILITIES);

const OPERATOR = granted(['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate']);

/**
 * The hub under test with its three answers stubbed (issue #873 added the last two): the cached
 * capability read, the platform-admin probe and whether a role preview is running. All three default
 * to what an ordinary member's session reports - the capabilities asked for, no platform access and
 * no preview - so a test only states what it is about.
 */
async function render(
  access: Observable<MemberAccessState> = of(OWNER),
  options: {
    diagnosticsAccess?: Observable<boolean>;
    rolePreviewActive?: Observable<boolean>;
    saveVisibility?: jest.Mock;
    refresh?: jest.Mock;
  } = {}
) {
  const saveVisibility =
    options.saveVisibility ??
    jest.fn((visible: boolean) => of({ historicalGstClassificationVisible: visible }));
  const refresh = options.refresh ?? jest.fn(() => of<MemberAccessState>({ status: 'unread' }));

  await TestBed.configureTestingModule({
    imports: [AdminComponent],
    providers: [
      provideRouter([]),
      { provide: AccessService, useValue: { access$: access, refresh } },
      {
        provide: PlatformDiagnosticsAccessService,
        useValue: { granted$: options.diagnosticsAccess ?? of(false) }
      },
      { provide: RolePreviewService, useValue: { active$: options.rolePreviewActive ?? of(false) } },
      {
        provide: OptionVisibilityService,
        useValue: { setHistoricalGstClassificationVisible: saveVisibility }
      }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AdminComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, saveVisibility, refresh };
}

/**
 * `[routerLink]` is a property binding here (issue #674's cards are data-driven), so the rendered
 * `href` is what proves a card's link exists - the same way `SidebarNavComponent`'s own spec reads
 * its dynamic links - rather than the static `routerLink="..."` attribute the old literal template
 * left behind.
 */
function link(host: HTMLElement, route: string): HTMLAnchorElement | null {
  return host.querySelector<HTMLAnchorElement>(`a[href="${route}"]`);
}

const ALL_ADMIN_ROUTES = [
  '/admin/nayax-settings',
  '/admin/site-commission-agreements',
  '/admin/imports',
  '/admin/historical-cost-recovery',
  '/admin/avco-transition',
  '/admin/costing-repair',
  '/admin/historical-gst-classification',
  '/admin/nayax-sale-timestamp-repair',
  '/admin/users',
  '/admin/roles',
  '/admin/business-settings'
];

describe('AdminComponent Admin split (issue #388)', () => {
  it('links to the dedicated Integrations and Site Commission Agreements pages instead of embedding their forms', async () => {
    const { host } = await render();

    expect(link(host, '/admin/nayax-settings')).not.toBeNull();
    expect(link(host, '/admin/site-commission-agreements')).not.toBeNull();
    expect(host.querySelector('input[type="date"]')).toBeNull();
  });
});

describe('AdminComponent Admin split (issue #389)', () => {
  it('links to the dedicated Imports page instead of hosting the import actions', async () => {
    const { host } = await render();

    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(host.querySelector('input[type="file"]')).toBeNull();
    const buttonText = Array.from(host.querySelectorAll('button')).map((button) => button.textContent ?? '');
    expect(buttonText.some((text) => text.includes('Import products'))).toBe(false);
    expect(buttonText.some((text) => text.includes('Import XML files'))).toBe(false);
    expect(buttonText.some((text) => text.includes('Download template'))).toBe(false);
  });
});

describe('AdminComponent Historical GST Classification (issue #433)', () => {
  it('links to the dedicated Historical GST Classification page', async () => {
    const { host } = await render();

    expect(link(host, '/admin/historical-gst-classification')).not.toBeNull();
  });
});

describe('AdminComponent Nayax Sale Timestamp Repair (issue #487)', () => {
  it('links to the dedicated Nayax Sale Timestamp Repair page', async () => {
    const { host } = await render();

    expect(link(host, '/admin/nayax-sale-timestamp-repair')).not.toBeNull();
  });
});

describe('AdminComponent Admin split (issue #390)', () => {
  it('links to the dedicated Historical Cost Recovery, AVCO Transition and Costing Repair pages', async () => {
    const { host } = await render();

    expect(link(host, '/admin/historical-cost-recovery')).not.toBeNull();
    expect(link(host, '/admin/avco-transition')).not.toBeNull();
    expect(link(host, '/admin/costing-repair')).not.toBeNull();
  });

  it('keeps no second copy of the moved maintenance tools: no action, form control or child workflow remains', async () => {
    const { host } = await render();

    expect(host.querySelectorAll('button')).toHaveLength(0);
    expect(host.querySelectorAll('input, select, textarea')).toHaveLength(0);
    expect(host.querySelector('app-costing-repair')).toBeNull();
    const text = host.textContent ?? '';
    expect(text).not.toContain('Dry Run');
    expect(text).not.toContain('Apply Nayax Cost Backfill');
    expect(text).not.toContain('Preview transition baseline');
    expect(text).not.toContain('Preview repair');
  });

  /**
   * The page is a link hub only, so it needs no data-access dependency of its own beyond the
   * cached capability read: rendering it with no HTTP, MSAL or maintenance-service provider proves
   * it cannot reach a costing or import service.
   */
  it('renders without any maintenance service dependency', async () => {
    await expect(render()).resolves.toBeDefined();
  });
});

/**
 * The Admin hub now completes the round trip the sidebar's `Admin` group already offers an Owner
 * (issue #674): Users, Roles and Business Settings were reachable from the sidebar and each page's
 * own "Back to Admin" link, but had no card here at all.
 */
describe('AdminComponent Owner-only destinations (issue #674)', () => {
  it('links to the Users, Roles and Business Settings pages', async () => {
    const { host } = await render();

    expect(link(host, '/admin/users')).not.toBeNull();
    expect(link(host, '/admin/roles')).not.toBeNull();
    expect(link(host, '/admin/business-settings')).not.toBeNull();
  });
});

/**
 * The capability-driven hub (issue #674). The hub renders the capability set `AccessService`
 * reports and decides nothing itself - a card it does not render hides a page rather than protects
 * one, because the endpoint behind that page authorizes every request regardless.
 */
describe('AdminComponent capability-driven cards (issue #674)', () => {
  it('renders no card at all until the capability set has been read', async () => {
    const { host } = await render(NEVER);

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).toBeNull();
    }
    expect(host.textContent).not.toContain('Integrations');
  });

  it('offers an Owner every destination the sidebar offers', async () => {
    const { host } = await render(of(OWNER));

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).not.toBeNull();
    }
  });

  it('offers a Manager only Imports and Site Commission Agreements, without any Owner-only card', async () => {
    const { host } = await render(of(MANAGER));

    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(link(host, '/admin/site-commission-agreements')).not.toBeNull();

    expect(link(host, '/admin/nayax-settings')).toBeNull();
    expect(link(host, '/admin/historical-cost-recovery')).toBeNull();
    expect(link(host, '/admin/avco-transition')).toBeNull();
    expect(link(host, '/admin/costing-repair')).toBeNull();
    expect(link(host, '/admin/historical-gst-classification')).toBeNull();
    expect(link(host, '/admin/nayax-sale-timestamp-repair')).toBeNull();
    expect(link(host, '/admin/users')).toBeNull();
    expect(link(host, '/admin/roles')).toBeNull();
    expect(link(host, '/admin/business-settings')).toBeNull();
  });

  it('offers an Operator no admin card at all', async () => {
    const { host } = await render(of(OPERATOR));

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).toBeNull();
    }
  });

  it('offers every destination when the capability set could not be read, the same fail-open rule the sidebar follows', async () => {
    const { host } = await render(of<MemberAccessState>({ status: 'unavailable' }));

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).not.toBeNull();
    }
  });

  it('re-renders when the capability set changes, so a role change and an identity change both reach the hub', async () => {
    const access = new BehaviorSubject<MemberAccessState>(OPERATOR);
    const { fixture, host } = await render(access);
    expect(link(host, '/admin/business-settings')).toBeNull();

    access.next(OWNER);
    fixture.detectChanges();
    expect(link(host, '/admin/business-settings')).not.toBeNull();

    // An identity change republishes `unread` before the new identity's own answer arrives
    // (issue #603); the hub must withdraw every card for that window rather than keep the
    // previous member's, exactly as the sidebar does.
    access.next({ status: 'unread' });
    fixture.detectChanges();
    expect(link(host, '/admin/business-settings')).toBeNull();
    expect(link(host, '/admin/imports')).toBeNull();

    access.next(MANAGER);
    fixture.detectChanges();
    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(link(host, '/admin/business-settings')).toBeNull();
  });
});

/**
 * The Super Admin's switch over Historical GST Classification (issue #873), on the hub where the
 * option's own entry is.
 *
 * The thing each test holds apart is "offered" from "permitted": the switch decides whether the card
 * is rendered and the capability set still decides whether it could be, so switching the option on
 * never gives a Manager a card their role never had, and switching it off never takes `Data.Repair`
 * away from an Owner.
 */
describe('AdminComponent switchable option visibility (issue #873)', () => {
  const ROUTE = '/admin/historical-gst-classification';
  const OFF: OptionVisibility = { historicalGstClassificationVisible: false };
  const ON: OptionVisibility = { historicalGstClassificationVisible: true };

  const OWNER_OFF = granted(CAPABILITIES, OFF);
  const OWNER_ON = granted(CAPABILITIES, ON);

  /** The switch itself: the labelled control the Super Admin is offered beside the card. */
  function toggle(host: HTMLElement): HTMLButtonElement | null {
    return host.querySelector<HTMLButtonElement>('#historical-gst-classification-visibility');
  }

  function cardText(host: HTMLElement): string {
    const card = link(host, ROUTE)?.closest('section');
    return card?.textContent ?? '';
  }

  it('hides the card from an eligible member who is not a super admin while the option is off', async () => {
    const { host } = await render(of(OWNER_OFF));

    expect(link(host, ROUTE)).toBeNull();
    // Every other Owner card is untouched: this is one option's visibility, not a page-wide switch.
    expect(link(host, '/admin/costing-repair')).not.toBeNull();
    expect(link(host, '/admin/historical-cost-recovery')).not.toBeNull();
  });

  it('offers the card with no switch and no pill to an eligible member while the option is on', async () => {
    const { host } = await render(of(OWNER_ON));

    expect(link(host, ROUTE)).not.toBeNull();
    expect(toggle(host)).toBeNull();
    expect(cardText(host)).not.toContain('Off');
  });

  it('keeps the card for a super admin while the option is off, with the Off pill beside its name', async () => {
    const { host } = await render(of(OWNER_OFF), { diagnosticsAccess: of(true) });

    expect(link(host, ROUTE)).not.toBeNull();

    const pill = host.querySelector<HTMLElement>('.badge');
    expect(pill?.textContent?.trim()).toBe('Off');
    expect(cardText(host)).toContain('Historical GST Classification');
  });

  it('omits the Off pill for a super admin while the option is on', async () => {
    const { host } = await render(of(OWNER_ON), { diagnosticsAccess: of(true) });

    expect(host.querySelector('.badge')).toBeNull();
    expect(toggle(host)).not.toBeNull();
  });

  it('offers a super admin a switch that says it would show the option while it is off', async () => {
    const { host } = await render(of(OWNER_OFF), { diagnosticsAccess: of(true) });

    expect(toggle(host)?.textContent).toContain('Show Historical GST Classification to other users');
  });

  it('offers a super admin a switch that says it would hide the option while it is on', async () => {
    const { host } = await render(of(OWNER_ON), { diagnosticsAccess: of(true) });

    expect(toggle(host)?.textContent).toContain('Hide Historical GST Classification from other users');
  });

  it('offers no switch at all to a member who is not a super admin', async () => {
    const { host } = await render(of(OWNER_ON));

    expect(toggle(host)).toBeNull();
  });

  /**
   * A role preview withholds the administrator's own treatment, exactly as it withholds the
   * `Super Admin` menu group (issue #721): what a preview shows is what the previewed role sees, and
   * the API refuses a write made inside one anyway.
   */
  it('withholds the super-admin treatment while a role preview is running', async () => {
    const { host } = await render(of(OWNER_OFF), {
      diagnosticsAccess: of(true),
      rolePreviewActive: of(true)
    });

    expect(link(host, ROUTE)).toBeNull();
    expect(toggle(host)).toBeNull();
  });

  it('offers the card while the setting has not been reported, hiding nothing over an unread answer', async () => {
    const { host } = await render(of(granted(CAPABILITIES)));

    expect(link(host, ROUTE)).not.toBeNull();
  });

  /**
   * The criterion "On restores it only to users already eligible under existing permissions": a
   * Manager holds no `Data.Repair`, so the option being on puts no card in their hub - and neither
   * does being the Super Admin, because the switch is not a capability.
   */
  it('never offers the card to a member whose capabilities do not allow it while it is on', async () => {
    const { host } = await render(of(granted(MANAGER_CAPABILITIES, ON)), {
      diagnosticsAccess: of(true)
    });

    expect(link(host, ROUTE)).toBeNull();
    expect(toggle(host)).toBeNull();
  });

  it('never offers the card to a member whose capabilities do not allow it while it is off', async () => {
    const { host } = await render(of(granted(MANAGER_CAPABILITIES, OFF)));

    expect(link(host, ROUTE)).toBeNull();
  });

  it('submits the new state and re-reads the shared answer once the server has stored it', async () => {
    const { host, saveVisibility, refresh } = await render(of(OWNER_ON), {
      diagnosticsAccess: of(true)
    });

    toggle(host)!.click();

    expect(saveVisibility).toHaveBeenCalledWith(false);
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  /**
   * The criterion "a failed save must not leave the UI claiming that an unsaved setting is active":
   * nothing is re-read, nothing on screen moves to the requested state, and the refusal is reported.
   */
  it('reports a failed save and leaves the option reading exactly as it is stored', async () => {
    const saveVisibility = jest.fn(() => throwError(() => new Error('refused')));
    const { host, refresh, fixture } = await render(of(OWNER_ON), {
      diagnosticsAccess: of(true),
      saveVisibility
    });

    toggle(host)!.click();
    fixture.detectChanges();

    expect(refresh).not.toHaveBeenCalled();
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('was not saved');
    // The switch still offers to hide an option that is still shown, the card is still offered, and
    // no `Off` pill has appeared.
    expect(toggle(host)?.textContent).toContain('Hide Historical GST Classification from other users');
    expect(link(host, ROUTE)).not.toBeNull();
    expect(host.querySelector('.badge')).toBeNull();
  });
});
