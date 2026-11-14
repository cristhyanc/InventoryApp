import {
  PURCHASE_DATE_AFTER_TODAY_MESSAGE,
  isPurchaseDateAfter,
  latestPurchaseDate
} from './purchase-date-limit';

describe('purchase date limit (issue #1246)', () => {
  describe('latestPurchaseDate', () => {
    /**
     * The boundary the acceptance criteria name: 00:30 in Sydney on 15 November is still 14
     * November in UTC, and the latest date a purchase may carry is the business's 15th.
     */
    it('is the business date, not the UTC date, just after midnight in Sydney', () => {
      const justAfterSydneyMidnight = new Date('2026-11-14T13:30:00Z');

      expect(latestPurchaseDate('Australia/Sydney', justAfterSydneyMidnight)).toBe('2026-11-15');
      expect(justAfterSydneyMidnight.toISOString().slice(0, 10)).toBe('2026-11-14');
    });

    it('is the business date in a zone behind UTC too', () => {
      // 23:30 UTC on 14 November is 18:30 on the 14th in New York - and the UTC date is about to
      // become the 15th while the business day has not changed.
      expect(latestPurchaseDate('America/New_York', new Date('2026-11-14T23:30:00Z'))).toBe('2026-11-14');
    });

    it('pads single-digit months and days, as a date input requires', () => {
      expect(latestPurchaseDate('Australia/Sydney', new Date('2026-01-04T00:00:00Z'))).toBe('2026-01-04');
    });

    /**
     * While the zone is still loading the browser's own local date is used rather than the UTC one,
     * which in a zone ahead of UTC would be yesterday for most of the working day.
     */
    it('falls back to the browser local date while the business zone is unknown', () => {
      const now = new Date(2026, 10, 15, 9, 0, 0);

      expect(latestPurchaseDate(null, now)).toBe('2026-11-15');
    });
  });

  describe('isPurchaseDateAfter', () => {
    it('reports a later date', () => {
      expect(isPurchaseDateAfter('2026-11-16', '2026-11-15')).toBe(true);
      expect(isPurchaseDateAfter('2026-12-01', '2026-11-15')).toBe(true);
      expect(isPurchaseDateAfter('2027-01-01', '2026-11-15')).toBe(true);
    });

    it('accepts today and every earlier date', () => {
      expect(isPurchaseDateAfter('2026-11-15', '2026-11-15')).toBe(false);
      expect(isPurchaseDateAfter('2026-11-14', '2026-11-15')).toBe(false);
      expect(isPurchaseDateAfter('2025-12-31', '2026-11-15')).toBe(false);
    });

    it('has nothing to judge for a blank or incomplete field', () => {
      expect(isPurchaseDateAfter('', '2026-11-15')).toBe(false);
      expect(isPurchaseDateAfter('2026-11', '2026-11-15')).toBe(false);
      expect(isPurchaseDateAfter('not-a-date', '2026-11-15')).toBe(false);
    });
  });

  it('shows the message the server refuses the save with', () => {
    expect(PURCHASE_DATE_AFTER_TODAY_MESSAGE).toBe(
      'Purchase date cannot be later than today. Correct the date and save again.'
    );
  });
});
