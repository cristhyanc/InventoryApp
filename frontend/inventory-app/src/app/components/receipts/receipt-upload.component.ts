import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ReceiptService } from '../../services/receipt.service';
import { SupplierService } from '../../services/supplier.service';
import { Supplier } from '../../models/models';

@Component({
  selector: 'app-receipt-upload',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './receipt-upload.component.html'
})
export class ReceiptUploadComponent implements OnInit {
  suppliers: Supplier[] = [];
  selectedFile: File | null = null;
  previewUrl: string | null = null;
  saving = false;
  error = '';

  form = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    purchaseDate: new Date().toISOString().substring(0, 10),
    supplierId: '' as number | ''
  };

  constructor(
    private receiptService: ReceiptService,
    private supplierService: SupplierService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
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
    if (!this.selectedFile) {
      this.error = 'Please select a receipt image or PDF to upload.';
      return;
    }
    if (!this.form.title.trim()) {
      this.error = 'Please enter a title.';
      return;
    }

    this.error = '';
    this.saving = true;

    this.receiptService
      .upload({
        file: this.selectedFile,
        title: this.form.title.trim(),
        notes: this.form.notes || null,
        totalAmount: this.form.totalAmount,
        purchaseDate: this.form.purchaseDate ? new Date(this.form.purchaseDate).toISOString() : null,
        supplierId: this.form.supplierId === '' ? null : this.form.supplierId
      })
      .subscribe({
        next: () => this.router.navigate(['/receipts']),
        error: (err) => {
          this.error = err?.error ?? 'Failed to upload receipt.';
          this.saving = false;
        }
      });
  }
}
