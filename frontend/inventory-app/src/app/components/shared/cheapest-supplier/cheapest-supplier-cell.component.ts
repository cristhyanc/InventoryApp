import { Component, ElementRef, Input, Renderer2, ViewChild } from '@angular/core';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import {
  CheapestSupplierOffer,
  CheapestSupplierProduct,
  SupplierOfferIneligibility,
  SupplierPriceBasis,
  SupplierStockStatus
} from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import { CheapestSuppliersState } from './cheapest-suppliers.component';

/**
 * What counts as a tab stop inside the popup. Its content is the two Close controls and one
 * supplier link per row, so this is the whole of the popup's keyboard order; `tabindex="-1"` is
 * excluded because the panel itself carries it to be focusable without being tabbable.
 */
const POPUP_TAB_STOPS = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])'
].join(',');

/** The note every popup carries: these are prices somebody observed, not costs this business paid. */
export const OBSERVED_PRICES_NOTE =
  'Observed supplier prices, for purchasing guidance. They are not recorded costs and never change '
  + 'what this business has paid.';

/** What the popup says about a match the collector made that nobody has checked yet (#941). */
export const AUTOMATIC_MATCH_NOTE = 'Matched automatically';

/** `$0.95`, the one money format in this column. Rounding happens here, where it is displayed. */
export function money(value: number): string {
  return `$${value.toFixed(2)}`;
}

/** `ex GST` / `inc GST`, so every figure in this column says which basis it is on. */
export function basisLabel(basis: SupplierPriceBasis | null): string {
  if (basis === SupplierPriceBasis.ExclusiveOfGst) {
    return 'ex GST';
  }
  return basis === SupplierPriceBasis.InclusiveOfGst ? 'inc GST' : '';
}

/** What the supplier said about availability, in words. `Unknown` is said, never read as "in stock". */
export function stockLabel(status: SupplierStockStatus | null): string | null {
  switch (status) {
    case SupplierStockStatus.InStock:
      return 'In stock';
    case SupplierStockStatus.OutOfStock:
      return 'Out of stock';
    case SupplierStockStatus.Unknown:
      return 'Stock unknown';
    default:
      return null;
  }
}

/**
 * Why an offer was left out of the comparison, in the owner's words rather than the enum's. The
 * offer is still listed: "BIG W is out of stock" is the answer somebody needs, and leaving the row
 * off would read as "BIG W does not sell it".
 */
export function ineligibilityLabel(reason: SupplierOfferIneligibility | null): string | null {
  switch (reason) {
    case SupplierOfferIneligibility.PackSizeUnknown:
      return 'Pack size not published, so there is no unit price';
    case SupplierOfferIneligibility.ListingNotConfirmed:
      return 'Match not confirmed, so this price is not compared';
    case SupplierOfferIneligibility.GstRateUnknown:
      return 'GST rate unknown, so this price is not compared';
    case SupplierOfferIneligibility.OutOfStock:
      return 'Out of stock';
    case SupplierOfferIneligibility.PromotionEnded:
      return 'The special has ended';
    case SupplierOfferIneligibility.Stale:
      return 'Last check is too old to compare';
    default:
      return null;
  }
}

/**
 * What one supplier's row says beside its name: its unit price on the business's basis, or the one
 * state that explains why there is none. One sentence per row, so a row is never silent.
 */
export function offerStatus(offer: CheapestSupplierOffer): string | null {
  if (!offer.hasListing) {
    return 'No match';
  }
  if (!offer.hasPrice) {
    return 'Not checked yet';
  }
  return ineligibilityLabel(offer.ineligibility);
}

/**
 * The Cheapest supplier cell's own text (issue #930's fourth criterion), and the order of the
 * checks is the point: only a successful read may say anything about a product, and "the business
 * does not track this", "nothing has matched it", "it was looked for and not found" and "no offer
 * could be compared" stay four different answers rather than one blank.
 */
