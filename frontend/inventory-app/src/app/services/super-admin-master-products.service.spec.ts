import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import { MasterProductSizeUnit } from './super-admin-price-catalogue-review.service';
import {
  MASTER_PRODUCT_MAX_PAGE_SIZE,
  MASTER_PRODUCT_PAGE_SIZE,
  SuperAdminMasterProductPage,
  SuperAdminMasterProductsService
} from './super-admin-master-products.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
  put: jest.Mock;
}

const emptyPage: SuperAdminMasterProductPage = {
  products: [],
  totalCount: 0,
  page: 1,
  pageSize: MASTER_PRODUCT_PAGE_SIZE,
  hasMore: false
};

function createService(apiBaseUrl = '/api'): {
  service: SuperAdminMasterProductsService;
  http: HttpStub;
} {
  const http: HttpStub = {
    get: jest.fn((): Observable<unknown> => of(emptyPage)),
    post: jest.fn((): Observable<unknown> => of(undefined)),
    put: jest.fn((): Observable<unknown> => of(undefined))
  };

  const service = new SuperAdminMasterProductsService(http as unknown as HttpClient, {
    apiBaseUrl
  } as ConfigService);

  return { service, http };
}

/** The query string the service built, as a readable string. */
function queryOf(http: HttpStub): string {
  const options = http.get.mock.calls[0][1] as { params: HttpParams } | undefined;
  return options?.params.toString() ?? '';
}

describe('SuperAdminMasterProductsService (issues #1023 and #1024)', () => {
  it('reads the catalogue from the master-products route', () => {
    const { service, http } = createService();

    service.list().subscribe();

    expect(http.get.mock.calls[0][0]).toBe('/api/super-admin/master-products');
  });

  it('trims a trailing slash off the configured API base, like every service here', () => {
    const { service, http } = createService('https://api.example.test/api/');

    service.list().subscribe();

    expect(http.get.mock.calls[0][0]).toBe('https://api.example.test/api/super-admin/master-products');
  });

  it('sends no parameter at all when nothing was asked for, so the server decides the page', () => {
    const { service, http } = createService();

    service.list().subscribe();

    expect(queryOf(http)).toBe('');
  });

  it('sends the search, the page and the page size when they were asked for', () => {
    const { service, http } = createService();

    service.list({ search: 'cola', page: 2, pageSize: MASTER_PRODUCT_MAX_PAGE_SIZE }).subscribe();

    const query = queryOf(http);
    expect(query).toContain('search=cola');
    expect(query).toContain('page=2');
    expect(query).toContain(`pageSize=${MASTER_PRODUCT_MAX_PAGE_SIZE}`);
  });

  it.each([null, undefined, '', '   '])('omits a blank search rather than sending an empty filter (%p)', (search) => {
    const { service, http } = createService();

    service.list({ search: search as string | null }).subscribe();

    expect(queryOf(http)).not.toContain('search');
  });

  it('trims a typed search, so a trailing space is not part of what is matched', () => {
    const { service, http } = createService();

    service.list({ search: '  coca-cola  ' }).subscribe();

    expect(queryOf(http)).toContain('search=coca-cola');
  });

  it('reads one master product from its own route', () => {
    const { service, http } = createService();

    service.get(7).subscribe();

    expect(http.get.mock.calls[0][0]).toBe('/api/super-admin/master-products/7');
  });

  it('posts a new master product to the master-products route (issue #1024)', () => {
    const { service, http } = createService();

    service
      .create({ brand: 'Solo', name: 'Lemon', sizeValue: 600, sizeUnit: MasterProductSizeUnit.Millilitre })
      .subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/master-products', {
      brand: 'Solo',
      name: 'Lemon',
      sizeValue: 600,
      sizeUnit: MasterProductSizeUnit.Millilitre
    });
  });

  /**
   * The correction carries the instant the page read, because that is what the server compares
   * against: an edit that presented nothing could silently overwrite somebody else's correction.
   */
  it('puts a correction with the instant the page read (issue #1024)', () => {
    const { service, http } = createService();

    service
      .update(7, {
        brand: 'Coca-Cola',
        name: 'Classic',
        sizeValue: 375,
        sizeUnit: MasterProductSizeUnit.Millilitre,
        lastChangedAtUtc: '2026-11-01T06:00:00Z'
      })
      .subscribe();

    expect(http.put).toHaveBeenCalledWith('/api/super-admin/master-products/7', {
      brand: 'Coca-Cola',
      name: 'Classic',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      lastChangedAtUtc: '2026-11-01T06:00:00Z'
    });
  });

  /**
   * Deleting a master product is explicitly out of issue #1024's scope and the API has no such
   * route, so there is nothing here that could call one.
   */
  it('offers no delete, because the API has none', () => {
    const { service } = createService();

    expect(Object.getOwnPropertyNames(Object.getPrototypeOf(service))).toEqual(
      expect.not.arrayContaining(['delete', 'remove'])
    );
  });
});
