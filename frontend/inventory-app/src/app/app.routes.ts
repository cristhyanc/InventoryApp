import { Routes } from '@angular/router';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { ProductListComponent } from './components/products/product-list.component';
import { ProductFormComponent } from './components/products/product-form.component';
import { CategoryListComponent } from './components/categories/category-list.component';
import { SupplierListComponent } from './components/suppliers/supplier-list.component';
import { StockHistoryComponent } from './components/stock/stock-history.component';
import { ReceiptListComponent } from './components/receipts/receipt-list.component';
import { ReceiptUploadComponent } from './components/receipts/receipt-upload.component';

export const routes: Routes = [
  { path: '', component: DashboardComponent },
  { path: 'products', component: ProductListComponent },
  { path: 'products/new', component: ProductFormComponent },
  { path: 'products/:id/edit', component: ProductFormComponent },
  { path: 'products/:id/stock', component: StockHistoryComponent },
  { path: 'categories', component: CategoryListComponent },
  { path: 'suppliers', component: SupplierListComponent },
  { path: 'receipts', component: ReceiptListComponent },
  { path: 'receipts/new', component: ReceiptUploadComponent },
  { path: '**', redirectTo: '' }
];
