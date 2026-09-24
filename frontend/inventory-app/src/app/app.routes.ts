import { Routes } from '@angular/router';
import { MsalGuard } from '@azure/msal-angular';
import { AuthCallbackComponent } from './auth/auth-callback.component';

export const routes: Routes = [
  // Public: the Entra redirect callback must be reachable without authentication.
  { path: 'auth', component: AuthCallbackComponent },
  {
    path: '',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/dashboard/dashboard.component').then((m) => m.DashboardComponent)
  },
  {
    path: 'products/:id/edit',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/products/product-form.component').then((m) => m.ProductFormComponent)
  },
  {
    path: 'products/:id/stock',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/stock/stock-history.component').then((m) => m.StockHistoryComponent)
  },
  {
    path: 'products',
    canActivate: [MsalGuard],
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
        path: 'on-order',
        loadComponent: () => import('./components/products/product-on-order.component').then((m) => m.ProductOnOrderComponent)
      }
    ]
  },
  {
    path: 'categories',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/categories/category-list.component').then((m) => m.CategoryListComponent)
  },
  {
    path: 'suppliers',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/suppliers/supplier-list.component').then((m) => m.SupplierListComponent)
  },
  {
    path: 'machines/:id',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/machines/machine-detail.component').then((m) => m.MachineDetailComponent)
  },
  {
    path: 'sites/:id/products',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/sites/site-products.component').then((m) => m.SiteProductsComponent)
  },
  {
    path: 'purchases',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/purchases/purchase-list.component').then((m) => m.PurchaseListComponent)
  },
  {
    path: 'purchases/new',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/purchases/purchase-upload.component').then((m) => m.PurchaseUploadComponent)
  },
  {
    path: 'reports',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/reports/dashboard-report.component').then((m) => m.DashboardReportComponent)
  },
  {
    path: 'reports/bookkeeping',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/reports/bookkeeping-report.component').then((m) => m.BookkeepingReportComponent)
  },
  {
    path: 'reports/daily',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/reports/daily-report.component').then((m) => m.DailyReportComponent)
  },
  {
    path: 'reports/transactions',
    canActivate: [MsalGuard],
    loadComponent: () =>
      import('./components/reports/transaction-sales-report.component').then((m) => m.TransactionSalesReportComponent)
  },
  {
    path: 'reports/reconciliation',
    canActivate: [MsalGuard],
    loadComponent: () =>
      import('./components/reports/reconciliation-report.component').then((m) => m.ReconciliationReportComponent)
  },
  {
    path: 'reports/machines',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/reports/machine-report.component').then((m) => m.MachineReportComponent)
  },
  {
    path: 'reports/products',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/reports/product-report.component').then((m) => m.ProductReportComponent)
  },
  {
    path: 'reports/gst',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/reports/gst-report.component').then((m) => m.GstReportComponent)
  },
  {
    path: 'reports/site-commissions',
    canActivate: [MsalGuard],
    loadComponent: () =>
      import('./components/reports/site-commissions-report.component').then((m) => m.SiteCommissionsReportComponent)
  },
  {
    path: 'admin',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/admin/admin.component').then((m) => m.AdminComponent)
  },
  {
    path: 'expenses',
    canActivate: [MsalGuard],
    loadComponent: () => import('./components/expenses/operating-expense.component').then((m) => m.OperatingExpenseComponent)
  },
  { path: '**', redirectTo: '' }
];
