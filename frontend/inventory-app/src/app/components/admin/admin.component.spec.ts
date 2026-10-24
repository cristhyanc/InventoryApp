import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, Subject, of, throwError } from 'rxjs';
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
  // An unanswered re-read by default: `AccessService.refresh()` answers when the server does, and
  // the window before it answers is the one the repaired lifecycle is about (issue #873).
  const refresh = options.refresh ?? jest.fn((): Observable<MemberAccessState> => NEVER);

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

  /** The control that asks for the shared answer again, offered only when a read of it failed. */
  function retry(host: HTMLElement): HTMLButtonElement | null {
    return host.querySelector<HTMLButtonElement>('#historical-gst-classification-visibility-retry');
  }

  function pill(host: HTMLElement): string | undefined {
    return host.querySelector<HTMLElement>('.badge')?.textContent?.trim();
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

  /**
   * The save-and-re-read lifecycle of that switch, repaired after the review's [P2] finding on
   * head `04f1728`.
   *
   * What each of these holds the hub to is that **what is on screen is an answer somebody gave**.
   * A successful `PUT` is the server's own answer for this identity, and it is not simultaneous with
   * the `GET /api/me/access` that publishes it to every other surface: that read takes a moment,
   * keeps the previous answer published while it runs, and may fail outright. Before the repair the
   * hub discarded the stored answer at the moment it arrived, so it stated the pre-save value for
   * the length of the read and then - when the read failed - an `On` that nothing had ever reported,
   * over a setting the server was holding as `Off`.
   */
  describe('the saved answer and the re-read that publishes it', () => {
    it('keeps the stored Off state while the re-read has not answered, and offers turning it back on', async () => {
      // The published capability answer still says `on` here, exactly as the real service leaves it
      // during a same-identity re-read; the stored answer is the newer of the two.
      const access = new BehaviorSubject<MemberAccessState>(OWNER_ON);
      const { host, fixture, saveVisibility, refresh } = await render(access, {
        diagnosticsAccess: of(true)
      });

      toggle(host)!.click();
      fixture.detectChanges();

      expect(saveVisibility).toHaveBeenCalledWith(false);
      expect(refresh).toHaveBeenCalledTimes(1);
      expect(access.value).toBe(OWNER_ON);

      // Accurate: the stored state, its `Off` pill, and no claim that the tool is shown.
      expect(pill(host)).toBe('Off');
      expect(cardText(host)).toContain('hidden from everybody but a Super Admin');
      expect(cardText(host)).not.toContain('shown to every user whose role allows it');

      // And the next permitted switch asks for the other state rather than repeating this one.
      toggle(host)!.click();
      expect(saveVisibility).toHaveBeenNthCalledWith(2, true);
    });

    it('hands the shared answer back over once the re-read carries it', async () => {
      const access = new BehaviorSubject<MemberAccessState>(OWNER_ON);
      const answer = new Subject<MemberAccessState>();
      const { host, fixture } = await render(access, {
        diagnosticsAccess: of(true),
        refresh: jest.fn((): Observable<MemberAccessState> => answer)
      });

      toggle(host)!.click();
      fixture.detectChanges();
      expect(pill(host)).toBe('Off');

      // The read answers, publishing the stored setting to every surface built from it.
      access.next(OWNER_OFF);
      answer.next(OWNER_OFF);
      fixture.detectChanges();

      expect(pill(host)).toBe('Off');
      expect(toggle(host)?.textContent).toContain('Show Historical GST Classification to other users');
      expect(host.querySelector('[role="alert"]')).toBeNull();
      expect(retry(host)).toBeNull();
    });

    it('keeps the stored Off state when the re-read fails, says so, and still offers turning it on', async () => {
      const access = new BehaviorSubject<MemberAccessState>(OWNER_ON);
      const { host, fixture, saveVisibility } = await render(access, {
        diagnosticsAccess: of(true),
        refresh: jest.fn((): Observable<MemberAccessState> => of({ status: 'unavailable' }))
      });

      toggle(host)!.click();
      // A failed read is published as `unavailable`, which reports no setting at all - the state
      // that used to make this screen claim the option was shown.
      access.next({ status: 'unavailable' });
      fixture.detectChanges();

      expect(pill(host)).toBe('Off');
      expect(cardText(host)).toContain('hidden from everybody but a Super Admin');
      expect(cardText(host)).not.toContain('shown to every user whose role allows it');
      expect(toggle(host)?.textContent).toContain('Show Historical GST Classification to other users');
      expect(host.querySelector('[role="alert"]')?.textContent).toContain('could not be read just now');
      expect(retry(host)).not.toBeNull();

      toggle(host)!.click();
      expect(saveVisibility).toHaveBeenNthCalledWith(2, true);
    });

    it('offers no switch and claims nothing while the setting could not be read at all', async () => {
      const access = new BehaviorSubject<MemberAccessState>({ status: 'unavailable' });
      const { host } = await render(access, { diagnosticsAccess: of(true) });

      // The card itself stays - that is the fail-open rule an unreadable capability set follows -
      // but nothing on it states the setting, and no control offers to change it from a guess.
      expect(link(host, ROUTE)).not.toBeNull();
      expect(toggle(host)).toBeNull();
      expect(pill(host)).toBeUndefined();
      expect(cardText(host)).toContain('could not be read');
      expect(cardText(host)).not.toContain('shown to every user whose role allows it');
      expect(retry(host)).not.toBeNull();
    });

    it('reads the setting again when the administrator asks, and renders the answer', async () => {
      const access = new BehaviorSubject<MemberAccessState>({ status: 'unavailable' });
      const refresh = jest.fn((): Observable<MemberAccessState> => {
        access.next(OWNER_OFF);
        return of(OWNER_OFF);
      });
      const { host, fixture } = await render(access, { diagnosticsAccess: of(true), refresh });

      retry(host)!.click();
      fixture.detectChanges();

      expect(refresh).toHaveBeenCalledTimes(1);
      expect(pill(host)).toBe('Off');
      expect(toggle(host)?.textContent).toContain('Show Historical GST Classification to other users');
      expect(host.querySelector('[role="alert"]')).toBeNull();
      expect(retry(host)).toBeNull();
    });

    it('offers no switch at all while the capability read has not answered', async () => {
      const { host } = await render(NEVER, { diagnosticsAccess: of(true) });

      // No card is rendered before the capabilities are known (issue #674), so there is no switch
      // either: an unread setting is never presented as a state, let alone a changeable one.
      expect(link(host, ROUTE)).toBeNull();
      expect(toggle(host)).toBeNull();
      expect(retry(host)).toBeNull();
    });

    /**
     * An identity change republishes `unread` (issue #603). A setting one administrator stored is
     * not the next identity's answer, so the stored answer is dropped with the published one rather
     * than carried across the change.
     */
    it('drops the stored answer when the published answer goes back to unread', async () => {
      const access = new BehaviorSubject<MemberAccessState>(OWNER_ON);
      const { host, fixture } = await render(access, { diagnosticsAccess: of(true) });

      toggle(host)!.click();
      fixture.detectChanges();
      expect(pill(host)).toBe('Off');

      access.next({ status: 'unread' });
      fixture.detectChanges();
      expect(link(host, ROUTE)).toBeNull();

      // The next identity's own answer decides it, with nothing of the previous one left.
      access.next(OWNER_ON);
      fixture.detectChanges();
      expect(pill(host)).toBeUndefined();
      expect(toggle(host)?.textContent).toContain('Hide Historical GST Classification from other users');
      expect(host.querySelector('[role="alert"]')).toBeNull();
    });
  });
});
