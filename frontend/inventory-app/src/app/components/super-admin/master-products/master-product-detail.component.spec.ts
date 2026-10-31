import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import {
  SuperAdminMasterProductDetail,
  SuperAdminMasterProductListing,
  SuperAdminMasterProductsService
} from '../../../services/super-admin-master-products.service';
import {
  MasterProductSizeUnit,
  SupplierListingMatchStatus,
  SupplierPriceBasis
} from '../../../services/super-admin-price-catalogue-review.service';
import { MasterProductDetailComponent } from './master-product-detail.component';

function listing(overrides: Partial<SuperAdminMasterProductListing> = {}): SuperAdminMasterProductListing {
  return {
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
    latestPrice: {
      packPrice: 42.5,
      currency: 'AUD',
      priceBasis: SupplierPriceBasis.InclusiveOfGst,
      observedAtUtc: '2026-10-29T00:00:00Z'
    },
    ...overrides
  };
}

function detail(listings: readonly SuperAdminMasterProductListing[]): SuperAdminMasterProductDetail {
  return {
    product: {
      id: 1,
      brand: 'Coca-Cola',
      name: 'Classic',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      createdAtUtc: '2026-10-28T00:00:00Z',
      updatedAtUtc: '2026-10-29T00:00:00Z',
      listingCounts: {
        proposed: 0,
        confirmed: listings.length,
        needsReview: 0,
        rejected: 0,
        total: listings.length
      }
    },
    listings
  };
}

/** The same detail for a named product, so two reads in flight can be told apart on screen. */
function detailOf(id: number, brand: string, name: string): SuperAdminMasterProductDetail {
  const base = detail([listing()]);
  return { ...base, product: { ...base.product, id, brand, name } };
}

