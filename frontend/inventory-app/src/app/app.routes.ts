import { Routes } from '@angular/router';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { ProductFormComponent } from './components/products/product-form.component';
import { CategoryListComponent } from './components/categories/category-list.component';
import { SupplierListComponent } from './components/suppliers/supplier-list.component';
import { StockHistoryComponent } from './components/stock/stock-history.component';
import { PurchaseListComponent } from './components/purchases/purchase-list.component';
import { PurchaseUploadComponent } from './components/purchases/purchase-upload.component';
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
  { path: '', component: DashboardComponent },
  { path: 'products/:id/edit', component: ProductFormComponent },
  { path: 'products/:id/stock', component: StockHistoryComponent },
  {
    path: 'products',
    canActivate: [productsLegacyRouteGuard],
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
  { path: 'categories', component: CategoryListComponent },
  { path: 'suppliers', component: SupplierListComponent },
  { path: 'machines/:id', component: MachineDetailComponent },
  { path: 'sites/:id/products', component: SiteProductsComponent },
  { path: 'purchases', component: PurchaseListComponent },
  { path: 'purchases/new', component: PurchaseUploadComponent },
  // Backward-compatible aliases for bookmarks/links to the old "/receipts" route.
  { path: 'receipts', pathMatch: 'full', redirectTo: 'purchases' },
  { path: 'receipts/new', pathMatch: 'full', redirectTo: 'purchases/new' },
  { path: 'reports', component: DashboardReportComponent },
  { path: 'reports/bookkeeping', component: BookkeepingReportComponent },
  { path: 'reports/daily', component: DailyReportComponent },
  { path: 'reports/transactions', component: TransactionSalesReportComponent },
  { path: 'reports/reconciliation', component: ReconciliationReportComponent },
  { path: 'reports/machines', component: MachineReportComponent },
  { path: 'reports/products', component: ProductReportComponent },
  { path: 'reports/gst', component: GstReportComponent },
  { path: 'reports/site-commissions', component: SiteCommissionsReportComponent },
  { path: 'admin', component: AdminComponent },
  { path: 'expenses', component: OperatingExpenseComponent },
  { path: '**', redirectTo: '' }
];
