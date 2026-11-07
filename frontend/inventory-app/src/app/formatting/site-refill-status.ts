/**
 * The Sites overview's refill status, filter and priority order (issue #1128).
 *
 * `GET /api/sites` publishes a site's low and empty product counts as whole numbers, and a site whose
 * stock read failed is not published at all, so today every count this page receives is known. The
 * page still reads each count defensively, because the issue's rule is that an **unknown** count must
 * never be shown or sorted as a healthy zero: a count that is absent, `null`, negative or not a finite
 * number is treated as unknown here, and only a count that is known to be zero contributes to
 * "Stock OK".
 *
 * Nothing here recalculates a stock figure. The counts and the percentage are the backend's own
 * (`SiteStockPolicy`); this module only decides how the page labels, filters and orders them.
 */

/** The two counts the refill status reads; a {@link Site} satisfies it. */
export interface SiteStockCounts {
  lowProductCount?: number | null;
  emptyProductCount?: number | null;
}

/** A site row as far as ordering needs it; a {@link Site} satisfies it. */
export interface SiteForOrdering extends SiteStockCounts {
  siteId: number;
  siteName: string;
}

/**
 * - `empty`: a known positive empty count (red), whatever the low count is.
 * - `low`: a known positive low count (amber) and no known positive empty count.
 * - `ok`: both counts known to be zero.
 * - `unavailable`: neither count known, or no known count indicates a refill and the other is missing.
 */
export type SiteRefillTone = 'empty' | 'low' | 'ok' | 'unavailable';

/** One chip of a site's refill status: a positive count, or the one count that is missing. */
export interface SiteRefillChip {
  kind: 'empty' | 'low' | 'missing';
  text: string;
}

export interface SiteRefillStatus {
  tone: SiteRefillTone;
  /** `null` when unknown. */
  low: number | null;
  /** `null` when unknown. */
  empty: number | null;
  /** True only for a known positive low or empty count; an unknown count never sets it. */
  needsRefill: boolean;
  /**
   * What the status shows, in order: the known positive empty count, the known positive low count,
   * then - when exactly one count is known and it indicates a refill - which count is missing.
   * Empty for `ok` and `unavailable`, which are shown by {@link label} alone.
   */
  chips: readonly SiteRefillChip[];
  /** The whole status as one sentence, for screen readers and for `ok`/`unavailable`. */
  label: string;
}

/** The count when it is known (a finite, non-negative number), otherwise `null`. */
export function knownCount(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
}

/** The stock percentage when it is a finite number, otherwise `null` (shown as unavailable, no fill). */
export function knownPercentage(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

export function siteRefillStatus(site: SiteStockCounts): SiteRefillStatus {
  const low = knownCount(site.lowProductCount);
  const empty = knownCount(site.emptyProductCount);
  const hasEmpty = empty !== null && empty > 0;
  const hasLow = low !== null && low > 0;

  if (hasEmpty || hasLow) {
    const chips: SiteRefillChip[] = [];
    if (hasEmpty) chips.push({ kind: 'empty', text: `${empty} empty` });
    if (hasLow) chips.push({ kind: 'low', text: `${low} low` });
    if (empty === null) chips.push({ kind: 'missing', text: 'Empty count unavailable' });
    if (low === null) chips.push({ kind: 'missing', text: 'Low count unavailable' });
    return {
      tone: hasEmpty ? 'empty' : 'low',
      low,
      empty,
      needsRefill: true,
      chips,
      label: chips.map((chip) => chip.text).join(', ')
    };
  }

  if (low === 0 && empty === 0) {
    return { tone: 'ok', low, empty, needsRefill: false, chips: [], label: 'Stock OK' };
  }

  return { tone: 'unavailable', low, empty, needsRefill: false, chips: [], label: 'Stock unavailable' };
}

/** The Needs refill filter: a known positive low or empty count, even when the other is unknown. */
export function siteNeedsRefill(site: SiteStockCounts): boolean {
  return siteRefillStatus(site).needsRefill;
}

/**
 * Where a tone sits in refill priority. An unavailable site sits ahead of a site known to be fine,
 * because an unknown count does not imply a healthy site.
 */
const TONE_RANK: Record<SiteRefillTone, number> = { empty: 0, low: 1, unavailable: 2, ok: 3 };

/** Name order with a stable id tie-break, so equal names never swap places between renders. */
export function compareSiteNames(a: SiteForOrdering, b: SiteForOrdering): number {
  return a.siteName.localeCompare(b.siteName, 'en', { sensitivity: 'base' }) || a.siteId - b.siteId;
}

/**
 * Refill priority: sites with a known positive empty count first (more empty slots first), then by
 * descending known low count (an unknown low count after any known one), then the unavailable sites,
 * then the sites known to be fine; name, then id, breaks every tie.
 */
export function compareRefillPriority(a: SiteForOrdering, b: SiteForOrdering): number {
  const left = siteRefillStatus(a);
  const right = siteRefillStatus(b);
  const byTone = TONE_RANK[left.tone] - TONE_RANK[right.tone];
  if (byTone !== 0) return byTone;
  if (left.tone === 'empty' || left.tone === 'low') {
    // Within the empty tier both empty counts are known and positive; within the low tier neither
    // site has a known positive empty count, so only the low counts order them.
    const byEmpty = left.tone === 'empty' ? (right.empty ?? 0) - (left.empty ?? 0) : 0;
    const byLow = (right.low ?? -1) - (left.low ?? -1);
    if (byEmpty !== 0) return byEmpty;
    if (byLow !== 0) return byLow;
  }
  return compareSiteNames(a, b);
}
