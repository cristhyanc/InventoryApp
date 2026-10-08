/**
 * Breadcrumb route metadata (issue #457).
 *
 * This is the one place that decides a breadcrumb trail's labels and parent destinations. It is
 * deliberately separate from `layout/navigation.ts`: the sidebar's groups (`Products`, `Purchases`,
 * `Reports`, `Admin`) are headings that toggle their children and are not themselves a page, while a
 * breadcrumb parent must be the real page that owns the child route — `/products`, `/purchases`,
 * `/reports`, `/admin` — so the two metadata sets name the same areas but are not interchangeable.
 *
 * Only a route with a meaningful parent appears here. A route with no entry (every top-level list
 * page, and the Dashboard) renders no breadcrumb at all: a one-item trail adds no hierarchy.
 */

/** A breadcrumb's parent entry. Omitting `path` renders a non-routable group as plain text. */
export interface BreadcrumbParent {
  readonly label: string;
  readonly path?: string;
}

export interface BreadcrumbRouteEntry {
  /** An `app.routes.ts` path pattern, `:id`-style segments included, for example `/machines/:id`. */
  readonly pattern: string;
  /** The current-page label, used as-is unless `dynamic` is true and a live label is available. */
  readonly label: string;
  readonly parent?: BreadcrumbParent;
  /**
   * True when the routed page may replace `label` with already-loaded page data through
   * `BreadcrumbService.setCurrentPageLabel`. `label` is still what renders until that data arrives.
   */
  readonly dynamic?: boolean;
}

export interface BreadcrumbItem {
  readonly label: string;
  /** Present only for a link; the current page and a non-routable group never carry one. */
  readonly path?: string;
  readonly current: boolean;
}

const PRODUCTS: BreadcrumbParent = { label: 'Products', path: '/products' };
const MACHINES: BreadcrumbParent = { label: 'Machines', path: '/machines' };
const SITES: BreadcrumbParent = { label: 'Sites', path: '/sites' };
const PURCHASES: BreadcrumbParent = { label: 'Purchases', path: '/purchases' };
const REPORTS: BreadcrumbParent = { label: 'Reports', path: '/reports' };
const ADMIN: BreadcrumbParent = { label: 'Admin', path: '/admin' };

/**
 * Every routed page with a meaningful parent. Covers the nested Products, Machines, Sites,
 * Purchases, Reports and Admin routes in `app.routes.ts` (issue #457's route inventory).
 *
 * `/products/:id/stock` and `/stock-history` are deliberately absent: both load the global Stock
 * History page (see `app.routes.ts`'s comment on that route), which is reached from several
 * unrelated areas, so no single parent would describe it. `/purchases/orders` and
 * `/products/needs-ordering` are absent too: the sidebar (`layout/navigation.ts`) treats both as
 * sibling views, not a drill-down, and the Products tab is already its own in-page navigation.
 */
export const breadcrumbRoutes: readonly BreadcrumbRouteEntry[] = [
  { pattern: '/products/:id/edit', label: 'Edit product', parent: PRODUCTS },
  { pattern: '/machines/:id', label: 'Machine details', parent: MACHINES, dynamic: true },
  { pattern: '/sites/:id/products', label: 'Site products', parent: SITES },
  { pattern: '/purchases/new', label: 'Add purchase', parent: PURCHASES },
  { pattern: '/purchases/:id/edit', label: 'Edit purchase', parent: PURCHASES },
  { pattern: '/reports/bookkeeping', label: 'Bookkeeping', parent: REPORTS },
  { pattern: '/reports/daily', label: 'Daily', parent: REPORTS },
  { pattern: '/reports/transactions', label: 'Transactions', parent: REPORTS },
  { pattern: '/reports/reconciliation', label: 'Reconciliation', parent: REPORTS },
  { pattern: '/reports/machines', label: 'Machines', parent: REPORTS },
  { pattern: '/reports/products', label: 'Products', parent: REPORTS },
  { pattern: '/reports/gst', label: 'GST', parent: REPORTS },
  { pattern: '/reports/site-commissions', label: 'Site Commissions', parent: REPORTS },
  { pattern: '/admin/imports', label: 'Imports', parent: ADMIN },
  { pattern: '/admin/historical-cost-recovery', label: 'Historical Cost Recovery', parent: ADMIN },
  { pattern: '/admin/avco-transition', label: 'AVCO Transition', parent: ADMIN },
  { pattern: '/admin/costing-repair', label: 'Costing Repair', parent: ADMIN },
  { pattern: '/admin/historical-gst-classification', label: 'Historical GST Classification', parent: ADMIN },
  { pattern: '/admin/diagnostics', label: 'Platform Diagnostics', parent: ADMIN },
  { pattern: '/admin/nayax-settings', label: 'Nayax Settings', parent: ADMIN },
  { pattern: '/admin/site-commission-agreements', label: 'Site Commission Agreements', parent: ADMIN }
];

function normalizePath(url: string): string {
  const path = url.split('?')[0].split('#')[0];
  if (path.length > 1 && path.endsWith('/')) {
    return path.slice(0, -1);
  }
  return path === '' ? '/' : path;
}

function matchesPattern(pattern: string, path: string): boolean {
  const patternSegments = pattern.split('/').filter(Boolean);
  const pathSegments = path.split('/').filter(Boolean);
  if (patternSegments.length !== pathSegments.length) {
    return false;
  }

  return patternSegments.every(
    (segment, index) => segment.startsWith(':') || segment === pathSegments[index]
  );
}

function findEntry(entries: readonly BreadcrumbRouteEntry[], path: string): BreadcrumbRouteEntry | undefined {
  return entries.find((entry) => matchesPattern(entry.pattern, path));
}

/**
 * Builds the breadcrumb trail for `url` against `entries`, resolving the current page's label to
 * `dynamicLabel` only when the matched entry is `dynamic` and a label is available.
 *
 * Returns an empty trail for a URL with no entry, and for an entry with no `parent` — a one-item
 * trail is exactly the "standalone top-level page" case issue #457 asks to skip.
 */
export function buildTrailFrom(
  entries: readonly BreadcrumbRouteEntry[],
  url: string,
  dynamicLabel: string | null
): readonly BreadcrumbItem[] {
  const entry = findEntry(entries, normalizePath(url));
  if (!entry?.parent) {
    return [];
  }

  const currentLabel = entry.dynamic && dynamicLabel ? dynamicLabel : entry.label;

  return [
    { label: entry.parent.label, path: entry.parent.path, current: false },
    { label: currentLabel, current: true }
  ];
}

export function buildBreadcrumbTrail(url: string, dynamicLabel: string | null): readonly BreadcrumbItem[] {
  return buildTrailFrom(breadcrumbRoutes, url, dynamicLabel);
}
