import { BusinessNavigationVisibility } from '../access/business-navigation';
import { Capability, CapabilitySet } from '../access/capabilities';
import { OptionVisibility } from '../access/option-visibility';
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
 * Since issue #729 (child E2 of issue #329, decision P6) a destination may also declare what it
 * needs from the business's sales provider: `lockedWithout` names the read, and while the shared
 * availability signal of issue #728 refuses it the entry is shown **locked** rather than hidden.
 * That answer is applied **after** the capability set, so an entry the role cannot see stays hidden
 * rather than becoming a locked advertisement for a page that role never had.
 *
 * Issue #740 (child G1) took `Machines` off that signal entirely: a business keeps its own machine
 * records now, so the destination always has something to show and its own create/rename/deactivate
 * actions to offer. The page withholds its live stock, revenue and sync parts itself while the
 * provider cannot serve them, which is a decision about one page's content rather than about
 * whether a destination exists.
 *
 * Issue #868 removed the "Needs Nayax" section issue #729 had put the live provider destinations
 * in: `Sites` sits immediately above `Machines`, `Site Commissions` is back among the reports, and
 * a destination's provider dependency is carried by the entry itself rather than by where it sits.
 * It also **supersedes decision P5 for five reports**, at the user's request: Daily, Transactions,
 * Reconciliation, Machines and Products declare `providerRecords`, so a business with no Nayax
 * connection at all is shown the same connection-required page on them that Site Commissions
 * already showed. `providerRecords` is deliberately the narrowest dependency there is - a working
 * connection with a refused read, and credentials that stopped working, keep these reports open,
 * because what they report on is already stored and nothing about it is read live. Bookkeeping and
 * GST declare nothing and are never locked.
 *
 * Issue #873 adds the one thing in this file that is not about a capability or a provider: a Super
 * Admin may stop `Historical GST Classification` being **offered** to everybody else. The shared
 * setting arrives with the capability set, on `GET /api/me/access`, and is applied here as
 * {@link OptionVisibility} - it hides that one destination from a member who is not the platform
 * administrator, leaves it in the Super Admin's own menu marked with an `Off` pill, and changes no
 * capability and no other entry. Like everything else here it hides a page rather than protecting
 * one: the classification endpoints keep authorizing every request by `Data.Repair` in both states,
 * and the route entered directly still resolves.
 *
 * Issue #869 does the same for two Admin destinations, on the **live fleet read** rather than on
 * stored records: `Site Commission Agreements`, whose site picker is that read, and `AVCO
 * Transition`, whose preview reads the machines and then their products. `Historical Cost Recovery`
 * deliberately declares nothing and is never locked (the user's decision of 24 October 2026): it
 * makes no Nayax call at all, reading the Nayax cost already stored on imported sales, so a
 * disconnected business with imported history can still recover those costs.
 *
 * Issue #972 brings section headings back, as the owner-approved Restockroo design's four groups:
 * **Operations** (Dashboard, Pick List, Sites, Machines), **Inventory & purchasing** (the Inventory
 * and Purchases groups), **Finance & reports** (Expenses and Reports) and **Settings** (the Admin
 * group). They only label what follows them: every destination, its route, its capability, its
 * provider dependency and its order are exactly what they were, and the Super Admin group keeps its
 * own **Super admin** heading, appended with it only for the platform administrator.
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
 * A heading over the entries that follow it, up to the next heading, with no page and no action of
 * its own (issue #729). It held the live provider destinations together as one "Needs Nayax"
 * section until issue #868 removed that section; issue #972 uses it for the Restockroo design's
 * group headings. `navigationFor` drops a heading when every entry under it was left out - a
 * heading over nothing says a section exists that this member does not have.
 */
export interface NavSectionHeading {
  readonly kind: 'section';
  readonly label: string;
  /**
   * An optional sentence under the heading. The issue #972 group headings carry none: their label
   * is the whole statement, and none of them explains a lock.
   */
  readonly description?: string;
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

/**
 * The route of the one Admin destination whose visibility a Super Admin may switch (issue #873).
 *
 * It is named by its route rather than by a flag on the entry because there is exactly one of them
 * and because the API names it the same way: `GET /api/me/access` reports one boolean about this one
 * option. A general feature-flag framework is explicitly out of scope of that issue, so a second
 * switchable option would be a second named constant and a second field, decided by its own issue -
 * never a map of arbitrary keys this file resolves.
 */
export const switchableOptionRoute = '/admin/historical-gst-classification';

/**
 * Whether `route` is the switchable destination and the application has switched it **off** - which
 * is what the sidebar and the Admin hub render the `Off` pill from, what this file withholds the
 * destination from an ordinary member for, and the only thing either of them renders it from.
 *
 * It says nothing about who is looking: a destination is only ever *offered* to a Super Admin while
 * it is off, so a rendered switched-off entry is by construction a Super Admin's. `undefined`
 * options are "not known" and are never switched off (see {@link OptionVisibility}).
 */
export function optionSwitchedOff(route: string, options: OptionVisibility | undefined): boolean {
  return route === switchableOptionRoute && options?.historicalGstClassificationVisible === false;
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

function section(label: string): NavSectionHeading {
  return { kind: 'section', label };
}

function link(label: string, route: string, capability: Capability): NavLink {
  return { kind: 'link', label, route, capability };
}

/** A provider repair or admin tool: hidden until the connection is usable (issue #729). */
function providerToolLink(label: string, route: string, capability: Capability): NavLink {
  return { ...link(label, route, capability), hiddenWithoutProvider: true };
}

/**
 * A report over sales this business recorded through Nayax (issue #868). It makes no live read, so
 * it declares `providerRecords`: it is locked only while the business has no Nayax connection at
 * all, and stays open for credentials that stopped working or a read Nayax refused.
 */
function providerRecordsReportLink(label: string, route: string): NavLink {
  return { ...link(label, route, 'Reports.View'), lockedWithout: 'providerRecords' };
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
    {
      // Locked on the live fleet read since issue #869, for the same reason the site commission
      // report is: this page's site picker *is* that read (`GET /api/sites`), and a site is known
      // only from it, so with no usable connection there is no site to write an agreement against.
      ...link('Site Commission Agreements', '/admin/site-commission-agreements', 'SiteCommissions.Manage'),
      lockedWithout: 'machineFleet'
    },
    // The Nayax sales and settlement imports, and the Nayax sale timestamp repair: hidden until the
    // provider is usable (issue #729).
    providerToolLink('Imports', '/admin/imports', 'Imports.Run'),
    // Deliberately never locked (issue #869, the user's decision of 24 October 2026): this page
    // makes no Nayax call at all - it reads the Nayax cost already stored on imported sales - so a
    // disconnected business with imported history can still recover those costs.
    link('Historical Cost Recovery', '/admin/historical-cost-recovery', 'Data.Repair'),
    {
      // Locked on the live fleet read since issue #869: the transition preview reads the machines
      // and then their products from Nayax, so without a usable connection it has no baseline to
      // offer.
      ...link('AVCO Transition', '/admin/avco-transition', 'Data.Repair'),
      lockedWithout: 'machineFleet'
    },
    link('Costing Repair', '/admin/costing-repair', 'Data.Repair'),
    link('Historical GST Classification', '/admin/historical-gst-classification', 'Data.Repair'),
    providerToolLink('Nayax Sale Timestamp Repair', '/admin/nayax-sale-timestamp-repair', 'Data.Repair'),
    link('Users', '/admin/users', 'Members.Manage'),
    link('Roles', '/admin/roles', 'Roles.View'),
    link('Business Settings', '/admin/business-settings', 'Business.Manage')
  ]
};

export const primaryNavigation: readonly NavItem[] = [
  section('Operations'),
  { kind: 'link', label: 'Dashboard', route: '/', icon: 'home', capability: 'Dashboard.View' },
  { kind: 'link', label: 'Pick List', route: '/pick-list', icon: 'assignment', capability: 'PickList.View' },
  {
    // A site is known only from the live fleet read's `CustomerID`, so the site list needs that
    // read and has nothing local to fall back on (AGENTS.md § Site commissions). Manual sites are
    // deliberately out of scope of issue #740. It sits immediately above Machines since issue #868,
    // which removed the "Needs Nayax" section it used to be the first entry of.
    kind: 'link',
    label: 'Sites',
    route: '/sites',
    icon: 'location_on',
    capability: 'Inventory.Operate',
    lockedWithout: 'machineFleet'
  },
  {
    // Never locked since issue #740 (child G1 of issue #329): a business now keeps its own machine
    // records, which it creates, renames, deactivates and reactivates with no provider at all, so
    // the destination declares no `lockedWithout`. The page hides its live stock, revenue and sync
    // parts itself while the provider cannot serve them.
    kind: 'link',
    label: 'Machines',
    route: '/machines',
    icon: 'refresh',
    capability: 'Inventory.Operate'
  },
  section('Inventory & purchasing'),
  {
    // "Inventory" rather than "Products" since issue #729 (decision P11): the group holds the whole
    // of the business's own stock keeping, all of which works with no provider at all. The Products
    // page inside it, its URL and its breadcrumb keep their own name.
    kind: 'group',
    label: 'Inventory',
    icon: 'inventory_2',
    children: [
      link('Products', '/products', 'Inventory.Operate'),
      // The dedicated Import products page (issue #888), directly under the page it imports into.
      // A provider tool like the Admin imports: with no usable connection there is no catalogue of
      // the provider's to review, so the entry is left out rather than shown locked.
      providerToolLink('Import products', '/products/import', 'Imports.Run'),
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
  section('Finance & reports'),
  { kind: 'link', label: 'Expenses', route: '/expenses', icon: 'attach_money', capability: 'Expenses.Manage' },
  {
    // Every report is here again since issue #868, which brought the site commission report back
    // from the "Needs Nayax" section it left for in issue #729. What a report needs from the
    // provider is declared on the entry: Bookkeeping and GST need nothing, the five
    // `providerRecordsReportLink` entries need a Nayax connection to exist at all, and Site
    // Commissions needs the live fleet read. Each one still reports its own availability states
    // inside the page (#730, #731).
    kind: 'group',
    label: 'Reports',
    icon: 'bar_chart',
    children: [
      link('Bookkeeping', '/reports/bookkeeping', 'Reports.View'),
      providerRecordsReportLink('Daily', '/reports/daily'),
      providerRecordsReportLink('Transactions', '/reports/transactions'),
      providerRecordsReportLink('Reconciliation', '/reports/reconciliation'),
      providerRecordsReportLink('Machines', '/reports/machines'),
      providerRecordsReportLink('Products', '/reports/products'),
      {
        // The one report that is not a read of stored data at all: with no usable provider it has
        // no sites to report on and the API answers `isAvailable: false` (issue #731), so it keeps
        // the live fleet read it has always declared. The API puts it on SiteCommissions.Manage
        // rather than Reports.View, because it is the surface the agreements are maintained from
        // (issue #526); its breadcrumb has always named Reports as its parent page.
        kind: 'link',
        label: 'Site Commissions',
        route: '/reports/site-commissions',
        capability: 'SiteCommissions.Manage',
        lockedWithout: 'machineFleet'
      },
      link('GST', '/reports/gst', 'Reports.View')
    ]
  },
  section('Settings'),
  // The Plan page (issue #970): the business's plan, storage and connected machines. It declares
  // Dashboard.View - every role and a support session hold it - because it is visible to every
  // member rather than gated like the Admin group beside it, so it sits as its own top-level link
  // under the same heading rather than one of Admin's children.
  { kind: 'link', label: 'Plan', route: '/plan', icon: 'attach_money', capability: 'Dashboard.View' },
  adminNavGroup
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
 * The Supplier presets page (issue #848): the one platform-wide list of suggested suppliers, which
 * issue #835 child C copies into each newly approved business. Like the two links above it declares
 * no capability - no business role grants it, only the platform-admin probe below does.
 */
export const supplierPresetsNavLink: NavLink = {
  kind: 'link',
  label: 'Supplier Presets',
  route: '/super-admin/supplier-presets'
};

/**
 * The Supplier price review page (issue #928): the shared supplier price catalogue's own review -
 * waiting products, automatic matches and the listings waiting to be confirmed. Like the three links
 * above it declares no capability, because no business role grants it: only the platform-admin probe
 * below does.
 */
export const priceCatalogueReviewNavLink: NavLink = {
  kind: 'link',
  label: 'Supplier Price Review',
  route: '/super-admin/price-catalogue-review'
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
  children: [platformDiagnosticsNavLink, businessesNavLink, supplierPresetsNavLink, priceCatalogueReviewNavLink]
};

/**
 * The heading over the Super Admin group (issue #972). It is appended with the group and only with
 * it, so a member who is not the platform administrator is never shown a heading for it either.
 */
export const superAdminNavSection: NavSectionHeading = section('Super admin');

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
 * `options` is the shared switchable-option answer (issue #873), applied in the same pass as the
 * capability set and **after** it in effect: the one switchable destination is left out while the
 * application has switched it off, unless `hasPlatformDiagnosticsAccess` says this is the platform
 * administrator, who keeps it in both states. It can only ever remove an entry the capabilities
 * already allowed - switching the option on adds nothing to a member whose role never had it - and
 * `undefined` removes nothing at all (see {@link OptionVisibility}).
 *
 * Everything here copies rather than mutates, so `primaryNavigation` stays the full declared
 * navigation and one member's rendered menu can never leak into another's.
 */
export function navigationFor(
  capabilities: CapabilitySet | undefined,
  hasPlatformDiagnosticsAccess: boolean,
  businessNavigation: BusinessNavigationVisibility,
  provider?: ProviderAvailability,
  options?: OptionVisibility
): readonly NavItem[] {
  const business: readonly NavItem[] = businessNavigation === 'offered' ? primaryNavigation : [];
  const offered = hasPlatformDiagnosticsAccess ? [...business, superAdminNavSection, superAdminNavGroup] : business;

  // A Super Admin is exempt from the switch, so nothing is withheld for them; neither is anything
  // withheld while the capability set itself is unknown, where the whole pass is skipped below.
  const switchedOff = hasPlatformDiagnosticsAccess ? undefined : options;

  const allowed =
    capabilities === undefined
      ? offered
      : offered.flatMap<NavItem>((item) => {
          if (item.kind === 'section') {
            return [item];
          }
          if (item.kind === 'link') {
            return allowsLink(capabilities, item, switchedOff) ? [item] : [];
          }

          const children = item.children.filter((child) => allowsChild(capabilities, child, switchedOff));
          return children.length === 0 ? [] : [{ ...item, children }];
        });

  return withOccupiedSections(withProviderTools(allowed, provider));
}

function allowsLink(
  capabilities: CapabilitySet,
  navLink: NavLink,
  switchedOff: OptionVisibility | undefined
): boolean {
  if (optionSwitchedOff(navLink.route, switchedOff)) {
    return false;
  }

  return navLink.capability === undefined || capabilities.has(navLink.capability);
}

/** A placeholder is always kept; a link is kept only while `allowsLink` allows it. */
function allowsChild(
  capabilities: CapabilitySet,
  child: NavGroupChild,
  switchedOff: OptionVisibility | undefined
): boolean {
  return child.kind === 'placeholder' || allowsLink(capabilities, child, switchedOff);
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

/**
 * The same navigation without a section heading that has nothing left under it (issue #729): no
 * entry between it and the next heading, or the end of the menu (issue #972).
 */
function withOccupiedSections(items: readonly NavItem[]): readonly NavItem[] {
  return items.filter((item, index) => item.kind !== 'section' || isOccupied(items, index));
}

function isOccupied(items: readonly NavItem[], headingIndex: number): boolean {
  const next = items[headingIndex + 1];
  return next !== undefined && next.kind !== 'section';
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
