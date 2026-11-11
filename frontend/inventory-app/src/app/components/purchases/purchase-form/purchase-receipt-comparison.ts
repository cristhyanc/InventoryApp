import { PurchaseLinePrice, isValidLineAmount, isValidLineQuantity, roundToCents, savedLineAmount } from '../purchase-line-price';

/**
 * The difference above which the server flags a purchase's entered total as a mismatch:
 * `Inventory.Domain.Purchases.PurchaseTotalValidationPolicy.Tolerance`. The comparison below uses
 * it only to say whether the Purchases list will flag the difference; it never blocks a save.
 */
export const RECEIPT_TOTAL_TOLERANCE = 1.0;

/**
 * What the optional Receipt total says about the purchase as entered (issue #1029):
 *
 * - `absent`: no receipt total was entered. Blank means absent, never zero.
 * - `incomplete`: some line has no usable quantity or price yet, so there is nothing honest to
 *   compare against.
 * - `match`: the receipt total equals the purchase total to the cent.
 * - `difference`: it does not; `difference` is the absolute amount, and `beyondTolerance` says
 *   whether it is large enough for the Purchases list's existing mismatch warning.
 *
 * It is a presentation of the existing rule, not a new one, and it changes nothing that is saved.
 */
export type ReceiptTotalComparison =
  | { kind: 'absent' }
  | { kind: 'incomplete' }
  | { kind: 'match' }
  | { kind: 'difference'; difference: number; beyondTolerance: boolean };

/** A charge as the purchase totals count it: an absent or unusable amount counts as nothing. */
function chargeAmount(amount: number | null | undefined): number {
  return isValidLineAmount(amount) ? amount : 0;
}

/**
 * The purchase total the summary shows: the items subtotal the forms have always summed (each line's
 * own `round(quantity * unitCost, 2)`) plus the delivery and packaging charges, to the cent. It is a
 * display figure; every saved total and every GST figure is still the server's.
 */
export function purchaseTotal(
  items: ReadonlyArray<PurchaseLinePrice>,
  deliveryCost: number | null | undefined,
  packageCost: number | null | undefined
): number {
  const subtotal = items.reduce((sum, item) => sum + savedLineAmount(item), 0);
  return roundToCents(subtotal + chargeAmount(deliveryCost) + chargeAmount(packageCost));
}

/**
 * Compares the entered receipt total with the purchase exactly as the server's
 * `PurchaseTotalValidationPolicy.Evaluate` does: the unrounded sum of quantity x unit cost, plus the
 * delivery and package charges, against the entered total, the absolute difference rounded to cents
 * with halves away from zero.
 *
 * `amountOutsideLines` is the one addition of issue #1166, and it is zero for every form that has
 * not scanned a receipt: the amounts a **scanned** receipt printed that this purchase deliberately
 * does not record - a line marked "Not stock, skip", and an unresolved container levy - so the
 * difference the operator is shown is explained rather than left to be chased. None of it is saved,
 * and it is never part of {@link purchaseTotal}: what the purchase costs is its own lines and
 * charges, exactly as before.
 */
export function compareReceiptTotal(
  receiptTotal: number | null | undefined,
  items: ReadonlyArray<PurchaseLinePrice>,
  deliveryCost: number | null | undefined,
  packageCost: number | null | undefined,
  amountOutsideLines: number | null | undefined = 0
): ReceiptTotalComparison {
  if (typeof receiptTotal !== 'number' || !Number.isFinite(receiptTotal)) return { kind: 'absent' };
  if (items.some((item) => !isValidLineQuantity(item.quantity) || !isValidLineAmount(item.unitCost))) {
    return { kind: 'incomplete' };
  }

  const itemSubtotal = items.reduce((sum, item) => sum + (item.quantity as number) * (item.unitCost as number), 0);
  const calculatedTotal =
    itemSubtotal + chargeAmount(deliveryCost) + chargeAmount(packageCost) + chargeAmount(amountOutsideLines);
  const difference = roundToCents(Math.abs(calculatedTotal - receiptTotal));

  return difference === 0
    ? { kind: 'match' }
    : { kind: 'difference', difference, beyondTolerance: difference > RECEIPT_TOTAL_TOLERANCE };
}
