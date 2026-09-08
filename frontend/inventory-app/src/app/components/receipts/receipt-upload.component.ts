import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ReceiptService } from '../../services/receipt.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Supplier } from '../../models/models';
import { ReceiptItemPayload } from '../../services/receipt.service';
import { ProductService } from '../../services/product.service';

@Component({
  selector: 'app-receipt-upload',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './receipt-upload.component.html'
})
export class ReceiptUploadComponent implements OnInit {
  suppliers: Supplier[] = [];
  products: Product[] = [];
  items: ReceiptItemPayload[] = [];
  selectedFile: File | null = null;
  previewUrl: string | null = null;
  saving = false;
  error = '';

  form = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    packageCost: null as number | null,
    purchaseDate: new Date().toISOString().substring(0, 10),
    supplierId: '' as number | ''
  };

  constructor(
    private receiptService: ReceiptService,
    private supplierService: SupplierService,
    private productService: ProductService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
    this.productService.getAll().subscribe((p) => (this.products = p));
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
      this.error = 'Please select a receipt or invoice (PDF/image).';
      return;
    }
    if (!this.form.title.trim()) {
      this.error = 'Please enter a purchase title.';
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
        deliveryCost: this.form.deliveryCost,
        packageCost: this.form.packageCost,
        purchaseDate: this.form.purchaseDate ? new Date(this.form.purchaseDate).toISOString() : null,
        supplierId: this.form.supplierId === '' ? null : this.form.supplierId
        , items: this.items
      })
      .subscribe({
        next: () => this.router.navigate(['/receipts']),
        error: (err) => {
          this.error = err?.error ?? 'Failed to add purchase.';
          this.saving = false;
        }
      });
  }

  addItem(): void { this.items.push({ productId: this.products[0]?.id ?? 0, quantity: 1, unitCost: 0 }); }
  removeItem(index: number): void { this.items.splice(index, 1); }
  itemProduct(item: ReceiptItemPayload): Product | undefined { return this.products.find(p => p.id === Number(item.productId)); }
  lineTotal(item: ReceiptItemPayload): number { return Number(item.quantity || 0) * Number(item.unitCost || 0); }
  get itemsSubtotal(): number { return this.items.reduce((sum, item) => sum + this.lineTotal(item), 0); }
}
