import { Routes } from '@angular/router';
import { MsalGuard } from '@azure/msal-angular';
import { ACCESS_NOT_ALLOWED_PATH, requireCapability } from './access/capability.guard';
import { BUSINESS_APPLICATION_ROUTE, accountStateGuard } from './access/account-state.guard';
import { platformAdminGuard } from './access/platform-admin.guard';
import { AccessUnavailableComponent } from './access/access-unavailable.component';
import { WelcomeComponent } from './access/welcome.component';
import { ApplicationPendingComponent } from './access/application-pending.component';
import { ApplicationRejectedComponent } from './access/application-rejected.component';
import { BusinessApplicationComponent } from './access/business-application.component';
import { AuthCallbackComponent } from './auth/auth-callback.component';
import { designSystemRoutes } from './design-system/design-system.routes';

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
 * `/application-rejected`, `/access-unavailable`, `/apply` and `/invite/:token` - because a person already
 * sent to one of them must not be sent away from it; `/access-not-allowed` carries no capability
 * for the reason already documented beside it, and `/super-admin/diagnostics` (issue #546) carries
 * neither guard: its authority is the platform-admin check, not a business role or membership, so
 * it carries `platformAdminGuard` instead and sends a refusal to the dashboard rather than to any
 * membership-handling destination (see `access/platform-admin.guard.ts`).
 */
export const routes: Routes = [
  // Public: the Entra redirect callback must be reachable without authentication.
  { path: 'auth', component: AuthCallbackComponent },
  {
    // Reached by a guard above, so it declares no capability of its own: a member sent here
    // because their role does not allow a page must not be sent away from the explanation too.
    path: ACCESS_NOT_ALLOWED_PATH,
    canActivate: [MsalGuard],
    loadComponent: () => import('./access/access-not-allowed.component').then((m) => m.AccessNotAllowedComponent)
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
    // The invite acceptance page (issue #535): reads the token, strips it from the address bar,
    // and lets a NoMembership/MembershipRevoked/ApplicationRejected caller redeem it. Carries only
    // `MsalGuard`, for the reason the comment above gives.
    path: 'invite/:token',
    canActivate: [MsalGuard],
    loadComponent: () => import('./access/invite-acceptance.component').then((m) => m.InviteAcceptanceComponent)
  },
  {
    path: '',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Dashboard.View')],
    loadComponent: () => import('./components/dashboard/dashboard.component').then((m) => m.DashboardComponent)
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
    path: 'machines',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/machines/machine-list.component').then((m) => m.MachineListComponent)
  },
  {
    path: 'machines/:id',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/machines/machine-detail.component').then((m) => m.MachineDetailComponent)
  },
  {
    path: 'sites',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
    loadComponent: () => import('./components/sites/site-list.component').then((m) => m.SiteListComponent)
  },
  {
    path: 'sites/:id/products',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Inventory.Operate')],
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
    path: 'reports',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () => import('./components/reports/dashboard-report.component').then((m) => m.DashboardReportComponent)
  },
  {
    path: 'reports/bookkeeping',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () => import('./components/reports/bookkeeping-report.component').then((m) => m.BookkeepingReportComponent)
  },
  {
    path: 'reports/daily',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () => import('./components/reports/daily-report.component').then((m) => m.DailyReportComponent)
  },
  {
    path: 'reports/transactions',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () =>
      import('./components/reports/transaction-sales-report.component').then((m) => m.TransactionSalesReportComponent)
  },
  {
    path: 'reports/reconciliation',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () =>
      import('./components/reports/reconciliation-report.component').then((m) => m.ReconciliationReportComponent)
  },
  {
    path: 'reports/machines',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
    loadComponent: () => import('./components/reports/machine-report.component').then((m) => m.MachineReportComponent)
  },
  {
    path: 'reports/products',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('Reports.View')],
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
    path: 'reports/site-commissions',
    canActivate: [MsalGuard, accountStateGuard, requireCapability('SiteCommissions.Manage')],
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
