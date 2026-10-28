import { capabilitySet } from '../access/capabilities';
import { OptionVisibility } from '../access/option-visibility';
import { routes } from '../app.routes';
import { ProviderAvailability, providerFeatureAvailabilities, providerConnectionOf } from '../provider/provider-availability.service';
import {
  ProviderPermissionEvidence,
  UNKNOWN_PROVIDER_PERMISSIONS,
  withProviderPermission
} from '../provider/provider-feature-permissions';
import {
  NavGroup,
  NavItem,
  NavLink,
  NavSectionHeading,
  activeNavGroup,
  activeNavLink,
  activeNavRoute,
  businessesNavLink,
  navLinks,
  navLockOf,
  navigationFor,
  optionSwitchedOff,
  platformDiagnosticsNavLink,
  primaryNavigation,
  superAdminNavGroup,
  superAdminNavSection,
  supplierPresetsNavLink,
  switchableOptionRoute
} from './navigation';

/**
 * Each role's capability set, written out literally from the agreed table of #328 rather than read
 * back from the vocabulary, so a navigation entry that quietly moves to another capability fails
 * here instead of agreeing with itself.
 */
const OPERATOR = capabilitySet(['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate']);

const MANAGER = capabilitySet([
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
]);

const OWNER = capabilitySet([
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate',
  'Suppliers.ManageGstDefaults',
  'Expenses.Manage',
  'Reports.View',
  'Imports.Run',
  'SiteCommissions.Manage',
  'Integration.Manage',
  'Data.Repair',
  'Members.Manage',
  'Roles.View',
  'Business.Manage'
]);

/** The navigation an Owner is offered, which is every declared destination. */
function ownerNavigation(hasPlatformDiagnosticsAccess = false): readonly NavItem[] {
  return navigationFor(OWNER, hasPlatformDiagnosticsAccess, 'offered');
}

/**
 * The shared provider answer (issue #728) for one published status and one body of permission
 * evidence, derived by that signal's own functions rather than written out here: the menu must react
 * to the answer the application actually produces, not to a shape this spec invented.
 */
function availability(
  status: ProviderAvailability['status'],
  permissions: ProviderPermissionEvidence = UNKNOWN_PROVIDER_PERMISSIONS
): ProviderAvailability {
  const connection = providerConnectionOf(status);
  return { status, connection, features: providerFeatureAvailabilities(connection, permissions), permissions };
}

/** A usable connection whose machine fleet read Nayax has refused for lack of permission. */
const FLEET_REFUSED = availability(
  'PendingPermissions',
  withProviderPermission(UNKNOWN_PROVIDER_PERMISSIONS, 'machineFleet', 'denied')
);

function item(label: string, items: readonly NavItem[] = primaryNavigation): NavItem {
  const match = items.find((candidate) => candidate.label === label);
  expect(match).toBeDefined();
  return match!;
}

function group(label: string, items: readonly NavItem[] = primaryNavigation): NavGroup {
  const match = item(label, items);
  expect(match.kind).toBe('group');
  return match as NavGroup;
}

function childRoutes(label: string, items: readonly NavItem[] = primaryNavigation): { label: string; route: string }[] {
  return navLinks([group(label, items)]).map((child) => ({ label: child.label, route: child.route }));
}

function declaredRoutePaths(): Set<string> {
  return new Set(routes.map((route) => route.path).filter((path): path is string => path !== undefined));
}

