import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { SupplierService } from '../../services/supplier.service';
import { Supplier } from '../../models/models';
import { SupplierGstDefaultsComponent } from './supplier-gst-defaults/supplier-gst-defaults.component';
import { IconComponent } from '../shared/icon.component';
import { SupportSessionService } from '../../support-session/support-session.service';
import { SUPPLIER_URL_MAX_LENGTH, isSafeSupplierUrl, normalizeSupplierUrl } from './supplier-url';

@Component({
  selector: 'app-supplier-list',
  standalone: true,
  imports: [CommonModule, FormsModule, SupplierGstDefaultsComponent, IconComponent],
  templateUrl: './supplier-list.component.html'
})
export class SupplierListComponent implements OnInit, OnDestroy {
  suppliers: Supplier[] = [];
  editing: Supplier | null = null;
  form = { name: '', contactName: '', phone: '', email: '', address: '', url: '' };

  /**
   * The website field's refusal (issue #847), or `null` while the field is acceptable. The server
   * applies the same rule and is the authority; this is shown so the operator reads it on the
   * field rather than as a failed request.
   */
  urlError: string | null = null;

  /** Mirrors the stored column's limit, so the input stops the operator at the same place. */
  readonly urlMaxLength = SUPPLIER_URL_MAX_LENGTH;

  /**
   * Whether a support session is active (issue #657, extending #554): Create/Update and Delete
   * are hidden while it is, because a session grants `PurchasingOperate` - the capability this
   * page would otherwise gate those on - and the backend refuses the write itself only by its
   * read-only method rule, never by withholding the capability. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly supplierService: SupplierService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  load(): void {
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
  }

  /** Whether this supplier's stored website may be rendered as a link (issue #847). */
  canLinkTo(supplier: Supplier): boolean {
    return isSafeSupplierUrl(supplier.url);
  }

  startCreate(): void {
    this.editing = null;
    this.form = { name: '', contactName: '', phone: '', email: '', address: '', url: '' };
    this.urlError = null;
  }

  startEdit(supplier: Supplier): void {
    this.editing = supplier;
    this.form = {
      name: supplier.name,
      contactName: supplier.contactName ?? '',
      phone: supplier.phone ?? '',
      email: supplier.email ?? '',
      address: supplier.address ?? '',
      // A supplier with no website edits with an empty field, and saving without touching it
      // sends null back rather than an empty string.
      url: supplier.url ?? ''
    };
    this.urlError = null;
  }

  save(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (!this.form.name.trim()) return;

    const url = normalizeSupplierUrl(this.form.url);
    this.urlError = url.error;
    if (url.error) return;

    const payload = {
      name: this.form.name.trim(),
      contactName: this.form.contactName || null,
      phone: this.form.phone || null,
      email: this.form.email || null,
      address: this.form.address || null,
      url: url.url
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
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (!confirm(`Delete supplier "${supplier.name}"?`)) return;
    this.supplierService.delete(supplier.id).subscribe(() => this.load());
  }

  cancel(): void {
    this.startCreate();
  }
}
