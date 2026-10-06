import { trend, trendClass, trendLabel } from './revenue-trend';

describe('trend', () => {
  it('returns null when there is no prior-period value to compare against', () => {
    expect(trend(100, 0)).toBeNull();
  });

  it('returns the signed percentage change from previous to current', () => {
    expect(trend(150, 100)).toBe(50);
    expect(trend(50, 100)).toBe(-50);
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
});