function undeclaredRoutes(items: readonly NavItem[]): string[] {
  const declared = declaredRoutePaths();
  return navLinks(items)
    .map((link) => (link.route === '/' ? '' : link.route.replace(/^\//, '')))
    .filter((path) => !declared.has(path));
}

const adminChildRoutes: { label: string; route: string }[] = [
  { label: 'Integrations', route: '/admin/nayax-settings' },
  { label: 'Site Commission Agreements', route: '/admin/site-commission-agreements' },
  { label: 'Imports', route: '/admin/imports' },
  { label: 'Historical Cost Recovery', route: '/admin/historical-cost-recovery' },
  { label: 'AVCO Transition', route: '/admin/avco-transition' },
  { label: 'Costing Repair', route: '/admin/costing-repair' },
  { label: 'Historical GST Classification', route: '/admin/historical-gst-classification' },
  { label: 'Nayax Sale Timestamp Repair', route: '/admin/nayax-sale-timestamp-repair' },
  { label: 'Users', route: '/admin/users' },
  { label: 'Roles', route: '/admin/roles' },
  { label: 'Business Settings', route: '/admin/business-settings' }
];

describe('primary navigation (issue #391)', () => {
  /**
   * The order is itself a decision. Issue #729 (decision P6) put the live provider destinations in
   * their own "Needs Nayax" section at the bottom; issue #868 removed that section at the user's
   * request, put `Sites` immediately above `Machines` and moved `Site Commissions` back into
   * `Reports`. A destination's provider dependency is now carried by the entry itself - its
   * `lockedWithout` - rather than by where it sits in the menu.
   */
  it('declares the target navigation in order, under the Restockroo group headings (issue #972)', () => {
    expect(primaryNavigation.map((entry) => `${entry.kind}:${entry.label}`)).toEqual([
      'section:Operations',
      'link:Dashboard',
      'link:Pick List',
      'link:Sites',
      'link:Machines',
      'section:Inventory & purchasing',
      'group:Inventory',
      'group:Purchases',
      'section:Finance & reports',
      'link:Expenses',
      'group:Reports',
      'section:Settings',
      'group:Admin'
    ]);
    // A heading only labels: there is no provider section heading and no heading sentence.
    expect(
      primaryNavigation.filter((entry): entry is NavSectionHeading => entry.kind === 'section').map((entry) => entry.description)
    ).toEqual([undefined, undefined, undefined, undefined]);
  });

  /**
   * Every destination that depends on the provider, and the read each one depends on. The five
   * reports over stored provider records lock only while the business has no Nayax connection at
   * all (issue #868); the two live fleet destinations lock on anything their read cannot serve.
   */
  it('declares the provider read each locked destination depends on, and locks nothing else', () => {
    const locked = navLinks(primaryNavigation)
      .filter((entry) => entry.lockedWithout !== undefined)
      .map((entry) => `${entry.route}:${entry.lockedWithout}`);

    expect(locked).toEqual([
      '/sites:machineFleet',
      '/reports/daily:providerRecords',
      '/reports/transactions:providerRecords',
      '/reports/reconciliation:providerRecords',
      '/reports/machines:providerRecords',
      '/reports/products:providerRecords',
      '/reports/site-commissions:machineFleet',
      '/admin/site-commission-agreements:machineFleet',
      '/admin/avco-transition:machineFleet'
    ]);
  });

  /**
   * Machines declares no live Nayax read at all (issue #740): the page's local machine list, and
   * its create/rename/deactivate actions, work with no provider. Sites sits immediately above it
   * (issue #868), keeping its own route, label, icon, capability and live fleet read.
   */
  it('puts Sites immediately above Machines, which depends on no provider read', () => {
    const sites = item('Sites') as NavLink;
    const machines = item('Machines') as NavLink;

    expect(primaryNavigation.indexOf(machines)).toBe(primaryNavigation.indexOf(sites) + 1);
    expect(sites).toEqual({
      kind: 'link',
      label: 'Sites',
      route: '/sites',
      icon: 'location_on',
      capability: 'Inventory.Operate',
      lockedWithout: 'machineFleet'
    });
    expect(machines.route).toBe('/machines');
    expect(machines.lockedWithout).toBeUndefined();
    expect(machines.hiddenWithoutProvider).toBeUndefined();
  });

  it('keeps Pick List, Sites, Machines and Expenses as top-level direct links', () => {
    expect(primaryNavigation.filter((entry) => entry.kind === 'link').map((entry) => (entry as NavLink).route)).toEqual([
      '/',
      '/pick-list',
      '/sites',
      '/machines',
      '/expenses'
    ]);
  });

  /**
   * The group is "Inventory" since issue #729 (decision P11), and the Products page inside it keeps
   * its own name, its URL and its breadcrumb - the rename is of the heading alone.
   */
  it('groups the Inventory pages, with Products keeping its own name and URL', () => {
    expect(childRoutes('Inventory')).toEqual([
      { label: 'Products', route: '/products' },
      // Directly under the page it imports into (issue #888).
      { label: 'Import products', route: '/products/import' },
      { label: 'Take Inventory', route: '/take-inventory' },
      { label: 'Categories', route: '/categories' },
      { label: 'Stock History', route: '/stock-history' }
    ]);
    expect(primaryNavigation.map((entry) => entry.label)).not.toContain('Products');
  });

  it('groups the Purchases pages', () => {
    expect(childRoutes('Purchases')).toEqual([
      { label: 'Purchases', route: '/purchases' },
      { label: 'Supplier Orders', route: '/purchases/orders' },
      { label: 'Suppliers', route: '/suppliers' }
    ]);
  });

  /**
   * Every report is in this group again since issue #868, including the site commission report
   * that issue #729 moved out of it: Site Commissions sits after Products and before GST. Five of
   * them declare `providerRecords`, so they are locked only while the business has no Nayax
   * connection at all; Bookkeeping and GST declare nothing and are never locked.
   */
  it('groups every report, with Site Commissions after Products and before GST', () => {
    expect(childRoutes('Reports')).toEqual([
      { label: 'Bookkeeping', route: '/reports/bookkeeping' },
      { label: 'Daily', route: '/reports/daily' },
      { label: 'Transactions', route: '/reports/transactions' },
      { label: 'Reconciliation', route: '/reports/reconciliation' },
      { label: 'Machines', route: '/reports/machines' },
      { label: 'Products', route: '/reports/products' },
      { label: 'Site Commissions', route: '/reports/site-commissions' },
      { label: 'GST', route: '/reports/gst' }
    ]);

    for (const report of navLinks([group('Reports')])) {
      expect(report.hiddenWithoutProvider).toBeUndefined();
    }
  });

  /**
   * Bookkeeping and GST stay unlocked (issue #868): they are the two reports a business without
   * Nayax is deliberately left with, so nothing may declare a provider read for them.
   */
  it('leaves Bookkeeping and GST declaring no provider read at all', () => {
    for (const route of ['/reports/bookkeeping', '/reports/gst']) {
      const report = navLinks([group('Reports')]).find((entry) => entry.route === route);

      expect(report?.lockedWithout).toBeUndefined();
    }
  });

  /**
   * The site commission report keeps the live fleet read it has always declared: a site is known
   * only from that read, so it is unavailable for a reason the other reports are not, and that
   * reason is unchanged by moving the entry back into the group.
   */
  it('keeps Site Commissions on the live fleet read and on SiteCommissions.Manage', () => {
    const commissions = navLinks([group('Reports')]).find((entry) => entry.route === '/reports/site-commissions');

    expect(commissions?.capability).toBe('SiteCommissions.Manage');
    expect(commissions?.lockedWithout).toBe('machineFleet');
  });

  it('groups the dedicated Admin pages', () => {
    expect(childRoutes('Admin')).toEqual(adminChildRoutes);
  });

  /**
   * The two Admin destinations issue #869 locks, and the live fleet read each one depends on: the
   * agreements page's site picker is that read (`GET /api/sites`), and the AVCO transition preview
   * reads the machines and their products. Both are locked rather than hidden, exactly as Site
   * Commissions is - a page an operator could use again by connecting Nayax has to stay visible.
   */
  it('declares the live fleet read on Site Commission Agreements and AVCO Transition', () => {
    const children = navLinks([group('Admin')]);

    expect(children.find((child) => child.route === '/admin/site-commission-agreements')?.lockedWithout).toBe(
      'machineFleet'
    );
    expect(children.find((child) => child.route === '/admin/avco-transition')?.lockedWithout).toBe('machineFleet');
  });

  /**
   * Nayax Historical Cost Recovery stays unlocked by the user's decision of 24 October 2026: it
   * makes no Nayax call at all, reading the Nayax cost already stored on imported sales, so a
   * disconnected business with imported history can still recover those costs. Every other Admin
   * child keeps declaring nothing too - the two provider tools are hidden rather than locked.
   */
  it('leaves every other Admin child declaring no provider read, Historical Cost Recovery included', () => {
    const declaring = navLinks([group('Admin')])
      .filter((child) => child.lockedWithout !== undefined)
      .map((child) => child.route);

    expect(declaring).toEqual(['/admin/site-commission-agreements', '/admin/avco-transition']);
    expect(
      navLinks([group('Admin')]).find((child) => child.route === '/admin/historical-cost-recovery')?.lockedWithout
    ).toBeUndefined();
  });

  /** A locked Admin destination is locked, never hidden: only the two provider tools are hidden. */
  it('hides neither of the two locked Admin destinations', () => {
    for (const route of ['/admin/site-commission-agreements', '/admin/avco-transition']) {
      expect(navLinks([group('Admin')]).find((child) => child.route === route)?.hiddenWithoutProvider).toBeUndefined();
    }
  });

  /**
   * The navigation may only point at pages that exist: no placeholder Audit/Settings/Profile
   * destination, and no route invented ahead of the page that would serve it. Users is a real
   * destination since issue #534, and Roles since issue #530.
   */
  it('points only at routes declared in app.routes.ts', () => {
    expect(undeclaredRoutes(primaryNavigation)).toEqual([]);
  });

  it('adds no Settings, profile or audit destination', () => {
    // "Settings" is a group heading since issue #972 (over the existing Admin group), never a page.
    const labels = [
      ...primaryNavigation.filter((entry) => entry.kind !== 'section').map((entry) => entry.label),
      ...navLinks(primaryNavigation).map((link) => link.label)
    ];

    expect(labels).not.toContain('Settings');
    expect(labels).not.toContain('Profile');
    expect(labels).not.toContain('Audit');
  });

  /**
   * `primaryNavigation` is the navigation every signed-in operator gets, so the super-admin
   * diagnostics destination (#335) is deliberately not in it: it only exists in the navigation
   * `navigationFor(true)` composes, and only the diagnostics access API can decide that.
   */
  it('holds no super-admin diagnostics destination of its own', () => {
    expect(navLinks(primaryNavigation).map((link) => link.route).join(' ')).not.toContain('diagnostics');
  });
});

/**
 * The top-level Super Admin group (issue #546, parent #510 under #328), which took
 * `Platform Diagnostics` out of the business `Admin` group (issue #335) and added Businesses
 * (issue #547) and Supplier Presets (issue #848).
 *
 * The group is presentation only. `GET /api/admin/diagnostics/access` decides whether it is
 * composed in, and the API is independently authorized on every request, so a navigation without
 * it hides a page rather than protecting one: `/super-admin/diagnostics` entered directly still
 * works, and still shows nothing an unauthorized caller could read.
 */
describe('navigationFor (issue #546)', () => {
  it('offers no Super Admin group until the API confirms platform-admin access', () => {
    expect(ownerNavigation().map((entry) => entry.label)).not.toContain('Super Admin');
    expect(childRoutes('Admin', ownerNavigation())).toEqual(adminChildRoutes);
  });

  it('adds it last, holding its three destinations, once the API confirms access', () => {
    expect(group('Super Admin', ownerNavigation(true)).children).toEqual([
      platformDiagnosticsNavLink,
      businessesNavLink,
      supplierPresetsNavLink
    ]);
  });

  it('changes nothing else: the same groups in the same order, with Super Admin appended last', () => {
    expect(ownerNavigation(true).map((entry) => `${entry.kind}:${entry.label}`)).toEqual([
      ...primaryNavigation.map((entry) => `${entry.kind}:${entry.label}`),
      'section:Super admin',
      'group:Super Admin'
    ]);
    expect(childRoutes('Admin', ownerNavigation(true))).toEqual(adminChildRoutes);
    expect(navLinks(ownerNavigation(true)).map((link) => link.route)).toEqual([
      ...navLinks(primaryNavigation).map((link) => link.route),
      platformDiagnosticsNavLink.route,
      businessesNavLink.route,
      supplierPresetsNavLink.route
    ]);
  });

  it('leaves primaryNavigation itself untouched, so one granted navigation cannot leak into another', () => {
    ownerNavigation(true);
    navigationFor(OPERATOR, true, 'offered');
    navigationFor(OWNER, false, 'offered', availability('NotConfigured'));

    expect(childRoutes('Admin')).toEqual(adminChildRoutes);
    expect(primaryNavigation.map((entry) => entry.label)).toEqual([
      'Operations',
      'Dashboard',
      'Pick List',
      'Sites',
      'Machines',
      'Inventory & purchasing',
      'Inventory',
      'Purchases',
      'Finance & reports',
      'Expenses',
      'Reports',
      'Settings',
      'Admin'
    ]);
  });

  it('points only at routes declared in app.routes.ts, with or without access', () => {
    expect(undeclaredRoutes(ownerNavigation())).toEqual([]);
    expect(undeclaredRoutes(ownerNavigation(true))).toEqual([]);
    expect(undeclaredRoutes(navigationFor(OPERATOR, false, 'offered'))).toEqual([]);
    expect(undeclaredRoutes(navigationFor(MANAGER, false, 'offered'))).toEqual([]);
    expect(undeclaredRoutes(navigationFor(undefined, true, 'offered'))).toEqual([]);
  });

  it('resolves the diagnostics page as the active entry inside the Super Admin group', () => {
    const granted = ownerNavigation(true);

    expect(activeNavRoute(granted, '/super-admin/diagnostics')).toBe('/super-admin/diagnostics');
    expect(activeNavGroup(granted, '/super-admin/diagnostics')?.label).toBe('Super Admin');
    expect(activeNavRoute(ownerNavigation(), '/super-admin/diagnostics')).toBeUndefined();
  });

  it('resolves the businesses page as the active entry inside the Super Admin group (#547)', () => {
    const granted = ownerNavigation(true);

    expect(activeNavRoute(granted, '/super-admin/businesses')).toBe('/super-admin/businesses');
    expect(activeNavGroup(granted, '/super-admin/businesses')?.label).toBe('Super Admin');
    expect(activeNavRoute(ownerNavigation(), '/super-admin/businesses')).toBeUndefined();
  });

  /**
   * Neither child declares a capability, so the group is offered to the configured platform
   * administrator whatever business role they hold - which is the point: no role grants either,
   * and only `GET /api/admin/diagnostics/access` can say yes.
   */
  it('keeps the group independent of the member capabilities', () => {
    const operator = navigationFor(OPERATOR, true, 'offered');

    expect(group('Super Admin', operator).children).toEqual([
      platformDiagnosticsNavLink,
      businessesNavLink,
      supplierPresetsNavLink
    ]);
    expect(navLinks(navigationFor(OPERATOR, false, 'offered')).map((link) => link.route)).not.toContain(
      platformDiagnosticsNavLink.route
    );
  });

  it("declares Businesses with no capability, so no business role is what decides it (#547)", () => {
    expect(businessesNavLink).toEqual({ kind: 'link', label: 'Businesses', route: '/super-admin/businesses' });
    expect(superAdminNavGroup.children).toContain(businessesNavLink);
  });

  it('declares Supplier Presets with no capability either, for the same reason (#848)', () => {
    expect(supplierPresetsNavLink).toEqual({
      kind: 'link',
      label: 'Supplier Presets',
      route: '/super-admin/supplier-presets'
    });
    expect(superAdminNavGroup.children).toContain(supplierPresetsNavLink);
    expect(navLinks(navigationFor(OPERATOR, false, 'offered')).map((link) => link.route)).not.toContain(
      supplierPresetsNavLink.route
    );
  });
});

/**
 * The navigation is built from the capabilities `GET /api/me/access` reported (issue #528). Each
 * destination declares the capability its own page needs, and the API checks the same capability
 * on every request behind that page - so a destination that is not rendered hides a page rather
 * than protecting one.
 */
describe('navigationFor capabilities (issue #528)', () => {
  /** The destinations and groups, without the issue #972 headings over them. */
  function labels(items: readonly NavItem[]): string[] {
    return items.filter((entry) => entry.kind !== 'section').map((entry) => entry.label);
  }

  it('declares the capability its own page needs on every destination', () => {
    const undeclared = navLinks(primaryNavigation)
      .filter((entry) => entry.capability === undefined)
      .map((entry) => entry.route);

    expect(undeclared).toEqual([]);
  });

  it('offers an Operator no Expenses, no Reports group and no Admin group', () => {
    const operator = navigationFor(OPERATOR, false, 'offered');

    expect(labels(operator)).toEqual(['Dashboard', 'Pick List', 'Sites', 'Machines', 'Inventory', 'Purchases']);
    expect(navLinks(operator).map((entry) => entry.route)).toEqual([
      '/',
      '/pick-list',
      '/sites',
      '/machines',
      '/products',
      '/take-inventory',
      '/categories',
      '/stock-history',
      '/purchases',
      '/purchases/orders',
      '/suppliers'
    ]);
  });

  it('offers a Manager the reports and expenses, but no Integrations and no data repair', () => {
    const manager = navigationFor(MANAGER, false, 'offered');

    expect(labels(manager)).toEqual([
      'Dashboard',
      'Pick List',
      'Sites',
      'Machines',
      'Inventory',
      'Purchases',
      'Expenses',
      'Reports',
      'Admin'
    ]);
    expect(childRoutes('Reports', manager).map((child) => child.route)).toEqual(
      childRoutes('Reports').map((child) => child.route)
    );
    expect(childRoutes('Admin', manager)).toEqual([
      { label: 'Site Commission Agreements', route: '/admin/site-commission-agreements' },
      { label: 'Imports', route: '/admin/imports' }
    ]);
  });

  it('offers an Owner every declared destination', () => {
    expect(navLinks(ownerNavigation()).map((entry) => entry.route)).toEqual(
      navLinks(primaryNavigation).map((entry) => entry.route)
    );
  });

  /** The Admin → Roles page (issue #530): visible only to the role that holds `Roles.View`. */
  it('offers Roles to an Owner and hides it from a Manager and an Operator', () => {
    expect(childRoutes('Admin', ownerNavigation())).toContainEqual({ label: 'Roles', route: '/admin/roles' });
    expect(childRoutes('Admin', navigationFor(MANAGER, false, 'offered'))).not.toContainEqual(
      expect.objectContaining({ route: '/admin/roles' })
    );
    expect(navLinks(navigationFor(OPERATOR, false, 'offered')).map((link) => link.route)).not.toContain('/admin/roles');
  });

  /** The Admin → Business Settings page (issue #549): visible only to the role that holds `Business.Manage`. */
  it('offers Business Settings to an Owner and hides it from a Manager and an Operator', () => {
    expect(childRoutes('Admin', ownerNavigation())).toContainEqual({
      label: 'Business Settings',
      route: '/admin/business-settings'
    });
    expect(childRoutes('Admin', navigationFor(MANAGER, false, 'offered'))).not.toContainEqual(
      expect.objectContaining({ route: '/admin/business-settings' })
    );
    expect(navLinks(navigationFor(OPERATOR, false, 'offered')).map((link) => link.route)).not.toContain(
      '/admin/business-settings'
    );
  });

  /**
   * No current role is missing `Reports.View` while holding `Imports.Run`, but the group must drop
   * only the children the capability set actually withholds, not the whole group.
   */
  it('keeps a group whose children are only partly allowed, with only those children', () => {
    const importsOnly = capabilitySet(['Imports.Run']);

    expect(childRoutes('Admin', navigationFor(importsOnly, false, 'offered'))).toEqual([
      { label: 'Imports', route: '/admin/imports' }
    ]);
  });

  /**
   * `SiteCommissions.Manage` alone leaves one report and one Admin page, and the site commission
   * report is inside the `Reports` group again since issue #868 - so the group is offered holding
   * that one child, rather than a top-level entry under a heading.
   */
  it('keeps the Reports group for the one report a capability set leaves in it', () => {
    const commissionsOnly = navigationFor(capabilitySet(['SiteCommissions.Manage']), false, 'offered');

    expect(labels(commissionsOnly)).toEqual(['Reports', 'Admin']);
    expect(childRoutes('Reports', commissionsOnly)).toEqual([
      { label: 'Site Commissions', route: '/reports/site-commissions' }
    ]);
  });

  it('offers nothing at all for an empty capability set, which is what an unread one renders as', () => {
    expect(navigationFor(capabilitySet([]), false, 'offered')).toEqual([]);
    expect(navLinks(navigationFor(capabilitySet([]), false, 'offered'))).toEqual([]);
  });

  /**
   * A capability set that could not be read offers everything: the API refuses what the role may
   * not do regardless, and an empty menu would strand a member whose capability read failed.
   */
  it('offers every destination when the capability set is not known', () => {
    expect(navigationFor(undefined, false, 'offered')).toEqual(primaryNavigation);
    expect(childRoutes('Admin', navigationFor(undefined, true, 'offered'))).toEqual(adminChildRoutes);
    expect(group('Super Admin', navigationFor(undefined, true, 'offered')).children).toEqual([
      platformDiagnosticsNavLink,
      businessesNavLink,
      supplierPresetsNavLink
    ]);
  });

  it('resolves the active entry only among the destinations the member was offered', () => {
    const operator = navigationFor(OPERATOR, false, 'offered');

    expect(activeNavRoute(operator, '/products/12/edit')).toBe('/products');
    expect(activeNavRoute(operator, '/reports/gst')).toBeUndefined();
    expect(activeNavGroup(operator, '/admin/costing-repair')).toBeUndefined();
  });
});

/**
 * Withholding the business navigation altogether (issue #676), which no capability answer could
 * decide: a signed-out visitor's and a known nonmember's capability read both answer `unavailable`,
 * the one answer that offers every destination.
 */
describe('navigationFor with the business navigation withheld (issue #676)', () => {
  it('offers no business destination at all, whatever the capability set says', () => {
    expect(navigationFor(OWNER, false, 'withheld')).toEqual([]);
    expect(navigationFor(OPERATOR, false, 'withheld')).toEqual([]);
    expect(navLinks(navigationFor(OWNER, false, 'withheld'))).toEqual([]);
  });

  /** The fail-open capability answer is exactly the case this withholding exists for. */
  it('offers none when the capability set could not be read either', () => {
    expect(navigationFor(undefined, false, 'withheld')).toEqual([]);
  });

  it('keeps the separately authorized Super Admin group, and only it', () => {
    expect(navigationFor(undefined, true, 'withheld')).toEqual([superAdminNavSection, superAdminNavGroup]);
    expect(navLinks(navigationFor(OWNER, true, 'withheld'))).toEqual([
      platformDiagnosticsNavLink,
      businessesNavLink,
      supplierPresetsNavLink
    ]);
  });

  it('leaves the declared navigation untouched for the next member it is composed for', () => {
    navigationFor(OWNER, true, 'withheld');

    expect(navigationFor(OWNER, false, 'offered')).toEqual(primaryNavigation);
  });
});

describe('activeNavRoute', () => {
  it('matches the dashboard only on the root URL', () => {
    expect(activeNavRoute(primaryNavigation, '/')).toBe('/');
    expect(activeNavRoute(primaryNavigation, '/machines')).toBe('/machines');
  });

  it('prefers the most specific matching link so a parent page does not steal its own child', () => {
    expect(activeNavRoute(primaryNavigation, '/purchases')).toBe('/purchases');
    expect(activeNavRoute(primaryNavigation, '/purchases/orders')).toBe('/purchases/orders');
    expect(activeNavRoute(primaryNavigation, '/reports/bookkeeping')).toBe('/reports/bookkeeping');
    expect(activeNavRoute(primaryNavigation, '/reports/site-commissions')).toBe('/reports/site-commissions');
  });

  it('keeps a detail or child URL on its list page', () => {
    expect(activeNavRoute(primaryNavigation, '/machines/7')).toBe('/machines');
    expect(activeNavRoute(primaryNavigation, '/sites/3/products')).toBe('/sites');
    expect(activeNavRoute(primaryNavigation, '/products/12/edit')).toBe('/products');
    expect(activeNavRoute(primaryNavigation, '/purchases/new')).toBe('/purchases');
  });

  it('ignores query parameters, the fragment and a trailing slash', () => {
    expect(activeNavRoute(primaryNavigation, '/stock-history?productId=5')).toBe('/stock-history');
    expect(activeNavRoute(primaryNavigation, '/reports/gst#totals')).toBe('/reports/gst');
    expect(activeNavRoute(primaryNavigation, '/categories/')).toBe('/categories');
  });

  it('reports no active link for a URL outside the navigation', () => {
    expect(activeNavRoute(primaryNavigation, '/auth')).toBeUndefined();
  });
});

describe('activeNavGroup', () => {
  it('names the group that owns the active page', () => {
    expect(activeNavGroup(primaryNavigation, '/reports/gst')?.label).toBe('Reports');
    expect(activeNavGroup(primaryNavigation, '/admin/costing-repair')?.label).toBe('Admin');
    expect(activeNavGroup(primaryNavigation, '/products/12/edit')?.label).toBe('Inventory');
    expect(activeNavGroup(primaryNavigation, '/purchases/orders')?.label).toBe('Purchases');
  });

  it('is undefined for a top-level link and for an unknown URL', () => {
    expect(activeNavGroup(primaryNavigation, '/machines')).toBeUndefined();
    expect(activeNavGroup(primaryNavigation, '/')).toBeUndefined();
    expect(activeNavGroup(primaryNavigation, '/auth')).toBeUndefined();
  });

  /** The site commission report is a Reports child again since issue #868. */
  it('names Reports for the site commission report, which is back in that group (issue #868)', () => {
    expect(activeNavGroup(primaryNavigation, '/reports/site-commissions')?.label).toBe('Reports');
    expect(activeNavRoute(primaryNavigation, '/reports/site-commissions')).toBe('/reports/site-commissions');
  });
});

/**
 * The live provider destinations, the locked state the menu renders them in, and the repair tools
 * that are hidden outright (issue #729, child E2 of issue #329, decision P6). Every answer here
 * comes from the shared availability signal of issue #728; this file only decides what the menu
 * does with it.
 */
describe('navigationFor with the provider answer (issue #729)', () => {
  /** The destinations and groups, without the issue #972 headings over them. */
  function labels(items: readonly NavItem[]): string[] {
    return items.filter((entry) => entry.kind !== 'section').map((entry) => entry.label);
  }

  /**
   * By route, not by label: "Machines" names both the machine list and the machine profitability
   * report, and "Sites" is the live fleet destination issue #729's distinction is now drawn on -
   * since issue #740 the machine list itself depends on no provider read.
   */
  function lockOf(route: string, provider: ProviderAvailability | undefined): string {
    const entry = navLinks(ownerNavigation()).find((candidate) => candidate.route === route);
    expect(entry).toBeDefined();
    return navLockOf(entry!, provider);
  }

  /** The five reports over stored provider records, locked only by a missing connection (#868). */
  const providerRecordReports = [
    '/reports/daily',
    '/reports/transactions',
    '/reports/reconciliation',
    '/reports/machines',
    '/reports/products'
  ];

  it('locks every live provider destination for a business with no connection at all', () => {
    const noProvider = availability('NotConfigured');

    expect(lockOf('/sites', noProvider)).toBe('connect');
    expect(lockOf('/reports/site-commissions', noProvider)).toBe('connect');
  });

  /**
   * Issue #868, which supersedes decision P5 for these five reports only: a business with no Nayax
   * connection at all is shown the same connection-required treatment on them that Site Commissions
   * already had.
   */
  it('locks each of the five stored-record reports for a business with no connection at all', () => {
    for (const route of providerRecordReports) {
      expect(lockOf(route, availability('NotConfigured'))).toBe('connect');
    }
  });

  /**
   * `providerRecords` asks only whether Nayax is part of this business, so stored credentials that
   * stopped working keep these reports **open**: the sales they report on are already stored, and
   * locking them would hide a business's own records behind a token that needs re-entering.
   */
  it('leaves them open for credentials that are stored and known not to work', () => {
    for (const route of providerRecordReports) {
      expect(lockOf(route, availability('NeedsAttention'))).toBe('open');
    }
  });

  /**
   * A refused Nayax **read** says nothing about stored records either: the connection works, and
   * these reports make no live read at all.
   */
  it('leaves them open on a usable connection whose fleet read Nayax refused', () => {
    for (const route of providerRecordReports) {
      expect(lockOf(route, FLEET_REFUSED)).toBe('open');
    }
    expect(lockOf('/reports/site-commissions', FLEET_REFUSED)).toBe('permission');
  });

  it('leaves them open on a usable connection with nothing refused, and on an unread status', () => {
    for (const route of providerRecordReports) {
      expect(lockOf(route, availability('Ready'))).toBe('open');
      expect(lockOf(route, availability(null))).toBe('open');
      expect(lockOf(route, undefined)).toBe('open');
    }
  });

  /** Bookkeeping and GST stay unlocked in every state, because neither declares a provider read. */
  it('never locks Bookkeeping or GST', () => {
    for (const route of ['/reports/bookkeeping', '/reports/gst']) {
      for (const provider of [availability('NotConfigured'), availability('NeedsAttention'), FLEET_REFUSED]) {
        expect(lockOf(route, provider)).toBe('open');
      }
    }
  });

  /**
   * The machine list is never locked (issue #740): it has the business's own machine records to
   * show and its own create/rename/deactivate actions. The machine profitability **report** is a
   * different destination, and since issue #868 it is locked with the other four stored-record
   * reports while the business has no Nayax connection at all.
   */
  it('leaves the machine list open with no provider at all, unlike the machine report', () => {
    expect(lockOf('/machines', availability('NotConfigured'))).toBe('open');
    expect(lockOf('/reports/machines', availability('NotConfigured'))).toBe('connect');
  });

  /**
   * Stored credentials that stopped working cannot serve a live read either, so the destination is
   * locked - and locked as a connection, because reconnecting is what fixes it.
   */
  it('locks them for credentials that are stored and known not to work', () => {
    expect(lockOf('/sites', availability('NeedsAttention'))).toBe('connect');
  });

  it('leaves them open on a usable connection', () => {
    expect(lockOf('/sites', availability('Ready'))).toBe('open');
    expect(lockOf('/reports/site-commissions', availability('Ready'))).toBe('open');
  });

  /**
   * The per-feature case of issue #329's decision P9: in `PendingPermissions` the connection is
   * usable and Nayax may have granted some reads and refused others, so one refused read locks the
   * destinations that need it and **nothing else**.
   */
  it('locks only the destinations whose own read Nayax refused, as a permission', () => {
    expect(lockOf('/sites', FLEET_REFUSED)).toBe('permission');
    expect(lockOf('/reports/site-commissions', FLEET_REFUSED)).toBe('permission');
    expect(lockOf('/machines', FLEET_REFUSED)).toBe('open');
    expect(lockOf('/reports/machines', FLEET_REFUSED)).toBe('open');
  });

  it('leaves a destination whose read is granted open beside a refused one', () => {
    const fleetGranted = availability(
      'PendingPermissions',
      withProviderPermission(
        withProviderPermission(UNKNOWN_PROVIDER_PERMISSIONS, 'machineFleet', 'granted'),
        'machineSales',
        'denied'
      )
    );

    expect(lockOf('/sites', fleetGranted)).toBe('open');
  });

  /** An unread status is not a refusal, so nothing locks before the answer settles. */
  it('locks nothing while the provider answer is not known, and nothing on an unreadable status', () => {
    expect(lockOf('/sites', undefined)).toBe('open');
    expect(lockOf('/sites', availability(null))).toBe('open');
  });

  /**
   * The two Admin destinations of issue #869. They declare the live fleet read, so they behave
   * exactly as `/sites` and `/reports/site-commissions` do rather than as the five stored-record
   * reports: credentials that stopped working lock them too, because the reads behind them need
   * credentials the status gate will use.
   */
  describe('the two locked Admin destinations (issue #869)', () => {
    const LOCKED_ADMIN = ['/admin/site-commission-agreements', '/admin/avco-transition'];

    it.each(LOCKED_ADMIN)('locks %s for a business with no connection at all', (route) => {
      expect(lockOf(route, availability('NotConfigured'))).toBe('connect');
    });

    it.each(LOCKED_ADMIN)('locks %s for credentials that are stored and known not to work', (route) => {
      expect(lockOf(route, availability('NeedsAttention'))).toBe('connect');
    });

    it.each(LOCKED_ADMIN)('locks %s as a permission when Nayax refused the fleet read', (route) => {
      expect(lockOf(route, FLEET_REFUSED)).toBe('permission');
    });

    it.each(LOCKED_ADMIN)('leaves %s open on a usable connection and on an unread status', (route) => {
      expect(lockOf(route, availability('Ready'))).toBe('open');
      expect(lockOf(route, availability(null))).toBe('open');
      expect(lockOf(route, undefined)).toBe('open');
    });

    /**
     * Nayax Historical Cost Recovery is the destination beside them that must stay open: it reads
     * the Nayax cost already stored on imported sales and makes no Nayax call, so a disconnected
     * business with imported history can still recover those costs (the user's decision of 24
     * October 2026).
     */
    it('never locks Nayax Historical Cost Recovery, in any provider state', () => {
      for (const provider of [availability('NotConfigured'), availability('NeedsAttention'), FLEET_REFUSED]) {
        expect(lockOf('/admin/historical-cost-recovery', provider)).toBe('open');
      }
    });

    /** Both stay in the Admin group with no provider at all, locked rather than withheld. */
    it('keeps both in the Admin group with no provider, beside the unlocked repairs', () => {
      const offered = childRoutes('Admin', navigationFor(OWNER, false, 'offered', availability('NotConfigured'))).map(
        (child) => child.route
      );

      expect(offered).toContain('/admin/site-commission-agreements');
      expect(offered).toContain('/admin/avco-transition');
      expect(offered).toContain('/admin/historical-cost-recovery');
    });
  });

  it('never locks a destination that declares no live provider read', () => {
    for (const entry of navLinks(ownerNavigation()).filter((candidate) => candidate.lockedWithout === undefined)) {
      expect(navLockOf(entry, availability('NotConfigured'))).toBe('open');
    }
  });

  /**
   * **A locked report is still offered** (issue #868): with no provider at all every report is
   * still in the menu, in the Reports group, and the five that need a connection are shown locked
   * rather than hidden - a page an operator could use again by connecting Nayax has to stay
   * visible and explain itself.
   */
  it('keeps every report in the group with no provider, locking the five and no others', () => {
    const noProvider = navigationFor(OWNER, false, 'offered', availability('NotConfigured'));

    expect(childRoutes('Reports', noProvider).map((child) => child.route)).toEqual([
      '/reports/bookkeeping',
      '/reports/daily',
      '/reports/transactions',
      '/reports/reconciliation',
      '/reports/machines',
      '/reports/products',
      '/reports/site-commissions',
      '/reports/gst'
    ]);

    const locked = navLinks([group('Reports', noProvider)])
      .filter((report) => navLockOf(report, availability('NotConfigured')) !== 'open')
      .map((report) => report.route);
    expect(locked).toEqual([
      '/reports/daily',
      '/reports/transactions',
      '/reports/reconciliation',
      '/reports/machines',
      '/reports/products',
      '/reports/site-commissions'
    ]);
  });

  /** The provider repair and admin tools are hidden outright, not locked. */
  it('hides the Nayax imports and the sale timestamp repair until the connection is usable', () => {
    const hidden = ['/admin/imports', '/admin/nayax-sale-timestamp-repair'];

    for (const status of ['NotConfigured', 'NeedsAttention'] as const) {
      const routesOffered = childRoutes('Admin', navigationFor(OWNER, false, 'offered', availability(status))).map(
        (child) => child.route
      );

      for (const tool of hidden) {
        expect(routesOffered).not.toContain(tool);
      }
      // Integrations stays: it is where the connection is fixed, so it can never depend on one.
      expect(routesOffered).toContain('/admin/nayax-settings');
      expect(routesOffered).toContain('/admin/costing-repair');
    }
  });

  it('offers them on a usable connection, and while the answer is not known', () => {
    for (const provider of [availability('Ready'), availability('PendingPermissions'), undefined]) {
      expect(childRoutes('Admin', navigationFor(OWNER, false, 'offered', provider))).toEqual(adminChildRoutes);
    }
  });

  /**
   * The dedicated Import products page (issue #888) is a provider tool for the same reason the
   * Admin imports are: with no usable connection there is no provider catalogue to review, so the
   * entry is left out rather than shown locked. The Inventory group survives it either way, because
   * the business's own stock keeping works with no provider at all.
   */
  it('hides Import products until the connection is usable, keeping the rest of the Inventory group', () => {
    for (const status of ['NotConfigured', 'NeedsAttention'] as const) {
      const inventory = childRoutes('Inventory', navigationFor(OWNER, false, 'offered', availability(status))).map(
        (child) => child.route
      );

      expect(inventory).not.toContain('/products/import');
      expect(inventory).toContain('/products');
    }

    for (const provider of [availability('Ready'), availability('PendingPermissions'), undefined]) {
      expect(
        childRoutes('Inventory', navigationFor(OWNER, false, 'offered', provider)).map((child) => child.route)
      ).toContain('/products/import');
    }
  });

  /** Capability rules come first here too: an Operator never had `Imports.Run`, so it stays hidden. */
  it('hides Import products from a member without Imports.Run, whatever the provider says', () => {
    for (const provider of [availability('Ready'), undefined]) {
      expect(navLinks(navigationFor(OPERATOR, false, 'offered', provider)).map((entry) => entry.route)).not.toContain(
        '/products/import'
      );
    }

    expect(navLinks(navigationFor(MANAGER, false, 'offered', availability('Ready'))).map((entry) => entry.route)).toContain(
      '/products/import'
    );
  });

  /**
   * A capability set whose only Admin child is the Nayax imports leaves that group with no child at
   * all once the provider is gone, so the group goes with its last one - exactly as it does for a
   * capability set that withholds every child. A Manager keeps the group, because the site
   * commission agreements are still there.
   */
  it("drops a group whose only children were the provider's", () => {
    const importsOnly = capabilitySet(['Imports.Run']);

    expect(childRoutes('Admin', navigationFor(importsOnly, false, 'offered'))).toEqual([
      { label: 'Imports', route: '/admin/imports' }
    ]);
    expect(labels(navigationFor(importsOnly, false, 'offered', availability('NotConfigured')))).not.toContain('Admin');
    expect(childRoutes('Admin', navigationFor(MANAGER, false, 'offered', availability('NotConfigured')))).toEqual([
      { label: 'Site Commission Agreements', route: '/admin/site-commission-agreements' }
    ]);
  });

  /** Capability rules apply first: an entry the role cannot see stays hidden, never locked. */
  it('hides a live provider destination the role may not see, rather than locking it', () => {
    const operator = navigationFor(OPERATOR, false, 'offered', availability('NotConfigured'));

    expect(navLinks(operator).map((entry) => entry.route)).toContain('/sites');
    expect(navLinks(operator).map((entry) => entry.route)).not.toContain('/reports/site-commissions');
  });

  it('withholds the business menu entirely whatever the provider answer says', () => {
    expect(navigationFor(OWNER, false, 'withheld', availability('Ready'))).toEqual([]);
    expect(navigationFor(OWNER, false, 'withheld', availability('NotConfigured'))).toEqual([]);
  });
});

/**
 * `activeNavLink` (issue #729): the declared destination a URL belongs to, which is how the "needs
 * a provider" page names the page an operator asked for and finds the read it needs.
 */
describe('activeNavLink (issue #729)', () => {
  it('names the destination a provider-locked URL belongs to, with its live read', () => {
    expect(activeNavLink(primaryNavigation, '/sites')?.label).toBe('Sites');
    expect(activeNavLink(primaryNavigation, '/sites')?.lockedWithout).toBe('machineFleet');
    expect(activeNavLink(primaryNavigation, '/reports/site-commissions')?.label).toBe('Site Commissions');

    // The machine list is no longer a provider-locked destination (issue #740), so the "needs a
    // provider" page has nothing to explain about it.
    expect(activeNavLink(primaryNavigation, '/machines')?.label).toBe('Machines');
    expect(activeNavLink(primaryNavigation, '/machines')?.lockedWithout).toBeUndefined();
  });

  it('keeps a detail URL on its own list destination', () => {
    expect(activeNavLink(primaryNavigation, '/machines/7')?.label).toBe('Machines');
    expect(activeNavLink(primaryNavigation, '/sites/3/products')?.label).toBe('Sites');
  });

  it('names a destination inside a group too', () => {
    expect(activeNavLink(primaryNavigation, '/stock-history')?.label).toBe('Stock History');
  });

  it('is undefined for a URL outside the navigation', () => {
    expect(activeNavLink(primaryNavigation, '/needs-provider')).toBeUndefined();
    expect(activeNavLink(primaryNavigation, '/auth')).toBeUndefined();
  });
});

/**
 * The one switchable Admin option (issue #873): a Super Admin may stop Historical GST Classification
 * being offered to everybody else, and the menu is where that takes effect.
 *
 * What these tests have to hold apart is "offered" from "permitted". The switch decides whether a
 * destination is rendered; the capability set still decides whether it could be, and the API behind
 * the page authorizes every request itself either way - so switching the option on never adds an
 * entry to a member whose role never had it, and switching it off never takes a capability away.
 */
describe('switchable Admin option visibility (issue #873)', () => {
  const OFF: OptionVisibility = { historicalGstClassificationVisible: false };
  const ON: OptionVisibility = { historicalGstClassificationVisible: true };

  const entry = { label: 'Historical GST Classification', route: '/admin/historical-gst-classification' };

  function adminEntries(
    capabilities = OWNER,
    hasPlatformDiagnosticsAccess = false,
    options?: OptionVisibility
  ): { label: string; route: string }[] {
    return childRoutes(
      'Admin',
      navigationFor(capabilities, hasPlatformDiagnosticsAccess, 'offered', undefined, options)
    );
  }

  it('offers the entry to an eligible member while the option is on', () => {
    expect(adminEntries(OWNER, false, ON)).toContainEqual(entry);
  });

  it('hides it from an eligible member who is not a super admin while the option is off', () => {
    expect(adminEntries(OWNER, false, OFF)).not.toContainEqual(entry);
  });

  it('keeps it for a super admin while the option is off', () => {
    expect(adminEntries(OWNER, true, OFF)).toContainEqual(entry);
  });

  /**
   * An unread setting offers it, the same fail-open rule an unreadable capability set follows: the
   * answer arrives with the capabilities, so "not known" means the read that would have said
   * otherwise has not answered, and an entry whose own page authorizes itself is the safer default.
   */
  it('offers it while the setting is not known yet', () => {
    expect(adminEntries(OWNER, false, undefined)).toContainEqual(entry);
  });

  /**
   * The criterion "On restores it only to users already eligible under existing permissions": a
   * Manager holds no `Data.Repair`, so neither state of the switch puts this entry in their menu -
   * and neither does being the Super Admin, because the switch is not a capability.
   */
  it('never offers it to a member whose capabilities do not allow it', () => {
    expect(adminEntries(MANAGER, false, ON)).not.toContainEqual(entry);
    expect(adminEntries(MANAGER, false, OFF)).not.toContainEqual(entry);
    expect(adminEntries(MANAGER, true, OFF)).not.toContainEqual(entry);
  });

  it('changes no other destination, in either state', () => {
    const on = navigationFor(OWNER, true, 'offered', undefined, ON);
    const off = navigationFor(OWNER, true, 'offered', undefined, OFF);

    expect(navLinks(off).map((link) => link.route)).toEqual(navLinks(on).map((link) => link.route));
    expect(adminEntries(OWNER, false, OFF)).toEqual(
      adminEntries(OWNER, false, ON).filter((child) => child.route !== entry.route)
    );
  });

  /**
   * What the sidebar renders the `Off` pill from, and nothing else: the one switchable entry, while
   * the setting says it is switched off.
   */
  it('reports the switched-off state for that entry alone', () => {
    expect(optionSwitchedOff(entry.route, OFF)).toBe(true);
    expect(optionSwitchedOff(entry.route, ON)).toBe(false);
    expect(optionSwitchedOff(entry.route, undefined)).toBe(false);
    expect(optionSwitchedOff('/admin/costing-repair', OFF)).toBe(false);
    expect(optionSwitchedOff('/admin/business-settings', OFF)).toBe(false);
  });

  /** The route the switch is about is the Admin destination it names, and only that one. */
  it('names the one switchable destination', () => {
    expect(switchableOptionRoute).toBe(entry.route);
    expect(childRoutes('Admin')).toContainEqual(entry);
  });
});

/**
 * The Restockroo group headings (issue #972) only label the entries under them, so they follow the
 * same capability, provider and platform-admin rules as those entries: a heading whose entries were
 * all left out is left out with them, and the Super admin heading exists only with its group.
 */
describe('navigation group headings (issue #972)', () => {
  function headings(items: readonly NavItem[]): string[] {
    return items.filter((entry) => entry.kind === 'section').map((entry) => entry.label);
  }

  it('gives an Owner every business heading and no Super admin heading', () => {
    expect(headings(ownerNavigation())).toEqual(['Operations', 'Inventory & purchasing', 'Finance & reports', 'Settings']);
  });

  it('drops the headings an Operator has nothing under', () => {
    expect(headings(navigationFor(OPERATOR, false, 'offered'))).toEqual(['Operations', 'Inventory & purchasing']);
  });

  it('keeps a heading for the one entry a capability set leaves under it', () => {
    expect(headings(navigationFor(capabilitySet(['SiteCommissions.Manage']), false, 'offered'))).toEqual([
      'Finance & reports',
      'Settings'
    ]);
  });

  it('appends the Super admin heading directly above its group, only for the platform administrator', () => {
    const navigation = ownerNavigation(true);
    expect(navigation.slice(-2)).toEqual([superAdminNavSection, superAdminNavGroup]);
    expect(headings(navigationFor(OPERATOR, false, 'offered'))).not.toContain('Super admin');
  });

  it('never leaves a heading directly above another heading or at the end of the menu', () => {
    for (const navigation of [
      ownerNavigation(),
      ownerNavigation(true),
      navigationFor(OPERATOR, false, 'offered'),
      navigationFor(capabilitySet(['Reports.View']), false, 'offered'),
      navigationFor(OWNER, false, 'offered', availability('NotConfigured'))
    ]) {
      navigation.forEach((entry, index) => {
        if (entry.kind === 'section') {
          expect(navigation[index + 1]?.kind ?? 'end').not.toMatch(/section|end/);
        }
      });
    }
  });
});
