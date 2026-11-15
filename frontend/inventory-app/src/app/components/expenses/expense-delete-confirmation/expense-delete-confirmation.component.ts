import { Component, EventEmitter, Inject, Input, LOCALE_ID, Output } from '@angular/core';
import { formatCurrency, formatDate } from '@angular/common';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { OperatingExpense } from '../../../services/operating-expense.service';

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
 * It is the shared `confirmation-dialog`, named for the expense it would delete. The dialog itself
 * now owns the keyboard behaviour common to every caller - initial focus onto **Cancel**, Tab and
 * Shift+Tab staying inside it, Escape cancelling (issue #1277) - so `[restoreFocus]="false"` is the
 * only thing this wrapper tells it: this is the one caller whose opener the dialog's own default (
 * whatever has focus when it opens) resolves wrongly, per `openerOf` above, so this wrapper restores
 * it itself instead rather than in the register's own page template (`docs/architecture.md` §
 * Page composition boundary).
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
      <app-confirmation-dialog
        title="Delete expense"
        confirmLabel="Delete"
        cancelLabel="Cancel"
        [message]="message(expense)"
        [restoreFocus]="false"
        (confirmed)="onConfirm()"
        (canceled)="onCancel()"
      ></app-confirmation-dialog>
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
      this.restoreOpenerFocus();
    }
  }

  @Output() readonly confirmed = new EventEmitter<OperatingExpense>();
  @Output() readonly canceled = new EventEmitter<void>();

  private pending: OperatingExpense | null = null;
  private opener: HTMLElement | null = null;
  /** Set while a confirmed delete closes the dialog, which is the one close that restores nothing. */
  private confirming = false;

  constructor(@Inject(LOCALE_ID) private readonly locale: string) {}

  /** What the confirmation says it would delete: the expense's description, date and total. */
  message(expense: OperatingExpense): string {
    const date = formatDate(expense.expenseDate, 'yyyy-MM-dd', this.locale);
    // `$` and this format are what the register's own `currency` pipe renders beside it.
    const total = formatCurrency(expense.totalAmount, this.locale, '$');
    return `Delete "${expense.description}" recorded on ${date} for ${total}? This cannot be undone.`;
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

  /**
   * Back to whatever opened the dialog, when it is still on the page. A cancelled delete always has
   * its trigger to go back to; a confirmed one leaves the choice to the register.
   */
  private restoreOpenerFocus(): void {
    const opener = this.opener;
    this.opener = null;
    const confirming = this.confirming;
    this.confirming = false;
    if (!confirming && opener?.isConnected) {
      opener.focus();
    }
  }
}
