import { routes } from '../../app.routes';
import { BreadcrumbRouteEntry, breadcrumbRoutes, buildBreadcrumbTrail, buildTrailFrom } from './breadcrumb-routes';

/** Every path `app.routes.ts` declares, flattened (including `products`'s children). */
function declaredRoutePaths(): Set<string> {
  const paths = new Set<string>();
  for (const route of routes) {
    if (route.path !== undefined) {
      paths.add(route.path);
    }
    for (const child of route.children ?? []) {
      if (route.path !== undefined && child.path !== undefined) {
        paths.add(child.path === '' ? route.path : `${route.path}/${child.path}`);
      }
    }
  }
  return paths;
}

/** Strips `:id`-style segments so a pattern can be checked against the declared route paths. */
function toRoutePath(pattern: string): string {
  return pattern.replace(/^\//, '');
}

describe('breadcrumbRoutes (issue #457 route inventory)', () => {
  it('maps only routes declared in app.routes.ts', () => {
    const declared = declaredRoutePaths();
    const undeclared = breadcrumbRoutes.map((entry) => toRoutePath(entry.pattern)).filter((path) => !declared.has(path));

    expect(undeclared).toEqual([]);
  });

  it("maps every parent's path to a route declared in app.routes.ts", () => {
    const declared = declaredRoutePaths();
    const undeclaredParents = breadcrumbRoutes
      .filter((entry) => entry.parent?.path)
      .map((entry) => entry.parent!.path!.replace(/^\//, ''))
      .filter((path) => !declared.has(path));

    expect(undeclaredParents).toEqual([]);
  });

  it('covers the nested Products, Machines, Sites, Purchases, Reports and Admin routes', () => {
    const patterns = breadcrumbRoutes.map((entry) => entry.pattern);

    expect(patterns).toEqual([
      '/products/:id/edit',
      '/machines/:id',
      '/sites/:id/products',
      '/purchases/new',
      '/purchases/:id/edit',
      '/reports/bookkeeping',
      '/reports/daily',
      '/reports/transactions',
      '/reports/reconciliation',
      '/reports/machines',
      '/reports/products',
      '/reports/gst',
      '/reports/site-commissions',
      '/admin/imports',
      '/admin/historical-cost-recovery',
      '/admin/avco-transition',
      '/admin/costing-repair',
      '/admin/historical-gst-classification',
      '/admin/nayax-sale-timestamp-repair',
      '/admin/diagnostics',
      '/admin/nayax-settings',
      '/admin/site-commission-agreements'
    ]);
  });
});

describe('buildBreadcrumbTrail', () => {
  it('builds Products > Edit product for a routed product edit page', () => {
    expect(buildBreadcrumbTrail('/products/42/edit', null)).toEqual([
      { label: 'Products', path: '/products', current: false },
      { label: 'Edit product', current: true }
    ]);
  });

  it('builds Purchases > Edit purchase for the dedicated purchase edit page (issue #475)', () => {
    expect(buildBreadcrumbTrail('/purchases/42/edit', null)).toEqual([
      { label: 'Purchases', path: '/purchases', current: false },
      { label: 'Edit purchase', current: true }
    ]);
  });

  it('builds Admin > Historical GST Classification', () => {
    expect(buildBreadcrumbTrail('/admin/historical-gst-classification', null)).toEqual([
      { label: 'Admin', path: '/admin', current: false },
      { label: 'Historical GST Classification', current: true }
    ]);
  });

  it('builds Reports > Reconciliation', () => {
    expect(buildBreadcrumbTrail('/reports/reconciliation', null)).toEqual([
      { label: 'Reports', path: '/reports', current: false },
      { label: 'Reconciliation', current: true }
    ]);
  });

  it('falls back to the generic label for a dynamic route while no live label is available', () => {
    expect(buildBreadcrumbTrail('/machines/7', null)).toEqual([
      { label: 'Machines', path: '/machines', current: false },
      { label: 'Machine details', current: true }
    ]);
  });

  it('uses the live label for a dynamic route once one is available', () => {
    expect(buildBreadcrumbTrail('/machines/7', 'Snack Attack 42')).toEqual([
      { label: 'Machines', path: '/machines', current: false },
      { label: 'Snack Attack 42', current: true }
    ]);
  });

  it('ignores a live label on a non-dynamic route, never leaking it onto the wrong page', () => {
    expect(buildBreadcrumbTrail('/products/42/edit', 'Snack Attack 42')).toEqual([
      { label: 'Products', path: '/products', current: false },
      { label: 'Edit product', current: true }
    ]);
  });

  it('matches a parameterized route regardless of the id value', () => {
    expect(buildBreadcrumbTrail('/machines/123456', null)[1].label).toBe('Machine details');
  });

  it('ignores query parameters and a trailing slash', () => {
    expect(buildBreadcrumbTrail('/reports/gst?year=2026', null)).toEqual(
      buildBreadcrumbTrail('/reports/gst', null)
    );
    expect(buildBreadcrumbTrail('/reports/gst/', null)).toEqual(buildBreadcrumbTrail('/reports/gst', null));
  });

  it('renders no trail for a one-item page, so a standalone top-level page gets no breadcrumb', () => {
    expect(buildBreadcrumbTrail('/', null)).toEqual([]);
    expect(buildBreadcrumbTrail('/machines', null)).toEqual([]);
    expect(buildBreadcrumbTrail('/reports', null)).toEqual([]);
    expect(buildBreadcrumbTrail('/admin', null)).toEqual([]);
  });

  it('renders no trail for a URL with no mapping, including the cross-cutting Stock History entry points', () => {
    expect(buildBreadcrumbTrail('/stock-history', null)).toEqual([]);
    expect(buildBreadcrumbTrail('/products/42/stock', null)).toEqual([]);
    expect(buildBreadcrumbTrail('/purchases/orders', null)).toEqual([]);
    expect(buildBreadcrumbTrail('/does-not-exist', null)).toEqual([]);
  });
});

describe('buildTrailFrom (non-routable group)', () => {
  const entries: readonly BreadcrumbRouteEntry[] = [
    { pattern: '/widgets/:id', label: 'Widget details', parent: { label: 'Widgets' } }
  ];

  it('renders a parent with no path as a non-routable group: a label, never a link', () => {
    expect(buildTrailFrom(entries, '/widgets/9', null)).toEqual([
      { label: 'Widgets', path: undefined, current: false },
      { label: 'Widget details', current: true }
    ]);
  });
});
