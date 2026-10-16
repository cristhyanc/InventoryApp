/**
 * Per-line price entry for the Add and Edit purchase forms (issue #707).
 *
 * Stock usually arrives in boxes priced per box, so each purchase line lets the operator enter
 * either the amount paid for the whole line or the price of one unit, and the form derives the
 * other from that line's purchased **unit** quantity. The selector is an entry convenience and
 * nothing else: no pack size, units-per-box or carton is modelled, the chosen mode is never sent to
 * or stored by the server, and a line is still persisted as the quantity and unit cost the purchase
 * contract already carried.
 *
 * Both forms share this one module so Add and Edit cannot drift apart on the arithmetic (AGENTS.md
 * § Architecture rules: one authoritative calculation feeds all presentations). The authoritative
 * money rules stay on the server: a line's GST-inclusive amount is `round(Quantity * UnitCost, 2)`
 * in `Inventory.Domain.Purchases.PurchaseGstPolicy`, and every GST figure is the API's. What is
 * computed here is the complementary figure the operator sees before saving, and the unit cost the
 * existing request already carried.
 */

/** Which of a line's two prices the operator types; the other one is calculated. */
export type PriceEntryMode = 'total' | 'unit';

/**
 * The mode a line starts in. Roughly 80% of purchases arrive priced per box, so the amount paid for
 * the whole line is what an operator usually has in front of them.
 */
export const DEFAULT_PRICE_ENTRY_MODE: PriceEntryMode = 'total';

/**
 * The mode a line already stored on a purchase opens in on the edit form: the unit cost is the
 * figure the purchase actually holds, and entering the quantity over it is what keeps an edit of
 * something else - a quantity correction, a date, a classification - from re-costing the line.
 */
export const STORED_LINE_PRICE_ENTRY_MODE: PriceEntryMode = 'unit';

/** The one price-entry vocabulary both purchase forms offer, so they cannot name a mode differently. */
export const PRICE_ENTRY_MODE_OPTIONS: ReadonlyArray<{ value: PriceEntryMode; label: string }> = [
  { value: 'total', label: 'Total purchase price' },
  { value: 'unit', label: 'Unit price' }
];

/**
 * One purchase line's quantity and the two prices that describe it. Both amounts are nullable
 * because a number input binds `null` for a blank field (and `NaN` for text that is not a number),
 * and because a figure that cannot be derived must be cleared rather than left stale.
 *
 * `unitCost` is the value the request carries, in either mode. `lineTotal` is the entered amount in
 * `total` mode and the calculated one in `unit` mode.
 */
export interface PurchaseLinePrice {
  quantity: number | null;
  priceEntryMode: PriceEntryMode;
  lineTotal: number | null;
  unitCost: number | null;
}

/**
 * A quantity the form may calculate with: a finite whole number of units above zero, which is also
 * what `Inventory.Domain.Purchases.PurchaseItemFormatPolicy` requires of a submitted line. It is
 * the divisor in `total` mode, so anything else leaves the line without a unit cost instead of
 * producing an infinity.
 */
export function isValidLineQuantity(quantity: number | null | undefined): quantity is number {
  return typeof quantity === 'number' && Number.isInteger(quantity) && quantity > 0;
}

/** An amount of money the form may calculate with: finite and not negative. */
export function isValidLineAmount(amount: number | null | undefined): amount is number {
  return typeof amount === 'number' && Number.isFinite(amount) && amount >= 0;
}

/**
 * `round(value, 2)` with halves away from zero - the rounding every purchase amount uses
 * (`PurchaseGstPolicy`), rather than the banker's rounding a naive implementation would inherit.
 * The scaled value is taken to 12 significant digits first, so a binary artefact such as
 * `39.900000000000006` cannot push a cent, which is the same figure the server would compute from
 * exact decimals.
 */
export function roundToCents(value: number): number {
  const cents = Number((value * 100).toPrecision(12));
  return (cents < 0 ? -Math.round(-cents) : Math.round(cents)) / 100;
}