export function cheapestSupplierDisplay(
  state: CheapestSuppliersState,
  product: CheapestSupplierProduct | undefined,
  displayBasis: SupplierPriceBasis | null
): string {
  if (state === 'loading') {
    return 'Loading';
  }
  if (state !== 'loaded') {
    return 'Unavailable';
  }
  if (product?.trackingStatus == null) {
    return 'Not tracked';
  }
  if (product.trackingStatus === ProductPriceTrackingStatus.NotFound) {
    return 'No match found';
  }
  if (
    product.trackingStatus === ProductPriceTrackingStatus.Requested
    || product.trackingStatus === ProductPriceTrackingStatus.AwaitingProductDetails
  ) {
    return 'Awaiting match';
  }

  const cheapest = product.suppliers.filter((offer) => offer.isCheapest);
  if (cheapest.length === 0 || product.cheapestUnitPrice === null) {
    return 'No eligible price';
  }

  const names = cheapest.map((offer) => offer.supplierName).join(' / ');
  const special = cheapest.some((offer) => offer.isSpecial) ? ' (special)' : '';

  return `${names} ${money(product.cheapestUnitPrice)}/unit ${basisLabel(displayBasis)}${special}`.trim();
}

/** The note under a cheapest price: suppliers' stock answers are unreliable, so none is promised. */
export const STOCK_MAY_DIFFER_NOTE = 'Stock may differ';

/**
 * The stock note for the cell, or `null` when the cell shows no price. Stock never removes a price
 * from the comparison (owner decision, 1 November 2026), so every cheapest price carries it.
 */
export function cheapestStockNote(state: CheapestSuppliersState, product: CheapestSupplierProduct | undefined): string | null {
  if (state !== 'loaded' || (product?.cheapestUnitPrice ?? null) === null) {
    return null;
  }
  return product?.suppliers.some((offer) => offer.isCheapest) ? STOCK_MAY_DIFFER_NOTE : null;
}

/**
 * The Cheapest supplier cell and its popup (issue #930, parent #817) - one standalone component the
 * Dashboard's Reorder Alerts (#939), Needs Ordering and Products (#940) all use, so the three
 * columns cannot say different things.
 *
 * **It formats and nothing else.** Which supplier is cheapest, every unit price on both GST bases,
 * which basis this business sees, why an offer could not be compared and which offers tie are the
 * server's answers from the one authoritative comparison (`CheapestUnitOfferPolicy`, issue #925).
 * There is no arithmetic in this file beyond `toFixed` for display, which is the only rounding this
 * column does.
 *
 * **The popup lists every one of the business's linked, not-hidden suppliers**, cheapest first,
 * with each one's pack size, pack price, unit price on both bases, special, stock, last check and a
 * link to the supplier's own page - and the suppliers with no match or no check yet, each saying
 * so. Each last-check instant is shown in the business's own configured time zone through
 * `BusinessDateTimePipe`, never in the operator's accidental browser zone.
 *
 * **It is a real modal, not a panel that says it is one.** The popup is a native `<dialog>` opened
 * with `showModal()`, so the browser puts it in the top layer and makes the rest of the page
 * inert: the page behind it cannot be clicked, tabbed into or read by assistive technology while
 * it is open. The Tab cycle below is that same contract written down rather than inferred, which
 * is what the regression tests can hold it to; Escape, the backdrop and the Close controls all
 * close it, and focus returns to the cell that opened it.
 */
@Component({
  selector: 'app-cheapest-supplier-cell',
  standalone: true,
  imports: [BusinessDateTimePipe],
  templateUrl: './cheapest-supplier-cell.component.html'
})
export class CheapestSupplierCellComponent {
  private static instances = 0;

  /** The server's answer for this product, or `undefined` while the read has not established one. */
  @Input() product?: CheapestSupplierProduct;

  /** Where the page's one cheapest-supplier read stands. */
  @Input() state: CheapestSuppliersState = 'loading';

  /** The GST basis the figures are on, as the server decided it. */
  @Input() displayBasis: SupplierPriceBasis | null = null;

  /** The product's own name, for the popup's heading and the cell's accessible label. */
  @Input() productName = '';

  readonly observedPricesNote = OBSERVED_PRICES_NOTE;
  readonly automaticMatchNote = AUTOMATIC_MATCH_NOTE;

