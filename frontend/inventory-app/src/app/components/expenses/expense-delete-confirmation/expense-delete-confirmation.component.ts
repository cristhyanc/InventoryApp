import { Component, ElementRef, EventEmitter, Inject, Input, LOCALE_ID, Output, ViewChild } from '@angular/core';
import { formatCurrency, formatDate } from '@angular/common';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { OperatingExpense } from '../../../services/operating-expense.service';

/** What takes focus inside the confirmation: every enabled control the dialog renders. */
const DIALOG_CONTROLS = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled])';

/**
 * The control focus should come back to, given whatever had it when the confirmation opened.
 *
 * Usually that is the focused element itself. But the register opens this confirmation from a row's
 * overflow menu, where the focused element is the menu's **Delete** item and the shared
 * `action-menu` removes its whole panel as it closes - so returning focus there would return it to
 * nothing (measured in the browser by the visual run of issue #1170; jsdom keeps a detached element
 * focusable, so only a real layout engine shows it). The element to go back to is then the menu's
 * own trigger, which stays on the row and which the panel names with `aria-labelledby`, as the
 * WAI-ARIA menu-button pattern requires of any menu.
 */
function openerOf(active: Element | null): HTMLElement | null {
  if (!(active instanceof HTMLElement)) {
    return null;
  }
  const triggerId = active.closest('[role="menu"]')?.getAttribute('aria-labelledby');
  const trigger = triggerId ? document.getElementById(triggerId) : null;
  return trigger ?? active;
}

/**
 * The register's delete confirmation (issue #1170).
 *
 * It is the shared `confirmation-dialog`, named for the expense it would delete, plus the keyboard
 * behaviour that dialog does not own: focus moves into it when it opens (onto **Cancel**, not the
 * destructive action), Tab and Shift+Tab stay inside it, Escape cancels, and focus returns to
 * whatever opened it. Changing the shared dialog itself is out of scope for issue #1170, so that
 * behaviour lives here, beside the one workflow that needs it, rather than in the register's own
 * page template (`docs/architecture.md` § Page composition boundary).
 *
 * A **confirmed** delete deliberately restores no focus: the row the trigger belonged to is about to
 * disappear with the expense, so the register decides where focus goes once the list has reloaded.
 */
@Component({
  selector: 'app-expense-delete-confirmation',
  standalone: true,
  imports: [ConfirmationDialogComponent],
  template: `
    @if (expense) {
      <div #dialog (keydown)="onKeydown($event)">
        <app-confirmation-dialog
          title="Delete expense"
          confirmLabel="Delete"
          cancelLabel="Cancel"
          [message]="message(expense)"
          (confirmed)="onConfirm()"
          (canceled)="onCancel()"
        ></app-confirmation-dialog>
      </div>
    }
  `
})
export class ExpenseDeleteConfirmationComponent {
  /** The expense waiting to be confirmed, or `null` while the register has nothing pending. */
  @Input()
  get expense(): OperatingExpense | null {
    return this.pending;
  }
  set expense(value: OperatingExpense | null) {
    if (value === this.pending) {
      return;
    }
    if (value !== null && this.pending === null) {
      // Recorded before the dialog renders, which is the last moment the opener still has focus.
      this.opener = openerOf(document.activeElement);
    }
    const closing = value === null && this.pending !== null;
    this.pending = value;
    if (closing) {
      this.restoreFocus();
    }
  }

  @Output() readonly confirmed = new EventEmitter<OperatingExpense>();
  @Output() readonly canceled = new EventEmitter<void>();

  private pending: OperatingExpense | null = null;
  private opener: HTMLElement | null = null;
  /** Set while a confirmed delete closes the dialog, which is the one close that restores nothing. */
  private confirming = false;
  private dialogElement?: HTMLElement;

  constructor(@Inject(LOCALE_ID) private readonly locale: string) {}

  /**
   * Focus moves into the dialog as soon as it is rendered, the way the shared action menu focuses
   * its panel: a `ViewChild` setter runs inside the same change detection that created the element.
   */
  @ViewChild('dialog')
  private set dialog(dialog: ElementRef<HTMLElement> | undefined) {
    if (dialog === undefined) {
      this.dialogElement = undefined;
      return;
    }
    if (dialog.nativeElement !== this.dialogElement) {
      this.dialogElement = dialog.nativeElement;
      this.controls()[0]?.focus();
    }
  }

  /** What the confirmation says it would delete: the expense's description, date and total. */
  message(expense: OperatingExpense): string {
    const date = formatDate(expense.expenseDate, 'yyyy-MM-dd', this.locale);
    // `$` and this format are what the register's own `currency` pipe renders beside it.
    const total = formatCurrency(expense.totalAmount, this.locale, '$');
    return `Delete "${expense.description}" recorded on ${date} for ${total}? This cannot be undone.`;
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.onCancel();
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }

    // Containment: the dialog has no other way out, so Tab cycles its own controls.
    const controls = this.controls();
    if (controls.length === 0) {
      return;
    }
    const current = controls.indexOf(document.activeElement as HTMLElement);
    const last = controls.length - 1;
    let next: number;
    if (event.shiftKey) {
      next = current <= 0 ? last : current - 1;
    } else {
      next = current === -1 || current === last ? 0 : current + 1;
    }
    event.preventDefault();
    controls[next].focus();
  }

  onConfirm(): void {
    const expense = this.pending;
    if (expense === null) {
      return;
    }
    this.confirming = true;
    this.confirmed.emit(expense);
  }

  onCancel(): void {
    if (this.pending === null) {
      return;
    }
    this.canceled.emit();
  }

  private controls(): HTMLElement[] {
    return this.dialogElement ? Array.from(this.dialogElement.querySelectorAll<HTMLElement>(DIALOG_CONTROLS)) : [];
  }

  /**
   * Back to whatever opened the dialog, when it is still on the page. A cancelled delete always has
   * its trigger to go back to; a confirmed one leaves the choice to the register.
   */
  private restoreFocus(): void {
    const opener = this.opener;
    this.opener = null;
    const confirming = this.confirming;
    this.confirming = false;
    if (!confirming && opener?.isConnected) {
      opener.focus();
    }
  }
}
