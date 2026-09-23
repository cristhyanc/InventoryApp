import { Routes } from '@angular/router';
import { MsalGuard } from '@azure/msal-angular';
import { AuthCallbackComponent } from './auth/auth-callback.component';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { ProductFormComponent } from './components/products/product-form.component';
import { CategoryListComponent } from './components/categories/category-list.component';
import { SupplierListComponent } from './components/suppliers/supplier-list.component';
import { StockHistoryComponent } from './components/stock/stock-history.component';
import { ReceiptListComponent } from './components/receipts/receipt-list.component';
import { ReceiptUploadComponent } from './components/receipts/receipt-upload.component';
import { MachineDetailComponent } from './components/machines/machine-detail.component';
import { SiteProductsComponent } from './components/sites/site-products.component';
import { DashboardReportComponent } from './components/reports/dashboard-report.component';
import { BookkeepingReportComponent } from './components/reports/bookkeeping-report.component';
import { DailyReportComponent } from './components/reports/daily-report.component';
import { ReconciliationReportComponent } from './components/reports/reconciliation-report.component';
import { MachineReportComponent } from './components/reports/machine-report.component';
import { ProductReportComponent } from './components/reports/product-report.component';
import { GstReportComponent } from './components/reports/gst-report.component';
import { AdminComponent } from './components/admin/admin.component';
import { OperatingExpenseComponent } from './components/expenses/operating-expense.component';
import { SiteCommissionsReportComponent } from './components/reports/site-commissions-report.component';
import { TransactionSalesReportComponent } from './components/reports/transaction-sales-report.component';
import { productsLegacyRouteGuard } from './components/products/products-legacy-route.guard';

export const routes: Routes = [
  // Public: the Entra redirect callback must be reachable without authentication.
  { path: 'auth', component: AuthCallbackComponent },
  { path: '', component: DashboardComponent, canActivate: [MsalGuard] },
  { path: 'products/:id/edit', component: ProductFormComponent, canActivate: [MsalGuard] },
  { path: 'products/:id/stock', component: StockHistoryComponent, canActivate: [MsalGuard] },
  {
    path: 'products',
    canActivate: [MsalGuard, productsLegacyRouteGuard],
    // Re-run the legacy guard on query-param-only changes; the reused shell route would otherwise skip it.
    runGuardsAndResolvers: 'paramsOrQueryParamsChange',
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
  { path: 'categories', component: CategoryListComponent, canActivate: [MsalGuard] },
  { path: 'suppliers', component: SupplierListComponent, canActivate: [MsalGuard] },
  { path: 'machines/:id', component: MachineDetailComponent, canActivate: [MsalGuard] },
  { path: 'sites/:id/products', component: SiteProductsComponent, canActivate: [MsalGuard] },
  { path: 'receipts', component: ReceiptListComponent, canActivate: [MsalGuard] },
  { path: 'receipts/new', component: ReceiptUploadComponent, canActivate: [MsalGuard] },
  { path: 'reports', component: DashboardReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/bookkeeping', component: BookkeepingReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/daily', component: DailyReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/transactions', component: TransactionSalesReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/reconciliation', component: ReconciliationReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/machines', component: MachineReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/products', component: ProductReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/gst', component: GstReportComponent, canActivate: [MsalGuard] },
  { path: 'reports/site-commissions', component: SiteCommissionsReportComponent, canActivate: [MsalGuard] },
  { path: 'admin', component: AdminComponent, canActivate: [MsalGuard] },
  { path: 'expenses', component: OperatingExpenseComponent, canActivate: [MsalGuard] },
  { path: '**', redirectTo: '' }
];
