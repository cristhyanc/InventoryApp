const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * The title Add Purchase suggests until the person types one of their own (issue #1029): the
 * supplier's name and the purchase date, such as "Costco Wholesale — 24 Oct 2026". It is a
 * suggestion only; the person can always overwrite it, and once they do nothing replaces it.
 *
 * `purchaseDate` is the form's own `yyyy-MM-dd` date-input value, read as the calendar day it names
 * rather than parsed as an instant, so no time zone can move it to another day. A purchase with no
 * supplier is titled "Purchase", and a blank or unreadable date is left out.
 */
export function purchaseTitleSuggestion(supplierName: string | null | undefined, purchaseDate: string): string {
  const name = supplierName?.trim() || 'Purchase';
  const day = formatCalendarDay(purchaseDate);
  return day ? `${name} — ${day}` : name;
}

function formatCalendarDay(value: string): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '');
  if (!match) return null;
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (month < 1 || month > 12 || day < 1 || day > 31) return null;
  return `${day} ${MONTHS[month - 1]} ${match[1]}`;
}
