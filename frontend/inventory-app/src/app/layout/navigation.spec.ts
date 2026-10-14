import { capabilitySet } from '../access/capabilities';
import { routes } from '../app.routes';
import {
  NavGroup,
  NavItem,
  NavLink,
  activeNavGroup,
  activeNavRoute,
  businessesNavLink,
  navLinks,
  navigationFor,
  platformDiagnosticsNavLink,
  primaryNavigation,
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
  return navigationFor(OWNER, hasPlatformDiagnosticsAccess);
}

function item(label: string, items: readonly NavItem[] = primaryNavigation): NavLink | NavGroup {
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
  { label: 'Nayax Settings', route: '/admin/nayax-settings' },
  { label: 'Site Commission Agreements', route: '/admin/site-commission-agreements' },
  { label: 'Imports', route: '/admin/imports' },
  { label: 'Historical Cost Recovery', route: '/admin/historical-cost-recovery' },
  { label: 'AVCO Transition', route: '/admin/avco-transition' },
  { label: 'Costing Repair', route: '/admin/costing-repair' },
  { label: 'Historical GST Classification', route: '/admin/historical-gst-classification' },
  { label: 'Nayax Sale Timestamp Repair', route: '/admin/nayax-sale-timestamp-repair' },
  { label: 'Users', route: '/admin/users' },
  { label: 'Roles', route: '/admin/roles' }
];

