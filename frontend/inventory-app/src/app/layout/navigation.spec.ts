import { routes } from '../app.routes';
import {
  NavGroup,
  NavItem,
  NavLink,
  activeNavGroup,
  activeNavRoute,
  navLinks,
  navigationFor,
  platformDiagnosticsNavLink,
  primaryNavigation
} from './navigation';

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
  return group(label, items).children.map((child) => ({ label: child.label, route: child.route }));
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
  { label: 'Nayax Sale Timestamp Repair', route: '/admin/nayax-sale-timestamp-repair' }
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
   * The navigation may only point at pages that exist: no placeholder Users/Roles/Audit/Settings/
   * Profile destination, and no route invented ahead of the page that would serve it.
   */
  it('points only at routes declared in app.routes.ts', () => {
    expect(undeclaredRoutes(primaryNavigation)).toEqual([]);
  });

  it('adds no Settings, profile or role destination', () => {
    const labels = [
      ...primaryNavigation.map((entry) => entry.label),
      ...navLinks(primaryNavigation).map((link) => link.label)
    ];

    expect(labels).not.toContain('Settings');
    expect(labels).not.toContain('Profile');
    expect(labels).not.toContain('Users');
    expect(labels).not.toContain('Roles');
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
 * The conditional super-admin diagnostics link (issue #335).
 *
 * The link is presentation only. `GET /api/admin/diagnostics/access` decides whether it is
 * composed in, and the API is independently authorized on every request, so a navigation without
 * the link hides a page rather than protecting one: `/admin/diagnostics` entered directly still
 * works, and still shows nothing an unauthorized caller could read.
 */
describe('navigationFor (issue #335)', () => {
  it('offers no diagnostics destination until the API confirms platform-admin access', () => {
    expect(navLinks(navigationFor(false)).map((link) => link.route)).not.toContain(platformDiagnosticsNavLink.route);
    expect(childRoutes('Admin', navigationFor(false))).toEqual(adminChildRoutes);
  });

  it('adds it to the Admin group, last, once the API confirms access', () => {
    expect(childRoutes('Admin', navigationFor(true))).toEqual([
      ...adminChildRoutes,
      { label: 'Platform Diagnostics', route: '/admin/diagnostics' }
    ]);
  });

  it('changes nothing else: the same groups, the same order, and no other new destination', () => {
    expect(navigationFor(true).map((entry) => `${entry.kind}:${entry.label}`)).toEqual(
      primaryNavigation.map((entry) => `${entry.kind}:${entry.label}`)
    );
    expect(navLinks(navigationFor(true)).map((link) => link.route)).toEqual([
      ...navLinks(primaryNavigation).map((link) => link.route),
      platformDiagnosticsNavLink.route
    ]);
  });

  it('leaves primaryNavigation itself untouched, so one granted navigation cannot leak into another', () => {
    navigationFor(true);

    expect(childRoutes('Admin')).toEqual(adminChildRoutes);
  });

  it('points only at routes declared in app.routes.ts, with or without access', () => {
    expect(undeclaredRoutes(navigationFor(false))).toEqual([]);
    expect(undeclaredRoutes(navigationFor(true))).toEqual([]);
  });

  it('resolves the diagnostics page as the active entry inside the Admin group', () => {
    const granted = navigationFor(true);

    expect(activeNavRoute(granted, '/admin/diagnostics')).toBe('/admin/diagnostics');
    expect(activeNavGroup(granted, '/admin/diagnostics')?.label).toBe('Admin');
    expect(activeNavRoute(navigationFor(false), '/admin/diagnostics')).toBeUndefined();
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
