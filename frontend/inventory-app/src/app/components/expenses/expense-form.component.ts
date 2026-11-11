import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Params, RouterLink } from '@angular/router';
import { LocalMachine, Supplier } from '../../models/models';
import { OperatingExpenseCategory, OperatingExpensePayload } from '../../services/operating-expense.service';
import { prepareImageForUpload } from '../shared/image-upload-preparation';
import { currentDateInTimeZone } from '../../formatting/business-time-zone';
import { OPERATING_EXPENSE_CATEGORIES, expenseCategoryLabel } from './expense-categories';

/** The `accept` list the supporting-document input has always offered. */
export const EXPENSE_ATTACHMENT_ACCEPT = '.jpg,.jpeg,.png,.pdf,.webp,.heic,image/*,application/pdf';

/**
 * The expense the form opens with (issue #1167): the fields it submits, plus the two things about a
 * stored expense the form shows but never changes.
 */
export interface ExpenseFormValue {
  /**
   * Every field create and update submit, including `siteId`, `servicePeriodStart` and
   * `servicePeriodEnd`, which this form has no control for and carries back exactly as loaded.
   */
  readonly payload: OperatingExpensePayload;
  /**
   * The machine the **stored** expense names, or `null`. It is kept on the machine list even once
   * that machine has been deactivated, so saving the form cannot silently detach a historical
   * expense from the machine the cost was incurred on (issue #746).
   */
  readonly recordedMachineId: number | null;
  /** The supporting document already stored, or `null` when the expense has none. */
  readonly attachmentFileName: string | null;
}

/** One press of the form's primary action: what to submit, and the document chosen with it. */
export interface ExpenseSaveRequest {
  readonly payload: OperatingExpensePayload;
  readonly attachment: File | null;
}

/**
 * Today's calendar date in `timeZoneId`, or the browser's own local date when the business time
 * zone is not known yet (issue #1171) - never the UTC date, which is a different calendar day from
 * late morning in a zone ahead of UTC (e.g. Sydney) until the UTC date catches up.
 */
