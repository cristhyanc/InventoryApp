import { Component, ElementRef, Input, ViewChild } from '@angular/core';
import { CheapestSupplierProduct, SupplierPriceBasis } from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import { CheapestSuppliersState } from './cheapest-suppliers.component';
import { basisLabel, money } from './cheapest-supplier-format';
import { SupplierComparisonDialogComponent } from './supplier-comparison-dialog.component';

// The shared wording moved to `cheapest-supplier-format.ts` (issue #1093); re-exported here so
// every existing import of it from the cell keeps working.
export {
  AUTOMATIC_MATCH_NOTE,
  OBSERVED_PRICES_NOTE,
  basisLabel,
  ineligibilityLabel,
  money,
  offerStatus,
  stockLabel
} from './cheapest-supplier-format';

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
 * The Cheapest supplier cell (issue #930, parent #817) - the one standalone cell the Dashboard's
 * Reorder priorities (#939) and Needs Ordering (#940) use, so their columns cannot say different
 * things. Products lays the same answer out through `CheapestSupplierSummaryComponent` (issue
 * #1093), and every one of them opens the same `SupplierComparisonDialogComponent`.
 *
 * **It formats and nothing else.** Which supplier is cheapest, every unit price on both GST bases,
 * which basis this business sees, why an offer could not be compared and which offers tie are the
 * server's answers from the one authoritative comparison (`CheapestUnitOfferPolicy`, issue #925).
 *
 * The cell is a button only when there is a comparison to open; the dialog is a real native modal,
 * and when it reports `closed` focus returns to the cell that opened it.
 */
@Component({
  selector: 'app-cheapest-supplier-cell',
  standalone: true,
  imports: [SupplierComparisonDialogComponent],
  templateUrl: './cheapest-supplier-cell.component.html'
})
export class CheapestSupplierCellComponent {
  /** The server's answer for this product, or `undefined` while the read has not established one. */
  @Input() product?: CheapestSupplierProduct;

  /** Where the page's one cheapest-supplier read stands. */
  @Input() state: CheapestSuppliersState = 'loading';

  /** The GST basis the figures are on, as the server decided it. */
  @Input() displayBasis: SupplierPriceBasis | null = null;

  /** The product's own name, for the dialog's heading and the cell's accessible label. */
  @Input() productName = '';

  open = false;

  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;

  /** The cell's own text: a cheapest supplier and its unit price, or the state that explains why not. */
  get display(): string {
    return cheapestSupplierDisplay(this.state, this.product, this.displayBasis);
  }

  /** "Stock may differ" under a cheapest price, and nothing otherwise. */
  get stockNote(): string | null {
    return cheapestStockNote(this.state, this.product);
  }

  /**
   * Whether there is a comparison to open. Only a matched product with supplier rows has one; a
   * product nothing has matched has nothing to list, and a failed read has nothing to say.
   */
  get canOpen(): boolean {
    return this.state === 'loaded' && (this.product?.suppliers.length ?? 0) > 0;
  }

  /** Whether this product's match was made automatically and has not been checked (#941). */
  get isAutomaticMatch(): boolean {
    return this.product?.trackingStatus === ProductPriceTrackingStatus.AutoMatched;
  }

  openPopup(): void {
    if (this.canOpen) {
      this.open = true;
    }
  }

  /** The dialog has closed itself; focus goes back to the cell that opened it. */
  onPopupClosed(): void {
    this.open = false;
    this.trigger?.nativeElement.focus();
  }
}
