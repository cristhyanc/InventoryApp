import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { PurchaseService } from '../../services/purchase.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Purchase, Supplier, PurchaseValidation } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { PurchaseItemPayload } from '../../services/purchase.service';
import { ObjectUrlCache } from '../shared/object-url-cache';

@Component({
  selector: 'app-purchase-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './purchase-list.component.html'
})
export class PurchaseListComponent implements OnInit, OnDestroy {
  purchases: Purchase[] = [];
  suppliers: Supplier[] = [];
  products: Product[] = [];
  editItems: PurchaseItemPayload[] = [];
  editingPurchaseId: number | null = null;
  editForm = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    packageCost: null as number | null,
    purchaseDate: '',
    supplierId: '' as number | ''
  };

  // Purchase documents are protected by the API, so they are fetched through HttpClient (which
  // attaches the bearer token) and rendered from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  private thumbnailUrls = new Map<number, string>();

  constructor(
    private purchaseService: PurchaseService,
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
    this.purchaseService.getAll().subscribe((r) => {
      this.purchases = r;
      this.loadThumbnails();
    });
  }

  getValidation(purchase: Purchase): PurchaseValidation | null {
    return this.purchaseService.getValidationFor(purchase.id);
  }

  thumbnailUrl(purchase: Purchase): string | null {
    return this.thumbnailUrls.get(purchase.id) ?? null;
  }

  openDocument(purchase: Purchase): void {
    this.purchaseService.getFile(purchase.id).subscribe({
      next: (blob) => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: (err) => console.error('Failed to open purchase document', err)
    });
  }

  private loadThumbnails(): void {
    this.objectUrls.releaseAll();
    this.thumbnailUrls = new Map<number, string>();
    this.purchases
      .filter((purchase) => this.isImage(purchase))
      .forEach((purchase) =>
        this.purchaseService.getFile(purchase.id).subscribe({
          next: (blob) => this.thumbnailUrls.set(purchase.id, this.objectUrls.create(blob)),
          error: (err) => console.error('Failed to load purchase document', err)
        })
      );
  }

  isImage(purchase: Purchase): boolean {
    return purchase.contentType.startsWith('image/');
  }

  startEdit(purchase: Purchase): void {
    this.editingPurchaseId = purchase.id;
    this.editForm = {
      title: purchase.title,
      notes: purchase.notes ?? '',
      totalAmount: purchase.totalAmount ?? null,
      deliveryCost: purchase.deliveryCost ?? null,
      packageCost: purchase.packageCost ?? null,
      purchaseDate: purchase.purchaseDate ? this.localDate(new Date(purchase.purchaseDate)) : '',
      supplierId: purchase.supplierId ?? ''
    };
    this.editItems = (purchase.items ?? []).map(item => ({
      productId: item.productId, quantity: item.quantity, unitCost: item.unitCost
    }));
  }

  cancelEdit(): void {
    this.editingPurchaseId = null;
  }

  saveEdit(purchase: Purchase): void {
    if (!this.editForm.title.trim()) return;

    this.purchaseService
      .update(purchase.id, {
        title: this.editForm.title.trim(),
        notes: this.editForm.notes || null,
        totalAmount: this.editForm.totalAmount,
        deliveryCost: this.editForm.deliveryCost,
        packageCost: this.editForm.packageCost,
        purchaseDate: this.editPurchaseTimestamp(purchase),
        supplierId: this.editForm.supplierId === '' ? null : this.editForm.supplierId
        , items: this.editItems
      })
      .subscribe({
        next: () => {
          this.editingPurchaseId = null;
          this.load();
        },
        error: (err) => console.error('Failed to update purchase', err)
      });
  }

  addEditItem(): void { this.editItems.push({ productId: this.products[0]?.id ?? 0, quantity: 1, unitCost: 0 }); }
  removeEditItem(index: number): void { this.editItems.splice(index, 1); }
  editLineTotal(item: PurchaseItemPayload): number { return Number(item.quantity || 0) * Number(item.unitCost || 0); }
  get editItemsSubtotal(): number { return this.editItems.reduce((sum, item) => sum + this.editLineTotal(item), 0); }

  remove(purchase: Purchase): void {
    if (!confirm(`Delete purchase "${purchase.title}"?`)) return;
    this.purchaseService.delete(purchase.id).subscribe(() => this.load());
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  private editPurchaseTimestamp(purchase: Purchase): string | null {
    if (!this.editForm.purchaseDate) return null;
    const original = new Date(purchase.purchaseDate);
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
