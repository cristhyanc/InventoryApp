import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink, ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { switchMap, tap, map, catchError } from 'rxjs/operators';
import { PurchaseService } from '../../services/purchase.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { GstClassification, Product, Supplier, SupplierOrder } from '../../models/models';
import { PurchaseItemPayload } from '../../services/purchase.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { GST_CLASSIFICATION_OPTIONS, isChargePresent } from './gst-classification-options';

// Draft item representation where unitCost may be null
interface DraftPurchaseItem {
  productId: number;
  quantity: number;
  unitCost: number | null;
  /**
   * This line's GST classification (issue #431). A new line starts as `Unknown`/Not classified and
   * stays that way unless a person picks something: nothing here pre-fills a classification from the
   * product, the supplier or the amount (parent issue #62, decision D4).
   */
  gstClassification: GstClassification;
}

@Component({
  selector: 'app-purchase-upload',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './purchase-upload.component.html'
})
export class PurchaseUploadComponent implements OnInit {
  suppliers: Supplier[] = [];
  products: Product[] = [];
  items: DraftPurchaseItem[] = [];
  selectedFile: File | null = null;
  previewUrl: string | null = null;
  saving = false;
  error = '';

  supplierOrderId: number | null = null;
  supplierOrder: SupplierOrder | null = null;
  loadingSupplierOrder = false;
  noOutstandingItems = false;
  supplierOrderLoadFailed = false;

  /** The GST picker's options, shared with the purchase edit form. */
  readonly gstOptions = GST_CLASSIFICATION_OPTIONS;

  form = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    deliveryGstClassification: GstClassification.Unknown,
    packageCost: null as number | null,
    packageGstClassification: GstClassification.Unknown,
    purchaseDate: PurchaseUploadComponent.localDate(new Date()),
    supplierId: '' as number | ''
  };

  constructor(
    private purchaseService: PurchaseService,
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
      deliveryGstClassification: GstClassification.Unknown,
      packageCost: null,
      packageGstClassification: GstClassification.Unknown,
      purchaseDate: PurchaseUploadComponent.localDate(new Date()),
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
    this.form.purchaseDate = PurchaseUploadComponent.localDate(new Date());

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

    // Use draft items with nullable unitCost - do NOT convert null to 0. The GST classification
    // stays Not classified: a supplier order says nothing about a line's GST status.
    this.items = outstandingLines.map((line) => ({
      productId: line.productId,
      quantity: line.outstandingQuantity,
      unitCost: line.unitPrice ?? null,
      gstClassification: GstClassification.Unknown
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

    // Map draft items to PurchaseItemPayload only after validation
    const validItems: PurchaseItemPayload[] = this.items.map(item => ({
      productId: item.productId,
      quantity: item.quantity,
      unitCost: item.unitCost as number,  // Safe to cast after validation
      gstClassification: PurchaseUploadComponent.submittedClassification(item.gstClassification)
    }));

    this.purchaseService
      .upload({
        file: this.selectedFile,
        title: this.form.title.trim(),
        notes: this.form.notes || null,
        totalAmount: this.form.totalAmount,
        deliveryCost: this.form.deliveryCost,
        deliveryGstClassification: this.chargeClassification(
          this.form.deliveryCost, this.form.deliveryGstClassification),
        packageCost: this.form.packageCost,
        packageGstClassification: this.chargeClassification(
          this.form.packageCost, this.form.packageGstClassification),
        purchaseDate: this.purchaseTimestamp(),
        supplierId: this.form.supplierId === '' ? null : this.form.supplierId,
        items: validItems
      })
      .subscribe({
        next: () => {
          if (this.supplierOrderId) {
            this.toastService.success('Purchase created from supplier order.');
            this.router.navigate(['/purchases/orders']);
          } else {
            this.toastService.success('Purchase created.');
            this.router.navigate(['/purchases']);
          }
        },
        error: (err) => {
          this.error = err?.error ?? 'Failed to add purchase.';
          this.saving = false;
        }
      });
  }

  addItem(): void {
    this.items.push({
      productId: this.products[0]?.id ?? 0,
      quantity: 1,
      unitCost: null,
      gstClassification: GstClassification.Unknown
    });
  }
  removeItem(index: number): void { this.items.splice(index, 1); }

  /**
   * Whether a delivery or package charge exists, which is what decides whether its GST picker is
   * shown. An absent charge has no classification and is never unresolved, so offering a picker for
   * one would invite a choice the server would correctly discard.
   */
  hasCharge(amount: number | null): boolean { return isChargePresent(amount); }

  /**
   * The classification to submit for a delivery or package charge: nothing at all unless the charge
   * exists and a person actually picked a state for it.
   */
  private chargeClassification(
    amount: number | null, classification: GstClassification): GstClassification | undefined {
    return isChargePresent(amount) ? PurchaseUploadComponent.submittedClassification(classification) : undefined;
  }

  /**
   * What a new purchase submits for one component. `Unknown` is left out of the request rather than
   * sent: it is already the server's state for a component nobody classified, and omitting it keeps
   * the request free of choices the person did not make.
   */
  private static submittedClassification(classification: GstClassification): GstClassification | undefined {
    return classification === GstClassification.Unknown ? undefined : classification;
  }
  itemProduct(item: DraftPurchaseItem): Product | undefined { return this.products.find(p => p.id === Number(item.productId)); }
  lineTotal(item: DraftPurchaseItem): number { return Number(item.quantity || 0) * Number(item.unitCost || 0); }
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
