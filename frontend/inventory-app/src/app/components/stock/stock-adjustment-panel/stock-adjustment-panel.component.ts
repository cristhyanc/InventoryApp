import {
  AfterViewInit,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnDestroy,
  Output,
  Renderer2,
  ViewChild
} from '@angular/core';
import { LocalMachine, Product } from '../../../models/models';
import { IconComponent } from '../../shared/icon.component';
import { StockAdjustmentFormComponent } from '../stock-adjustment-form/stock-adjustment-form.component';

/**
 * The Adjust stock panel on Stock History (issue #1156): a drawer on the right over the ledger from
 * `md` up, and a full-screen form on a phone, around the existing {@link StockAdjustmentFormComponent}.
 *
 * It is a native modal `<dialog>` for the reason the bulk GST rule question is one: `showModal()`
 * puts it in the top layer and makes the page behind it inert, so the ledger, its selection and its
 * filters stay exactly where they were - visible behind the backdrop, and unreachable until the
 * panel closes. Neither `role` nor `aria-modal` is written, because the platform maps a modal
 * `<dialog>` to both.
 *
 * Focus moves to Quantity change when the panel opens; the page returns it to the Adjust stock
 * button when the panel closes. Escape and a click on the backdrop are answered as Cancel, and every
 * way of closing is refused while an adjustment is being saved, so a write in flight is never left
 * behind a closed panel. The panel records nothing itself: the form posts to the existing
 * adjustment endpoint and this component only passes on that it did.
 */
@Component({
  selector: 'app-stock-adjustment-panel',
  standalone: true,
  imports: [IconComponent, StockAdjustmentFormComponent],
  template: `
    <dialog
      #dialogPanel
      tabindex="-1"
      [attr.aria-labelledby]="titleId"
      [attr.aria-describedby]="summaryId"
      class="fixed inset-0 m-0 flex h-full max-h-none w-full max-w-none flex-col overflow-hidden border-0 bg-white p-0 font-rr text-rr-navy outline-none backdrop:bg-rr-navy/40 md:left-auto md:w-[28rem] md:border-l md:border-rr-line md:shadow-md-lg"
      data-testid="adjustment-panel"
    >
      <div class="flex items-start justify-between gap-3 border-b border-rr-line px-4 py-4 sm:px-6">
        <div class="min-w-0">
          <h2 [id]="titleId" class="m-0 text-lg font-bold text-rr-navy">Adjust stock</h2>
          <p [id]="summaryId" class="m-0 mt-1 text-sm text-rr-muted" data-testid="adjustment-panel-product">
            <span class="break-words font-semibold text-rr-navy">{{ product.name }}</span>
            <span class="block">Current stock: <span class="font-semibold tabular-nums text-rr-navy">{{ product.quantityInStock }}</span> units</span>
          </p>
        </div>
        <button
          type="button"
          class="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-rr-muted hover:bg-rr-canvas hover:text-rr-navy disabled:cursor-not-allowed disabled:opacity-50"
          aria-label="Close Adjust stock"
          data-testid="adjustment-panel-close"
          [disabled]="saving"
          (click)="requestClose()"
        ><app-icon name="close" [size]="20" /></button>
      </div>

      <app-stock-adjustment-form
        [productId]="product.id"
        [machines]="machines"
        [machinesFailed]="machinesFailed"
        [machinesLoading]="machinesLoading"
        (savingChange)="saving = $event"
        (cancelled)="requestClose()"
        (adjustmentApplied)="adjustmentApplied.emit()"
      ></app-stock-adjustment-form>
    </dialog>
  `
})
export class StockAdjustmentPanelComponent implements AfterViewInit, OnDestroy {
  private static instances = 0;

  /** The product being adjusted; its name and current stock head the panel. */
  @Input({ required: true }) product!: Product;
  @Input() machines: LocalMachine[] = [];
  @Input() machinesFailed = false;
  @Input() machinesLoading = false;

  /** Raised after the adjustment endpoint accepted a movement. */
  @Output() readonly adjustmentApplied = new EventEmitter<void>();

  /** Raised by Cancel, Close, Escape or the backdrop, never while an adjustment is being saved. */
  @Output() readonly closed = new EventEmitter<void>();

  saving = false;

  private readonly instance = ++StockAdjustmentPanelComponent.instances;
  readonly titleId = `stock-adjustment-panel-title-${this.instance}`;
  readonly summaryId = `stock-adjustment-panel-summary-${this.instance}`;

  private panel?: HTMLDialogElement;
  private readonly panelListeners: (() => void)[] = [];

  constructor(private readonly renderer: Renderer2) {}

  @ViewChild('dialogPanel') private readonly dialogPanel?: ElementRef<HTMLDialogElement>;

  ngAfterViewInit(): void {
    if (this.dialogPanel) {
      this.openAsModal(this.dialogPanel.nativeElement);
    }
  }

  requestClose(): void {
    if (this.saving) {
      return;
    }
    this.closed.emit();
  }

  ngOnDestroy(): void {
    this.panelListeners.splice(0).forEach((unlisten) => unlisten());
    if (this.panel?.open && typeof this.panel.close === 'function') {
      this.panel.close();
    }
    this.panel = undefined;
  }

  private openAsModal(panel: HTMLDialogElement): void {
    this.panel = panel;
    if (typeof panel.showModal === 'function' && !panel.open) {
      panel.showModal();
    }

    // The first field, so the operator can type the quantity straight away.
    queueMicrotask(() => panel.querySelector<HTMLInputElement>('#adjustment-quantity-change')?.focus());

    this.panelListeners.push(
      this.renderer.listen(panel, 'cancel', (event: Event) => {
        event.preventDefault();
        this.requestClose();
      }),
      // A click on the `::backdrop` is reported with the dialog itself as its target.
      this.renderer.listen(panel, 'click', (event: MouseEvent) => {
        if (event.target === panel) {
          this.requestClose();
        }
      })
    );
  }
}
