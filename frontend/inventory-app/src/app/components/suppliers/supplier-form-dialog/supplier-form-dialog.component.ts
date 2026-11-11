import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Supplier } from '../../../models/models';
import { SupplierService } from '../../../services/supplier.service';
import { ModalDialogComponent } from '../../shared/modal-dialog/modal-dialog.component';
import { SUPPLIER_URL_MAX_LENGTH, normalizeSupplierUrl } from '../supplier-url';
import { supplierRefusal } from '../supplier-refusal';

/** The form's own message for the one field the API requires; the server refuses a blank name too. */
export const SUPPLIER_NAME_REQUIRED_MESSAGE = 'A supplier needs a name.';

/** The form values, which are also what "has anything changed?" is decided from. */
interface SupplierFormValues {
  name: string;
  contactName: string;
  phone: string;
  email: string;
  address: string;
  url: string;
}

/**
 * Add supplier and Edit supplier (issue #1174): one form, one dialog, over
 * `POST /api/suppliers` and `PUT /api/suppliers/{id}`.
 *
 * It is a dedicated feature component rather than markup grown inside `SupplierListComponent`, per
 * the page composition boundary (docs/architecture.md § Page composition boundary): it owns its own
 * form values, field validation, submission state, server refusal and its discard question, and the
 * page composes it through `[supplier]`/`[sessionActive]` and reacts only to `(saved)`/`(closed)`.
 * A `null` {@link supplier} is an Add and an existing one is an Edit; the only differences are the
 * title, the primary action's name and which call is made.
 *
 * **No API, model or service contract changed.** The payload is exactly the one the inline form
 * sent: the website is normalised by the one browser-side copy of issue #847's rule
 * (`components/suppliers/supplier-url.ts`), and a linked supplier's locked name and website are
 * sent back **unchanged** whatever the read-only inputs hold (issue #934), so an edit of the
 * contact details below goes through instead of being refused for a field the operator could not
 * even type in.
 *
 * **A save in flight holds everything still.** The primary action, Cancel and the Close control are
 * all disabled while {@link saving}, so a second submission cannot be issued; a refusal gives every
 * entered value back with the server's own sentence above the fields, and only a successful save
 * reports `saved` - which is what lets the page reload the list and show its toast *after* the
 * write rather than beside it.
 */
@Component({
  selector: 'app-supplier-form-dialog',
  standalone: true,
  imports: [FormsModule, ModalDialogComponent],
  templateUrl: './supplier-form-dialog.component.html'
})
export class SupplierFormDialogComponent implements OnInit {
  /** The supplier being edited, or `null` to add one. */
  @Input() supplier: Supplier | null = null;

  /**
   * Whether a read-only support session is active (issue #657). The save is refused here as well as
   * disabled in the template, so the write is actually unavailable rather than merely inert - the
   * endpoint refuses it either way.
   */
  @Input() sessionActive = false;

  /** Reported only after the create or update actually succeeded. */
  @Output() readonly saved = new EventEmitter<void>();

  /** Reported when the dialog should go: an unchanged form dismissed, or discarded changes. */
  @Output() readonly closed = new EventEmitter<void>();

  form: SupplierFormValues = emptyForm();

  /** Mirrors the stored column's limit, so the input stops the operator at the same place. */
  readonly urlMaxLength = SUPPLIER_URL_MAX_LENGTH;

  /** The name field's refusal, or `null` while it is acceptable. */
  nameError: string | null = null;

  /**
   * The website field's refusal (issue #847), or `null` while the field is acceptable. The server
   * applies the same rule and is the authority; this is shown so the operator reads it on the field
   * rather than as a failed request.
   */
  urlError: string | null = null;

  /** What the server refused, verbatim, or `null` when nothing is being reported (issue #934). */
  error: string | null = null;

  saving = false;

  /** Whether the "Discard changes?" question is on screen, over this dialog. */
  confirmingDiscard = false;

