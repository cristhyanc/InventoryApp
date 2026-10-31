import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { CheapestSupplierSummary, SupplierPriceBasis } from '../models/cheapest-supplier';
import { CHEAPEST_SUPPLIER_MAX_IDS, CheapestSupplierService } from './cheapest-supplier.service';
import { ConfigService } from './config.service';

describe('CheapestSupplierService (issue #930)', () => {
  let service: CheapestSupplierService;
  let http: HttpTestingController;

  const url = 'http://localhost/api/cheapest-suppliers/summary';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        CheapestSupplierService,
        { provide: ConfigService, useValue: { apiBaseUrl: 'http://localhost/api/' } }
      ]
    });

    service = TestBed.inject(CheapestSupplierService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function answer(productIds: number[]): CheapestSupplierSummary {
    return {
      displayBasis: SupplierPriceBasis.ExclusiveOfGst,
      products: productIds.map((productId) => ({
        productId,
        trackingStatus: null,
        cheapestUnitPrice: null,
        suppliers: []
      }))
    };
  }

  it('asks once for a page that fits in one batch, as a POST carrying only the ids', () => {
    let summary: CheapestSupplierSummary | undefined;
    service.getSummary([7, 9]).subscribe((received) => (summary = received));

    const request = http.expectOne(url);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ productIds: [7, 9] });
    request.flush(answer([7, 9]));

    expect(summary?.products.map((product) => product.productId)).toEqual([7, 9]);
    expect(summary?.displayBasis).toBe(SupplierPriceBasis.ExclusiveOfGst);
  });

  it('splits a page larger than the endpoint accepts into batches and merges the answers', () => {
    const ids = Array.from({ length: CHEAPEST_SUPPLIER_MAX_IDS + 1 }, (_, index) => index + 1);
    let summary: CheapestSupplierSummary | undefined;

    service.getSummary(ids).subscribe((received) => (summary = received));

    const requests = http.match(url);
    expect(requests).toHaveLength(2);
    expect(requests[0].request.body.productIds).toHaveLength(CHEAPEST_SUPPLIER_MAX_IDS);
    expect(requests[1].request.body.productIds).toEqual([CHEAPEST_SUPPLIER_MAX_IDS + 1]);

    requests[0].flush(answer(requests[0].request.body.productIds));
    requests[1].flush(answer(requests[1].request.body.productIds));

    expect(summary?.products.length).toBe(ids.length);
  });

  it('collapses a repeated id before splitting, so one product is asked about once', () => {
    service.getSummary([7, 7, 9]).subscribe();

    const request = http.expectOne(url);
    expect(request.request.body).toEqual({ productIds: [7, 9] });
    request.flush(answer([7, 9]));
  });
});
