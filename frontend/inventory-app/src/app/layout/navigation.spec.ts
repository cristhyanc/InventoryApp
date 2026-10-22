import { capabilitySet } from '../access/capabilities';
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
  activeNavGroup,
  activeNavLink,
  activeNavRoute,
  businessesNavLink,
  navLinks,
  navLockOf,
  navigationFor,
  platformDiagnosticsNavLink,
  primaryNavigation,
  providerSectionHeading,
  superAdminNavGroup
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
   * The order is itself a decision (issue #729, decision P6): everything that works without a sales
   * provider first, then one section holding the destinations that read live provider data.
   *
   * Machines left that section in issue #740 (child G1): a business now has machine records of its
   * own, which it creates, renames and deactivates with no provider at all, so the destination no
   * longer depends on one.
   */
  it('declares the target navigation in order, provider-independent entries first', () => {
    expect(primaryNavigation.map((entry) => `${entry.kind}:${entry.label}`)).toEqual([
      'link:Dashboard',
      'link:Pick List',
      'link:Machines',
      'group:Inventory',
      'group:Purchases',
      'link:Expenses',
      'group:Reports',
      'group:Admin',
      'section:Needs Nayax',
      'link:Sites',
      'link:Site Commissions'
    ]);
  });

  it('puts every live provider destination after the section heading, and nothing else there', () => {
    const headingAt = primaryNavigation.indexOf(providerSectionHeading);
    const locked = primaryNavigation.filter((entry) => entry.kind === 'link' && entry.lockedWithout !== undefined);

    expect(headingAt).toBeGreaterThan(0);
    expect(locked.map((entry) => entry.label)).toEqual(['Sites', 'Site Commissions']);
    for (const entry of locked) {
      expect(primaryNavigation.indexOf(entry)).toBeGreaterThan(headingAt);
    }
  });

  /**
   * Machines is in the top section and declares no live Nayax read at all (issue #740): the page's
   * local machine list, and its create/rename/deactivate actions, work with no provider.
   */
  it('keeps Machines in the top section, depending on no provider read', () => {
    const machines = item('Machines') as NavLink;
    const headingAt = primaryNavigation.indexOf(providerSectionHeading);

    expect(primaryNavigation.indexOf(machines)).toBeLessThan(headingAt);
    expect(machines.route).toBe('/machines');
    expect(machines.lockedWithout).toBeUndefined();
    expect(machines.hiddenWithoutProvider).toBeUndefined();
  });

  it('keeps Pick List, Machines, Sites, Site Commissions and Expenses as top-level direct links', () => {
    expect(primaryNavigation.filter((entry) => entry.kind === 'link').map((entry) => (entry as NavLink).route)).toEqual([
      '/',
      '/pick-list',
      '/machines',
      '/expenses',
      '/sites',
      '/reports/site-commissions'
    ]);
  });

  /**
   * The group is "Inventory" since issue #729 (decision P11), and the Products page inside it keeps
   * its own name, its URL and its breadcrumb - the rename is of the heading alone.
   */
  it('groups the Inventory pages, with Products keeping its own name and URL', () => {
    expect(childRoutes('Inventory')).toEqual([
      { label: 'Products', route: '/products' },
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
   * Every report left in this group reads stored data, so none of them is ever locked by provider
   * status (issue #729, decision P5). The site commission report left the group: a site is known
   * only from the live fleet read, so it is a live provider destination rather than a report over
   * sales already imported.
   */
  it('groups every report over stored data, and locks none of them', () => {
    expect(childRoutes('Reports')).toEqual([
      { label: 'Bookkeeping', route: '/reports/bookkeeping' },
      { label: 'Daily', route: '/reports/daily' },
      { label: 'Transactions', route: '/reports/transactions' },
      { label: 'Reconciliation', route: '/reports/reconciliation' },
      { label: 'Machines', route: '/reports/machines' },
      { label: 'Products', route: '/reports/products' },
      { label: 'GST', route: '/reports/gst' }
    ]);

    for (const report of navLinks([group('Reports')])) {
      expect(report.lockedWithout).toBeUndefined();
      expect(report.hiddenWithoutProvider).toBeUndefined();
    }
  });

  it('groups the dedicated Admin pages', () => {
    expect(childRoutes('Admin')).toEqual(adminChildRoutes);
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
    const labels = [
      ...primaryNavigation.map((entry) => entry.label),
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
 * (issue #547).
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

  it('adds it last, holding Platform Diagnostics and Businesses, once the API confirms access', () => {
    expect(group('Super Admin', ownerNavigation(true)).children).toEqual([platformDiagnosticsNavLink, businessesNavLink]);
  });

  it('changes nothing else: the same groups in the same order, with Super Admin appended last', () => {
    expect(ownerNavigation(true).map((entry) => `${entry.kind}:${entry.label}`)).toEqual([
      ...primaryNavigation.map((entry) => `${entry.kind}:${entry.label}`),
      'group:Super Admin'
    ]);
    expect(childRoutes('Admin', ownerNavigation(true))).toEqual(adminChildRoutes);
    expect(navLinks(ownerNavigation(true)).map((link) => link.route)).toEqual([
      ...navLinks(primaryNavigation).map((link) => link.route),
      platformDiagnosticsNavLink.route,
      businessesNavLink.route
    ]);
  });

  it('leaves primaryNavigation itself untouched, so one granted navigation cannot leak into another', () => {
    ownerNavigation(true);
    navigationFor(OPERATOR, true, 'offered');
    navigationFor(OWNER, false, 'offered', availability('NotConfigured'));

    expect(childRoutes('Admin')).toEqual(adminChildRoutes);
    expect(primaryNavigation.map((entry) => entry.label)).toEqual([
      'Dashboard',
      'Pick List',
      'Machines',
      'Inventory',
      'Purchases',
      'Expenses',
      'Reports',
      'Admin',
      'Needs Nayax',
      'Sites',
      'Site Commissions'
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

    expect(group('Super Admin', operator).children).toEqual([platformDiagnosticsNavLink, businessesNavLink]);
    expect(navLinks(navigationFor(OPERATOR, false, 'offered')).map((link) => link.route)).not.toContain(
      platformDiagnosticsNavLink.route
    );
  });

  it("declares Businesses with no capability, so no business role is what decides it (#547)", () => {
    expect(businessesNavLink).toEqual({ kind: 'link', label: 'Businesses', route: '/super-admin/businesses' });
    expect(superAdminNavGroup.children).toContain(businessesNavLink);
  });
});

/**
 * The navigation is built from the capabilities `GET /api/me/access` reported (issue #528). Each
 * destination declares the capability its own page needs, and the API checks the same capability
 * on every request behind that page - so a destination that is not rendered hides a page rather
 * than protecting one.
 */
describe('navigationFor capabilities (issue #528)', () => {
  function labels(items: readonly NavItem[]): string[] {
    return items.map((entry) => entry.label);
  }

  it('declares the capability its own page needs on every destination', () => {
    const undeclared = navLinks(primaryNavigation)
      .filter((entry) => entry.capability === undefined)
      .map((entry) => entry.route);

    expect(undeclared).toEqual([]);
  });

  it('offers an Operator no Expenses, no Reports group and no Admin group', () => {
    const operator = navigationFor(OPERATOR, false, 'offered');

    expect(labels(operator)).toEqual([
      'Dashboard',
      'Pick List',
      'Machines',
      'Inventory',
      'Purchases',
      'Needs Nayax',
      'Sites'
    ]);
    expect(navLinks(operator).map((entry) => entry.route)).toEqual([
      '/',
      '/pick-list',
      '/machines',
      '/products',
      '/take-inventory',
      '/categories',
      '/stock-history',
      '/purchases',
      '/purchases/orders',
      '/suppliers',
      '/sites'
    ]);
  });

  it('offers a Manager the reports and expenses, but no Integrations and no data repair', () => {
    const manager = navigationFor(MANAGER, false, 'offered');

    expect(labels(manager)).toEqual([
      'Dashboard',
      'Pick List',
      'Machines',
      'Inventory',
      'Purchases',
      'Expenses',
      'Reports',
      'Admin',
      'Needs Nayax',
      'Sites',
      'Site Commissions'
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
   * `SiteCommissions.Manage` alone leaves one live provider destination, so the section heading
   * stays with it - and nothing else does.
   */
  it('keeps the provider section heading for the one destination a capability set leaves in it', () => {
    const commissionsOnly = navigationFor(capabilitySet(['SiteCommissions.Manage']), false, 'offered');

    expect(labels(commissionsOnly)).toEqual(['Admin', 'Needs Nayax', 'Site Commissions']);
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
      businessesNavLink
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
    expect(navigationFor(undefined, true, 'withheld')).toEqual([superAdminNavGroup]);
    expect(navLinks(navigationFor(OWNER, true, 'withheld'))).toEqual([
      platformDiagnosticsNavLink,
      businessesNavLink
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

  /** The site commission report is a top-level provider destination now, not a Reports child. */
  it('is undefined for the site commission report, which left the Reports group (issue #729)', () => {
    expect(activeNavGroup(primaryNavigation, '/reports/site-commissions')).toBeUndefined();
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
  function labels(items: readonly NavItem[]): string[] {
    return items.map((entry) => entry.label);
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

  it('locks every live provider destination for a business with no connection at all', () => {
    const noProvider = availability('NotConfigured');

    expect(lockOf('/sites', noProvider)).toBe('connect');
    expect(lockOf('/reports/site-commissions', noProvider)).toBe('connect');
  });

  /**
   * The machine list and the machine profitability report are both never locked (issue #740): the
   * list has the business's own machine records to show and its own create/rename/deactivate
   * actions, and the report reads stored sales.
   */
  it('leaves the machine list and the machine profitability report open with no provider at all', () => {
    expect(lockOf('/machines', availability('NotConfigured'))).toBe('open');
    expect(lockOf('/reports/machines', availability('NotConfigured'))).toBe('open');
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

  it('never locks a destination that declares no live provider read', () => {
    for (const entry of navLinks(ownerNavigation()).filter((candidate) => candidate.lockedWithout === undefined)) {
      expect(navLockOf(entry, availability('NotConfigured'))).toBe('open');
    }
  });

  /**
   * **Historical reports stay accessible** (decision P5): with no provider at all they are still in
   * the menu, in the Reports group, and not locked - the availability states inside each report say
   * what their figures are based on (issues #730 and #731).
   */
  it('keeps every report over stored data in the Reports group, unlocked, with no provider', () => {
    const noProvider = navigationFor(OWNER, false, 'offered', availability('NotConfigured'));

    expect(childRoutes('Reports', noProvider).map((child) => child.route)).toEqual([
      '/reports/bookkeeping',
      '/reports/daily',
      '/reports/transactions',
      '/reports/reconciliation',
      '/reports/machines',
      '/reports/products',
      '/reports/gst'
    ]);

    for (const report of navLinks([group('Reports', noProvider)])) {
      expect(navLockOf(report, availability('NotConfigured'))).toBe('open');
    }
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
