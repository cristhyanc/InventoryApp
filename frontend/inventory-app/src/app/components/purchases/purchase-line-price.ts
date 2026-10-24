/**
 * Per-line price entry for the Add and Edit purchase forms (issues #707 and #878).
 *
 * Stock usually arrives in boxes priced per box, so each purchase line shows both of its prices as
 * editable fields - Unit price and Total price - and the one the operator typed into last decides
 * the other, which the form derives from that line's purchased **unit** quantity. Linked entry
 * fields are a convenience and nothing else: no pack size, units-per-box or carton is modelled,
 * which field was typed is never sent to or stored by the server, and a line is still persisted as
 * the quantity and unit cost the purchase contract already carried.
 *
 * Both forms share this one module so Add and Edit cannot drift apart on the arithmetic (AGENTS.md
 * § Architecture rules: one authoritative calculation feeds all presentations). The authoritative
 * money rules stay on the server: a line's GST-inclusive amount is `round(Quantity * UnitCost, 2)`
 * in `Inventory.Domain.Purchases.PurchaseGstPolicy`, and every GST figure is the API's. What is
 * computed here is the complementary figure the operator sees before saving, and the unit cost the
 * existing request already carried.
 */

/**
 * Which of a line's two prices is the entered figure - the one the operator typed into last - and
 * so which one the other is calculated from. Both are editable fields; this is the winner, not a
 * mode the operator chooses (issue #878).
 */
export type PriceEntryMode = 'total' | 'unit';

/**
 * Which field wins on a line nobody has typed into yet. Roughly 80% of purchases arrive priced per
 * box, so the amount paid for the whole line is what an operator usually has in front of them.
 */
export const DEFAULT_PRICE_ENTRY_MODE: PriceEntryMode = 'total';

/**
 * Which field wins on a line already stored on a purchase, when the edit form opens it: the unit
 * cost is the figure the purchase actually holds, and calculating the total from it is what keeps an
 * edit of something else - a quantity correction, a date, a classification - from re-costing the
 * line.
 */
export const STORED_LINE_PRICE_ENTRY_MODE: PriceEntryMode = 'unit';

/**
 * Decimals the Unit price field shows for a unit cost the form calculated. It is a display figure
 * only: what the line submits is the unrounded quotient (see {@link unitCostFromLineTotal}).
 */
const CALCULATED_UNIT_PRICE_DECIMALS = 4;

/** Decimals the Total price field shows for a total the form calculated, which saves to the cent. */
const CALCULATED_TOTAL_PRICE_DECIMALS = 2;

/**
 * One purchase line's quantity and the two prices that describe it. Both amounts are nullable
 * because a number input binds `null` for a blank field (and `NaN` for text that is not a number),
 * and because a figure that cannot be derived must be cleared rather than left stale.
 *
 * `unitCost` is the value the request carries, whichever field was typed into. `lineTotal` is the
 * entered amount while `priceEntryMode` is `total` and the calculated one while it is `unit`.
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
 * Call it whenever the quantity or the entered amount changes: an entered total is spread over the
 * line's units, and an entered unit price is multiplied by them.
 */
export function syncPurchaseLinePrice(line: PurchaseLinePrice): void {
  if (line.priceEntryMode === 'total') {
    line.unitCost = unitCostFromLineTotal(line.lineTotal, line.quantity);
  } else {
    line.lineTotal = lineTotalFromUnitCost(line.unitCost, line.quantity);
  }
}

/**
 * The operator typed in the Total price field, so that amount is now the entered figure and the
 * unit cost is calculated from it. Both purchase forms route that field's changes through here, so
 * which field wins is decided in one place rather than once per form (issue #878).
 */
export function enterLineTotal(line: PurchaseLinePrice, lineTotal: number | null): void {
  line.priceEntryMode = 'total';
  line.lineTotal = lineTotal;
  syncPurchaseLinePrice(line);
}

/**
 * The operator typed in the Unit price field, so that price is now the entered figure and the line
 * total is calculated from it. It is called only on an actual edit of the field - never on focus or
 * blur - which is what keeps an unrounded calculated unit cost from being replaced by the rounded
 * figure the field was displaying.
 */
export function enterUnitCost(line: PurchaseLinePrice, unitCost: number | null): void {
  line.priceEntryMode = 'unit';
  line.unitCost = unitCost;
  syncPurchaseLinePrice(line);
}

/**
 * What the Unit price field shows. The entered figure is shown exactly as it was typed; a
 * calculated one is shown rounded to four decimals, as a string so the trailing decimals survive -
 * $100.00 over 300 units displays `0.3333` while `line.unitCost`, the value the request carries,
 * stays the unrounded quotient. `null` - an empty field - while there is no unit cost to show.
 */
export function unitPriceFieldValue(line: PurchaseLinePrice): number | string | null {
  if (line.priceEntryMode === 'unit') return line.unitCost;
  return calculatedFieldValue(line.unitCost, CALCULATED_UNIT_PRICE_DECIMALS);
}

/**
 * What the Total price field shows: the entered amount exactly as it was typed, or the calculated
 * `round(quantity x unitCost, 2)` to the cent it will save at - `42.00` rather than `42` - and
 * `null` while there is no total to show.
 */
export function totalPriceFieldValue(line: PurchaseLinePrice): number | string | null {
  if (line.priceEntryMode === 'total') return line.lineTotal;
  return calculatedFieldValue(line.lineTotal, CALCULATED_TOTAL_PRICE_DECIMALS);
}

/** A calculated amount as its field displays it, or `null` when there is none to display. */
function calculatedFieldValue(amount: number | null, decimals: number): string | null {
  return isValidLineAmount(amount) ? amount.toFixed(decimals) : null;
}

/**
 * The one thing to tell the operator about this line's quantity and price, or `null` when it is
 * ready to save. The quantity comes first: under an entered total, an unusable quantity is also the
 * reason no unit cost can be derived from the amount paid.
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
