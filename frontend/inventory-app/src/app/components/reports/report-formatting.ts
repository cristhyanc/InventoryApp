import { ReportAvailability } from '../../features/reports/shared/models/reporting.models';

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

/**
 * The "as of" instant is rendered in UTC on purpose (issue #730). It is the moment a successful
 * synchronization or import reached, not a business day, so it must not be shifted into the
 * browser's zone - which would also contradict the rule that business dates come from the
 * business's own configured zone and never from the browser (AGENTS.md § Reporting dates).
 */
const asOfFormatter = new Intl.DateTimeFormat('en-AU', {
  timeZone: 'UTC', day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false,
});

/**
 * The short label for a provider figure family whose coverage is not complete (issue #730), or an
 * empty string when it is complete (or when the API did not report a state at all, which an older
 * response may not).
 */
export function availabilityBadge(availability?: ReportAvailability | null): string {
  switch (availability?.state) {
    case 'Missing':
      return 'No provider data';
    case 'Stale':
      return availability.asOfUtc
        ? `As of ${asOfFormatter.format(new Date(availability.asOfUtc))} UTC`
        : 'Not covered to the period end';
    case 'Unverified':
      return 'Unverified';
    default:
      return '';
  }
}

/**
 * A provider-sourced amount, or the availability state in its place.
 *
 * A zero is only ever printed when coverage says the period was read end to end. In every other
 * state an amount of zero is indistinguishable from "we never read this", so the state is shown
 * instead - which is the whole point of issue #730 and of the repository rule that unavailable
 * financial values are never rendered as `0` (AGENTS.md § Frontend rules). A non-zero amount in an
 * incomplete state is real partial data and stays visible, with its badge beside it.
 */
export function moneyOrState(
  value: number | null | undefined,
  availability?: ReportAvailability | null,
  unavailableLabel = 'Unavailable'
): string {
  if (!availability || availability.state === 'Complete') return moneyOrUnavailable(value, unavailableLabel);
  return value == null || value === 0 ? availabilityBadge(availability) : money(value);
}

/**
 * A provider-sourced count or quantity, or the availability state in its place, by exactly the rule
 * `moneyOrState` applies to an amount (issue #731). A transaction count, a quantity or a machine
 * count of `0` with no coverage behind it reads as "nothing happened here", which nothing
 * established - and unlike an amount it must not be formatted as currency.
 */
export function countOrState(
  value: number | null | undefined,
  availability?: ReportAvailability | null,
  unavailableLabel = 'Unavailable'
): string {
  if (!availability || availability.state === 'Complete') return value == null ? unavailableLabel : `${value}`;
  return value == null || value === 0 ? availabilityBadge(availability) : `${value}`;
}

/**
 * A provider-sourced percentage, or the availability state in its place, by exactly the rule
 * `moneyOrState` applies to an amount (issue #730). A margin of `0.0%` without coverage behind it
 * is the same false measurement as `$0.00`: it reads as "this period broke even", which nothing
 * established. A real non-zero margin stays visible with its badge.
 */
export function percentOrState(
  value: number | null | undefined,
  availability?: ReportAvailability | null,
  unavailableLabel = 'Profit unavailable'
): string {
  if (!availability || availability.state === 'Complete') return percentOrUnavailable(value, '', unavailableLabel);
  return value == null || value === 0 ? availabilityBadge(availability) : percentOrUnavailable(value);
}
