import { Component, ElementRef, EventEmitter, Input, OnDestroy, Output, Renderer2, ViewChild } from '@angular/core';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import { CheapestSupplierOffer, CheapestSupplierProduct, SupplierPriceBasis } from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import {
  AUTOMATIC_MATCH_NOTE,
  OBSERVED_PRICES_NOTE,
  basisLabel,
  money,
  offerOnlinePriceNote,
  offerStatus,
  stockLabel
} from './cheapest-supplier-format';

/**
 * What counts as a tab stop inside the dialog. Its content is the two Close controls and one
 * supplier link per row, so this is the whole of its keyboard order; `tabindex="-1"` is excluded
 * because the panel itself carries it to be focusable without being tabbable.
 */
const DIALOG_TAB_STOPS = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])'
].join(',');

/** The sentence under the comparison that says which GST basis its unit prices are on. */
export function comparisonBasisNote(displayBasis: SupplierPriceBasis | null): string | null {
  const basis = basisLabel(displayBasis);
  return basis === '' ? null : `Unit prices are compared ${basis}, the basis this business is shown.`;
}

/**
 * The supplier comparison (issue #1093, extracted from issue #930's cell popup): one shared
 * component every Cheapest supplier surface opens - the Dashboard's Reorder priorities, Needs
 * ordering and Products - so a supplier's name, pack, prices and last check read the same wherever
 * the same offer is shown.
 *
 * **It formats and nothing else.** Every figure is the server's answer from the one comparison
 * (`CheapestUnitOfferPolicy`, issue #925); there is no arithmetic here beyond `toFixed` for display.
 * A pack size, pack price or unit price the server does not know is shown as unavailable (`—`),
 * never inferred, and the GST basis of every price is said beside it.
 *
 * **It is a real modal.** The panel is a native `<dialog>` opened with `showModal()` as soon as it
 * renders, so the browser puts it in the top layer and makes the rest of the page inert. Escape,
 * the backdrop and the Close controls all close it and report `closed`; the Tab cycle is written
 * down here rather than left to the browser, so the regression tests can hold it to that. The
 * opener restores focus to its own trigger once `closed` arrives.
 */
@Component({
  selector: 'app-supplier-comparison-dialog',
  standalone: true,
  imports: [BusinessDateTimePipe],
  templateUrl: './supplier-comparison-dialog.component.html'
})
export class SupplierComparisonDialogComponent implements OnDestroy {
  private static instances = 0;

  /** The server's answer for this product; the dialog is only opened for a product with suppliers. */
  @Input({ required: true }) product!: CheapestSupplierProduct;

  /** The GST basis the unit prices are on, as the server decided it. */
  @Input() displayBasis: SupplierPriceBasis | null = null;

  @Input() productName = '';
  @Input() productSku: string | null = null;
  @Input() categoryName: string | null = null;

  /** Reported once the dialog has closed itself, by any of its dismissal paths. */
  @Output() readonly closed = new EventEmitter<void>();

  readonly observedPricesNote = OBSERVED_PRICES_NOTE;
  readonly automaticMatchNote = AUTOMATIC_MATCH_NOTE;

  /** This dialog's own heading id, so several on one page never share one. */
  readonly titleId = `supplier-comparison-title-${++SupplierComparisonDialogComponent.instances}`;

  private panel?: HTMLDialogElement;
  private readonly panelListeners: (() => void)[] = [];

  constructor(private readonly renderer: Renderer2) {}

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLDialogElement> | undefined) {
    if (panel !== undefined && panel.nativeElement !== this.panel) {
      this.openAsModal(panel.nativeElement);
    }
  }

  ngOnDestroy(): void {
    this.release();
  }

  get dialogTitle(): string {
    return `Supplier prices for ${this.productName}`;
  }

  get basisNote(): string | null {
    return comparisonBasisNote(this.displayBasis);
  }

  get isAutomaticMatch(): boolean {
    return this.product?.trackingStatus === ProductPriceTrackingStatus.AutoMatched;
  }

  get suppliers(): CheapestSupplierOffer[] {
    return this.product?.suppliers ?? [];
  }

  close(): void {
    if (this.panel === undefined) {
      return;
    }
    // Closing the dialog before reporting it is the order that matters: while it is open the rest
    // of the page is inert, so the opener cannot take focus back yet.
    this.release();
    this.closed.emit();
  }

  readonly exclusiveOfGst = SupplierPriceBasis.ExclusiveOfGst;
  readonly inclusiveOfGst = SupplierPriceBasis.InclusiveOfGst;

  /** Whether the bold headline price is already on this basis, so its secondary line would repeat it. */
  headlineIs(basis: SupplierPriceBasis): boolean {
    return this.displayBasis === basis;
  }

  onlinePriceOf(offer: CheapestSupplierOffer): string | null {
    return offerOnlinePriceNote(offer);
  }

  basisOf(basis: SupplierPriceBasis | null): string {
    return basisLabel(basis);
  }

  priceOf(value: number | null): string | null {
    return value === null ? null : money(value);
  }

  stockOf(offer: CheapestSupplierOffer): string | null {
    return stockLabel(offer.stockStatus);
  }

  statusOf(offer: CheapestSupplierOffer): string | null {
    return offerStatus(offer);
  }

  private release(): void {
    for (const unlisten of this.panelListeners) {
      unlisten();
    }
    this.panelListeners.length = 0;
    const panel = this.panel;
    this.panel = undefined;
    panel?.close();
  }

  private openAsModal(panel: HTMLDialogElement): void {
    this.panel = panel;
    panel.showModal();
    panel.focus();

    this.panelListeners.push(
      this.renderer.listen(panel, 'keydown', (event: KeyboardEvent) => this.onPanelKeydown(panel, event)),
      // `::backdrop` is the panel's own pseudo-element, so a click beside the panel is reported
      // with the panel as its target; a click on anything inside it never is.
      this.renderer.listen(panel, 'click', (event: MouseEvent) => {
        if (event.target === panel) {
          this.close();
        }
      }),
      // The browser's own close request leaves this component in the state Close leaves it in.
      this.renderer.listen(panel, 'cancel', (event: Event) => {
        event.preventDefault();
        this.close();
      })
    );
  }

  /** Escape closes; Tab and Shift+Tab cycle inside the dialog and never reach the page behind it. */
  private onPanelKeydown(panel: HTMLDialogElement, event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }

    event.preventDefault();
    const stops = Array.from(panel.querySelectorAll<HTMLElement>(DIALOG_TAB_STOPS));
    if (stops.length === 0) {
      panel.focus();
      return;
    }
    const current = stops.indexOf(document.activeElement as HTMLElement);
    // Wraps at either end; with nothing focused yet, Tab lands on the first stop and Shift+Tab on the last.
    const from = current === -1 && event.shiftKey ? 0 : current;
    const step = event.shiftKey ? -1 : 1;
    stops[(from + step + stops.length) % stops.length].focus();
  }
}
