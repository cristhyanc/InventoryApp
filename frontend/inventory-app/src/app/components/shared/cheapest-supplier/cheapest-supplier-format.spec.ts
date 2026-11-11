import { CheapestSupplierOffer, SupplierOfferIneligibility, SupplierPriceBasis, SupplierStockStatus } from '../../../models/cheapest-supplier';
import { ineligibilityLabel, offerStatus } from './cheapest-supplier-format';

function offer(overrides: Partial<CheapestSupplierOffer> = {}): CheapestSupplierOffer {
  return {
    supplierId: 1,
    supplierName: 'Coles',
    hasListing: true,
    hasPrice: true,
    packQuantity: 24,
    packPrice: 22.8,
    packPriceBasis: SupplierPriceBasis.InclusiveOfGst,
    unitPrice: 0.95,
    unitPriceExGst: 0.8636,
    unitPriceIncGst: 0.95,
    isSpecial: false,
    promotionEndsAtUtc: null,
    stockStatus: SupplierStockStatus.InStock,
    lastCheckedAtUtc: '2026-11-11T04:00:00Z',
    productUrl: 'https://example.test/coles/cola',
    ineligibility: null,
    isCheapest: false,
    ...overrides
  };
}

describe('ineligibilityLabel (issues #925 and #1200)', () => {
  it.each([
    [SupplierOfferIneligibility.PackSizeUnknown, 'Pack size not published, so there is no unit price'],
    [SupplierOfferIneligibility.ListingNotConfirmed, 'Match not confirmed, so this price is not compared'],
    [SupplierOfferIneligibility.GstRateUnknown, 'GST rate unknown, so this price is not compared'],
    [SupplierOfferIneligibility.OutOfStock, 'Out of stock'],
    [SupplierOfferIneligibility.PromotionEnded, 'The special has ended'],
    [SupplierOfferIneligibility.Stale, 'Last check is too old to compare'],
    [
      SupplierOfferIneligibility.SearchStopped,
      'Price search stopped for this supplier, so this price is not compared'
    ]
  ])('says in words why reason %s was not compared', (reason, expected) => {
    expect(ineligibilityLabel(reason)).toBe(expected);
  });

  /**
   * The server's vocabulary and this map are the same size, so a reason added to the contract
   * without a sentence here is a row that would read as having no reason at all (issue #1200's new
   * value 7 is the case that found this).
   */
  it('has a sentence for every reason the server can report', () => {
    const reasons = Object.values(SupplierOfferIneligibility).filter(
      (value): value is SupplierOfferIneligibility => typeof value === 'number'
    );

    expect(reasons.length).toBe(7);
    for (const reason of reasons) {
      expect(ineligibilityLabel(reason)).not.toBeNull();
    }
  });

  it('says nothing when the offer was compared', () => {
    expect(ineligibilityLabel(null)).toBeNull();
  });
});

describe('offerStatus (issue #930)', () => {
  it('says a stopped search is why a listed, priced supplier is not compared', () => {
    expect(offerStatus(offer({ ineligibility: SupplierOfferIneligibility.SearchStopped }))).toBe(
      'Price search stopped for this supplier, so this price is not compared'
    );
  });

  it('keeps "no match" and "not checked yet" their own answers', () => {
    expect(offerStatus(offer({ hasListing: false, hasPrice: false }))).toBe('No match');
    expect(offerStatus(offer({ hasPrice: false }))).toBe('Not checked yet');
  });

  it('says nothing beside a supplier whose price was compared', () => {
    expect(offerStatus(offer())).toBeNull();
  });
});
