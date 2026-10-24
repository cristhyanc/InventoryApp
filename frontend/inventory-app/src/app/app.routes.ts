import { inject } from '@angular/core';
import { Router, Routes } from '@angular/router';
import { MsalGuard } from '@azure/msal-angular';
import { ACCESS_NOT_ALLOWED_PATH, requireCapability } from './access/capability.guard';
import { BUSINESS_APPLICATION_ROUTE, accountStateGuard } from './access/account-state.guard';
import { platformAdminGuard } from './access/platform-admin.guard';
import { AccessUnavailableComponent } from './access/access-unavailable.component';
import { WelcomeComponent } from './access/welcome.component';
import { ApplicationPendingComponent } from './access/application-pending.component';
import { ApplicationRejectedComponent } from './access/application-rejected.component';
import { BusinessApplicationComponent } from './access/business-application.component';
import {
  INVITE_ADDRESS_BAR_ROUTE,
  inviteTokenAddressBarGuard,
  pendingInviteTokenGuard
} from './access/invite-address-bar.guard';
import { InviteLinkReopenComponent } from './access/invite-link-reopen.component';
import { AuthCallbackComponent } from './auth/auth-callback.component';
import { SIGN_IN_ROUTE } from './auth/intended-destination';
import { SignInComponent } from './auth/sign-in.component';
import { designSystemRoutes } from './design-system/design-system.routes';
import { PROVIDER_LOCKED_PATH } from './provider/provider-lock';
import { requireProvider } from './provider/provider-lock.guard';

/**
 * Every authenticated route carries `MsalGuard`, then, since issue #529, `accountStateGuard`, then
 * the capability its page needs since issue #528 - the same capability the endpoints behind that
 * page already require (see docs/architecture.md § Capability authorization on the Manager and
 * Owner endpoints for the matrix these mirror).
 *
 * Neither guard is a boundary: `requireCapability` replaces a page full of refusals with the
 * "access not allowed" page, and `accountStateGuard` sends a person whose account state is not
 * `Member` to the route that state calls for before the page's own business calls ever run, and
 * protects nothing either way. The routes that deliberately carry no `accountStateGuard` are the
 * ones it would otherwise send somebody *to* - `/welcome`, `/application-pending`,
 * `/application-rejected`, `/access-unavailable`, `/apply` and the `/invite` acceptance page - because a person already
 * sent to one of them must not be sent away from it; `/access-not-allowed` carries no capability
 * for the reason already documented beside it, and `/super-admin/diagnostics`,
 * `/super-admin/businesses` and `/super-admin/supplier-presets` (issues #546, #547, #848) carry
 * neither guard: their authority is the
 * platform-admin check, not a business role or membership, so each carries `platformAdminGuard`
 * instead and sends a refusal to the dashboard rather than to any membership-handling destination
 * (see `access/platform-admin.guard.ts`).
 */
