import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink, ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { switchMap, tap, map, catchError } from 'rxjs/operators';
import { ReceiptService } from '../../services/receipt.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { Product, Supplier, SupplierOrder } from '../../models/models';
import { ReceiptItemPayload } from '../../services/receipt.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';

// Draft item representation where unitCost may be null
interface DraftReceiptItem {
  productId: number;
  quantity: number;
  unitCost: number | null;
}

@Component({
  selector: 'app-receipt-upload',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './receipt-upload.component.html'
})
export class ReceiptUploadComponent implements OnInit {
  suppliers: Supplier[] = [];
  products: Product[] = [];
  items: DraftReceiptItem[] = [];
  selectedFile: File | null = null;
  previewUrl: string | null = null;
  saving = false;
  error = '';
  
  supplierOrderId: number | null = null;
  supplierOrder: SupplierOrder | null = null;
  loadingSupplierOrder = false;
  noOutstandingItems = false;
  supplierOrderLoadFailed = false;

  form = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    packageCost: null as number | null,
    purchaseDate: ReceiptUploadComponent.localDate(new Date()),
    supplierId: '' as number | ''
  };

  constructor(
    private receiptService: ReceiptService,
    private supplierService: SupplierService,
    private supplierOrderService: SupplierOrderService,
    private productService: ProductService,
    private route: ActivatedRoute,
    private router: Router,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
    this.productService.getAll().subscribe((p) => (this.products = p));

    // switchMap cancels any in-flight supplier order request whenever the
    // id changes, so a stale response can never overwrite a newer one.
    this.route.queryParamMap
      .pipe(
        map((params) => {
          const raw = params.get('supplierOrderId');
          return raw ? Number(raw) : null;
        }),
        tap(() => this.resetPurchaseForm()),
        switchMap((id) => {
          if (id === null) {
            return of(null);
          }
          this.loadingSupplierOrder = true;
          return this.supplierOrderService.getById(id).pipe(
            map((order) => ({ id, order })),
            catchError(() => of({ id, order: null }))
          );
        })
      )
      .subscribe((result) => {
        if (result === null) {
          return;
        }

        this.loadingSupplierOrder = false;
        this.supplierOrderId = result.id;

        if (result.order) {
          this.supplierOrder = result.order;
          this.prefillFromSupplierOrder(result.order);
        } else {
          this.supplierOrderLoadFailed = true;
          this.supplierOrder = null;
        }
      });
  }

  // Restores a fresh, empty Create Purchase form (manual-purchase defaults).
  private resetPurchaseForm(): void {
    this.items = [];
    this.selectedFile = null;
    this.previewUrl = null;
    this.error = '';
    this.saving = false;

    this.form = {
      title: '',
      notes: '',
      totalAmount: null,
      deliveryCost: null,
      packageCost: null,
      purchaseDate: ReceiptUploadComponent.localDate(new Date()),
      supplierId: ''
    };

    this.supplierOrderId = null;
    this.supplierOrder = null;
    this.loadingSupplierOrder = false;
    this.noOutstandingItems = false;
    this.supplierOrderLoadFailed = false;
  }

  private prefillFromSupplierOrder(order: SupplierOrder): void {
    // Prefill supplier
    if (order.supplierId) {
      this.form.supplierId = order.supplierId;
    }

    // Set purchase date to today
    this.form.purchaseDate = ReceiptUploadComponent.localDate(new Date());

    // Prefill reference/notes with order information
    if (order.reference) {
      this.form.notes = `Supplier Order #${order.id}: ${order.reference}`;
    } else {
      this.form.notes = `Supplier Order #${order.id}`;
    }

    // Prefill items from outstanding quantities only
    const outstandingLines = order.lines.filter((line) => line.outstandingQuantity > 0);

    if (outstandingLines.length === 0) {
      this.noOutstandingItems = true;
      return;
    }

    // Use draft items with nullable unitCost - do NOT convert null to 0
    this.items = outstandingLines.map((line) => ({
      productId: line.productId,
      quantity: line.outstandingQuantity,
      unitCost: line.unitPrice ?? null
    }));
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.selectedFile = input.files[0];
      if (!this.form.title) this.form.title = this.selectedFile.name;

      if (this.selectedFile.type.startsWith('image/')) {
        const reader = new FileReader();
        reader.onload = () => (this.previewUrl = reader.result as string);
        reader.readAsDataURL(this.selectedFile);
      } else {
        this.previewUrl = null;
      }
    }
  }

  upload(): void {
    if (this.loadingSupplierOrder) {
      this.error = 'Please wait while the supplier order is loading.';
      return;
    }

    if (this.supplierOrderLoadFailed) {
      this.error = 'Unable to load supplier order.';
      return;
    }

    if (this.noOutstandingItems) {
      this.error = 'This supplier order has no outstanding items to receive.';
      return;
    }

    if (!this.selectedFile) {
      this.error = 'Please select a receipt or invoice (PDF/image).';
      return;
    }
    if (!this.form.title.trim()) {
      this.error = 'Please enter a purchase title.';
      return;
    }

    // Validate that all items have a valid unitCost
    const invalidItems = this.items.filter(item => item.unitCost === null || item.unitCost === undefined || item.unitCost < 0);
    if (invalidItems.length > 0) {
      this.error = `Please enter a unit cost for all items. ${invalidItems.length} item(s) have missing or invalid cost.`;
      return;
    }

    this.error = '';
    this.saving = true;

    // Map draft items to ReceiptItemPayload only after validation
    const validItems: ReceiptItemPayload[] = this.items.map(item => ({
      productId: item.productId,
      quantity: item.quantity,
      unitCost: item.unitCost as number  // Safe to cast after validation
    }));

    this.receiptService
      .upload({
        file: this.selectedFile,
        title: this.form.title.trim(),
        notes: this.form.notes || null,
        totalAmount: this.form.totalAmount,
        deliveryCost: this.form.deliveryCost,
        packageCost: this.form.packageCost,
        purchaseDate: this.purchaseTimestamp(),
        supplierId: this.form.supplierId === '' ? null : this.form.supplierId,
        items: validItems
      })
      .subscribe({
        next: () => {
          if (this.supplierOrderId) {
            this.toastService.success('Purchase created from supplier order.');
            this.router.navigate(['/products/on-order']);
          } else {
            this.toastService.success('Purchase created.');
            this.router.navigate(['/receipts']);
          }
        },
        error: (err) => {
          this.error = err?.error ?? 'Failed to add purchase.';
          this.saving = false;
        }
      });
  }

  addItem(): void { this.items.push({ productId: this.products[0]?.id ?? 0, quantity: 1, unitCost: null }); }
  removeItem(index: number): void { this.items.splice(index, 1); }
  itemProduct(item: DraftReceiptItem): Product | undefined { return this.products.find(p => p.id === Number(item.productId)); }
  lineTotal(item: DraftReceiptItem): number { return Number(item.quantity || 0) * Number(item.unitCost || 0); }
  get itemsSubtotal(): number { return this.items.reduce((sum, item) => sum + this.lineTotal(item), 0); }

  private purchaseTimestamp(): string | null {
    if (!this.form.purchaseDate) return null;
    const selected = new Date(`${this.form.purchaseDate}T00:00:00`);
    const now = new Date();
    if (selected.getFullYear() === now.getFullYear() &&
        selected.getMonth() === now.getMonth() &&
        selected.getDate() === now.getDate()) {
      return now.toISOString();
    }
    return selected.toISOString();
  }

  private static localDate(value: Date): string {
    const year = value.getFullYear();
    const month = String(value.getMonth() + 1).padStart(2, '0');
    const day = String(value.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