function defaultExpenseDate(timeZoneId: string | null, now: Date): string {
  const pad = (value: number) => value.toString().padStart(2, '0');
  if (timeZoneId !== null) {
    const { year, month, day } = currentDateInTimeZone(now, timeZoneId);
    return `${year}-${pad(month)}-${pad(day)}`;
  }
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/**
 * A brand-new expense: today's date in the business's own time zone (issue #1171; `timeZoneId`
 * `null` while it is still loading, which falls back to the browser's local date rather than the
 * UTC one), category Other, three zero amounts, nothing attributed.
 */
export function newExpenseFormValue(timeZoneId: string | null = null, now: Date = new Date()): ExpenseFormValue {
  return {
    payload: {
      expenseDate: defaultExpenseDate(timeZoneId, now),
      category: OperatingExpenseCategory.Other,
      description: '',
      amountExGst: 0,
      gstAmount: 0,
      totalAmount: 0,
      supplierId: null,
      siteId: null,
      machineId: null,
      notes: null
    },
    recordedMachineId: null,
    attachmentFileName: null
  };
}

/**
 * The one expense form, used by both Add expense and Edit expense (issue #1167).
 *
 * It replaces the card the `/expenses` register used to open above itself, and keeps that card's
 * business behaviour exactly:
 *
 * - **The three amounts stay three independent inputs.** Amount ex GST, GST and Total are typed and
 *   submitted exactly as entered - no auto-calculation, no rounding, no assumed GST rate and no
 *   mismatch warning - because the amounts on a supplier's document are the record, and the API
 *   refuses only a negative one.
 * - **The fields the form has no control for survive an edit.** `siteId`, `servicePeriodStart` and
 *   `servicePeriodEnd` are carried in {@link ExpenseFormValue.payload} and submitted exactly as
 *   loaded, so editing a description can never silently clear an expense's service period.
 * - **The "Nayax monthly fee" category** is offered for a **new** expense only when Nayax is part of
 *   the business, but an expense already recorded under it keeps it: dropping the category an
 *   existing expense carries would reclassify it the next time somebody saved the form.
 * - **The machine field** offers the business's **active** machines, plus the machine the stored
 *   expense already names even once it has been deactivated. A failed machine read reports itself
 *   where the field was and an outstanding one says it is loading; neither is ever presented as "no
 *   machines".
 * - **A chosen photo is shrunk before it is uploaded** through the shared preparation of issue
 *   #1161: the field says so while it happens and the primary action waits for it, so an expense is
 *   never saved with the document that does not exist yet. The preparation belongs to the selection
 *   that started it, so one that finishes after another file was chosen is discarded.
 * - **The server stays the authority on the document itself**: size, type and the business's storage
 *   quota are its refusals, and choosing a new file on Edit replaces the stored one, which is the
 *   only thing the API offers - there is no remove.
 *
 * Validation here is the one check the register's card already made - a blank Description is not
 * submitted - except that the refusal is now visible beside the field instead of silent. Every other
 * rule is the server's, and its sentence is shown by the page that owns the request.
 */
@Component({
  selector: 'app-expense-form',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './expense-form.component.html'
})
export class ExpenseFormComponent {
  /** Whether this is a new expense or one that already exists; it names the primary action. */
  @Input() mode: 'create' | 'edit' = 'create';

  /**
   * The expense to edit, or a new one. Setting it is what opens (or reopens) the form: the fields
   * are filled in from it and anything typed or chosen before is discarded, which is exactly what
   * "reload the expense" means after a conflicting save.
   */
  @Input({ required: true }) set value(value: ExpenseFormValue) {
    this.open(value);
  }

  @Input() suppliers: readonly Supplier[] = [];

  /** Every machine this business has recorded, active and inactive alike (issue #746). */
  @Input() machines: readonly LocalMachine[] = [];

  /** Set when the machine list could not be read at all; never shown as "no machines". */
  @Input() machinesError = '';

  /** Whether that read is still outstanding, which is not the same as there being no machines. */
  @Input() machinesLoading = false;

  /** Whether Nayax is part of this business at all; the Nayax fee category needs it. */
  @Input() providerRecordsAvailable = true;

  /** Whether the page's save request is in flight, so the form holds still and says so. */
  @Input() saving = false;

  /**
   * Whether this expense may only be read - a support session (issue #659). Every field is held
   * still and the primary action is gone; the page says why.
   */
  @Input() readOnly = false;

  /** The register's applied filters, so Cancel returns to the register the operator left. */
  @Input() returnQueryParams: Params = {};

  @Output() readonly saveRequested = new EventEmitter<ExpenseSaveRequest>();

  /** The stored supporting document was asked for; the page fetches it through the authorized API. */
  @Output() readonly viewDocumentRequested = new EventEmitter<void>();

  readonly categoryLabel = expenseCategoryLabel;
  readonly accept = EXPENSE_ATTACHMENT_ACCEPT;

  form: OperatingExpensePayload = newExpenseFormValue().payload;

  /** The document chosen on this form, already prepared for upload, or `null`. */
  selectedAttachment: File | null = null;

  /** Whether the chosen supporting document is still being shrunk or converted (issue #1161). */
  attachmentPreparing = false;

  /** The stored document's file name, shown so the replacement it offers is an informed choice. */
  attachmentFileName: string | null = null;

  /** Whether the primary action has been pressed, so a blank Description now shows its refusal. */
  submitAttempted = false;

  private recordedMachineId: number | null = null;

  /**
   * Which selection the document being prepared belongs to (issue #1161). Preparation is
   * asynchronous, so a photo chosen for this expense can finish after another file - or a reload of
   * the expense - has moved the form on. Each of those takes a fresh number, and a preparation that
   * comes back holding an old one is discarded: it neither replaces the newer selection's file nor
   * clears the preparing state that is guarding it.
   */
  private attachmentSelection = 0;

  /** The fields as loaded, so {@link dirty} is a real comparison rather than a typed-in-anything. */
  private storedFields = '';

  /**
   * The categories the form offers. Without Nayax the monthly fee is not one of them - there is no
   * Nayax account to be billed by - unless the expense being edited already carries it.
   */
  get formCategories(): OperatingExpenseCategory[] {
    if (this.providerRecordsAvailable || this.form.category === OperatingExpenseCategory.NayaxMonthlyFee) {
      return OPERATING_EXPENSE_CATEGORIES;
    }
    return OPERATING_EXPENSE_CATEGORIES.filter((category) => category !== OperatingExpenseCategory.NayaxMonthlyFee);
  }

  /**
   * The machines this expense may be attributed to: the business's **active** machines, plus the one
   * the stored expense already names even once it has been deactivated. It is taken from the stored
   * expense rather than from the form, so choosing "None" and changing one's mind still offers it.
   */
  get machineOptions(): LocalMachine[] {
    return this.machines.filter((machine) => machine.isActive || machine.id === this.recordedMachineId);
  }

  /**
   * Whether an expense may be attributed to a machine at all: only when there is a machine to
   * attribute it to. A failed or outstanding read is reported in the field's place instead.
   */
  get machineFieldShown(): boolean {
    return this.machinesError === '' && !this.machinesLoading && this.machineOptions.length > 0;
  }

  /** Whether the Description is blank, which is the one refusal this form makes itself. */
  get descriptionMissing(): boolean {
    return !this.form.description.trim();
  }

  /** Whether that refusal is being shown, which it is only once the operator has pressed Save. */
  get descriptionInvalid(): boolean {
    return this.submitAttempted && this.descriptionMissing;
  }

  /** The primary action's label, which names the write it would actually make. */
  get submitLabel(): string {
    if (this.saving) {
      return 'Saving...';
    }
    return this.mode === 'edit' ? 'Save changes' : 'Save expense';
  }

  /**
   * Whether the primary action is unavailable: while a request is in flight, so a double press
   * sends one request, and while a chosen photo is still being prepared, because the file this
   * would upload does not exist yet. A blank Description does **not** disable it - pressing it is
   * what shows the refusal beside the field.
   */
  get submitDisabled(): boolean {
    return this.saving || this.attachmentPreparing;
  }

  /** Whether leaving now would lose something: an edited field, or a document chosen for upload. */
  get dirty(): boolean {
    return (
      this.serialise(this.form) !== this.storedFields ||
      this.selectedAttachment !== null ||
      this.attachmentPreparing
    );
  }

  /** The primary action: submit the values on screen, or show why they were not submitted. */
  submit(): void {
    this.submitAttempted = true;
    // Checked here as well as disabled or hidden in the template, so the write is actually
    // unavailable rather than merely inert - the endpoint refuses it either way.
    if (this.readOnly || this.submitDisabled || this.descriptionMissing) {
      return;
    }

    this.saveRequested.emit({ payload: { ...this.form }, attachment: this.selectedAttachment });
  }

  /**
   * The chosen document, prepared for upload first (issue #1161): a photo is shrunk and HEIC
   * converted where the browser can, and anything else - a PDF, an already small image, a photo the
   * browser cannot decode - is kept exactly as it was chosen.
   */
  async onAttachmentSelected(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    const selection = this.forgetAttachment();
    if (!file) {
      return;
    }

    this.attachmentPreparing = true;
    try {
      const prepared = await prepareImageForUpload(file);
      if (selection !== this.attachmentSelection) {
        return;
      }
      this.selectedAttachment = prepared;
    } finally {
      // Only this selection's own preparing state: a newer selection is still preparing its own
      // photo, and clearing its state here would let the primary action run before that photo exists.
      if (selection === this.attachmentSelection) {
        this.attachmentPreparing = false;
      }
    }
  }

  /** Fills the form in from `value`, discarding anything typed or chosen before it. */
  private open(value: ExpenseFormValue): void {
    this.form = { ...value.payload };
    this.recordedMachineId = value.recordedMachineId;
    this.attachmentFileName = value.attachmentFileName;
    this.storedFields = this.serialise(this.form);
    this.submitAttempted = false;
    this.forgetAttachment();
  }

  /**
   * Forgets the chosen supporting document and invalidates any preparation still running for it,
   * returning the number the new selection is identified by (issue #1161).
   */
  private forgetAttachment(): number {
    this.selectedAttachment = null;
    this.attachmentPreparing = false;
    return ++this.attachmentSelection;
  }

  private serialise(fields: OperatingExpensePayload): string {
    return JSON.stringify([
      fields.expenseDate,
      fields.category,
      fields.description,
      fields.amountExGst,
      fields.gstAmount,
      fields.totalAmount,
      fields.supplierId,
      fields.siteId,
      fields.machineId,
      fields.servicePeriodStart,
      fields.servicePeriodEnd,
      fields.notes
    ]);
  }
}