export const routes: Routes = [
  // Public: the Entra redirect callback must be reachable without authentication.
  { path: 'auth', component: AuthCallbackComponent },
  {
    // Public: the sign-in page where a signed-out visitor chooses which trusted authority to sign
    // in against (issue #711). It is what `SignInAuthorityGuard` sends them to instead of starting
    // a redirect, so it cannot itself be behind a guard, and it starts no redirect of its own.
    path: SIGN_IN_ROUTE,
    component: SignInComponent
  },
  {
    // Reached by a guard above, so it declares no capability of its own: a member sent here
    // because their role does not allow a page must not be sent away from the explanation too.
    path: ACCESS_NOT_ALLOWED_PATH,
    canActivate: [MsalGuard],
    loadComponent: () => import('./access/access-not-allowed.component').then((m) => m.AccessNotAllowedComponent)
  },
  {
    // The "needs a provider" page (issue #729). Like `/access-not-allowed` above it is where a
    // guard *sends* an operator, so it carries no capability of its own: the destinations that
    // redirect here need different ones (`Inventory.Operate`, `Reports.View`,
    // `SiteCommissions.Manage`), each
    // already checked by the capability guard that ran before `requireProvider`, and sending
    // somebody away from their own explanation is the one thing this page must not do.
    path: PROVIDER_LOCKED_PATH,
    canActivate: [MsalGuard],
    loadComponent: () => import('./provider/provider-locked.component').then((m) => m.ProviderLockedComponent)
  },
  {
    // Account-state routing destinations (issue #529) and the invite route it bypasses for
    // (issue #505). None of them carries `accountStateGuard` or a capability - see the comment
    // above - and all of them are also what `memberForbiddenNoticeInterceptor` bypasses
    // (`access/account-state.guard.ts`'s `ACCOUNT_STATE_BYPASS_PATHS`). `welcome`,
    // `application-pending` and `application-rejected` carried `OnboardingPlaceholderComponent`
    // until issue #542 built each one's own page.
    path: 'welcome',
    canActivate: [MsalGuard],
    component: WelcomeComponent
  },
  {
    path: 'application-pending',
    canActivate: [MsalGuard],
    component: ApplicationPendingComponent
  },
  {
    path: 'application-rejected',
    canActivate: [MsalGuard],
    component: ApplicationRejectedComponent
  },
  {
    path: 'access-unavailable',
    canActivate: [MsalGuard],
    component: AccessUnavailableComponent
  },
  {
    // The business application form (issue #543), linked from the welcome, rejected and
    // access-unavailable pages. It carries no `accountStateGuard` for the same reason they do not:
    // it is where those pages send a person whose state is not `Member`, and the form itself sends
    // anyone it is not for to the page their state calls for.
    path: BUSINESS_APPLICATION_ROUTE,
    canActivate: [MsalGuard],
    component: BusinessApplicationComponent
  },
  {
    // An invite link (issue #535). It never activates: `inviteTokenAddressBarGuard` is a `canMatch`
    // guard, so it runs before any `canActivate` guard, hands the token to this tab's pending
    // invite (`access/pending-invite-token.ts`), strips it from the address bar and redirects to
    // `/invite` (issue #681). `MsalGuard` therefore never sees the token, so a signed-out visitor's
    // sign-in redirect does not carry it. The component is only there because a route needs one.
    path: 'invite/:token',
    canMatch: [inviteTokenAddressBarGuard],
    component: InviteLinkReopenComponent
  },
  {
    // The invite acceptance page (issue #535), matched only while this tab holds a pending invite
    // token (issue #681): lets a NoMembership/MembershipRevoked/ApplicationRejected caller redeem
    // it. Carries only `MsalGuard`, for the reason the comment above gives.
    path: INVITE_ADDRESS_BAR_ROUTE,
    canMatch: [pendingInviteTokenGuard],
    canActivate: [MsalGuard],
    loadComponent: () => import('./access/invite-acceptance.component').then((m) => m.InviteAcceptanceComponent)
  },
  {
    // `/invite` with no pending invite (issue #681): a reload or a Back into it after the page read
    // the token explains that the invite link must be opened again, instead of the wildcard
    // silently landing on the dashboard. Public, like `auth`: it makes no request at all.
    path: INVITE_ADDRESS_BAR_ROUTE,
    component: InviteLinkReopenComponent
  },
  {
    path: '',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Dashboard.View')],
    loadComponent: () => import('./components/dashboard/dashboard.component').then((m) => m.DashboardComponent)
  },
  {
    // The "New product" action (issue #737): the same routed edit page with no `:id`, so a
    // business with no sales provider can start a catalogue instead of only ever importing one.
    path: 'products/new',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/products/product-form.component').then((m) => m.ProductFormComponent)
  },
  {
    path: 'products/:id/edit',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/products/product-form.component').then((m) => m.ProductFormComponent)
  },
  {
    path: 'stock-history',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/stock/stock-history-page.component').then((m) => m.StockHistoryPageComponent)
  },
  {
    // The product entry point every existing "Stock" link uses (issue #384). It opens the global
    // Stock History page with that product preselected - the same experience as
    // /stock-history?productId=123 - so the links, bookmarks and deep links that already exist keep
    // working instead of being redirected to a page that has lost the product.
    path: 'products/:id/stock',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/stock/stock-history-page.component').then((m) => m.StockHistoryPageComponent)
  },
  {
    path: 'products',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/products/products-shell.component').then((m) => m.ProductsShellComponent),
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () => import('./components/products/product-list.component').then((m) => m.ProductListComponent)
      },
      {
        path: 'needs-ordering',
        loadComponent: () => import('./components/products/product-needs-ordering.component').then((m) => m.ProductNeedsOrderingComponent)
      },
      {
        // The dedicated Supplier Orders page (/purchases/orders, issue #387) replaced this
        // Products-area listing; this redirect preserves the old bookmark/link instead of
        // keeping a second, diverging supplier-order listing implementation here.
        path: 'on-order',
        pathMatch: 'full',
        redirectTo: '/purchases/orders'
      }
    ]
  },
  {
    path: 'categories',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/categories/category-list.component').then((m) => m.CategoryListComponent)
  },
  {
    // Suppliers are purchasing work, so an Operator recording a purchase reaches this page; the
    // GST defaults editor on it is the one action it hides (issue #528).
    path: 'suppliers',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Purchasing.Operate')],
    loadComponent: () => import('./components/suppliers/supplier-list.component').then((m) => m.SupplierListComponent)
  },
  {
    // The machine list carries no `requireProvider` since issue #740 (child G1): a business keeps
    // its own machine records, which it creates, renames and deactivates with no provider at all,
    // so the page always has something to show. It hides its live stock, revenue and sync parts
    // itself while the shared availability answer refuses the fleet read.
    path: 'machines',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/machines/machine-list.component').then((m) => m.MachineListComponent)
  },
  {
    // The machine details page is still a live provider destination of issue #729: every panel on
    // it - stock, revenue, latest sales, Sync Restock - is a Nayax read, so `requireProvider` shows
    // the "needs a provider" page instead of a screen of refusals. It is declared last, after the
    // capability guard, so a role that does not include the page is sent to its own explanation
    // first. Giving it a local half belongs to the provider machine review (issue #742).
    path: 'machines/:id',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate'), requireProvider('machineFleet')],
    loadComponent: () => import('./components/machines/machine-detail.component').then((m) => m.MachineDetailComponent)
  },
  {
    // A site is known only from the fleet read's `CustomerID` (AGENTS.md § Site commissions), so the
    // site pages depend on the same read the machine pages do.
    path: 'sites',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate'), requireProvider('machineFleet')],
    loadComponent: () => import('./components/sites/site-list.component').then((m) => m.SiteListComponent)
  },
  {
    path: 'sites/:id/products',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate'), requireProvider('machineFleet')],
    loadComponent: () => import('./components/sites/site-products.component').then((m) => m.SiteProductsComponent)
  },
  {
    path: 'purchases',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Purchasing.Operate')],
    loadComponent: () => import('./components/purchases/purchase-list.component').then((m) => m.PurchaseListComponent)
  },
  {
    path: 'purchases/new',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Purchasing.Operate')],
    loadComponent: () => import('./components/purchases/purchase-upload.component').then((m) => m.PurchaseUploadComponent)
  },
  {
    path: 'purchases/orders',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Purchasing.Operate')],
    loadComponent: () => import('./components/purchases/supplier-orders.component').then((m) => m.SupplierOrdersComponent)
  },
  {
    // The dedicated Edit purchase page (issue #475), which replaced the purchases table's inline
    // editor. It loads the purchase from this id, so the URL is bookmarkable and survives a refresh.
    // Declared after `purchases/new` and `purchases/orders`, which are two-segment paths this
    // three-segment pattern cannot shadow either way.
    path: 'purchases/:id/edit',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Purchasing.Operate')],
    loadComponent: () =>
      import('./components/purchases/purchase-edit/purchase-edit-page.component').then((m) => m.PurchaseEditPageComponent)
  },
  {
    // The retired Reporting Dashboard page (issue #806, parent #803): its distinct capabilities -
    // transaction count, average sale and the expected-versus-actual reimbursement comparison, with
    // exports - now live on Bookkeeping, so the old address redirects there instead of serving its
    // own page. A string `redirectTo` only copies a query parameter it names itself (Angular's
    // `:name` placeholder convention), so a plain string here would silently drop every filter -
    // this returns a `UrlTree` built from the original request's own `queryParams` instead, which
    // carries whatever the old bookmark held (date, period, machine) forward untouched.
    // `/reports/bookkeeping`'s own guards below decide access, exactly as `/reports`'s guards once
    // did.
    path: 'reports',
    pathMatch: 'full',
    redirectTo: ({ queryParams }) => inject(Router).createUrlTree(['/reports/bookkeeping'], { queryParams })
  },
  {
    path: 'reports/bookkeeping',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () => import('./components/reports/bookkeeping-report.component').then((m) => m.BookkeepingReportComponent)
  },
  {
    // Daily, Transactions, Reconciliation, Machines and Products carry `requireProvider` since
    // issue #868, which supersedes decision P5 for exactly these five at the user's request: a
    // business with no Nayax connection at all sees the connection-required page here, as it
    // already did on Site Commissions. The feature is `providerRecords`, the narrowest dependency
    // there is - these reports read **stored** sales, so a connection whose read Nayax refused, and
    // credentials that stopped working, leave them open, and the history they report on is never
    // deleted. Bookkeeping and GST carry no provider guard at all. Like every provider guard it is
    // declared **after** `requireCapability`, so a role without `Reports.View` reaches its own
    // explanation rather than an invitation to connect a provider it still could not use.
    path: 'reports/daily',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View'), requireProvider('providerRecords')],
    loadComponent: () => import('./components/reports/daily-report.component').then((m) => m.DailyReportComponent)
  },
  {
    path: 'reports/transactions',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View'), requireProvider('providerRecords')],
    loadComponent: () =>
      import('./components/reports/transaction-sales-report.component').then((m) => m.TransactionSalesReportComponent)
  },
  {
    path: 'reports/reconciliation',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View'), requireProvider('providerRecords')],
    loadComponent: () =>
      import('./components/reports/reconciliation-report.component').then((m) => m.ReconciliationReportComponent)
  },
  {
    path: 'reports/machines',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View'), requireProvider('providerRecords')],
    loadComponent: () => import('./components/reports/machine-report.component').then((m) => m.MachineReportComponent)
  },
  {
    path: 'reports/products',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View'), requireProvider('providerRecords')],
    loadComponent: () => import('./components/reports/product-report.component').then((m) => m.ProductReportComponent)
  },
  {
    path: 'reports/gst',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () => import('./components/reports/gst-report.component').then((m) => m.GstReportComponent)
  },
  {
    // The site commission report is on `SiteCommissions.Manage`, not `Reports.View`: it is the
    // surface the agreements are maintained from, and the API draws the same line (issue #526).
    //
    // It is also the one report that is not a read of stored data: a site is known only from the
    // live fleet read, so with no usable provider it has no sites to report on and the API answers
    // `isAvailable: false` (issue #731). Issue #729 therefore locked it on the live fleet read,
    // which issue #868 left exactly as it is - it is the five reports over stored records that
    // joined it, on the narrower `providerRecords`.
    path: 'reports/site-commissions',
    canActivate: [
      MsalGuard,
      accountStateGuard,
      requireCapability('SiteCommissions.Manage'),
      requireProvider('machineFleet')
    ],
    loadComponent: () =>
      import('./components/reports/site-commissions-report.component').then((m) => m.SiteCommissionsReportComponent)
  },
  {
    path: 'pick-list',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('PickList.View')],
    loadComponent: () => import('./components/pick-list/pick-list.component').then((m) => m.PickListComponent)
  },
  {
    path: 'take-inventory',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/take-inventory/take-inventory.component').then((m) => m.TakeInventoryComponent)
  },
  {
    // The Admin link hub owns no workflow of its own, so it is offered to a member who holds any
    // of the capabilities its pages need rather than to one particular role.
    path: 'admin',
    canActivate: [
      MsalGuard,
      accountStateGuard,
      requireCapability('Imports.Run', 'SiteCommissions.Manage', 'Integration.Manage', 'Data.Repair')
    ],
    loadComponent: () => import('./components/admin/admin.component').then((m) => m.AdminComponent)
  },
  {
    path: 'admin/imports',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Imports.Run')],
    loadComponent: () => import('./components/admin/imports/admin-imports.component').then((m) => m.AdminImportsComponent)
  },
  {
    path: 'admin/historical-cost-recovery',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Data.Repair')],
    loadComponent: () =>
      import('./components/admin/historical-cost-recovery/historical-cost-recovery.component').then(
        (m) => m.HistoricalCostRecoveryComponent
      )
  },
  {
    path: 'admin/avco-transition',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Data.Repair')],
    loadComponent: () =>
      import('./components/admin/avco-transition/avco-transition.component').then((m) => m.AvcoTransitionComponent)
  },
  {
    path: 'admin/costing-repair',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Data.Repair')],
    loadComponent: () =>
      import('./components/admin/costing-repair/costing-repair-page.component').then((m) => m.CostingRepairPageComponent)
  },
  {
    path: 'admin/historical-gst-classification',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Data.Repair')],
    loadComponent: () =>
      import('./components/admin/historical-gst-classification/historical-gst-classification.component').then(
        (m) => m.HistoricalGstClassificationComponent
      )
  },
  {
    path: 'admin/nayax-sale-timestamp-repair',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Data.Repair')],
    loadComponent: () =>
      import('./components/admin/nayax-sale-timestamp-repair/nayax-sale-timestamp-repair.component').then(
        (m) => m.NayaxSaleTimestampRepairComponent
      )
  },
  {
    // The super-admin diagnostics page (issue #335), moved under /super-admin in issue #546.
    // `platformAdminGuard` asks the same platform-admin check the page itself asks on arrival and
    // redirects a refusal to the dashboard; entering this URL directly is still safe either way,
    // because the diagnostics API decides authority per request regardless of what the guard did.
    path: 'super-admin/diagnostics',
    canActivate: [MsalGuard, platformAdminGuard],
    loadComponent: () =>
      import('./components/admin/diagnostics/platform-diagnostics.component').then((m) => m.PlatformDiagnosticsComponent)
  },
  {
    // The old address (issue #335) redirects to its new home (issue #546) so existing bookmarks
    // and links keep working.
    path: 'admin/diagnostics',
    pathMatch: 'full',
    redirectTo: '/super-admin/diagnostics'
  },
  {
    // The Super Admin Businesses page (issue #546's placeholder, built in issue #547): approve,
    // reject, deactivate and reactivate a business, and release a member of a deactivated one.
    // Platform-admin authority, not a business role, decides it - the same `platformAdminGuard`
    // as the diagnostics route above, and for the same reason.
    path: 'super-admin/businesses',
    canActivate: [MsalGuard, platformAdminGuard],
    loadComponent: () =>
      import('./components/super-admin/businesses/super-admin-businesses.component').then(
        (m) => m.SuperAdminBusinessesComponent
      )
  },
  {
    // The Super Admin Supplier presets page (issue #848): the one platform-wide list of suggested
    // suppliers, which #835 child C copies into each newly approved business. Platform-admin
    // authority, not a business role, decides it - the same `platformAdminGuard` as the two
    // super-admin routes above, and for the same reason.
    path: 'super-admin/supplier-presets',
    canActivate: [MsalGuard, platformAdminGuard],
    loadComponent: () =>
      import('./components/super-admin/supplier-presets/super-admin-supplier-presets.component').then(
        (m) => m.SuperAdminSupplierPresetsComponent
      )
  },
  {
    path: 'admin/nayax-settings',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Integration.Manage')],
    loadComponent: () =>
      import('./components/admin/nayax-settings/nayax-settings.component').then((m) => m.NayaxSettingsComponent)
  },
  {
    path: 'admin/site-commission-agreements',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('SiteCommissions.Manage')],
    loadComponent: () =>
      import('./components/admin/site-commission-agreements/site-commission-agreements.component').then(
        (m) => m.SiteCommissionAgreementsComponent
      )
  },
  {
    // The Admin → Users page (issue #534): members and invite links, Owner-only.
    path: 'admin/users',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Members.Manage')],
    loadComponent: () => import('./components/admin/users/admin-users.component').then((m) => m.AdminUsersComponent)
  },
  {
    // The Admin → Roles page (issue #530): a read-only explanation of what each role can access,
    // Owner-only.
    path: 'admin/roles',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Roles.View')],
    loadComponent: () => import('./components/admin/roles/roles.component').then((m) => m.RolesComponent)
  },
  {
    // The Admin → Business Settings page (issue #549): the business's own name, contact email,
    // ABN, GST registration and time zone, Owner-only.
    path: 'admin/business-settings',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Business.Manage')],
    loadComponent: () =>
      import('./components/admin/business-settings/business-settings.component').then(
        (m) => m.BusinessSettingsComponent
      )
  },
  {
    path: 'expenses',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Expenses.Manage')],
    loadComponent: () => import('./components/expenses/operating-expense.component').then((m) => m.OperatingExpenseComponent)
  },
  // Empty in every optimized build; the unoptimized dev/e2e servers add the #411 screenshot
  // fixture. Must stay above the wildcard, which would otherwise swallow it.
  ...designSystemRoutes,
  { path: '**', redirectTo: '' }
];
