import { currentDateInTimeZone } from '../../../formatting/business-time-zone';

/**
 * The purchase date's own rule on both purchase forms (issue #1246): it is never later than today
 * in the business's own time zone.
 *
 * The server refuses such a save with `400` (`Inventory.Domain.Purchases.PurchaseDatePolicy`); this
 * is what keeps the date input from offering the date at all, and what explains a date a receipt
 * scan filled in before anything is sent. The message is deliberately the server's own wording, so
 * a refusal reads the same whichever side produced it.
 */
export const PURCHASE_DATE_AFTER_TODAY_MESSAGE =
  'Purchase date cannot be later than today. Correct the date and save again.';

/**
 * Today's calendar date in `timeZoneId` as an `<input type="date">` value, which is the latest date
 * a purchase may carry. `null` is "the business time zone is not known yet", which falls back to the
 * browser's own local date rather than the UTC one (the same fallback issue #1171 made for the
 * expense form): the UTC date is a different calendar day from late morning in a zone ahead of UTC
 * until it catches up, so using it would refuse today's own purchases there.
 */
export function latestPurchaseDate(timeZoneId: string | null, now: Date = new Date()): string {
  const pad = (value: number) => value.toString().padStart(2, '0');
  if (timeZoneId !== null) {
    const { year, month, day } = currentDateInTimeZone(now, timeZoneId);
    return `${year}-${pad(month)}-${pad(day)}`;
  }
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/**
 * Whether `purchaseDate` - a date input's own `yyyy-MM-dd` value - is later than `latest`. Both are
 * zero-padded calendar dates, so comparing them as text compares the days themselves, with no
 * instant, zone or browser-local midnight in between. A blank or incomplete field has no date to be
 * later than anything.
 */
export function isPurchaseDateAfter(purchaseDate: string, latest: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(purchaseDate)) return false;
  return purchaseDate > latest;
}
