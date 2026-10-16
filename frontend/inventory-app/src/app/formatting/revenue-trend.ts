/**
 * The week-over-week (or any current-vs-previous) percentage change, or `null` when `previous` is
 * zero and a percentage change is undefined, or when either figure is `null`/`undefined` - a
 * revenue figure withheld from an Operator (issue #699) or later redacted by the backend (#697,
 * #698). Shared by the dashboard's site/machine/total rows and the Sites list page so a change to
 * the formula only needs to happen once.
 */
export function trend(current: number | null | undefined, previous: number | null | undefined): number | null {
  if (current == null || previous == null || previous === 0) return null;
  return ((current - previous) / previous) * 100;
}

/**
 * The arrow-and-percentage label for `trend(current, previous)`, or `'Unavailable'` when either
 * figure is withheld - a different statement from "no prior sales", which is a real, known zero.
 */
export function trendLabel(current: number | null | undefined, previous: number | null | undefined): string {
  if (current == null || previous == null) return 'Unavailable';
  const value = trend(current, previous);
  if (value === null) return 'No prior sales';
  return `${value >= 0 ? '↑' : '↓'} ${Math.abs(value).toFixed(1)}%`;
}

/** The shared Material Dashboard value class for `trend(current, previous)`. */
export function trendClass(current: number | null | undefined, previous: number | null | undefined): string {
  if (current == null || previous == null) return 'value-muted';
  const value = trend(current, previous);
  if (value === null) return 'value-muted';
  return value >= 0 ? 'value-positive' : 'value-negative';
}
