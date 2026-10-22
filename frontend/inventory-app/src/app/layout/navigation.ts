import { BusinessNavigationVisibility } from '../access/business-navigation';
import { Capability, CapabilitySet } from '../access/capabilities';
import { ProviderAvailability } from '../provider/provider-availability.service';
import { ProviderFeature } from '../provider/provider-feature-permissions';
import { ProviderLock, providerLockOf, providerToolsOffered } from '../provider/provider-lock';

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
 *
 * Since issue #729 (child E2 of issue #329, decision P6) the order is also a statement about the
 * business's sales provider: everything that works without one comes first, and the live provider
 * destinations sit in their own section at the bottom, where they are shown **locked** rather than
 * hidden while the provider cannot serve them. The provider answer comes from the shared signal of
 * issue #728 and is applied **after** the capability set, so an entry the role cannot see stays
 * hidden rather than becoming a locked advertisement for a page that role never had.
 *
 * Issue #740 (child G1) moved `Machines` out of that section and into the top one: a business keeps
 * its own machine records now, so the destination always has something to show and its own
 * create/rename/deactivate actions to offer. The page withholds its live stock, revenue and sync
 * parts itself while the provider cannot serve them, which is a decision about one page's content
 * rather than about whether a destination exists.
 *
 * The reports are deliberately not in that section: Bookkeeping, Daily, Transactions,
 * Reconciliation and machine profitability read **stored** data, so they open normally with no
 * provider and report their own availability states (issues #730 and #731), and previously imported
 * history stays readable after a disconnect (decision P5). Locking them would hide a business's own
 * records behind a connection they no longer have.
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

  /**
   * The live Nayax read this destination's page needs (issue #729). While the shared availability
   * signal refuses that read, the entry is shown **locked** and its route redirects to the "needs a
   * provider" page - it is never hidden, because a page an operator could have once used, and could
   * use again by connecting Nayax, must stay visible and explain itself.
   */
  readonly lockedWithout?: ProviderFeature;

  /**
   * True for a provider repair or admin tool that is left out of the menu entirely until the
   * connection is usable (issue #729): the Nayax sales and settlement imports and the Nayax sale
   * timestamp repair. Without a provider there is nothing of the provider's to import or repair, so
   * a locked entry would offer a tool for data that does not exist.
   */
  readonly hiddenWithoutProvider?: boolean;
}

/**
 * A heading over the entries that follow it, with no page and no action of its own (issue #729).
 * It exists so the live provider destinations read as one section at the bottom of the menu rather
 * than as three unexplained entries, and `navigationFor` drops it when every entry under it was
 * left out - a heading over nothing says a section exists that this member does not have.
 */
