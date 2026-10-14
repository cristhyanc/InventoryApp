import { Capability, CapabilitySet } from '../access/capabilities';

/**
 * The application shell's primary navigation (issue #391, parent #383).
 *
 * This file is navigation data plus the pure route-matching rules the sidebar renders. It owns no
 * business meaning: every `route` must be a real page declared in `app.routes.ts` (the
 * co-located spec asserts that), and nothing here may introduce a destination ahead of the page
 * that serves it.
 *
 * Since issue #528 each destination also declares the capability its page needs, and
 * `navigationFor` renders only the destinations the signed-in member's capability set allows. The
 * capability is presentation data like the label and the icon: the endpoints behind each page
 * check the same capability themselves (see docs/architecture.md § Capability authorization), so a
 * destination that is not rendered hides a page rather than protecting one.
 */

/**
 * A navigable destination. `icon` is decorative and only the top-level rail needs one; it names a
 * glyph the sidebar renders through `app-icon` in its outlined variant, so the name must have an
 * entry in `OUTLINED_ICON_SHAPES` (issues #411 and #456) and not only in the Rounded `ICON_PATHS`.
 *
 * `capability` is the capability the destination's own page needs, as `GET /api/me/access` names
 * it. A destination without one is offered to every member, which today is only the conditional
 * `Platform Diagnostics` link, whose own API decides it instead (issue #335).
 */
export interface NavLink {
  readonly kind: 'link';
  readonly label: string;
  readonly route: string;
  readonly icon?: string;
  readonly capability?: Capability;
}

/**
 * An inert group child with no page of its own yet. It renders as plain text rather than a link,
 * because this file's one rule - every `route` must be a real page already declared in
 * `app.routes.ts` - leaves no route to give it before that page exists. Issue #546 introduced this
 * for the Businesses destination, and issue #547 built that page, so nothing uses it today; the
 * type is kept for the next Super Admin destination that lands before its own page does.
 */
export interface NavPlaceholder {
  readonly kind: 'placeholder';
  readonly label: string;
}

/** Anything a group may hold: a real destination or an inert placeholder. */
export type NavGroupChild = NavLink | NavPlaceholder;

/** An expandable heading. It toggles its children and is deliberately not itself a destination. */
export interface NavGroup {
  readonly kind: 'group';
  readonly label: string;
  readonly icon: string;
  readonly children: readonly NavGroupChild[];
}

export type NavItem = NavLink | NavGroup;

function link(label: string, route: string, capability: Capability): NavLink {
  return { kind: 'link', label, route, capability };
}

export const primaryNavigation: readonly NavItem[] = [
  { kind: 'link', label: 'Dashboard', route: '/', icon: 'home', capability: 'Dashboard.View' },
  { kind: 'link', label: 'Pick List', route: '/pick-list', icon: 'assignment', capability: 'PickList.View' },
  {
    kind: 'group',
    label: 'Products',
    icon: 'inventory_2',
    children: [
      link('Products', '/products', 'Inventory.Operate'),
      link('Take Inventory', '/take-inventory', 'Inventory.Operate'),
      link('Categories', '/categories', 'Inventory.Operate'),
      link('Stock History', '/stock-history', 'Inventory.Operate')
    ]
  },
  { kind: 'link', label: 'Machines', route: '/machines', icon: 'refresh', capability: 'Inventory.Operate' },
  { kind: 'link', label: 'Sites', route: '/sites', icon: 'location_on', capability: 'Inventory.Operate' },
  {
    kind: 'group',
    label: 'Purchases',
    icon: 'download',
    children: [
      link('Purchases', '/purchases', 'Purchasing.Operate'),
      link('Supplier Orders', '/purchases/orders', 'Purchasing.Operate'),
      link('Suppliers', '/suppliers', 'Purchasing.Operate')
    ]
  },
  { kind: 'link', label: 'Expenses', route: '/expenses', icon: 'attach_money', capability: 'Expenses.Manage' },
  {
    kind: 'group',
    label: 'Reports',
    icon: 'bar_chart',
    children: [
      link('Dashboard', '/reports', 'Reports.View'),
      link('Bookkeeping', '/reports/bookkeeping', 'Reports.View'),
      link('Daily', '/reports/daily', 'Reports.View'),
      link('Transactions', '/reports/transactions', 'Reports.View'),
      link('Reconciliation', '/reports/reconciliation', 'Reports.View'),
      link('Machines', '/reports/machines', 'Reports.View'),
      link('Products', '/reports/products', 'Reports.View'),
      link('GST', '/reports/gst', 'Reports.View'),
      // The site commission report is the surface the agreements are maintained from, so the API
      // puts it on SiteCommissions.Manage rather than Reports.View (issue #526).
      link('Site Commissions', '/reports/site-commissions', 'SiteCommissions.Manage')
    ]
  },
  {
    kind: 'group',
    label: 'Admin',
    icon: 'settings',
    children: [
      link('Nayax Settings', '/admin/nayax-settings', 'Integration.Manage'),
      link('Site Commission Agreements', '/admin/site-commission-agreements', 'SiteCommissions.Manage'),
      link('Imports', '/admin/imports', 'Imports.Run'),
      link('Historical Cost Recovery', '/admin/historical-cost-recovery', 'Data.Repair'),
      link('AVCO Transition', '/admin/avco-transition', 'Data.Repair'),
      link('Costing Repair', '/admin/costing-repair', 'Data.Repair'),
      link('Historical GST Classification', '/admin/historical-gst-classification', 'Data.Repair'),
      link('Nayax Sale Timestamp Repair', '/admin/nayax-sale-timestamp-repair', 'Data.Repair'),
      link('Users', '/admin/users', 'Members.Manage'),
      link('Roles', '/admin/roles', 'Roles.View')
    ]
  }
];

