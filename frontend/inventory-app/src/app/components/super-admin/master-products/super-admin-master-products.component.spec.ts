import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import {
  MASTER_PRODUCT_PAGE_SIZE,
  SuperAdminMasterProduct,
  SuperAdminMasterProductDetail,
  SuperAdminMasterProductPage,
  SuperAdminMasterProductsService
} from '../../../services/super-admin-master-products.service';
import { MasterProductSizeUnit, SupplierListingMatchStatus } from '../../../services/super-admin-price-catalogue-review.service';
import { SuperAdminMasterProductsComponent } from './super-admin-master-products.component';

function product(overrides: Partial<SuperAdminMasterProduct> = {}): SuperAdminMasterProduct {
  return {
    id: 1,
    brand: 'Coca-Cola',
    name: 'Classic',
    sizeValue: 375,
    sizeUnit: MasterProductSizeUnit.Millilitre,
    createdAtUtc: '2026-10-28T00:00:00Z',
    updatedAtUtc: '2026-10-29T00:00:00Z',
    listingCounts: { proposed: 1, confirmed: 2, needsReview: 0, rejected: 0, total: 3 },
    ...overrides
  };
}

function page(overrides: Partial<SuperAdminMasterProductPage> = {}): SuperAdminMasterProductPage {
  return {
    products: [product()],
    totalCount: 1,
    page: 1,
    pageSize: MASTER_PRODUCT_PAGE_SIZE,
    hasMore: false,
    ...overrides
  };
}

const detail: SuperAdminMasterProductDetail = {
  product: product(),
  listings: [
    {
      id: 11,
      supplierName: 'Alpha Grocer',
      supplierProductId: 'SKU-COLA-30',
      title: 'Alpha Grocer Coca-Cola Classic 375mL 30 pack',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      unitsPerPack: 30,
      matchStatus: SupplierListingMatchStatus.Confirmed,
      url: 'https://alpha.example/cola',
      lastCheckedAtUtc: '2026-10-29T00:00:00Z',
      latestPrice: null
    }
  ]
};

