import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import {
  BarcodeScope,
  LISTING_MATCH_STATUS_LABELS,
  MASTER_PRODUCT_SIZE_UNIT_LABELS,
  MasterProductSizeUnit,
  PriceCatalogueReviewQueue,
  ProductMatchReviewDecision,
  SupplierListingMatchStatus,
  SupplierListingReviewOutcome,
  SuperAdminPriceCatalogueReviewService
} from './super-admin-price-catalogue-review.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
}

const emptyQueue: PriceCatalogueReviewQueue = {
  waitingProducts: [],
  automaticMatches: [],
  listings: [],
  masterProducts: []
};

function createService(apiBaseUrl = '/api'): {
  service: SuperAdminPriceCatalogueReviewService;
  http: HttpStub;
} {
  const http: HttpStub = {
    get: jest.fn((): Observable<unknown> => of(emptyQueue)),
    post: jest.fn((): Observable<unknown> => of({}))
  };

  const service = new SuperAdminPriceCatalogueReviewService(http as unknown as HttpClient, {
    apiBaseUrl
  } as ConfigService);

  return { service, http };
}

describe('SuperAdminPriceCatalogueReviewService (issue #928)', () => {
  it('reads the whole queue, with no filter, page or sort parameter', () => {
    const { service, http } = createService();

    service.queue().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/super-admin/price-catalogue-review');
  });

  it('trims a trailing slash off the configured API base, like every service here', () => {
    const { service, http } = createService('https://api.example.test/api/');

    service.queue().subscribe();

    expect(http.get).toHaveBeenCalledWith('https://api.example.test/api/super-admin/price-catalogue-review');
  });

  it('posts one listing decision to that listing own route, with the identity as reviewed', () => {
    const { service, http } = createService();

    service
      .decideListing(7, {
        outcome: SupplierListingReviewOutcome.Confirmed,
        unitsPerPack: 36,
        barcode: '0012345678905',
        barcodeScope: BarcodeScope.Unit,
        variant: 'Classic',
        sizeValue: 375,
        sizeUnit: MasterProductSizeUnit.Millilitre,
        approvePriceException: true
      })
      .subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/price-catalogue-review/listings/7/decision', {
      outcome: SupplierListingReviewOutcome.Confirmed,
      unitsPerPack: 36,
      barcode: '0012345678905',
      barcodeScope: BarcodeScope.Unit,
      variant: 'Classic',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      approvePriceException: true
    });
  });

  /**
   * The waiting key is in the body and never in the route: a key is a folded description containing
   * `|` and `:`, compared and never parsed, so putting one in a path segment would make a URL's
   * escaping part of the comparison.
   */
  it('posts a match decision with the waiting key in the body', () => {
    const { service, http } = createService();

    service
      .decideMatch({
        waitingKey: '16:COCA-COLACLASSIC|5:375ML',
        decision: ProductMatchReviewDecision.ChooseMasterProduct,
        suggestionId: null,
        masterProductId: 5,
        newBrand: null,
        newName: null,
        newSizeValue: null,
        newSizeUnit: null
      })
      .subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/price-catalogue-review/matches', {
      waitingKey: '16:COCA-COLACLASSIC|5:375ML',
      decision: ProductMatchReviewDecision.ChooseMasterProduct,
      suggestionId: null,
      masterProductId: 5,
      newBrand: null,
      newName: null,
      newSizeValue: null,
      newSizeUnit: null
    });
  });

  it('confirms and undoes an automatic match on two separate routes', () => {
    const { service, http } = createService();

    service.confirmAutomaticMatch(9).subscribe();
    service.undoAutomaticMatch(9).subscribe();

    expect(http.post).toHaveBeenNthCalledWith(
      1,
      '/api/super-admin/price-catalogue-review/automatic-matches/9/confirm',
      null
    );
    expect(http.post).toHaveBeenNthCalledWith(
      2,
      '/api/super-admin/price-catalogue-review/automatic-matches/9/undo',
      null
    );
  });

  /**
   * The enum numbers are the contract, so they are pinned here: a value that drifted from the
   * server's would silently submit a different decision.
   */
  it('mirrors the server own enum numbers', () => {
    expect(MasterProductSizeUnit.Millilitre).toBe(1);
    expect(MasterProductSizeUnit.Each).toBe(5);
    expect(BarcodeScope.Unit).toBe(1);
    expect(BarcodeScope.Carton).toBe(2);
    expect(SupplierListingMatchStatus.Proposed).toBe(1);
    expect(SupplierListingMatchStatus.Rejected).toBe(4);
    expect(SupplierListingReviewOutcome.Confirmed).toBe(1);
    expect(SupplierListingReviewOutcome.Rejected).toBe(2);
    expect(ProductMatchReviewDecision.AcceptSuggestion).toBe(1);
    expect(ProductMatchReviewDecision.MarkNotFound).toBe(5);
  });

  it('labels every size unit in the server own codes and every listing status', () => {
    expect(MASTER_PRODUCT_SIZE_UNIT_LABELS[MasterProductSizeUnit.Millilitre]).toBe('ml');
    expect(MASTER_PRODUCT_SIZE_UNIT_LABELS[MasterProductSizeUnit.Each]).toBe('each');
    expect(LISTING_MATCH_STATUS_LABELS[SupplierListingMatchStatus.NeedsReview]).toBe('Needs review');
    expect(LISTING_MATCH_STATUS_LABELS[SupplierListingMatchStatus.Rejected]).toBe('Refused');
  });
});
