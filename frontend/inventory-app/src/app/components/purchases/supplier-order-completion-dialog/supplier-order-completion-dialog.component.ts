import { Component, ElementRef, EventEmitter, Input, Output, Renderer2, ViewChild } from '@angular/core';
import { SupplierOrderCompletionPreview } from '../../../models/models';

/**
 * Asks the operator what should happen to a supplier order that this purchase will leave short
 * (issue #974): keep it open because more is still coming, or complete it because nothing more
 * will arrive.
 *
 * <p>It is a dedicated feature component rather than markup grown inside
 * `PurchaseUploadComponent`, per the page composition boundary (docs/architecture.md § Frontend
 * architecture: page composition): it is a distinct workflow with its own substantial UI and its
 * own modal lifecycle, and the page composes it through one input and three outputs. The automated
 * `page-composition.guard.ts` check is the same rule written down.</p>
 *
 * <p><strong>It decides nothing and calculates nothing.</strong> Every quantity it shows is the
 * server's own projection, handed in as {@link choice}; the three outputs are the operator's
 * answer, and what each one then submits is the page's business. Cancelling is an answer in its own
 * right - the purchase form comes back untouched - which is why the browser's close request emits
 * it rather than quietly picking the safe default.</p>
 */
@Component({
  selector: 'app-supplier-order-completion-dialog',
  standalone: true,
  templateUrl: './supplier-order-completion-dialog.component.html'
})
export class SupplierOrderCompletionDialogComponent {
  private static instances = 0;

  /**
   * The server's projection of what the order would still be owed, or `null` while there is nothing
   * to ask. Non-null is exactly "the question is open".
   */
  @Input() choice: SupplierOrderCompletionPreview | null = null;

  /** "Keep order open": the safe default, and the save this page has always performed. */
  @Output() readonly keepOpen = new EventEmitter<void>();

  /** "Complete order": no further delivery is expected. */
  @Output() readonly complete = new EventEmitter<void>();

  /** Back to the purchase form with nothing saved and nothing entered lost. */
  @Output() readonly cancelled = new EventEmitter<void>();

  /**
   * This dialog's own heading id, so it is labelled by its own heading even if a page ever renders
   * more than one of these.
   */
  readonly titleId = `supplier-order-completion-title-${++SupplierOrderCompletionDialogComponent.instances}`;

  @ViewChild('keepOpenButton') private readonly keepOpenButton?: ElementRef<HTMLButtonElement>;

  /** The open panel, while there is one, so withdrawing the question leaves the top layer. */
  private panel?: HTMLDialogElement;

  /** The listeners bound for one opening, undone when it closes with the element they belong to. */
  private readonly panelListeners: (() => void)[] = [];

  constructor(private readonly renderer: Renderer2) {}

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLDialogElement> | undefined) {
    if (panel === undefined) {
      this.closePanel();
      return;
    }

    this.openAsModal(panel.nativeElement);
  }

  /**
   * A quantity in the unit it was ordered in, so an outstanding balance is never a bare number
   * whose meaning the operator has to guess. A product with no recorded unit shows the quantity
   * alone rather than an invented label.
   */
  quantity(value: number, unit?: string | null): string {
    const label = unit?.trim();
    return label ? `${value} ${label}` : `${value}`;
  }

  keepOrderOpen(): void {
    this.keepOpen.emit();
  }

  completeOrder(): void {
    this.complete.emit();
  }

  cancelChoice(): void {
    this.cancelled.emit();
  }

  /**
   * Hands the panel to the browser as a modal: the top layer, the inert form behind it and the
   * `::backdrop` are the platform's. Focus goes to the safe default, and the browser's own close
   * request is answered as the cancel choice.
   */
  private openAsModal(panel: HTMLDialogElement): void {
    this.panel = panel;
    if (typeof panel.showModal === 'function') {
      panel.showModal();
    }

    this.keepOpenButton?.nativeElement.focus();

    this.panelListeners.push(
      this.renderer.listen(panel, 'cancel', (event: Event) => {
        event.preventDefault();
        this.cancelChoice();
      }),
      // `::backdrop` is the panel's own pseudo-element, so a click beside the panel is reported
      // with the panel as its target; a click on anything inside it never is. Dismissing by the
      // backdrop is the same answer Escape gives.
      this.renderer.listen(panel, 'click', (event: MouseEvent) => {
        if (event.target === panel) {
          this.cancelChoice();
        }
      })
    );
  }

  private closePanel(): void {
    for (const unlisten of this.panelListeners) {
      unlisten();
    }
    this.panelListeners.length = 0;

    const panel = this.panel;
    this.panel = undefined;
    panel?.close();
  }
}