describe('primary navigation (issue #391)', () => {
  it('declares the target navigation in order', () => {
    expect(primaryNavigation.map((entry) => `${entry.kind}:${entry.label}`)).toEqual([
      'link:Dashboard',
      'link:Pick List',
      'group:Products',
      'link:Machines',
      'link:Sites',
      'group:Purchases',
      'link:Expenses',
      'group:Reports',
      'group:Admin'
    ]);
  });

  it('keeps Pick List, Machines, Sites and Expenses as top-level direct links', () => {
    expect(primaryNavigation.filter((entry) => entry.kind === 'link').map((entry) => (entry as NavLink).route)).toEqual([
      '/',
      '/pick-list',
      '/machines',
      '/sites',
      '/expenses'
    ]);
  });

  it('groups the Products pages', () => {
    expect(childRoutes('Products')).toEqual([
      { label: 'Products', route: '/products' },
      { label: 'Take Inventory', route: '/take-inventory' },
      { label: 'Categories', route: '/categories' },
      { label: 'Stock History', route: '/stock-history' }
    ]);
  });

  it('groups the Purchases pages', () => {
    expect(childRoutes('Purchases')).toEqual([
      { label: 'Purchases', route: '/purchases' },
      { label: 'Supplier Orders', route: '/purchases/orders' },
      { label: 'Suppliers', route: '/suppliers' }
    ]);
  });

  it('groups every report page', () => {
    expect(childRoutes('Reports')).toEqual([
      { label: 'Dashboard', route: '/reports' },
      { label: 'Bookkeeping', route: '/reports/bookkeeping' },
      { label: 'Daily', route: '/reports/daily' },
      { label: 'Transactions', route: '/reports/transactions' },
      { label: 'Reconciliation', route: '/reports/reconciliation' },
      { label: 'Machines', route: '/reports/machines' },
      { label: 'Products', route: '/reports/products' },
      { label: 'GST', route: '/reports/gst' },
      { label: 'Site Commissions', route: '/reports/site-commissions' }
    ]);
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
    navigationFor(OPERATOR, true);

    expect(childRoutes('Admin')).toEqual(adminChildRoutes);
    expect(primaryNavigation.map((entry) => entry.label)).toEqual([
      'Dashboard',
      'Pick List',
      'Products',
      'Machines',
      'Sites',
      'Purchases',
      'Expenses',
      'Reports',
      'Admin'
    ]);
  });

  it('points only at routes declared in app.routes.ts, with or without access', () => {
    expect(undeclaredRoutes(ownerNavigation())).toEqual([]);
    expect(undeclaredRoutes(ownerNavigation(true))).toEqual([]);
    expect(undeclaredRoutes(navigationFor(OPERATOR, false))).toEqual([]);
    expect(undeclaredRoutes(navigationFor(MANAGER, false))).toEqual([]);
    expect(undeclaredRoutes(navigationFor(undefined, true))).toEqual([]);
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
    const operator = navigationFor(OPERATOR, true);

    expect(group('Super Admin', operator).children).toEqual([platformDiagnosticsNavLink, businessesNavLink]);
    expect(navLinks(navigationFor(OPERATOR, false)).map((link) => link.route)).not.toContain(
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
    const operator = navigationFor(OPERATOR, false);

    expect(labels(operator)).toEqual(['Dashboard', 'Pick List', 'Products', 'Machines', 'Sites', 'Purchases']);
    expect(navLinks(operator).map((entry) => entry.route)).toEqual([
      '/',
      '/pick-list',
      '/products',
      '/take-inventory',
      '/categories',
      '/stock-history',
      '/machines',
      '/sites',
      '/purchases',
      '/purchases/orders',
      '/suppliers'
    ]);
  });

  it('offers a Manager the reports and expenses, but no Nayax Settings and no data repair', () => {
    const manager = navigationFor(MANAGER, false);

    expect(labels(manager)).toEqual([
      'Dashboard',
      'Pick List',
      'Products',
      'Machines',
      'Sites',
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
    expect(childRoutes('Admin', navigationFor(MANAGER, false))).not.toContainEqual(
      expect.objectContaining({ route: '/admin/roles' })
    );
    expect(navLinks(navigationFor(OPERATOR, false)).map((link) => link.route)).not.toContain('/admin/roles');
  });

  /**
   * No current role is missing `Reports.View` while holding `SiteCommissions.Manage`, but the
   * group must drop only the children the capability set actually withholds, not the whole group.
   */
  it('keeps a group whose children are only partly allowed, with only those children', () => {
    const reportsOnly = capabilitySet(['SiteCommissions.Manage']);

    expect(childRoutes('Reports', navigationFor(reportsOnly, false))).toEqual([
      { label: 'Site Commissions', route: '/reports/site-commissions' }
    ]);
  });

  it('offers nothing at all for an empty capability set, which is what an unread one renders as', () => {
    expect(navigationFor(capabilitySet([]), false)).toEqual([]);
    expect(navLinks(navigationFor(capabilitySet([]), false))).toEqual([]);
  });

  /**
   * A capability set that could not be read offers everything: the API refuses what the role may
   * not do regardless, and an empty menu would strand a member whose capability read failed.
   */
  it('offers every destination when the capability set is not known', () => {
    expect(navigationFor(undefined, false)).toEqual(primaryNavigation);
    expect(childRoutes('Admin', navigationFor(undefined, true))).toEqual(adminChildRoutes);
    expect(group('Super Admin', navigationFor(undefined, true)).children).toEqual([
      platformDiagnosticsNavLink,
      businessesNavLink
    ]);
  });

  it('resolves the active entry only among the destinations the member was offered', () => {
    const operator = navigationFor(OPERATOR, false);

    expect(activeNavRoute(operator, '/products/12/edit')).toBe('/products');
    expect(activeNavRoute(operator, '/reports/gst')).toBeUndefined();
    expect(activeNavGroup(operator, '/admin/costing-repair')).toBeUndefined();
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
    expect(activeNavRoute(primaryNavigation, '/reports')).toBe('/reports');
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
    expect(activeNavGroup(primaryNavigation, '/products/12/edit')?.label).toBe('Products');
    expect(activeNavGroup(primaryNavigation, '/purchases/orders')?.label).toBe('Purchases');
  });

  it('is undefined for a top-level link and for an unknown URL', () => {
    expect(activeNavGroup(primaryNavigation, '/machines')).toBeUndefined();
    expect(activeNavGroup(primaryNavigation, '/')).toBeUndefined();
    expect(activeNavGroup(primaryNavigation, '/auth')).toBeUndefined();
  });
});
