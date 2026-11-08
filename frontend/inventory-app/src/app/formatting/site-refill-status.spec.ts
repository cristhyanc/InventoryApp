import {
  compareRefillPriority,
  compareSiteNames,
  knownCount,
  knownPercentage,
  siteNeedsRefill,
  siteRefillStatus,
  SiteForOrdering
} from './site-refill-status';

function site(siteId: number, siteName: string, low: number | null | undefined, empty: number | null | undefined): SiteForOrdering {
  return { siteId, siteName, lowProductCount: low, emptyProductCount: empty };
}

describe('siteRefillStatus (issue #1128)', () => {
  it('shows Stock OK only when both counts are known zeros', () => {
    const status = siteRefillStatus({ lowProductCount: 0, emptyProductCount: 0 });
    expect(status.tone).toBe('ok');
    expect(status.label).toBe('Stock OK');
    expect(status.needsRefill).toBe(false);
  });

  it('shows red empty and amber low counts as explicit text, empty first', () => {
    const status = siteRefillStatus({ lowProductCount: 4, emptyProductCount: 2 });
    expect(status.tone).toBe('empty');
    expect(status.chips).toEqual([
      { kind: 'empty', text: '2 empty' },
      { kind: 'low', text: '4 low' }
    ]);
    expect(status.needsRefill).toBe(true);
  });

  it('shows a low-only site as amber without a zero empty chip', () => {
    const status = siteRefillStatus({ lowProductCount: 4, emptyProductCount: 0 });
    expect(status.tone).toBe('low');
    expect(status.chips).toEqual([{ kind: 'low', text: '4 low' }]);
  });

  it('keeps a known positive count and names the missing one when only one count is known', () => {
    expect(siteRefillStatus({ lowProductCount: 3, emptyProductCount: null }).chips).toEqual([
      { kind: 'low', text: '3 low' },
      { kind: 'missing', text: 'Empty count unavailable' }
    ]);
    const emptyOnly = siteRefillStatus({ lowProductCount: undefined, emptyProductCount: 1 });
    expect(emptyOnly.tone).toBe('empty');
    expect(emptyOnly.chips).toEqual([
      { kind: 'empty', text: '1 empty' },
      { kind: 'missing', text: 'Low count unavailable' }
    ]);
    expect(emptyOnly.needsRefill).toBe(true);
  });

  it('shows Stock unavailable when neither count is known', () => {
    const status = siteRefillStatus({ lowProductCount: null, emptyProductCount: undefined });
    expect(status.tone).toBe('unavailable');
    expect(status.label).toBe('Stock unavailable');
    expect(status.needsRefill).toBe(false);
  });

  it('shows Stock unavailable, not Stock OK, when the one known count is zero and the other is missing', () => {
    expect(siteRefillStatus({ lowProductCount: 0, emptyProductCount: null }).tone).toBe('unavailable');
    expect(siteRefillStatus({ lowProductCount: undefined, emptyProductCount: 0 }).tone).toBe('unavailable');
  });

  it('treats a negative or non-finite count as unknown rather than as a healthy zero', () => {
    expect(knownCount(-1)).toBeNull();
    expect(knownCount(Number.NaN)).toBeNull();
    expect(knownCount(Number.POSITIVE_INFINITY)).toBeNull();
    expect(knownCount(0)).toBe(0);
    expect(siteRefillStatus({ lowProductCount: -1, emptyProductCount: 0 }).tone).toBe('unavailable');
  });

  it('treats a non-finite percentage as unavailable and keeps a real 0%', () => {
    expect(knownPercentage(null)).toBeNull();
    expect(knownPercentage(undefined)).toBeNull();
    expect(knownPercentage(Number.NaN)).toBeNull();
    expect(knownPercentage(0)).toBe(0);
    expect(knownPercentage(92.4)).toBe(92.4);
  });
});

describe('siteNeedsRefill (issue #1128)', () => {
  it('includes a known positive low or empty count even when the other count is unknown', () => {
    expect(siteNeedsRefill({ lowProductCount: 2, emptyProductCount: null })).toBe(true);
    expect(siteNeedsRefill({ lowProductCount: null, emptyProductCount: 1 })).toBe(true);
  });

  it('excludes known-healthy and unknown sites', () => {
    expect(siteNeedsRefill({ lowProductCount: 0, emptyProductCount: 0 })).toBe(false);
    expect(siteNeedsRefill({ lowProductCount: null, emptyProductCount: null })).toBe(false);
    expect(siteNeedsRefill({ lowProductCount: 0, emptyProductCount: null })).toBe(false);
  });
});

describe('compareRefillPriority (issue #1128)', () => {
  const order = (sites: SiteForOrdering[]) => [...sites].sort(compareRefillPriority).map((s) => s.siteName);

  it('puts more empty slots first, then more low slots, then unavailable, then Stock OK', () => {
    expect(
      order([
        site(1, 'Healthy', 0, 0),
        site(2, 'Low two', 2, 0),
        site(3, 'Unknown', null, null),
        site(4, 'Empty one', 9, 1),
        site(5, 'Empty three', 0, 3),
        site(6, 'Low seven', 7, 0)
      ])
    ).toEqual(['Empty three', 'Empty one', 'Low seven', 'Low two', 'Unknown', 'Healthy']);
  });

  it('orders equal empty counts by descending low count', () => {
    expect(order([site(1, 'A', 1, 2), site(2, 'B', 5, 2)])).toEqual(['B', 'A']);
  });

  it('never sorts an unknown count as a healthy site', () => {
    expect(order([site(1, 'Aardvark OK', 0, 0), site(2, 'Zebra unknown', 0, null)])).toEqual(['Zebra unknown', 'Aardvark OK']);
  });

  it('orders a known low count by the low count alone when the empty count is missing', () => {
    expect(order([site(1, 'Known empty zero', 2, 0), site(2, 'Missing empty', 5, null)])).toEqual([
      'Missing empty',
      'Known empty zero'
    ]);
  });

  it('breaks ties by name, case-insensitively, and then by id', () => {
    expect(order([site(3, 'pavilion', 1, 0), site(1, 'Avenue', 1, 0), site(2, 'Pavilion', 1, 0)])).toEqual([
      'Avenue',
      'Pavilion',
      'pavilion'
    ]);
    expect([site(9, 'Same', 0, 0), site(4, 'Same', 0, 0)].sort(compareRefillPriority).map((s) => s.siteId)).toEqual([4, 9]);
  });

  it('gives the same order whatever the input order', () => {
    const sites = [site(1, 'C', 1, 0), site(2, 'A', 1, 0), site(3, 'B', 0, 1), site(4, 'D', 0, 0)];
    expect(order(sites)).toEqual(order([...sites].reverse()));
  });
});

describe('compareSiteNames (issue #1128)', () => {
  it('orders by name then id', () => {
    expect(
      [site(2, 'b', 0, 0), site(3, 'A', 0, 0), site(1, 'B', 0, 0)].sort(compareSiteNames).map((s) => s.siteId)
    ).toEqual([3, 1, 2]);
  });
});
