import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ReceiptService } from '../../services/receipt.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Receipt, Supplier } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { ReceiptItemPayload } from '../../services/receipt.service';

@Component({
  selector: 'app-receipt-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './receipt-list.component.html'
})
export class ReceiptListComponent implements OnInit {
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

  load(): void {
    this.receiptService.getAll().subscribe((r) => (this.receipts = r));
  }

  fileUrl(receipt: Receipt): string {
    return this.receiptService.fileUrl(receipt.id);
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
      purchaseDate: receipt.purchaseDate ? new Date(receipt.purchaseDate).toISOString().substring(0, 10) : '',
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
        purchaseDate: this.editForm.purchaseDate ? new Date(this.editForm.purchaseDate).toISOString() : null,
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
}