export interface NavSectionHeading {
  readonly kind: 'section';
  readonly label: string;
  /** The one sentence the heading carries, so the section explains why its entries may be locked. */
  readonly description: string;
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

export type NavItem = NavLink | NavGroup | NavSectionHeading;

function link(label: string, route: string, capability: Capability): NavLink {
  return { kind: 'link', label, route, capability };
}

/** A provider repair or admin tool: hidden until the connection is usable (issue #729). */
function providerToolLink(label: string, route: string, capability: Capability): NavLink {
  return { ...link(label, route, capability), hiddenWithoutProvider: true };
}

/**
 * The business `Admin` group's declared destinations, exported on their own so the Admin hub page
 * (`AdminComponent`) can filter its own cards from the same capability-to-route data the sidebar
 * renders, rather than a second, hand-kept copy that could drift from it (issue #674).
 */
export const adminNavGroup: NavGroup = {
  kind: 'group',
  label: 'Admin',
  icon: 'settings',
  children: [
    // "Integrations" rather than "Nayax Settings" since issue #729: it is where a business connects
    // a sales provider, so it is also the one destination every locked page sends an Owner to, and
    // it stays in the top section whatever the provider's state - it has to be reachable to fix it.
    link('Integrations', '/admin/nayax-settings', 'Integration.Manage'),
    link('Site Commission Agreements', '/admin/site-commission-agreements', 'SiteCommissions.Manage'),
    // The Nayax sales and settlement imports, and the Nayax sale timestamp repair: hidden until the
    // provider is usable (issue #729).
    providerToolLink('Imports', '/admin/imports', 'Imports.Run'),
    link('Historical Cost Recovery', '/admin/historical-cost-recovery', 'Data.Repair'),
    link('AVCO Transition', '/admin/avco-transition', 'Data.Repair'),
    link('Costing Repair', '/admin/costing-repair', 'Data.Repair'),
    link('Historical GST Classification', '/admin/historical-gst-classification', 'Data.Repair'),
    providerToolLink('Nayax Sale Timestamp Repair', '/admin/nayax-sale-timestamp-repair', 'Data.Repair'),
    link('Users', '/admin/users', 'Members.Manage'),
    link('Roles', '/admin/roles', 'Roles.View'),
    link('Business Settings', '/admin/business-settings', 'Business.Manage')
  ]
};

/**
 * The heading over the live provider destinations (issue #729). It names the provider, because that
 * is what an operator has to act on, and its sentence says what the section needs rather than what
 * it currently is: the entries under it carry their own lock state, one per feature.
 */
export const providerSectionHeading: NavSectionHeading = {
  kind: 'section',
  label: 'Needs Nayax',
  description: 'These read live data from Nayax.'
};

export const primaryNavigation: readonly NavItem[] = [
  { kind: 'link', label: 'Dashboard', route: '/', icon: 'home', capability: 'Dashboard.View' },
  { kind: 'link', label: 'Pick List', route: '/pick-list', icon: 'assignment', capability: 'PickList.View' },
  {
    // In the top section since issue #740 (child G1 of issue #329): a business now keeps its own
    // machine records, which it creates, renames, deactivates and reactivates with no provider at
    // all, so the destination declares no `lockedWithout` and is never locked. The page hides its
    // live stock, revenue and sync parts itself while the provider cannot serve them.
    kind: 'link',
    label: 'Machines',
    route: '/machines',
    icon: 'refresh',
    capability: 'Inventory.Operate'
  },
  {
    // "Inventory" rather than "Products" since issue #729 (decision P11): the group holds the whole
    // of the business's own stock keeping, all of which works with no provider at all. The Products
    // page inside it, its URL and its breadcrumb keep their own name.
    kind: 'group',
    label: 'Inventory',
    icon: 'inventory_2',
    children: [
      link('Products', '/products', 'Inventory.Operate'),
      link('Take Inventory', '/take-inventory', 'Inventory.Operate'),
      link('Categories', '/categories', 'Inventory.Operate'),
      link('Stock History', '/stock-history', 'Inventory.Operate')
    ]
  },
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
    // Every report here reads stored data, so none of them is ever locked by provider status
    // (issue #729, decision P5): each one reports its own availability states instead (#730, #731).
    kind: 'group',
    label: 'Reports',
    icon: 'bar_chart',
    children: [
      link('Bookkeeping', '/reports/bookkeeping', 'Reports.View'),
      link('Daily', '/reports/daily', 'Reports.View'),
      link('Transactions', '/reports/transactions', 'Reports.View'),
      link('Reconciliation', '/reports/reconciliation', 'Reports.View'),
      link('Machines', '/reports/machines', 'Reports.View'),
      link('Products', '/reports/products', 'Reports.View'),
      link('GST', '/reports/gst', 'Reports.View')
    ]
  },
  adminNavGroup,
  providerSectionHeading,
  {
    // A site is known only from the live fleet read's `CustomerID`, so the site list needs that
    // read and has nothing local to fall back on (AGENTS.md § Site commissions). Manual sites are
    // deliberately out of scope of issue #740.
    kind: 'link',
    label: 'Sites',
    route: '/sites',
    icon: 'location_on',
    capability: 'Inventory.Operate',
    lockedWithout: 'machineFleet'
  },
  {
    // The site commission report moved out of the Reports group here (issue #729): unlike every
    // report left there it is not a read of stored data at all - with no usable provider it has no
    // sites to report on and answers `isAvailable: false` (issue #731). The API puts it on
    // SiteCommissions.Manage rather than Reports.View, because it is the surface the agreements are
    // maintained from (issue #526); its breadcrumb still names Reports as its parent page.
    kind: 'link',
    label: 'Site Commissions',
    route: '/reports/site-commissions',
    icon: 'attach_money',
    capability: 'SiteCommissions.Manage',
    lockedWithout: 'machineFleet'
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
 * site commission agreements without `Integrations` or any data repair. `capabilities` of
 * `undefined` means the capability set is not known (it could not be read); every destination is
 * then offered, because the API refuses what the role may not do regardless and hiding the whole
 * menu over a failed read would be worse than offering a page that answers `403` - see
 * `AccessService`.
 *
 * `businessNavigation` says whether those business destinations are on offer **at all**
 * (issue #676). `withheld` leaves none of them in, whatever the capability set says and whether or
 * not one was ever read: a signed-out visitor and an account the API has already said holds no
 * usable membership are offered no business menu, which no capability answer could decide, because
 * the answer for both of them is the fail-open `undefined` one. Withholding is structural rather
 * than an empty capability set, so a destination that declares no capability of its own cannot
 * leak into it either. Who that is is `businessNavigationFor`'s decision, not this file's.
 *
 * `Super Admin` keeps the behaviour issue #335 gave `Platform Diagnostics`, unchanged and
 * independent of any capability **and of `businessNavigation`**: the whole group is appended,
 * last, only while `GET /api/admin/diagnostics/access` confirms access, and neither of its
 * children declares a capability of its own because no business role grants either. An
 * administrator with no membership of their own is therefore offered that group and nothing else.
 *
 * `provider` is the shared provider-availability answer (issue #728), and it is applied **after**
 * the capability set, because the capability rules come first: an entry the role may not see stays
 * hidden rather than becoming a locked advertisement for a page that role never had. It decides two
 * things and nothing else - whether a `hiddenWithoutProvider` repair tool is offered at all, and
 * whether a section heading has anything left under it - and leaves a `lockedWithout` destination
 * in the menu either way, because {@link navLockOf} is what renders it locked. `undefined` means
 * the answer has not settled yet, and then nothing is withheld and nothing is locked: the menu must
 * not flicker items away, and an unread status is not a refusal (issue #728's fail-open rule).
 *
 * Everything here copies rather than mutates, so `primaryNavigation` stays the full declared
 * navigation and one member's rendered menu can never leak into another's.
 */
export function navigationFor(
  capabilities: CapabilitySet | undefined,
  hasPlatformDiagnosticsAccess: boolean,
  businessNavigation: BusinessNavigationVisibility,
  provider?: ProviderAvailability
): readonly NavItem[] {
  const business: readonly NavItem[] = businessNavigation === 'offered' ? primaryNavigation : [];
  const offered = hasPlatformDiagnosticsAccess ? [...business, superAdminNavGroup] : business;

  const allowed =
    capabilities === undefined
      ? offered
      : offered.flatMap<NavItem>((item) => {
          if (item.kind === 'section') {
            return [item];
          }
          if (item.kind === 'link') {
            return allowsLink(capabilities, item) ? [item] : [];
          }

          const children = item.children.filter((child) => allowsChild(capabilities, child));
          return children.length === 0 ? [] : [{ ...item, children }];
        });

  return withOccupiedSections(withProviderTools(allowed, provider));
}

function allowsLink(capabilities: CapabilitySet, navLink: NavLink): boolean {
  return navLink.capability === undefined || capabilities.has(navLink.capability);
}

/** A placeholder is always kept; a link is kept only while `allowsLink` allows it. */
function allowsChild(capabilities: CapabilitySet, child: NavGroupChild): boolean {
  return child.kind === 'placeholder' || allowsLink(capabilities, child);
}

/**
 * The same navigation without the provider repair and admin tools, while the connection cannot be
 * used (issue #729). A group left with no child at all goes with them, exactly as it does when the
 * capability set withholds every child.
 */
function withProviderTools(items: readonly NavItem[], provider: ProviderAvailability | undefined): readonly NavItem[] {
  if (provider === undefined || providerToolsOffered(provider.connection)) {
    return items;
  }

  return items.flatMap<NavItem>((item) => {
    if (item.kind === 'section') {
      return [item];
    }
    if (item.kind === 'link') {
      return item.hiddenWithoutProvider ? [] : [item];
    }

    const children = item.children.filter((child) => child.kind === 'placeholder' || !child.hiddenWithoutProvider);
    return children.length === 0 ? [] : [{ ...item, children }];
  });
}

/** The same navigation without a section heading that has nothing left under it (issue #729). */
function withOccupiedSections(items: readonly NavItem[]): readonly NavItem[] {
  return items.filter(
    (item, index) => item.kind !== 'section' || items.slice(index + 1).some((next) => next.kind !== 'section')
  );
}

/**
 * How one destination is rendered right now (issue #729): `open` unless it declares a live provider
 * read the shared answer refuses. A destination with no `lockedWithout`, and every destination while
 * that answer has not settled, is `open`.
 */
export function navLockOf(navLink: NavLink, provider: ProviderAvailability | undefined): ProviderLock {
  if (navLink.lockedWithout === undefined || provider === undefined) {
    return 'open';
  }
  return providerLockOf(provider.features[navLink.lockedWithout]);
}

/** Every real destination in the navigation, top-level links first, then each group's children. */
export function navLinks(items: readonly NavItem[]): readonly NavLink[] {
  return items.flatMap<NavLink>((item) => {
    if (item.kind === 'group') {
      return item.children.filter((child): child is NavLink => child.kind === 'link');
    }
    return item.kind === 'link' ? [item] : [];
  });
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

/**
 * The declared destination that owns `url`, or `undefined` for a URL outside the navigation.
 *
 * It is how the "needs a provider" page of issue #729 names the destination an operator asked for
 * and finds the live read it needs: the guard hands that page the refused URL, and the declared
 * navigation - not the URL, and nothing the request carries - says what that destination is called.
 */
export function activeNavLink(items: readonly NavItem[], url: string): NavLink | undefined {
  const active = activeNavRoute(items, url);
  return active === undefined ? undefined : navLinks(items).find((candidate) => candidate.route === active);
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