  /**
   * This cell's own heading id, so a table of them produces no duplicate ids and every popup is
   * labelled by its own heading rather than by the first one on the page.
   */
  readonly titleId = `cheapest-supplier-title-${++CheapestSupplierCellComponent.instances}`;

  open = false;

  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;

  /** The open panel, while there is one, so closing it can leave the top layer before focus moves. */
  private panel?: HTMLDialogElement;

  /** The listeners bound for one opening, undone when it closes with the element it belongs to. */
  private readonly panelListeners: (() => void)[] = [];

  constructor(private readonly renderer: Renderer2) {}

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLDialogElement> | undefined) {
    if (panel !== undefined) {
      this.openAsModal(panel.nativeElement);
    }
  }

  /** The cell's own text: a cheapest supplier and its unit price, or the state that explains why not. */
  get display(): string {
    return cheapestSupplierDisplay(this.state, this.product, this.displayBasis);
  }

  /** "Stock may differ" under a cheapest price, and nothing otherwise. */
  get stockNote(): string | null {
    return cheapestStockNote(this.state, this.product);
  }

  /**
   * Whether there is a popup to open. Only a matched product with supplier rows has one; a product
   * nothing has matched has nothing to list, and a failed read has nothing to say.
   */
  get canOpen(): boolean {
    return this.state === 'loaded' && (this.product?.suppliers.length ?? 0) > 0;
  }

  /** Whether this product's match was made automatically and has not been checked (#941). */
  get isAutomaticMatch(): boolean {
    return this.product?.trackingStatus === ProductPriceTrackingStatus.AutoMatched;
  }

  get suppliers(): CheapestSupplierOffer[] {
    return this.product?.suppliers ?? [];
  }

  openPopup(): void {
    if (this.canOpen) {
      this.open = true;
    }
  }

  closePopup(): void {
    if (!this.open) {
      return;
    }

    this.open = false;
    for (const unlisten of this.panelListeners) {
      unlisten();
    }
    this.panelListeners.length = 0;

    // Closing the dialog before moving focus is the order that matters: while it is open the rest
    // of the page is inert, so the cell underneath cannot take focus yet.
    const panel = this.panel;
    this.panel = undefined;
    panel?.close();

    this.trigger?.nativeElement.focus();
  }

  /**
   * Hands the panel to the browser as a modal: the top layer, the inert page behind it and the
   * `::backdrop` are the platform's, and these three listeners are the popup's own behaviour over
   * them. They are bound here rather than in the template because each one is a decision about the
   * event rather than a handler on an element - which click counts as "outside", which key moves
   * focus and where - and because a mouse handler on the panel markup would be exactly the
   * mouse-only control the panel is not.
   */
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
          this.closePopup();
        }
      }),
      // The browser's own close request. It must leave this component in the state Close leaves it
      // in, never a closed dialog the cell still reports as expanded.
      this.renderer.listen(panel, 'cancel', (event: Event) => {
        event.preventDefault();
        this.closePopup();
      })
    );
  }

  /**
   * Escape closes; Tab and Shift+Tab stay inside. Every Tab is answered here rather than left to
   * the browser so the cycle is one definition in one place: from the panel itself forward to its
   * first control and backward to its last, and from either end around to the other. Nothing in
   * this cycle is outside the panel, which is the half of modality the page behind it cannot be
   * trusted to enforce on its own.
   */
  private onPanelKeydown(panel: HTMLDialogElement, event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closePopup();
      return;
    }

    if (event.key !== 'Tab') {
      return;
    }

    event.preventDefault();
    const stops = Array.from(panel.querySelectorAll<HTMLElement>(POPUP_TAB_STOPS));
    if (stops.length === 0) {
      panel.focus();
      return;
    }

    const current = stops.indexOf(document.activeElement as HTMLElement);
    const next = event.shiftKey
      ? stops[current <= 0 ? stops.length - 1 : current - 1]
      : stops[current === -1 || current === stops.length - 1 ? 0 : current + 1];

    next.focus();
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

  /** The popup's accessible name, which names the product it is about. */
  get dialogTitle(): string {
    return `Supplier prices for ${this.productName}`;
  }
}
