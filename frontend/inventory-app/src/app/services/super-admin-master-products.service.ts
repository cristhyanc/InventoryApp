import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';
import { MasterProductSizeUnit, SupplierListingMatchStatus, SupplierPriceBasis } from './super-admin-price-catalogue-review.service';

/**
 * The default page the server serves when a request asks for no size, and the largest it will serve
 * whatever a request asks for (backend `MasterProductCataloguePaging`, issue #1023).
 *
 * They are here so the page can offer a size the server will actually honour. They are not a rule
 * this client enforces: the server bounds every request itself and reports the page it served, which
 * is what the page displays.
 */
export const MASTER_PRODUCT_PAGE_SIZE = 50;
export const MASTER_PRODUCT_MAX_PAGE_SIZE = 200;

/**
 * How many supplier listings one master product has in each match status, as
 * `GET /api/super-admin/master-products` reports them (issue #1023).
 *
 * Four counts of *supplier* listings and a total. There is deliberately no count of the businesses
 * tracking the product: that count is about businesses, and this surface publishes nothing about any.
 */
export interface MasterProductListingCounts {
  readonly proposed: number;
  readonly confirmed: number;
  readonly needsReview: number;
  readonly rejected: number;
  readonly total: number;
}

/**
 * One master product of the shared catalogue as the Super Admin listing reports it (issue #1023):
 * what the product is, when it was created and last changed, and how its supplier listings stand.
 */
export interface SuperAdminMasterProduct {
  readonly id: number;
  readonly brand: string;
  readonly name: string;
  readonly sizeValue: number;
  readonly sizeUnit: MasterProductSizeUnit;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly listingCounts: MasterProductListingCounts;
}

/**
 * One bounded page of the catalogue. `page` and `pageSize` are the sizes the server actually served,
 * which may differ from what was asked for - a page size above the maximum is bounded rather than
 * refused - and `totalCount` is how many products the search matched, not how many came back.
 */
export interface SuperAdminMasterProductPage {
  readonly products: readonly SuperAdminMasterProduct[];
  readonly totalCount: number;
  readonly page: number;
  readonly pageSize: number;
  readonly hasMore: boolean;
}

/**
 * What one supplier listing currently costs (issue #1023): the pack price of the accepted reading the
 * listing points at, with the currency and the tax basis it was read on and the instant it was
 * observed.
 *
 * The basis and the instant travel with the amount because a pack price including GST and the same
 * number excluding it are different figures. Nothing here is a per-unit price: that comparison is the
 * cheapest-supplier summary's, on its own endpoint.
 */
export interface SuperAdminMasterProductPrice {
  readonly packPrice: number;
  readonly currency: string;
  readonly priceBasis: SupplierPriceBasis;
  readonly observedAtUtc: string;
}

/**
 * One supplier listing of a master product. Every text value is the supplier's own wording and is
 * rendered as plain text; the only link is {@link url}. `latestPrice` is `null` for a listing that has
 * never been checked successfully, which is a state and not a missing value.
 */
export interface SuperAdminMasterProductListing {
  readonly id: number;
  readonly supplierName: string;
  readonly supplierProductId: string;
  readonly title: string | null;
  readonly sizeValue: number | null;
  readonly sizeUnit: MasterProductSizeUnit | null;
  readonly unitsPerPack: number | null;
  readonly matchStatus: SupplierListingMatchStatus;
  readonly url: string | null;
  readonly lastCheckedAtUtc: string | null;
  readonly latestPrice: SuperAdminMasterProductPrice | null;
}

/** One master product and its supplier listings, as `GET .../master-products/{id}` reports them. */
export interface SuperAdminMasterProductDetail {
  readonly product: SuperAdminMasterProduct;
  readonly listings: readonly SuperAdminMasterProductListing[];
}

/**
 * The frontend client for `/api/super-admin/master-products` (issue #1023), consumed by the Super
 * Admin Master products page.
 *
 * It is a thin wrapper over the two **read** endpoints: there is deliberately no `create`, `update`
 * or `delete` here, because the API has none - a master product is written only by the Super Admin
 * review of issue #928, and creating, editing and deleting one are separate issues that follow this.
 *
 * It decides nothing. The page bound, the ordering and the search are the server's, and a blank
 * search is simply not sent.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminMasterProductsService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /**
   * One page of the catalogue, optionally narrowed by a search over the brand and the name.
   *
   * A blank search is omitted rather than sent as an empty parameter, so the request says what it
   * means: the server treats both the same way, and a URL a person copies out of the address bar
   * should not carry a filter nobody typed.
   */
  list(options: { search?: string | null; page?: number; pageSize?: number } = {}): Observable<SuperAdminMasterProductPage> {
    let params = new HttpParams();
    const search = options.search?.trim();
    if (search) {
      params = params.set('search', search);
    }
    if (options.page !== undefined) {
      params = params.set('page', options.page);
    }
    if (options.pageSize !== undefined) {
      params = params.set('pageSize', options.pageSize);
    }

    return this.http.get<SuperAdminMasterProductPage>(this.baseUrl, { params });
  }

  /** One master product with its supplier listings. A `404` means no master product has that id. */
  get(id: number): Observable<SuperAdminMasterProductDetail> {
    return this.http.get<SuperAdminMasterProductDetail>(`${this.baseUrl}/${id}`);
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/master-products`;
  }
}
