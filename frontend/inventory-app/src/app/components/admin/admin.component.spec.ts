import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of } from 'rxjs';
import { AccessService, MemberAccess, MemberAccessState } from '../../access/access.service';
import { CAPABILITIES, Capability, capabilitySet } from '../../access/capabilities';
import { MENU_VISIBILITY_KEYS, MenuVisibilityKey, OptionVisibility } from '../../access/option-visibility';
import { RolePreviewService } from '../../role-preview/role-preview.service';
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
 *
 * Since issue #1151 the hub only *renders* the switches: it stores nothing and re-reads nothing, so
 * there is no save to stub here any more.
 */
async function render(
  access: Observable<MemberAccessState> = of(OWNER),
  options: {
    diagnosticsAccess?: Observable<boolean>;
    rolePreviewActive?: Observable<boolean>;
  } = {}
) {
  const refresh = jest.fn((): Observable<MemberAccessState> => NEVER);

  await TestBed.configureTestingModule({
    imports: [AdminComponent],
    providers: [
      provideRouter([]),
      { provide: AccessService, useValue: { access$: access, refresh } },
      {
        provide: PlatformDiagnosticsAccessService,
        useValue: { granted$: options.diagnosticsAccess ?? of(false) }
      },
      { provide: RolePreviewService, useValue: { active$: options.rolePreviewActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AdminComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, refresh };
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
 * The switchable Admin destinations on the hub (issues #873, #1148 and #1151): every card whose
 * destination declares a switched-off key is withheld from a member, and the platform administrator
 * keeps them all with the `Off` pill.
 *
 * The thing each test holds apart is "offered" from "permitted": a switch decides whether a card is
 * rendered and the capability set still decides whether it could be, so switching a key on never
 * gives a Manager a card their role never had, and switching it off never takes `Data.Repair` away
 * from an Owner. **The switch itself is no longer here**: it is the Super Admin Menu Visibility page
 * (issue #1151), and this hub only renders the result.
 */
describe('AdminComponent switchable menu visibility (issues #873, #1148 and #1151)', () => {
  const ROUTE = '/admin/historical-gst-classification';

  /** Every switchable key, with the Admin card it withholds. */
  const SWITCHABLE: readonly { readonly key: MenuVisibilityKey; readonly route: string }[] = [
    { key: 'admin.site-commission-agreements', route: '/admin/site-commission-agreements' },
    { key: 'admin.imports', route: '/admin/imports' },
    { key: 'admin.historical-cost-recovery', route: '/admin/historical-cost-recovery' },
    { key: 'admin.avco-transition', route: '/admin/avco-transition' },
    { key: 'admin.costing-repair', route: '/admin/costing-repair' },
    { key: 'admin.historical-gst-classification', route: ROUTE },
    { key: 'admin.nayax-sale-timestamp-repair', route: '/admin/nayax-sale-timestamp-repair' }
  ];

  const NEVER_SWITCHABLE = [
    '/admin/nayax-settings',
    '/admin/users',
    '/admin/roles',
    '/admin/business-settings'
  ];

  const EVERY_KEY_OFF: OptionVisibility = { switchedOffKeys: [...MENU_VISIBILITY_KEYS] };
  const NOTHING_OFF: OptionVisibility = { switchedOffKeys: [] };

  const OWNER_OFF = granted(CAPABILITIES, { switchedOffKeys: ['admin.historical-gst-classification'] });
  const OWNER_ON = granted(CAPABILITIES, NOTHING_OFF);

  function pills(host: HTMLElement): string[] {
    return Array.from(host.querySelectorAll('.badge')).map((badge) => badge.textContent?.trim() ?? '');
  }

  function cardText(host: HTMLElement, route = ROUTE): string {
    return link(host, route)?.closest('section')?.textContent ?? '';
  }

  it.each(SWITCHABLE)('withholds the $route card from a member while $key is off', async ({ key, route }) => {
    const { host } = await render(of(granted(CAPABILITIES, { switchedOffKeys: [key] })));

    expect(link(host, route)).toBeNull();
    // Every other card is untouched: this is one destination's visibility, not a page-wide switch.
    SWITCHABLE.filter((other) => other.route !== route).forEach((other) =>
      expect(link(host, other.route)).not.toBeNull()
    );
    NEVER_SWITCHABLE.forEach((other) => expect(link(host, other)).not.toBeNull());
  });

  it.each(SWITCHABLE)('keeps the $route card for a super admin while $key is off, marked Off', async ({ route }) => {
    const { host } = await render(of(granted(CAPABILITIES, EVERY_KEY_OFF)), { diagnosticsAccess: of(true) });

    expect(link(host, route)).not.toBeNull();
    expect(cardText(host, route)).toContain('hidden from everybody but a Super Admin');
  });

  it('marks every switched-off card, and only those, for a super admin', async () => {
    const { host } = await render(of(granted(CAPABILITIES, EVERY_KEY_OFF)), { diagnosticsAccess: of(true) });

    expect(pills(host)).toEqual(SWITCHABLE.map(() => 'Off'));
  });

  it('offers every card with no pill while nothing is switched off', async () => {
    const { host } = await render(of(OWNER_ON), { diagnosticsAccess: of(true) });

    expect(pills(host)).toEqual([]);
    [...SWITCHABLE.map((entry) => entry.route), ...NEVER_SWITCHABLE].forEach((route) =>
      expect(link(host, route)).not.toBeNull()
    );
  });

  it('never withholds a never-switchable card, whatever is switched off', async () => {
    const { host } = await render(of(granted(CAPABILITIES, EVERY_KEY_OFF)));

    NEVER_SWITCHABLE.forEach((route) => expect(link(host, route)).not.toBeNull());
    SWITCHABLE.forEach((entry) => expect(link(host, entry.route)).toBeNull());
    expect(pills(host)).toEqual([]);
  });

  /** A key this build does not declare matches no card, so it withholds nothing. */
  it('withholds nothing for a key no card declares', async () => {
    const { host } = await render(of(granted(CAPABILITIES, { switchedOffKeys: ['admin.something-else'] })));

    SWITCHABLE.forEach((entry) => expect(link(host, entry.route)).not.toBeNull());
  });

  /**
   * A role preview withholds the administrator's own treatment, exactly as it withholds the
   * `Super Admin` menu group (issue #721): what a preview shows is what the previewed role sees.
   */
  it('withholds the super-admin treatment while a role preview is running', async () => {
    const { host } = await render(of(OWNER_OFF), {
      diagnosticsAccess: of(true),
      rolePreviewActive: of(true)
    });

    expect(link(host, ROUTE)).toBeNull();
    expect(pills(host)).toEqual([]);
  });

  it('offers every card while the setting has not been reported, hiding nothing over an unread answer', async () => {
    const { host } = await render(of(granted(CAPABILITIES)));

    SWITCHABLE.forEach((entry) => expect(link(host, entry.route)).not.toBeNull());
    expect(pills(host)).toEqual([]);
  });

  /**
   * The criterion "switching On adds nothing": a Manager holds no `Data.Repair`, so a key being on
   * puts no repair card in their hub - and neither does being the Super Admin, because a switch is
   * not a capability.
   */
  it('never offers a card to a member whose capabilities do not allow it while its key is on', async () => {
    const { host } = await render(of(granted(MANAGER_CAPABILITIES, NOTHING_OFF)), {
      diagnosticsAccess: of(true)
    });

    expect(link(host, ROUTE)).toBeNull();
  });

  it('never offers a card to a member whose capabilities do not allow it while its key is off', async () => {
    const { host } = await render(of(granted(MANAGER_CAPABILITIES, EVERY_KEY_OFF)));

    expect(link(host, ROUTE)).toBeNull();
  });

  /** The GST switch has moved to the Menu Visibility page: no control on this hub changes it. */
  it('offers no visibility switch of its own any more', async () => {
    const { host } = await render(of(OWNER_OFF), { diagnosticsAccess: of(true) });

    expect(host.querySelector('#historical-gst-classification-visibility')).toBeNull();
    expect(host.querySelector('app-historical-gst-visibility-toggle')).toBeNull();
    expect(host.querySelectorAll('button')).toHaveLength(0);
  });
});

/**
 * The hub with nothing left to offer (issue #1151). Before this it rendered an empty grid, which
 * reads as a page that failed rather than as a page with nothing on it.
 */
describe('AdminComponent empty state (issue #1151)', () => {
  function emptyState(host: HTMLElement): HTMLElement | null {
    return host.querySelector<HTMLElement>('[data-testid="admin-empty"]');
  }

  it('says there is nothing to show when every card a role had is withheld by a switch', async () => {
    const { host } = await render(
      of(granted(MANAGER_CAPABILITIES, { switchedOffKeys: [...MENU_VISIBILITY_KEYS] }))
    );

    expect(emptyState(host)).not.toBeNull();
    expect(emptyState(host)?.textContent).toContain('nothing');
  });

  /** An Owner still has the never-switchable destinations, so they are never the empty case. */
  it('says nothing to an Owner with every key switched off, who still has four cards', async () => {
    const { host } = await render(of(granted(CAPABILITIES, { switchedOffKeys: [...MENU_VISIBILITY_KEYS] })));

    expect(emptyState(host)).toBeNull();
  });

  it('says it for a role with no admin destination at all', async () => {
    const { host } = await render(of(OPERATOR));

    expect(emptyState(host)).not.toBeNull();
  });

  /** Not before the answer arrives: an unread capability set is not an empty hub (issue #674). */
  it('says nothing while the capability set has not been read', async () => {
    const { host } = await render(NEVER);

    expect(emptyState(host)).toBeNull();
  });

  it('says nothing once there is a card to show', async () => {
    const { host } = await render(of(OWNER));

    expect(emptyState(host)).toBeNull();
  });
});

