import { trend, trendClass, trendLabel } from './revenue-trend';

describe('trend', () => {
  it('returns null when there is no prior-period value to compare against', () => {
    expect(trend(100, 0)).toBeNull();
  });

  it('returns the signed percentage change from previous to current', () => {
    expect(trend(150, 100)).toBe(50);
    expect(trend(50, 100)).toBe(-50);
  });

  /**
   * Revenue is withheld from an Operator by the frontend-level `Dashboard.ViewFinancials` check
   * (issue #699) before #697/#698 make the backend redact it the same way #527 already redacts
   * the Dashboard summary; either way, the field arrives `null` rather than omitted from a typed
   * object, so this must not be confused with "no prior sales" (a real, known zero).
   */
  it('returns null when either figure is null or undefined, rather than treating a withheld figure as zero', () => {
    expect(trend(null, 100)).toBeNull();
    expect(trend(100, null)).toBeNull();
    expect(trend(undefined, 100)).toBeNull();
    expect(trend(100, undefined)).toBeNull();
  });
});

describe('trendLabel', () => {
  it('labels a missing prior period explicitly rather than showing a misleading 0%', () => {
    expect(trendLabel(100, 0)).toBe('No prior sales');
  });

  it('labels an increase with an up arrow and the absolute percentage', () => {
    expect(trendLabel(150, 100)).toBe('↑ 50.0%');
  });

  it('labels a decrease with a down arrow and the absolute percentage', () => {
    expect(trendLabel(50, 100)).toBe('↓ 50.0%');
  });

  it('labels a withheld figure as unavailable rather than as no prior sales', () => {
    expect(trendLabel(null, 100)).toBe('Unavailable');
    expect(trendLabel(100, null)).toBe('Unavailable');
    expect(trendLabel(undefined, undefined)).toBe('Unavailable');
  });
});

describe('trendClass', () => {
  it('uses a neutral class when there is no prior period to compare against', () => {
    expect(trendClass(100, 0)).toBe('value-muted');
  });

  it('uses the positive class for an increase', () => {
    expect(trendClass(150, 100)).toBe('value-positive');
  });

  it('uses the negative class for a decrease', () => {
    expect(trendClass(50, 100)).toBe('value-negative');
  });

  it('uses the neutral class for a withheld figure', () => {
    expect(trendClass(null, 100)).toBe('value-muted');
    expect(trendClass(100, null)).toBe('value-muted');
  });
});