  /** The values the dialog opened with, so only a real modification asks before closing. */
  private opened: SupplierFormValues = emptyForm();

  constructor(private readonly suppliers: SupplierService) {}

  ngOnInit(): void {
    this.form = this.supplier === null ? emptyForm() : formOf(this.supplier);
    this.opened = { ...this.form };
  }

  get title(): string {
    return this.supplier === null ? 'Add supplier' : 'Edit supplier';
  }

  get submitLabel(): string {
    return this.supplier === null ? 'Create supplier' : 'Save changes';
  }

  /**
   * Whether the name and website inputs are locked for the supplier being edited. It reads the
   * server's own `canRename` rather than deriving a second rule from `isLinked`: the backend is the
   * authority and refuses the write either way, and this is what keeps the form from offering one
   * it will refuse.
   */
  get nameAndWebsiteLocked(): boolean {
    return this.supplier !== null && this.supplier.canRename === false;
  }

  /** Whether anything on screen differs from what the dialog opened with. */
  get dirty(): boolean {
    return (
      this.form.name !== this.opened.name ||
      this.form.contactName !== this.opened.contactName ||
      this.form.phone !== this.opened.phone ||
      this.form.email !== this.opened.email ||
      this.form.address !== this.opened.address ||
      this.form.url !== this.opened.url
    );
  }

  /** Clears the field and server messages as soon as the value they were about changes. */
  onFieldChanged(): void {
    this.nameError = null;
    this.urlError = null;
    this.error = null;
  }

  /**
   * Escape, the backdrop, the Close control and Cancel all arrive here. An unchanged form is
   * dismissed; a dirty one asks first, so entered values are never dropped by a stray Escape.
   */
  requestClose(): void {
    if (this.saving) {
      return;
    }

    if (this.dirty) {
      this.confirmingDiscard = true;
      return;
    }

    this.closed.emit();
  }

  keepEditing(): void {
    this.confirmingDiscard = false;
  }

  discardChanges(): void {
    this.confirmingDiscard = false;
    this.closed.emit();
  }

  save(): void {
    if (this.sessionActive || this.saving) {
      return;
    }

    const name = this.form.name.trim();
    const website = normalizeSupplierUrl(this.form.url);

    this.nameError = name === '' ? SUPPLIER_NAME_REQUIRED_MESSAGE : null;
    this.urlError = website.error;
    this.error = null;

    if (this.nameError || this.urlError) {
      return;
    }

    // A linked copy's name and website are the master's, so the stored values are sent back
    // unchanged whatever the locked inputs hold. The server compares them and refuses a change.
    const locked = this.nameAndWebsiteLocked;
    const payload = {
      name: locked ? this.supplier!.name : name,
      contactName: this.form.contactName || null,
      phone: this.form.phone || null,
      email: this.form.email || null,
      address: this.form.address || null,
      url: locked ? this.supplier!.url ?? null : website.url
    };

    this.saving = true;
    const request: Observable<unknown> =
      this.supplier === null
        ? this.suppliers.create(payload)
        : this.suppliers.update(this.supplier.id, payload);

    request.subscribe({
      next: () => {
        this.saving = false;
        this.saved.emit();
      },
      error: (response: HttpErrorResponse) => {
        this.saving = false;
        this.error = supplierRefusal(response, 'Failed to save the supplier.');
      }
    });
  }
}

function emptyForm(): SupplierFormValues {
  return { name: '', contactName: '', phone: '', email: '', address: '', url: '' };
}

function formOf(supplier: Supplier): SupplierFormValues {
  return {
    name: supplier.name,
    contactName: supplier.contactName ?? '',
    phone: supplier.phone ?? '',
    email: supplier.email ?? '',
    address: supplier.address ?? '',
    // A supplier with no website edits with an empty field, and saving without touching it sends
    // null back rather than an empty string.
    url: supplier.url ?? ''
  };
}
