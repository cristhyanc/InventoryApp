/**
 * The week-over-week (or any current-vs-previous) percentage change, or `null` when `previous` is
 * zero and a percentage change is undefined. Shared by the dashboard's site/machine/total rows and
 * the Sites list page so a change to the formula only needs to happen once.
 */
export function trend(current: number, previous: number): number | null {
  return previous === 0 ? null : ((current - previous) / previous) * 100;
}

/** The arrow-and-percentage label for `trend(current, previous)`. */
export function trendLabel(current: number, previous: number): string {
  const value = trend(current, previous);
  if (value === null) return 'No prior sales';
  return `${value >= 0 ? '↑' : '↓'} ${Math.abs(value).toFixed(1)}%`;
}

/** The Tailwind text color class for `trend(current, previous)`. */
export function trendClass(current: number, previous: number): string {
  const value = trend(current, previous);
  if (value === null) return 'text-slate-500';
  return value >= 0 ? 'text-emerald-600' : 'text-rose-600';
}
