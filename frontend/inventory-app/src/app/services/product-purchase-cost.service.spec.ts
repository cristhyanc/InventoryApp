import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProductPurchaseCostSummary } from '../models/product-purchase-cost-summary';
import { ConfigService } from './config.service';
import { PURCHASE_COST_MAX_IDS_PER_REQUEST, ProductPurchaseCostService } from './product-purchase-cost.service';

/**
 * The request this client makes (issue #788, B of #786). What is pinned here is the URL and the
 * `productIds` query contract, because that is the whole of the client's own behaviour: the costs,
 * the suppliers, the dates and every tie-break between them come from the backend's one
 * authoritative calculation, and this service deliberately derives nothing.
 */
const SUMMARY_URL = '/api/products/purchase-cost-summary';

function summary(productId: number): ProductPurchaseCostSummary {
  return {
    productId,
    lastUnitCost: 2.4,
    lastSupplierName: 'Acme Supplies',
    lastPurchaseDate: '2026-03-05T00:00:00',
    lowestUnitCost: 0,
    lowestSupplierName: null,
    lowestPurchaseDate: '2026-02-05T00:00:00'
  };
}

describe('ProductPurchaseCostService (issue #788)', () => {
  let service: ProductPurchaseCostService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfigService, useValue: { apiBaseUrl: '/api' } as ConfigService },
        ProductPurchaseCostService
      ]
    });

    service = TestBed.inject(ProductPurchaseCostService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks the summary endpoint for the whole business when no ids are given', () => {
    let received: ProductPurchaseCostSummary[] | undefined;
    service.getSummaries().subscribe((summaries) => (received = summaries));

    const request = http.expectOne((candidate) => candidate.url === SUMMARY_URL);

    expect(request.request.method).toBe('GET');
    expect(request.request.params.has('productIds')).toBe(false);

    request.flush([summary(7)]);
    expect(received).toEqual([summary(7)]);
  });

  /**
   * Repeated values, not a comma-joined string: the endpoint binds `productIds` as an array, and a
   * single joined value would bind to nothing and silently widen the answer to the whole business.
   */
  it('sends each requested product id as its own repeated query value', () => {
    service.getSummaries([7, 9, 11]).subscribe();

    const request = http.expectOne((candidate) => candidate.url === SUMMARY_URL);

    expect(request.request.params.getAll('productIds')).toEqual(['7', '9', '11']);
    request.flush([]);
  });

  it('trims a trailing slash from the configured API base URL', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfigService, useValue: { apiBaseUrl: 'https://host.example/api/' } as ConfigService },
        ProductPurchaseCostService
      ]
    });

    TestBed.inject(ProductPurchaseCostService).getSummaries([3]).subscribe();

    const testing = TestBed.inject(HttpTestingController);
    const request = testing.expectOne(
      (candidate) => candidate.url === 'https://host.example/api/products/purchase-cost-summary'
    );

    expect(request.request.params.getAll('productIds')).toEqual(['3']);
    request.flush([]);
    testing.verify();
  });

  /**
   * A product with no recorded purchase history is absent from the answer, and this client passes
   * that through untouched: it never fills a missing product in with a zero cost, which would read
   * as "this product was bought for nothing".
   */
  it('passes an absent product through as absent rather than a zero-cost row', () => {
    let received: ProductPurchaseCostSummary[] | undefined;
    service.getSummaries([7, 9]).subscribe((summaries) => (received = summaries));

    http.expectOne((candidate) => candidate.url === SUMMARY_URL).flush([summary(7)]);

    expect(received).toHaveLength(1);
    expect(received?.[0].productId).toBe(7);
  });

  describe('getSummariesBatched (issue #1017 repair)', () => {
    /** 15-digit ids, the width real catalogue ids have. */
    const wideIds = (count: number): number[] => Array.from({ length: count }, (_, i) => 100_000_000_000_001 + i);

    it('sends nothing and answers [] for an empty id list, never asking for the whole business', () => {
      let received: ProductPurchaseCostSummary[] | undefined;
      service.getSummariesBatched([]).subscribe((summaries) => (received = summaries));

      http.expectNone((candidate) => candidate.url === SUMMARY_URL);
      expect(received).toEqual([]);
    });

    it('splits a long id list into GETs of at most the bound, covering each id once, under 8 KB', () => {
      const ids = wideIds(310);
      let received: ProductPurchaseCostSummary[] | undefined;
      service.getSummariesBatched([...ids, ids[0]]).subscribe((summaries) => (received = summaries));

      const requests = http.match((candidate) => candidate.url === SUMMARY_URL);
      expect(requests).toHaveLength(Math.ceil(310 / PURCHASE_COST_MAX_IDS_PER_REQUEST));
      const batches = requests.map((request) => request.request.params.getAll('productIds') ?? []);
      for (const batch of batches) {
        expect(batch.length).toBeGreaterThan(0);
        expect(batch.length).toBeLessThanOrEqual(PURCHASE_COST_MAX_IDS_PER_REQUEST);
      }
      for (const request of requests) {
        expect(`GET ${request.request.urlWithParams} HTTP/1.1`.length).toBeLessThan(8192);
      }
      expect(batches.flat()).toEqual(ids.map(String));

      requests.forEach((request, index) => request.flush([summary(index + 1)]));
      expect(received).toEqual(requests.map((_, index) => summary(index + 1)));
    });

    it('fails the whole load when any batch fails, rather than answering part of it', () => {
      let failed = false;
      let received: ProductPurchaseCostSummary[] | undefined;
      service.getSummariesBatched(wideIds(250)).subscribe({
        next: (summaries) => (received = summaries),
        error: () => (failed = true)
      });

      const requests = http.match((candidate) => candidate.url === SUMMARY_URL);
      requests[0].flush([summary(1)]);
      requests[1].flush('boom', { status: 500, statusText: 'Server Error' });

      expect(failed).toBe(true);
      expect(received).toBeUndefined();
      expect(requests[2].cancelled).toBe(true);
    });
  });
});
