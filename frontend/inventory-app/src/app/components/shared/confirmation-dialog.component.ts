import { AfterViewInit, Component, ElementRef, EventEmitter, Input, OnDestroy, Output, Renderer2, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { focusPageHeading } from './confirmation-focus';

/** The two controls a confirmation ever renders, in the order Tab should visit them. */
const DIALOG_CONTROLS = 'button:not([disabled])';

/**
 * The shared confirmation (title, message, Confirm/Cancel) used for every delete and revoke across
 * the app (audit #1225, finding FE-06). It is a self-contained `alertdialog`: opening it moves focus
 * to Cancel, Tab and Shift+Tab stay inside it, Escape cancels, and closing it gives focus back,
 * exactly as `components/shared/modal-dialog` already does for its own shell.
 *
 * Focus is returned to whatever had it when the dialog opened, unless `restoreFocus` is `false` -
 * the escape hatch `ExpenseDeleteConfirmationComponent` uses, because it already resolves and
 * restores a better opener itself (the row's menu trigger, not the menu item the dialog's own open
 * would capture) and deliberately restores nothing at all after a confirmed delete.
 *
 * `confirmed` carries that opener, so a caller whose confirmed action is asynchronous can hand
 * focus on once the action succeeded and removed it - see `shared/confirmation-focus.ts`, which
 * explains why the dialog cannot do that part itself.
 */
@Component({
  selector: 'app-confirmation-dialog',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="modal-backdrop" #backdrop>
      <div
        #panel
        class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg"
        role="alertdialog"
        aria-modal="true"
        [attr.aria-labelledby]="titleId"
        [attr.aria-describedby]="messageId"
      >
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800" [id]="titleId">{{ title }}</h2>
        <p class="mb-5 text-md-body text-md-gray-600" [id]="messageId">{{ message }}</p>
        <div class="confirm-actions">
          <button #cancelButton class="btn btn-secondary btn-sm" type="button" (click)="cancelChoice()">{{ cancelLabel }}</button>
          <button class="btn btn-danger btn-sm" type="button" (click)="confirmChoice()">{{ confirmLabel }}</button>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      .modal-backdrop {
        position: fixed;
        inset: 0;
        background: rgba(15, 23, 42, 0.5);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 50;
        padding: 1rem;
      }

      .confirm-modal {
        max-width: 420px;
        width: 100%;
      }

      .confirm-actions {
        display: flex;
        justify-content: flex-end;
        gap: 0.75rem;
      }
    `
  ]
})
export class ConfirmationDialogComponent implements AfterViewInit, OnDestroy {
  private static instances = 0;

  @Input() title = 'Confirm';
  @Input() message = 'Are you sure?';
  @Input() confirmLabel = 'Confirm';
  @Input() cancelLabel = 'Cancel';

  /**
   * `false` lets the caller capture and restore focus itself instead - see
   * `ExpenseDeleteConfirmationComponent`, the one caller that needs to.
   */
  @Input() restoreFocus = true;

  /**
   * Where to send focus on close when the opener this dialog captured on open is no longer in the
   * document (for example, a deleted row's own button). Defaults to the page's own heading.
   */
  @Input() returnFocusTo: HTMLElement | null = null;

  /**
   * Emitted with the control this dialog captured on open and returns focus to as it closes, or
   * `null` when there is none (nothing had focus, or `restoreFocus` is `false`). A caller whose
   * confirmed action removes that control passes it back to
   * `handOffFocusFromRemovedOpener` once the action has succeeded; a caller whose action leaves the
   * control in place ignores it.
   */
  @Output() readonly confirmed = new EventEmitter<HTMLElement | null>();
  @Output() readonly canceled = new EventEmitter<void>();

  private readonly instanceId = ++ConfirmationDialogComponent.instances;
  readonly titleId = `confirmation-dialog-title-${this.instanceId}`;
  readonly messageId = `confirmation-dialog-message-${this.instanceId}`;

  @ViewChild('backdrop') private readonly backdropRef!: ElementRef<HTMLElement>;
  @ViewChild('panel') private readonly panelRef!: ElementRef<HTMLElement>;
  @ViewChild('cancelButton') private readonly cancelButtonRef!: ElementRef<HTMLButtonElement>;

  private opener: HTMLElement | null = null;
  private readonly listeners: (() => void)[] = [];

  constructor(private readonly renderer: Renderer2) {}

  ngAfterViewInit(): void {
    if (this.restoreFocus) {
      const active = document.activeElement;
      this.opener = active instanceof HTMLElement && active !== document.body ? active : null;
    }

    this.cancelButtonRef.nativeElement.focus();

    const panel = this.panelRef.nativeElement;
    this.listeners.push(
      this.renderer.listen(panel, 'keydown', (event: KeyboardEvent) => this.onPanelKeydown(panel, event)),
      // The backdrop is the only element this listener is bound to, so a click that bubbled up from
      // the panel (whose own content never reaches here as the backdrop) never closes the dialog.
      this.renderer.listen(this.backdropRef.nativeElement, 'click', (event: MouseEvent) => {
        if (event.target === this.backdropRef.nativeElement) {
          this.cancelChoice();
        }
      })
    );
  }

  ngOnDestroy(): void {
    for (const unlisten of this.listeners) {
      unlisten();
    }
    this.listeners.length = 0;

    if (!this.restoreFocus) {
      return;
    }

    const opener = this.opener;
    this.opener = null;
    if (opener?.isConnected) {
      opener.focus();
      return;
    }

    const fallback = this.returnFocusTo;
    if (fallback?.isConnected) {
      fallback.focus();
      return;
    }

    focusPageHeading();
  }

  confirmChoice(): void {
    this.confirmed.emit(this.opener);
  }

  cancelChoice(): void {
    this.canceled.emit();
  }

  /** Escape cancels; Tab and Shift+Tab wrap between Cancel and the confirm action, never leaving. */
  private onPanelKeydown(panel: HTMLElement, event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.cancelChoice();
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }

    const stops = Array.from(panel.querySelectorAll<HTMLElement>(DIALOG_CONTROLS));
    if (stops.length === 0) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    const current = stops.indexOf(document.activeElement as HTMLElement);
    const from = current === -1 && event.shiftKey ? 0 : current;
    const step = event.shiftKey ? -1 : 1;
    stops[(from + step + stops.length) % stops.length].focus();
  }
}
