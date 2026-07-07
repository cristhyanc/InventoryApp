import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SupplierService } from '../../services/supplier.service';
import { Supplier } from '../../models/models';

@Component({
  selector: 'app-supplier-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './supplier-list.component.html'
})
export class SupplierListComponent implements OnInit {
  suppliers: Supplier[] = [];
  editing: Supplier | null = null;
  form = { name: '', contactName: '', phone: '', email: '', address: '' };

  constructor(private supplierService: SupplierService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
  }

  startCreate(): void {
    this.editing = null;
    this.form = { name: '', contactName: '', phone: '', email: '', address: '' };
  }

  startEdit(supplier: Supplier): void {
    this.editing = supplier;
    this.form = {
      name: supplier.name,
      contactName: supplier.contactName ?? '',
      phone: supplier.phone ?? '',
      email: supplier.email ?? '',
      address: supplier.address ?? ''
    };
  }

  save(): void {
    if (!this.form.name.trim()) return;

    const payload = {
      name: this.form.name.trim(),
      contactName: this.form.contactName || null,
      phone: this.form.phone || null,
      email: this.form.email || null,
      address: this.form.address || null
    };

    if (this.editing) {
      this.supplierService.update(this.editing.id, payload).subscribe(() => {
        this.startCreate();
        this.load();
      });
    } else {
      this.supplierService.create(payload).subscribe(() => {
        this.startCreate();
        this.load();
      });
    }
  }

  remove(supplier: Supplier): void {
    if (!confirm(`Delete supplier "${supplier.name}"?`)) return;
    this.supplierService.delete(supplier.id).subscribe(() => this.load());
  }

  cancel(): void {
    this.startCreate();
  }
}