async function render(service: Partial<SuperAdminMasterProductsService>) {
  await TestBed.configureTestingModule({
    imports: [SuperAdminMasterProductsComponent],
    providers: [{ provide: SuperAdminMasterProductsService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SuperAdminMasterProductsComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function testId(host: HTMLElement, id: string): HTMLElement {
  const match = host.querySelector(`[data-testid="${id}"]`);
  expect(match).not.toBeNull();
  return match as HTMLElement;
}

describe('SuperAdminMasterProductsComponent (issue #1023)', () => {
  it('reads the first page on arrival and lists each product with its listing counts', async () => {
    const list = jest.fn(() =>
      of(
        page({
          products: [
            product({ id: 1, brand: 'Coca-Cola', name: 'Classic' }),
            product({
              id: 2,
              brand: 'Pepsi',
              name: 'Max',
              listingCounts: { proposed: 0, confirmed: 0, needsReview: 0, rejected: 0, total: 0 }
            })
          ],
          totalCount: 2
        })
      )
    );

    const { host } = await render({ list });

    expect(list).toHaveBeenCalledWith({ search: '', page: 1, pageSize: MASTER_PRODUCT_PAGE_SIZE });
    expect(host.textContent).toContain('Coca-Cola');
    expect(host.textContent).toContain('Pepsi');
    expect(host.textContent).toContain('375 ml');
    expect(testId(host, 'master-product-page-summary').textContent).toContain('Showing 1–2 of 2');
  });

  it('shows the loading state while the catalogue is still being read', async () => {
    // A read that has not answered yet, which is what the loading state is for.
    const { host, fixture } = await render({ list: jest.fn(() => new Subject<SuperAdminMasterProductPage>()) });

    expect(host.textContent).toContain('Loading master products…');
    expect(host.querySelector('tbody')).toBeNull();

    fixture.destroy();
  });

  it('shows an actionable error state when the catalogue cannot be read, and retries on request', async () => {
    const list = jest.fn(() => throwError(() => new Error('nope')));
    const { host, component } = await render({ list });

    expect(testId(host, 'master-product-error').textContent).toContain('Unable to load master products.');
    expect(component.products).toEqual([]);

    component.load();
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('shows an empty state, and says what was searched for when a search found nothing', async () => {
    const list = jest.fn(() => of(page({ products: [], totalCount: 0 })));
    const { host, fixture, component } = await render({ list });

    expect(testId(host, 'master-product-empty').textContent).toContain('No master products yet.');

    component.searchText = 'rhubarb';
    component.search();
    fixture.detectChanges();

    expect(testId(host, 'master-product-empty').textContent).toContain('No master product matches “rhubarb”');
  });

  /**
   * The search is the server's: the page sends the text and re-reads, rather than filtering the one
   * page it happens to be holding - which would silently hide the matches on every other page.
   */
  it('searches on the server and starts again at the first page', async () => {
    const list = jest.fn(() => of(page({ page: 1, totalCount: 1, hasMore: false })));
    const { component } = await render({ list });

    component.page = 3;
    component.searchText = '  cola  ';
    component.search();

    expect(list).toHaveBeenLastCalledWith({ search: 'cola', page: 1, pageSize: MASTER_PRODUCT_PAGE_SIZE });
    expect(component.page).toBe(1);
  });

  it('clears a search back to the whole catalogue', async () => {
    const list = jest.fn(() => of(page()));
    const { component } = await render({ list });

    component.searchText = 'cola';
    component.search();
    component.clearSearch();

    expect(component.searchText).toBe('');
    expect(list).toHaveBeenLastCalledWith({ search: '', page: 1, pageSize: MASTER_PRODUCT_PAGE_SIZE });
  });

  it('pages forward and back, and offers neither beyond the ends', async () => {
    const list = jest.fn(() => of(page({ page: 1, pageSize: 1, totalCount: 3, hasMore: true })));
    const { host, component, fixture } = await render({ list });

    // Previous is unavailable on the first page; Next is offered while the server says more remain.
    expect((testId(host, 'master-product-previous') as HTMLButtonElement).disabled).toBe(true);
    expect((testId(host, 'master-product-next') as HTMLButtonElement).disabled).toBe(false);

    (testId(host, 'master-product-next') as HTMLButtonElement).click();
    expect(list).toHaveBeenLastCalledWith({ search: '', page: 2, pageSize: 1 });

    component.page = 2;
    component.hasMore = false;
    fixture.detectChanges();
    expect((testId(host, 'master-product-next') as HTMLButtonElement).disabled).toBe(true);
    expect((testId(host, 'master-product-previous') as HTMLButtonElement).disabled).toBe(false);

    (testId(host, 'master-product-previous') as HTMLButtonElement).click();
    expect(list).toHaveBeenLastCalledWith({ search: '', page: 1, pageSize: 1 });
  });

  /**
   * The server bounds the page it serves and says which one it served; the page displays that answer
   * rather than what it asked for, so the "Showing …" line is never a guess.
   */
  it('displays the page and size the server says it served', async () => {
    const list = jest.fn(() => of(page({ page: 2, pageSize: 200, totalCount: 500, hasMore: true })));
    const { component, host } = await render({ list });

    expect(component.page).toBe(2);
    expect(component.pageSize).toBe(200);
    expect(testId(host, 'master-product-page-summary').textContent).toContain('Showing 201–201 of 500');
  });

  it('opens and closes one product’s supplier listings', async () => {
    const list = jest.fn(() => of(page()));
    const get = jest.fn(() => of(detail));
    const { host, fixture, component } = await render({ list, get });

    (testId(host, 'master-product-listings-1') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component.selectedProductId).toBe(1);
    expect(get).toHaveBeenCalledWith(1);
    expect(testId(host, 'master-product-detail').textContent).toContain('Alpha Grocer');

    (testId(host, 'master-product-detail-close') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component.selectedProductId).toBeNull();
    expect(host.querySelector('[data-testid="master-product-detail"]')).toBeNull();
  });

  it('closes an open product when the page or the search changes, so the panel is never about another row', async () => {
    const list = jest.fn(() => of(page({ hasMore: true })));
    const { component } = await render({ list, get: jest.fn(() => of(detail)) });

    component.toggleListings(product());
    component.nextPage();
    expect(component.selectedProductId).toBeNull();

    component.toggleListings(product());
    component.search();
    expect(component.selectedProductId).toBeNull();
  });

  /**
   * The issue's last criterion, as the page's own contract: this is a read-only screen. No Add, Edit
   * or Delete control exists on it, because the API has none and those actions are separate issues.
   */
  it('offers no create, edit or delete control', async () => {
    const { host } = await render({ list: jest.fn(() => of(page())) });

    const labels = Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim() ?? '');
    expect(labels).not.toContain('Add master product');
    expect(labels).not.toContain('Edit');
    expect(labels).not.toContain('Delete');
    expect(host.querySelector('[role="dialog"]')).toBeNull();
  });

  /**
   * Nothing about any business can be displayed, because the API publishes none: the rendered page
   * must not invent a tracking count or name a business anywhere.
   */
  it('displays nothing about any business', async () => {
    const { host } = await render({ list: jest.fn(() => of(page())) });

    const text = host.textContent ?? '';
    expect(text).not.toMatch(/business/i);
    expect(text).not.toMatch(/tracking/i);
  });
});
