import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ReceiptService } from '../../services/receipt.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Receipt, Supplier, ReceiptValidation } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { ReceiptItemPayload } from '../../services/receipt.service';
import { ObjectUrlCache } from '../shared/object-url-cache';

@Component({
  selector: 'app-receipt-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './receipt-list.component.html'
})
export class ReceiptListComponent implements OnInit, OnDestroy {
  receipts: Receipt[] = [];
  suppliers: Supplier[] = [];
  products: Product[] = [];
  editItems: ReceiptItemPayload[] = [];
  editingReceiptId: number | null = null;
  editForm = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    packageCost: null as number | null,
    purchaseDate: '',
    supplierId: '' as number | ''
  };

  // Receipt documents are protected by the API, so they are fetched through HttpClient (which
  // attaches the bearer token) and rendered from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  private thumbnailUrls = new Map<number, string>();

  constructor(
    private receiptService: ReceiptService,
    private supplierService: SupplierService,
    private productService: ProductService
  ) {}

  ngOnInit(): void {
    this.load();
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
    this.productService.getAll().subscribe((p) => (this.products = p));
  }

  ngOnDestroy(): void {
    this.objectUrls.releaseAll();
  }

  load(): void {
    this.receiptService.getAll().subscribe((r) => {
      this.receipts = r;
      this.loadThumbnails();
    });
  }

  getValidation(receipt: Receipt): ReceiptValidation | null {
    return this.receiptService.getValidationFor(receipt.id);
  }

  thumbnailUrl(receipt: Receipt): string | null {
    return this.thumbnailUrls.get(receipt.id) ?? null;
  }

  openDocument(receipt: Receipt): void {
    this.receiptService.getFile(receipt.id).subscribe({
      next: (blob) => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: (err) => console.error('Failed to open purchase document', err)
    });
  }

  private loadThumbnails(): void {
    this.objectUrls.releaseAll();
    this.thumbnailUrls = new Map<number, string>();
    this.receipts
      .filter((receipt) => this.isImage(receipt))
      .forEach((receipt) =>
        this.receiptService.getFile(receipt.id).subscribe({
          next: (blob) => this.thumbnailUrls.set(receipt.id, this.objectUrls.create(blob)),
          error: (err) => console.error('Failed to load purchase document', err)
        })
      );
  }

  isImage(receipt: Receipt): boolean {
    return receipt.contentType.startsWith('image/');
  }

  startEdit(receipt: Receipt): void {
    this.editingReceiptId = receipt.id;
    this.editForm = {
      title: receipt.title,
      notes: receipt.notes ?? '',
      totalAmount: receipt.totalAmount ?? null,
      deliveryCost: receipt.deliveryCost ?? null,
      packageCost: receipt.packageCost ?? null,
      purchaseDate: receipt.purchaseDate ? this.localDate(new Date(receipt.purchaseDate)) : '',
      supplierId: receipt.supplierId ?? ''
    };
    this.editItems = (receipt.items ?? []).map(item => ({
      productId: item.productId, quantity: item.quantity, unitCost: item.unitCost
    }));
  }

  cancelEdit(): void {
    this.editingReceiptId = null;
  }

  saveEdit(receipt: Receipt): void {
    if (!this.editForm.title.trim()) return;

    this.receiptService
      .update(receipt.id, {
        title: this.editForm.title.trim(),
        notes: this.editForm.notes || null,
        totalAmount: this.editForm.totalAmount,
        deliveryCost: this.editForm.deliveryCost,
        packageCost: this.editForm.packageCost,
        purchaseDate: this.editPurchaseTimestamp(receipt),
        supplierId: this.editForm.supplierId === '' ? null : this.editForm.supplierId
        , items: this.editItems
      })
      .subscribe({
        next: () => {
          this.editingReceiptId = null;
          this.load();
        },
        error: (err) => console.error('Failed to update receipt', err)
      });
  }

  addEditItem(): void { this.editItems.push({ productId: this.products[0]?.id ?? 0, quantity: 1, unitCost: 0 }); }
  removeEditItem(index: number): void { this.editItems.splice(index, 1); }
  editLineTotal(item: ReceiptItemPayload): number { return Number(item.quantity || 0) * Number(item.unitCost || 0); }
  get editItemsSubtotal(): number { return this.editItems.reduce((sum, item) => sum + this.editLineTotal(item), 0); }

  remove(receipt: Receipt): void {
    if (!confirm(`Delete purchase "${receipt.title}"?`)) return;
    this.receiptService.delete(receipt.id).subscribe(() => this.load());
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  private editPurchaseTimestamp(receipt: Receipt): string | null {
    if (!this.editForm.purchaseDate) return null;
    const original = new Date(receipt.purchaseDate);
    if (this.editForm.purchaseDate === this.localDate(original))
      return original.toISOString();
    const selected = new Date(`${this.editForm.purchaseDate}T00:00:00`);
    const now = new Date();
    if (this.editForm.purchaseDate === this.localDate(now))
      return now.toISOString();
    return selected.toISOString();
  }

  private localDate(value: Date): string {
    const year = value.getFullYear();
    const month = String(value.getMonth() + 1).padStart(2, '0');
    const day = String(value.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
