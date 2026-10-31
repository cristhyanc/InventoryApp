import { Site } from '../models/models';
import { siteStockClass, stockPercentageClass } from './site-stock-status';

function site(totalStockPercentage: number): Site {
  return {
    siteId: 1,
    siteName: 'North Mall',
    machineCount: 3,
    totalStockPercentage,
    lowProductCount: 0,
    emptyProductCount: 0,
    todayRevenue: 0,
    currentWeekRevenue: 0,
    previousComparableWeekRevenue: 0
  };
}

describe('siteStockClass', () => {
  it('flags stock below 30% as low', () => {
    expect(siteStockClass(site(29))).toBe('badge-danger');
  });

  it('flags stock from 30% up to (but excluding) 80% as medium', () => {
    expect(siteStockClass(site(30))).toBe('badge-warning');
    expect(siteStockClass(site(79))).toBe('badge-warning');
  });

  it('flags stock at or above 80% as healthy', () => {
    expect(siteStockClass(site(80))).toBe('badge-success');
  });
});

/**
 * A machine's own percentage (issue #1013) is classified on exactly the same thresholds, which is
 * what keeps a machine row's colour and words agreeing with the site row above it.
 */
describe('stockPercentageClass', () => {
  it('uses the same thresholds a site is classified on', () => {
    expect(stockPercentageClass(29)).toBe('badge-danger');
    expect(stockPercentageClass(30)).toBe('badge-warning');
    expect(stockPercentageClass(79)).toBe('badge-warning');
    expect(stockPercentageClass(80)).toBe('badge-success');
  });

  it('classifies a percentage outside the usual range rather than refusing it', () => {
    expect(stockPercentageClass(-50)).toBe('badge-danger');
    expect(stockPercentageClass(140)).toBe('badge-success');
  });

  it('agrees with siteStockClass for the same figure', () => {
    for (const percentage of [0, 29, 30, 79, 80, 100]) {
      expect(stockPercentageClass(percentage)).toBe(siteStockClass(site(percentage)));
    }
  });
});
