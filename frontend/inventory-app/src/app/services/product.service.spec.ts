import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Product, ReorderAlertListing } from '../models/models';
import { ConfigService } from './config.service';
import { ProductService } from './product.service';

/**
 * The reorder-alert listing's response metadata (issue #727). `GET /api/products/alerts/low-stock`
 * answers the same JSON **array** it always has, and states which facts the alerts were calculated
 * from in two additive response headers (issue #726) - so the body contract is unchanged and the
 * screens can tell "the machines need nothing" from "the machines were never read".
 */

const LOW_STOCK_URL = '/api/products/alerts/low-stock';

function product(): Product {
  return {
    id: 1,
    name: 'Cola 375mL',
    sku: null,
    description: null,
    averageUnitCost: 1,
    costingQuantity: null,
    inventoryValue: null,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 2,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 15,
    needToOrder: 13,
    mdbCode: null,
    unit: 'ea',
    lastEatBefore1: null,
    lastEatBefore2: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId: null,
    category: null,
    isActive: true,
    isLowStock: true,
    isReorderAlert: true
  };
}

function createService(): { service: ProductService; httpMock: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      ProductService,
      { provide: ConfigService, useValue: { apiBaseUrl: '/api' } }
    ]
  });
  return { service: TestBed.inject(ProductService), httpMock: TestBed.inject(HttpTestingController) };
}

/** Answers the one outstanding low-stock request with `body` and `headers`. */
function answerLowStock(
  httpMock: HttpTestingController,
  body: Product[],
  headers: Record<string, string>
): void {
  httpMock.expectOne((request) => request.url === LOW_STOCK_URL).flush(body, { headers });
}

describe('ProductService reorder-alert listing metadata (issue #727)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('reads the storage-only mode and the reason the machines are missing', () => {
    const { service, httpMock } = createService();
    let listing: ReorderAlertListing | undefined;

    service.getLowStock().subscribe((result) => (listing = result));
    answerLowStock(httpMock, [product()], {
      'X-Reorder-Mode': 'StorageOnly',
      'X-Reorder-Machine-Need-Unavailable-Reason': 'NayaxNotConnected'
    });

    expect(listing).toEqual({
      products: [product()],
      mode: 'StorageOnly',
      machineNeedUnavailableReason: 'NayaxNotConnected'
    });
  });

  it('reads the machine-aware mode, which has nothing unavailable to explain', () => {
    const { service, httpMock } = createService();
    let listing: ReorderAlertListing | undefined;

    service.getLowStock().subscribe((result) => (listing = result));
    answerLowStock(httpMock, [product()], { 'X-Reorder-Mode': 'MachineAware' });

    expect(listing?.mode).toBe('MachineAware');
    expect(listing?.machineNeedUnavailableReason).toBeNull();
  });

  /** An empty storage-only listing is exactly the answer a per-row field could not express. */
  it('keeps the mode of an empty listing', () => {
    const { service, httpMock } = createService();
    let listing: ReorderAlertListing | undefined;

    service.getLowStock().subscribe((result) => (listing = result));
    answerLowStock(httpMock, [], {
      'X-Reorder-Mode': 'StorageOnly',
      'X-Reorder-Machine-Need-Unavailable-Reason': 'PermissionNotGranted'
    });

    expect(listing).toEqual({
      products: [],
      mode: 'StorageOnly',
      machineNeedUnavailableReason: 'PermissionNotGranted'
    });
  });

  it('claims no mode for a response that published none', () => {
    const { service, httpMock } = createService();
    let listing: ReorderAlertListing | undefined;

    service.getLowStock().subscribe((result) => (listing = result));
    answerLowStock(httpMock, [product()], {});

    expect(listing?.products).toHaveLength(1);
    expect(listing?.mode).toBeNull();
    expect(listing?.machineNeedUnavailableReason).toBeNull();
  });

  it('claims no mode for a code this build has never heard of', () => {
    const { service, httpMock } = createService();
    let listing: ReorderAlertListing | undefined;

    service.getLowStock().subscribe((result) => (listing = result));
    answerLowStock(httpMock, [product()], { 'X-Reorder-Mode': 'FleetEstimated' });

    expect(listing?.mode).toBeNull();
  });

  it('reports no unavailability reason for an unrecognised one, rather than guessing', () => {
    const { service, httpMock } = createService();
    let listing: ReorderAlertListing | undefined;

    service.getLowStock().subscribe((result) => (listing = result));
    answerLowStock(httpMock, [product()], {
      'X-Reorder-Mode': 'StorageOnly',
      'X-Reorder-Machine-Need-Unavailable-Reason': 'TokenExpired'
    });

    expect(listing?.mode).toBe('StorageOnly');
    expect(listing?.machineNeedUnavailableReason).toBeNull();
  });

  /**
   * A machine-aware listing left nothing out, so a reason beside it describes nothing and is not
   * carried into the application as something this listing is missing.
   */
  it('ignores an unavailability reason published beside a machine-aware mode', () => {
    const { service, httpMock } = createService();
    let listing: ReorderAlertListing | undefined;

    service.getLowStock().subscribe((result) => (listing = result));
    answerLowStock(httpMock, [product()], {
      'X-Reorder-Mode': 'MachineAware',
      'X-Reorder-Machine-Need-Unavailable-Reason': 'NayaxNotConnected'
    });

    expect(listing?.machineNeedUnavailableReason).toBeNull();
  });

  /**
   * The page filters the reorder listing still sends. `supplierId` is deliberately not one of them
   * since issue #787 (A1 of #786): a product carries no supplier, so the endpoint accepts no such
   * narrowing and the service must not invent the parameter.
   */
  it('still sends the page filters it has always sent, and no supplier narrowing', () => {
    const { service, httpMock } = createService();

    service.getLowStock({ search: 'cola', categoryId: 2 }).subscribe();
    const request = httpMock.expectOne((candidate) => candidate.url === LOW_STOCK_URL);

    expect(request.request.params.get('search')).toBe('cola');
    expect(request.request.params.get('categoryId')).toBe('2');
    expect(request.request.params.has('supplierId')).toBe(false);
    request.flush([]);
  });

  /**
   * The "Track supplier prices" requests (issue #938). Each is the product's own sub-resource rather
   * than a catalogue field, for the reason the GST rule is - the product payload is a pinned API
   * contract - and the write carries one boolean, because the search text is the product's own name
   * and unit label read by the server.
   */
  describe('supplier price tracking (issue #938)', () => {
    it('reads one product\'s tracking state from its own resource', () => {
      const { service, httpMock } = createService();

      service.getPriceTracking(7).subscribe();
      const request = httpMock.expectOne('/api/products/7/price-tracking');

      expect(request.request.method).toBe('GET');
      request.flush({
        productId: 7,
        tracked: false,
        status: null,
        searchName: null,
        unitLabel: null,
        masterProduct: null,
        canTrack: true,
        trackRefusal: null
      });
    });

    it.each([true, false])('writes the switch as one boolean (%s)', (tracked) => {
      const { service, httpMock } = createService();

      service.setPriceTracking(7, tracked).subscribe();
      const request = httpMock.expectOne('/api/products/7/price-tracking');

      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ tracked });
      request.flush(null);
    });

    it('reads the tracked products with no parameter at all', () => {
      const { service, httpMock } = createService();

      service.getTrackedProducts().subscribe();
      const request = httpMock.expectOne((candidate) => candidate.url === '/api/products/price-tracking');

      expect(request.request.method).toBe('GET');
      expect(request.request.params.keys()).toEqual([]);
      request.flush([]);
    });
  });
});
