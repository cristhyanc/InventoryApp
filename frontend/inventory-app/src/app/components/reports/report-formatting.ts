const currencyFormatter = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' });

/**
 * Formats a known monetary amount. Only call this with a value that is guaranteed non-null;
 * for a nullable financial field (COGS, profit, etc.) use `moneyOrUnavailable` instead so an
 * unknown value is never displayed as if it were a real `$0.00`.
 */
export function money(value: number | null | undefined): string {
  return currencyFormatter.format(value ?? 0);
}

export function moneyOrUnavailable(value: number | null | undefined, unavailableLabel = 'Profit unavailable'): string {
  return value == null ? unavailableLabel : money(value);
}

export function percentOrUnavailable(value: number | null | undefined, suffix = '', unavailableLabel = '—'): string {
  return value == null ? unavailableLabel : `${value.toFixed(1)}%${suffix}`;
}