async function render(service: Partial<SuperAdminMasterProductsService>, productId = 1) {
  await TestBed.configureTestingModule({
    imports: [MasterProductDetailComponent],
    providers: [{ provide: SuperAdminMasterProductsService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(MasterProductDetailComponent);
  fixture.componentRef.setInput('productId', productId);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function rowFor(host: HTMLElement, text: string): HTMLTableRowElement {
  const match = Array.from(host.querySelectorAll('tbody tr')).find((row) => row.textContent?.includes(text));
  expect(match).toBeDefined();
  return match as HTMLTableRowElement;
}

describe('MasterProductDetailComponent (issue #1023)', () => {
  it('reads the product it was given and lists its supplier listings', async () => {
    const get = jest.fn(() => of(detail([listing()])));
    const { host } = await render({ get }, 7);

    expect(get).toHaveBeenCalledWith(7);
    expect(host.textContent).toContain('Supplier listings — Coca-Cola Classic');

    const row = rowFor(host, 'Alpha Grocer');
    expect(row.textContent).toContain('SKU-COLA-30');
    expect(row.textContent).toContain('375 ml');
    expect(row.textContent).toContain('30');
    expect(row.textContent).toContain('Confirmed');
  });

  /**
   * The pack price is shown exactly as it was observed, with the basis it was read on: "incl. GST"
   * and "excl. GST" are different figures, and nothing here divides a pack price by a pack quantity -
   * that comparison is the cheapest-supplier summary's, on its own endpoint and its own rule.
   */
  it('shows the observed pack price with its currency and its GST basis, and no per-unit figure', async () => {
    const get = jest.fn(() => of(detail([listing()])));
    const { host } = await render({ get });

    const row = rowFor(host, 'Alpha Grocer');
    expect(row.textContent).toContain('$42.50');
    expect(row.textContent).toContain('incl. GST');
    expect(row.textContent).not.toContain('1.42');
    expect(row.textContent).not.toMatch(/per unit/i);
  });

  it('shows a GST-exclusive reading as such', async () => {
    const get = jest.fn(() =>
      of(
        detail([
          listing({
            latestPrice: {
              packPrice: 38,
              currency: 'AUD',
              priceBasis: SupplierPriceBasis.ExclusiveOfGst,
              observedAtUtc: '2026-10-29T00:00:00Z'
            }
          })
        ])
      )
    );
    const { host } = await render({ get });

    expect(rowFor(host, 'Alpha Grocer').textContent).toContain('excl. GST');
  });

  /** Unknown stays unknown: a listing nobody has priced says so rather than reading as free. */
  it('says a listing has never been checked instead of showing a zero price', async () => {
    const get = jest.fn(() =>
      of(detail([listing({ latestPrice: null, lastCheckedAtUtc: null, unitsPerPack: null, sizeValue: null, sizeUnit: null })]))
    );
    const { host } = await render({ get });

    const row = rowFor(host, 'Alpha Grocer');
    expect(row.textContent).toContain('Never checked');
    expect(row.textContent).toContain('Unknown');
    expect(row.textContent).toContain('Not published');
    expect(row.textContent).not.toContain('$0.00');
  });

  it('links a listing page only when the one website rule accepts it', async () => {
    const get = jest.fn(() =>
      of(
        detail([
          listing({ id: 11, supplierName: 'Alpha Grocer' }),
          listing({ id: 12, supplierName: 'Smuggled Grocer', url: 'javascript:alert(1)' }),
          listing({ id: 13, supplierName: 'No Page Grocer', url: null })
        ])
      )
    );
    const { host } = await render({ get });

    const link = rowFor(host, 'Alpha Grocer').querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://alpha.example/cola');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.getAttribute('target')).toBe('_blank');

    const smuggled = rowFor(host, 'Smuggled Grocer');
    expect(smuggled.querySelector('a')).toBeNull();
    expect(smuggled.textContent).toContain('javascript:alert(1)');

    expect(rowFor(host, 'No Page Grocer').querySelector('a')).toBeNull();
  });

  it('shows the loading state while the product is still being read', async () => {
    const { host } = await render({ get: jest.fn(() => new Subject<SuperAdminMasterProductDetail>()) });

    expect(host.textContent).toContain('Loading supplier listings…');
  });

  it('shows an actionable error state when the product cannot be read, and retries on request', async () => {
    const get = jest.fn(() => throwError(() => new Error('nope')));
    const { host, component } = await render({ get });

    expect(host.textContent).toContain('Unable to load this master product.');

    component.load();
    expect(get).toHaveBeenCalledTimes(2);
  });

  it('shows an empty state for a product no supplier lists yet', async () => {
    const { host } = await render({ get: jest.fn(() => of(detail([]))) });

    expect(host.textContent).toContain('No supplier lists this product yet.');
  });

  it('re-reads when the page selects another product', async () => {
    const get = jest.fn(() => of(detail([listing()])));
    const { fixture } = await render({ get }, 1);

    fixture.componentRef.setInput('productId', 2);
    fixture.detectChanges();

    expect(get).toHaveBeenNthCalledWith(1, 1);
    expect(get).toHaveBeenNthCalledWith(2, 2);
  });

  /**
   * The page reuses this one component for whichever row is open, so viewing product A's listings and
   * then product B's leaves two reads in flight. Only the product last asked for may be described: A's
   * slower answer must not appear under B's heading.
   */
  it('ignores a slower read for a product the page has already moved on from', async () => {
    const firstRead = new Subject<SuperAdminMasterProductDetail>();
    const secondRead = new Subject<SuperAdminMasterProductDetail>();
    const get = jest.fn().mockReturnValueOnce(firstRead).mockReturnValueOnce(secondRead);
    const { fixture, host, component } = await render({ get }, 1);

    fixture.componentRef.setInput('productId', 2);
    fixture.detectChanges();

    secondRead.next(detailOf(2, 'Pepsi', 'Max'));
    // Product 1's read answers afterwards, describing the product nobody is looking at any more.
    firstRead.next(detailOf(1, 'Coca-Cola', 'Classic'));
    fixture.detectChanges();

    expect(component.detail?.product.id).toBe(2);
    expect(host.textContent).toContain('Supplier listings — Pepsi Max');
    expect(host.textContent).not.toContain('Supplier listings — Coca-Cola');
  });

  /** An older failure is just as stale as an older success, and must not replace a newer product. */
  it('keeps the newest product on screen when an older read fails afterwards', async () => {
    const firstRead = new Subject<SuperAdminMasterProductDetail>();
    const secondRead = new Subject<SuperAdminMasterProductDetail>();
    const get = jest.fn().mockReturnValueOnce(firstRead).mockReturnValueOnce(secondRead);
    const { fixture, host, component } = await render({ get }, 1);

    fixture.componentRef.setInput('productId', 2);
    fixture.detectChanges();

    secondRead.next(detailOf(2, 'Pepsi', 'Max'));
    firstRead.error(new Error('the abandoned read failed'));
    fixture.detectChanges();

    expect(component.loadError).toBe(false);
    expect(component.loading).toBe(false);
    expect(component.detail?.product.id).toBe(2);
    expect(host.querySelector('[data-testid="master-product-detail-error"]')).toBeNull();
  });

  /** A failed read must not end this panel's read stream: the next selection has to be answered. */
  it('reads again after a failed read instead of going silent', async () => {
    const get = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('nope')))
      .mockReturnValueOnce(of(detailOf(2, 'Pepsi', 'Max')));
    const { fixture, component } = await render({ get }, 1);

    expect(component.loadError).toBe(true);

    fixture.componentRef.setInput('productId', 2);
    fixture.detectChanges();

    expect(component.loadError).toBe(false);
    expect(component.detail?.product.id).toBe(2);
  });

  it('abandons the read still in flight when it is destroyed', async () => {
    const pending = new Subject<SuperAdminMasterProductDetail>();
    const { fixture } = await render({ get: jest.fn(() => pending) });

    expect(pending.observed).toBe(true);

    fixture.destroy();

    expect(pending.observed).toBe(false);
  });

  it('asks the page to close it, rather than closing itself', async () => {
    const { fixture, host, component } = await render({ get: jest.fn(() => of(detail([listing()]))) });
    const closed = jest.fn();
    component.closed.subscribe(closed);

    (host.querySelector('[data-testid="master-product-detail-close"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(closed).toHaveBeenCalledTimes(1);
  });

  /** It is a read: no control here confirms, corrects, refuses or deletes anything (issue #928's authority). */
  it('offers no decision or write control', async () => {
    const { host } = await render({ get: jest.fn(() => of(detail([listing()]))) });

    const labels = Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim() ?? '');
    expect(labels).toEqual(['Close']);
  });

  it('displays nothing about any business', async () => {
    const { host } = await render({ get: jest.fn(() => of(detail([listing()]))) });

    expect(host.textContent ?? '').not.toMatch(/business/i);
  });
});
