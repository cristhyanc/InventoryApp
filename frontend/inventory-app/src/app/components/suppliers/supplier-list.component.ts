import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
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

  /**
   * What the server refused, verbatim, or `null` when nothing is being reported (issue #934). The
   * refusals a linked supplier produces - its name, its website, a delete, a "Not used" switch it
   * does not allow - are sentences written for the operator, so they are shown rather than
   * replaced with a message of this page's own.
   */
  error: string | null = null;

  /** Mirrors the stored column's limit, so the input stops the operator at the same place. */
  readonly urlMaxLength = SUPPLIER_URL_MAX_LENGTH;

  /**
   * Whether a support session is active (issue #657, extending #554): Create/Update and Delete
   * are hidden while it is, because a session grants `PurchasingOperate` - the capability this
   * page would otherwise gate those on - and the backend refuses the write itself only by its
   * read-only method rule, never by withholding the capability. The "Not used" switch of issue
   * #934 is on the same capability and is hidden for exactly the same reason. See
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
    // The one request in the application that asks for the suppliers marked "Not used" (issue
    // #934): this page is where one is switched back on, which is impossible if it is not shown.
    this.supplierService.getAllIncludingHidden().subscribe((s) => (this.suppliers = s));
  }

  /** Whether this supplier's stored website may be rendered as a link (issue #847). */
  canLinkTo(supplier: Supplier): boolean {
    return isSafeSupplierUrl(supplier.url);
  }

  /**
   * Whether this supplier came from the platform's supplier list (issue #934). The server decides
   * it; an older response that says nothing is treated as "not linked", which is what every
   * supplier was before this issue.
   */
  isLinked(supplier: Supplier): boolean {
    return supplier.isLinked === true;
  }

  /**
   * Whether the name and website inputs are locked for the supplier being edited. It reads the
   * server's own `canRename` rather than deriving a second rule from `isLinked`: the backend is the
   * authority and refuses the write either way, and this is what keeps the form from offering one
   * it will refuse.
   */
  get nameAndWebsiteLocked(): boolean {
    return this.editing !== null && this.editing.canRename === false;
  }

  startCreate(): void {
    this.editing = null;
    this.form = { name: '', contactName: '', phone: '', email: '', address: '', url: '' };
    this.urlError = null;
    this.error = null;
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
    this.error = null;
  }

  save(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (!this.form.name.trim()) return;

    const url = normalizeSupplierUrl(this.form.url);
    this.urlError = url.error;
    if (url.error) return;

    // A linked copy's name and website are the master's, so the stored values are sent back
    // unchanged whatever the locked inputs hold. The server compares them and refuses a change;
    // this makes an edit of the contact details below go through rather than being refused for a
    // field the operator could not even type in.
    const locked = this.editing !== null && this.editing.canRename === false;

    const payload = {
      name: locked ? this.editing!.name : this.form.name.trim(),
      contactName: this.form.contactName || null,
      phone: this.form.phone || null,
      email: this.form.email || null,
      address: this.form.address || null,
      url: locked ? this.editing!.url ?? null : url.url
    };

    this.error = null;

    if (this.editing) {
      this.supplierService.update(this.editing.id, payload).subscribe({
        next: () => {
          this.startCreate();
          this.load();
        },
        error: (response: HttpErrorResponse) => (this.error = this.refusal(response, 'Failed to save the supplier.'))
      });
    } else {
      this.supplierService.create(payload).subscribe({
        next: () => {
          this.startCreate();
          this.load();
        },
        error: (response: HttpErrorResponse) => (this.error = this.refusal(response, 'Failed to save the supplier.'))
      });
    }
  }

  remove(supplier: Supplier): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (supplier.canDelete === false) return;
    if (!confirm(`Delete supplier "${supplier.name}"?`)) return;

    this.error = null;
    this.supplierService.delete(supplier.id).subscribe({
      next: () => this.load(),
      error: (response: HttpErrorResponse) =>
        (this.error = this.refusal(response, 'Failed to delete the supplier.'))
    });
  }

  /**
   * Sets or clears "Not used" on a linked supplier (issue #934). The row stays either way: what
   * changes is whether a purchase, a supplier order or a product's order dialog offers it.
   */
  setNotUsed(supplier: Supplier, isHidden: boolean): void {
    if (this.sessionActive) return;
    if (!this.isLinked(supplier)) return;

    this.error = null;
    this.supplierService.setNotUsed(supplier.id, isHidden).subscribe({
      next: () => this.load(),
      error: (response: HttpErrorResponse) => {
        this.error = this.refusal(response, 'Failed to change "Not used".');
        // The switch shows the stored state, so a refused change must not leave it showing the
        // state the server rejected.
        this.load();
      }
    });
  }

  cancel(): void {
    this.startCreate();
  }

  /**
   * The server's own refusal sentence when it sent one, and a fixed fallback otherwise. Both
   * shapes the API uses are read: a field-level `errors` map (the website and name refusals) and a
   * `ProblemDetails` `message`/`detail` (the delete and "Not used" refusals).
   */
  private refusal(response: HttpErrorResponse, fallback: string): string {
    const body = response.error as
      | { message?: string; detail?: string; errors?: Record<string, string[]> }
      | null
      | undefined;

    const fieldMessage = body?.errors
      ? Object.values(body.errors)
          .flat()
          .find((message) => !!message)
      : undefined;

    return fieldMessage ?? body?.message ?? body?.detail ?? fallback;
  }
}
