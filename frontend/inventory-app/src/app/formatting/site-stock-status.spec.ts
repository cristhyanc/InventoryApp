import { Site } from '../models/models';
import { siteStockClass } from './site-stock-status';

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