/**
 * The unit cost a line total implies: the plain quotient, deliberately **not** rounded, because
 * that is what makes the saved line's `round(quantity * unitCost, 2)` come back to the amount the
 * operator entered. A quotient rounded to the four decimals the form displays would not: $100.00
 * over 300 units is $0.3333... per unit, and 300 x $0.3333 is $99.99.
 *
 * `null` - no unit cost to show and none to submit - for a quantity that is not a whole number of
 * units above zero, or an amount that is not finite and non-negative, so a division by zero or a
 * `NaN` can never reach the request.
 */
export function unitCostFromLineTotal(
  lineTotal: number | null | undefined, quantity: number | null | undefined): number | null {
  if (!isValidLineAmount(lineTotal) || !isValidLineQuantity(quantity)) return null;
  const unitCost = lineTotal / quantity;
  return Number.isFinite(unitCost) ? unitCost : null;
}

/**
 * The line total a unit price implies, rounded to cents exactly as the server's own line amount is
 * (`PurchaseGstPolicy.LineAmount`), so the figure shown is the amount the purchase will hold.
 */
export function lineTotalFromUnitCost(
  unitCost: number | null | undefined, quantity: number | null | undefined): number | null {
  if (!isValidLineAmount(unitCost) || !isValidLineQuantity(quantity)) return null;
  return roundToCents(unitCost * quantity);
}

/**
 * The amount this line will actually save: the server's own `round(quantity * unitCost, 2)`, and
 * `0` while the line has no usable quantity or cost yet - the figure the items subtotal has always
 * summed.
 */
export function savedLineAmount(line: PurchaseLinePrice): number {
  return lineTotalFromUnitCost(line.unitCost, line.quantity) ?? 0;
}

/**
 * Brings the calculated figure back in step with the entered one, so neither can be shown stale.
 * Call it whenever the quantity or the entered amount changes: in `total` mode the entered total
 * decides the unit cost, and in `unit` mode the entered unit price decides the total.
 */
export function syncPurchaseLinePrice(line: PurchaseLinePrice): void {
  if (line.priceEntryMode === 'total') {
    line.unitCost = unitCostFromLineTotal(line.lineTotal, line.quantity);
  } else {
    line.lineTotal = lineTotalFromUnitCost(line.unitCost, line.quantity);
  }
}

/**
 * Switches which price the operator types. Changing the entry method never changes the unit cost
 * the line would submit - it only fills the figures the new mode shows - so toggling the selector
 * on a purchase being edited cannot quietly re-cost its stored line. The operator's next amount or
 * quantity change derives from there, through `syncPurchaseLinePrice`.
 */
export function applyPriceEntryMode(line: PurchaseLinePrice, mode: PriceEntryMode): void {
  line.priceEntryMode = mode;
  line.lineTotal = lineTotalFromUnitCost(line.unitCost, line.quantity);
}

/**
 * The one thing to tell the operator about this line's quantity and price, or `null` when it is
 * ready to save. The quantity comes first: in `total` mode an unusable quantity is also the reason
 * no unit cost can be derived from the amount paid.
 */
export function purchaseLinePriceProblem(line: PurchaseLinePrice): string | null {
  if (!isValidLineQuantity(line.quantity)) return 'Enter a whole number of units above zero.';
  if (line.priceEntryMode === 'total') {
    return isValidLineAmount(line.lineTotal) ? null : 'Enter the total price paid for this line.';
  }
  return isValidLineAmount(line.unitCost) ? null : 'Enter a unit price.';
}

/**
 * Whether this line may be submitted. The unit cost is checked in its own right as well as the
 * entered amount: it is the value the request carries, so a blank, negative or non-finite one must
 * stop the save whatever the entry fields look like.
 */
export function isPurchaseLinePriceComplete(line: PurchaseLinePrice): boolean {
  return purchaseLinePriceProblem(line) === null && isValidLineAmount(line.unitCost);
}

/** The message a form shows above itself when some of its lines cannot be saved. */
export function purchaseLinePriceErrorMessage(invalidLineCount: number): string {
  return 'Please enter a whole number of units and a valid price for every item. ' +
    `${invalidLineCount} item(s) are incomplete or invalid.`;
}