/**
 * The super-admin platform diagnostics page (issue #335, moved to its own group in issue #546).
 *
 * It is deliberately not part of `primaryNavigation`, because it is not a destination every
 * signed-in operator has: it is composed into the top-level `Super Admin` group by `navigationFor`
 * only while `GET /api/admin/diagnostics/access` confirms the signed-in actor is the configured
 * platform administrator. The link is presentation, never a boundary - `/super-admin/diagnostics`
 * entered directly still resolves, and both diagnostics endpoints authorize every request
 * themselves, so a hidden link hides a page rather than protecting one.
 */
export const platformDiagnosticsNavLink: NavLink = {
  kind: 'link',
  label: 'Platform Diagnostics',
  route: '/super-admin/diagnostics'
};

/**
 * The Businesses page (issue #546's placeholder, built in issue #547): approve/reject a pending
 * application, deactivate/reactivate a trading one, and release a member of a deactivated one.
 * Like `platformDiagnosticsNavLink`, it declares no capability - no business role grants it, only
 * the platform-admin probe below does.
 */
export const businessesNavLink: NavLink = {
  kind: 'link',
  label: 'Businesses',
  route: '/super-admin/businesses'
};

/**
 * The top-level Super Admin group (issue #546, parent #510 under #328): kept apart from the
 * business `Admin` group because neither of its destinations is granted by a business role.
 * `navigationFor` composes it in only while `GET /api/admin/diagnostics/access` confirms the
 * signed-in actor is the configured platform administrator - the same probe that used to append
 * `platformDiagnosticsNavLink` to `Admin` instead.
 */
export const superAdminNavGroup: NavGroup = {
  kind: 'group',
  label: 'Super Admin',
  icon: 'settings',
  children: [platformDiagnosticsNavLink, businessesNavLink]
};

/**
 * The navigation to render for a member, from the capabilities `GET /api/me/access` reported for
 * them and the diagnostics API's separate answer about platform-admin access.
 *
 * A destination whose capability the member does not hold is left out, and a group all of whose
 * children were left out is left out with them - so an Operator is offered no `Expenses`, no
 * `Reports` group and no `Admin` group, and a Manager's `Admin` group holds the imports and the
 * site commission agreements without the Nayax settings or any data repair. `capabilities` of
 * `undefined` means the capability set is not known (it could not be read); every destination is
 * then offered, because the API refuses what the role may not do regardless and hiding the whole
 * menu over a failed read would be worse than offering a page that answers `403` - see
 * `AccessService`.
 *
 * `Super Admin` keeps the behaviour issue #335 gave `Platform Diagnostics`, unchanged and
 * independent of any capability: the whole group is appended, last, only while
 * `GET /api/admin/diagnostics/access` confirms access, and neither of its children declares a
 * capability of its own because no business role grants either.
 *
 * Everything here copies rather than mutates, so `primaryNavigation` stays the full declared
 * navigation and one member's rendered menu can never leak into another's.
 */
export function navigationFor(
  capabilities: CapabilitySet | undefined,
  hasPlatformDiagnosticsAccess: boolean
): readonly NavItem[] {
  const offered = hasPlatformDiagnosticsAccess ? [...primaryNavigation, superAdminNavGroup] : primaryNavigation;

  if (capabilities === undefined) {
    return offered;
  }

  return offered.flatMap<NavItem>((item) => {
    if (item.kind === 'link') {
      return allowsLink(capabilities, item) ? [item] : [];
    }

    const children = item.children.filter((child) => allowsChild(capabilities, child));
    return children.length === 0 ? [] : [{ ...item, children }];
  });
}

function allowsLink(capabilities: CapabilitySet, navLink: NavLink): boolean {
  return navLink.capability === undefined || capabilities.has(navLink.capability);
}

/** A placeholder is always kept; a link is kept only while `allowsLink` allows it. */
function allowsChild(capabilities: CapabilitySet, child: NavGroupChild): boolean {
  return child.kind === 'placeholder' || allowsLink(capabilities, child);
}

/** Every real destination in the navigation, top-level links first, then each group's children. */
export function navLinks(items: readonly NavItem[]): readonly NavLink[] {
  return items.flatMap((item) =>
    item.kind === 'group' ? item.children.filter((child): child is NavLink => child.kind === 'link') : [item]
  );
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
    (item): item is NavGroup =>
      item.kind === 'group' && item.children.some((child) => child.kind === 'link' && child.route === active)
  );
}
