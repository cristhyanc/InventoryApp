/**
 * The application shell's primary navigation (issue #391, parent #383).
 *
 * This file is navigation data plus the pure route-matching rules the sidebar renders. It owns no
 * business meaning: every `route` must be a real page declared in `app.routes.ts` (the
 * co-located spec asserts that), and nothing here may introduce a destination ahead of the page
 * that serves it.
 */

/**
 * A navigable destination. `icon` is decorative and only the top-level rail needs one; it names a
 * glyph the sidebar renders through `app-icon` in its outlined variant, so the name must have an
 * entry in `OUTLINED_ICON_SHAPES` (issues #411 and #456) and not only in the Rounded `ICON_PATHS`.
 */
export interface NavLink {
  readonly kind: 'link';
  readonly label: string;
  readonly route: string;
  readonly icon?: string;
}

/** An expandable heading. It toggles its children and is deliberately not itself a destination. */
export interface NavGroup {
  readonly kind: 'group';
  readonly label: string;
  readonly icon: string;
  readonly children: readonly NavLink[];
}

export type NavItem = NavLink | NavGroup;

function link(label: string, route: string): NavLink {
  return { kind: 'link', label, route };
}

export const primaryNavigation: readonly NavItem[] = [
  { kind: 'link', label: 'Dashboard', route: '/', icon: 'home' },
  { kind: 'link', label: 'Pick List', route: '/pick-list', icon: 'assignment' },
  {
    kind: 'group',
    label: 'Products',
    icon: 'inventory_2',
    children: [
      link('Products', '/products'),
      link('Take Inventory', '/take-inventory'),
      link('Categories', '/categories'),
      link('Stock History', '/stock-history')
    ]
  },
  { kind: 'link', label: 'Machines', route: '/machines', icon: 'refresh' },
  { kind: 'link', label: 'Sites', route: '/sites', icon: 'location_on' },
  {
    kind: 'group',
    label: 'Purchases',
    icon: 'download',
    children: [link('Purchases', '/purchases'), link('Supplier Orders', '/purchases/orders'), link('Suppliers', '/suppliers')]
  },
  { kind: 'link', label: 'Expenses', route: '/expenses', icon: 'attach_money' },
  {
    kind: 'group',
    label: 'Reports',
    icon: 'bar_chart',
    children: [
      link('Dashboard', '/reports'),
      link('Bookkeeping', '/reports/bookkeeping'),
      link('Daily', '/reports/daily'),
      link('Transactions', '/reports/transactions'),
      link('Reconciliation', '/reports/reconciliation'),
      link('Machines', '/reports/machines'),
      link('Products', '/reports/products'),
      link('GST', '/reports/gst'),
      link('Site Commissions', '/reports/site-commissions')
    ]
  },
  {
    kind: 'group',
    label: 'Admin',
    icon: 'settings',
    children: [
      link('Nayax Settings', '/admin/nayax-settings'),
      link('Site Commission Agreements', '/admin/site-commission-agreements'),
      link('Imports', '/admin/imports'),
      link('Historical Cost Recovery', '/admin/historical-cost-recovery'),
      link('AVCO Transition', '/admin/avco-transition'),
      link('Costing Repair', '/admin/costing-repair'),
      link('Historical GST Classification', '/admin/historical-gst-classification')
    ]
  }
];

/**
 * The super-admin platform diagnostics page (issue #335).
 *
 * It is deliberately not part of `primaryNavigation`, because it is not a destination every
 * signed-in operator has: it is composed into the `Admin` group by `navigationFor` only while
 * `GET /api/admin/diagnostics/access` confirms the signed-in actor is the configured platform
 * administrator. The link is presentation, never a boundary - `/admin/diagnostics` entered
 * directly still resolves, and both diagnostics endpoints authorize every request themselves, so
 * a hidden link hides a page rather than protecting one.
 */
export const platformDiagnosticsNavLink: NavLink = link('Platform Diagnostics', '/admin/diagnostics');

/**
 * The navigation to render for an actor the diagnostics API has, or has not, confirmed as the
 * platform administrator. It adds exactly one destination to the end of the `Admin` group and
 * changes nothing else, and it copies rather than mutates, so `primaryNavigation` stays the
 * navigation everyone else sees.
 */
export function navigationFor(hasPlatformDiagnosticsAccess: boolean): readonly NavItem[] {
  if (!hasPlatformDiagnosticsAccess) {
    return primaryNavigation;
  }

  return primaryNavigation.map((item) =>
    item.kind === 'group' && item.label === 'Admin'
      ? { ...item, children: [...item.children, platformDiagnosticsNavLink] }
      : item
  );
}

/** Every destination in the navigation, top-level links first, then each group's children. */
export function navLinks(items: readonly NavItem[]): readonly NavLink[] {
  return items.flatMap((item) => (item.kind === 'group' ? item.children : [item]));
}

function normalizePath(url: string): string {
  const path = url.split('?')[0].split('#')[0];
  if (path.length > 1 && path.endsWith('/')) {
    return path.slice(0, -1);
  }
  return path === '' ? '/' : path;
}

function matchesRoute(path: string, route: string): boolean {
  // The dashboard is the only route that would otherwise prefix-match every URL.
  if (route === '/') {
    return path === '/';
  }
  return path === route || path.startsWith(`${route}/`);
}

/**
 * The route of the navigation entry that owns `url`, or `undefined` for a URL outside the
 * navigation (`/auth`, for example).
 *
 * The most specific match wins, which is what makes nested destinations readable: `/purchases`
 * highlights `Purchases` while `/purchases/orders` highlights `Supplier Orders`, and a detail URL
 * such as `/machines/7` or `/products/12/edit` stays on its list page.
 */
export function activeNavRoute(items: readonly NavItem[], url: string): string | undefined {
  const path = normalizePath(url);
  let active: string | undefined;

  for (const candidate of navLinks(items)) {
    if (matchesRoute(path, candidate.route) && (active === undefined || candidate.route.length > active.length)) {
      active = candidate.route;
    }
  }

  return active;
}

/** The group that owns the active page, so the sidebar can show and open it. */
export function activeNavGroup(items: readonly NavItem[], url: string): NavGroup | undefined {
  const active = activeNavRoute(items, url);
  if (active === undefined) {
    return undefined;
  }

  return items.find(
    (item): item is NavGroup => item.kind === 'group' && item.children.some((child) => child.route === active)
  );
}
