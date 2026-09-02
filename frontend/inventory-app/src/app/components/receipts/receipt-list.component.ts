import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ReceiptService } from '../../services/receipt.service';
import { SupplierService } from '../../services/supplier.service';
import { Receipt, Supplier } from '../../models/models';

@Component({
  selector: 'app-receipt-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './receipt-list.component.html'
})
export class ReceiptListComponent implements OnInit {
  receipts: Receipt[] = [];
  suppliers: Supplier[] = [];
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
    private supplierService: SupplierService
  ) {}

  ngOnInit(): void {
    this.load();
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
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
      })
      .subscribe({
        next: () => {
          this.editingReceiptId = null;
          this.load();
        },
        error: (err) => console.error('Failed to update receipt', err)
      });
  }

  remove(receipt: Receipt): void {
    if (!confirm(`Delete receipt "${receipt.title}"?`)) return;
    this.receiptService.delete(receipt.id).subscribe(() => this.load());
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}
