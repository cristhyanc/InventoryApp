import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';
import {
  CheapestSupplierOffer,
  CheapestSupplierProduct,
  SupplierOfferIneligibility,
  SupplierPriceBasis,
  SupplierStockStatus
} from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import { CheapestSupplierSummaryComponent, cheapestSupplierSummaryView } from './cheapest-supplier-summary.component';
import { CheapestSuppliersState } from './cheapest-suppliers.component';

function offer(overrides: Partial<CheapestSupplierOffer> = {}): CheapestSupplierOffer {
  return {
    supplierId: 1,
    supplierName: 'Coles',
    hasListing: true,
    hasPrice: true,
    packQuantity: 12,
    packPrice: 22.08,
    packPriceBasis: SupplierPriceBasis.InclusiveOfGst,
    unitPrice: 1.84,
    unitPriceExGst: 1.6727,
    unitPriceIncGst: 1.84,
    isSpecial: false,
    promotionEndsAtUtc: null,
    stockStatus: SupplierStockStatus.InStock,
    lastCheckedAtUtc: '2026-11-03T22:00:00Z',
    productUrl: 'https://example.test/coles/espresso',
    ineligibility: null,
    isCheapest: true,
    ...overrides
  };
}

function product(overrides: Partial<CheapestSupplierProduct> = {}): CheapestSupplierProduct {
  return {
    productId: 4,
    trackingStatus: ProductPriceTrackingStatus.Matched,
    cheapestUnitPrice: 1.84,
    suppliers: [offer()],
    ...overrides
  };
}

const INC = SupplierPriceBasis.InclusiveOfGst;

/**
 * The Products page's Cheapest supplier states (issue #1093). Each stays its own answer; the order
 * of the checks is issue #930's, and nothing here computes a price - every figure is the server's.
 */
describe('cheapestSupplierSummaryView (issue #1093)', () => {
  it('says Loading while the read is in flight, and Unavailable for every unsuccessful read', () => {
    expect(cheapestSupplierSummaryView('loading', product(), INC).kind).toBe('loading');
    for (const state of ['failed', 'unavailable', 'planLocked'] as CheapestSuppliersState[]) {
      expect(cheapestSupplierSummaryView(state, product(), INC).kind).toBe('unavailable');
    }
  });

  it('keeps not tracked, no match found and awaiting match three different answers', () => {
    expect(cheapestSupplierSummaryView('loaded', undefined, INC).kind).toBe('notTracked');
    expect(cheapestSupplierSummaryView('loaded', product({ trackingStatus: null, suppliers: [] }), INC).kind).toBe('notTracked');
    expect(cheapestSupplierSummaryView('loaded', product({ trackingStatus: ProductPriceTrackingStatus.NotFound }), INC).kind).toBe('noMatch');
    expect(cheapestSupplierSummaryView('loaded', product({ trackingStatus: ProductPriceTrackingStatus.Requested }), INC).kind).toBe('awaitingMatch');
  });

  it('shows the server\'s cheapest unit price on the basis the server put it on, with its supplier', () => {
    expect(cheapestSupplierSummaryView('loaded', product(), INC)).toEqual({
      kind: 'price',
      unitPrice: '$1.84',
      basis: 'inc GST',
      supplierNames: ['Coles'],
      tied: false,
      special: false,
      cheapestOffers: [offer()]
    });
    // Never a hardcoded "inc GST": a GST-registered business is shown ex GST.
    expect(cheapestSupplierSummaryView('loaded', product(), SupplierPriceBasis.ExclusiveOfGst)).toEqual(
      expect.objectContaining({ basis: 'ex GST' })
    );
  });

  it('names every tied supplier and marks a special, because the server reported them', () => {
    const tiedOffers = [offer(), offer({ supplierId: 2, supplierName: 'Woolworths', isSpecial: true })];
    const view = cheapestSupplierSummaryView('loaded', product({ suppliers: tiedOffers }), INC);

    expect(view).toEqual(
      expect.objectContaining({ supplierNames: ['Coles', 'Woolworths'], tied: true, special: true, cheapestOffers: tiedOffers })
    );
  });

  it('says Stale with the stale offer\'s own price and check time when only stale prices exist', () => {
    const view = cheapestSupplierSummaryView(
      'loaded',
      product({
        cheapestUnitPrice: null,
        suppliers: [offer({ supplierName: 'Woolworths', unitPrice: 2.1, isCheapest: false, ineligibility: SupplierOfferIneligibility.Stale })]
      }),
      INC
    );

    expect(view).toEqual({
      kind: 'stale',
      unitPrice: '$2.10',
      basis: 'inc GST',
      supplierName: 'Woolworths',
      lastCheckedAtUtc: '2026-11-03T22:00:00Z'
    });
  });

  it('says No current offers when nothing could be compared and nothing is merely stale', () => {
    const view = cheapestSupplierSummaryView(
      'loaded',
      product({ cheapestUnitPrice: null, suppliers: [offer({ isCheapest: false, ineligibility: SupplierOfferIneligibility.PromotionEnded })] }),
      INC
    );

    expect(view.kind).toBe('noOffers');
  });

  it('never reads a missing unit price as a price', () => {
    const view = cheapestSupplierSummaryView(
      'loaded',
      product({
        cheapestUnitPrice: null,
        suppliers: [offer({ unitPrice: null, isCheapest: false, ineligibility: SupplierOfferIneligibility.Stale })]
      }),
      INC
    );

    expect(view).toEqual(expect.objectContaining({ kind: 'stale', unitPrice: null }));
  });
});

describe('CheapestSupplierSummaryComponent (issue #1093)', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  async function render(value: CheapestSupplierProduct | undefined, state: CheapestSuppliersState = 'loaded') {
    await TestBed.configureTestingModule({ imports: [CheapestSupplierSummaryComponent] }).compileComponents();
    TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');
    const fixture: ComponentFixture<CheapestSupplierSummaryComponent> = TestBed.createComponent(CheapestSupplierSummaryComponent);
    fixture.componentRef.setInput('product', value);
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('displayBasis', INC);
    fixture.componentRef.setInput('productName', 'Boss Iced Double Espresso 237mL');
    fixture.componentRef.setInput('productSku', 'SNT-BOSS-ESP237');
    fixture.componentRef.setInput('categoryName', 'Drinks');
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host,
      compare: () => host.querySelector<HTMLButtonElement>('[data-testid="cheapest-supplier-compare"]'),
      dialog: () => host.querySelector<HTMLDialogElement>('[data-testid="cheapest-supplier-popup"]')
    };
  }

  it('lays out price, basis, supplier, Tied and Special as separate parts', async () => {
    const { host } = await render(
      product({ suppliers: [offer(), offer({ supplierId: 2, supplierName: 'Woolworths', isSpecial: true })] })
    );

    expect(host.querySelector('[data-testid="cheapest-supplier-summary-price"]')?.textContent?.trim()).toBe('$1.84 / unit');
    expect(host.querySelector('[data-testid="cheapest-supplier-summary-basis"]')?.textContent?.trim()).toBe('inc GST');
    expect(host.querySelector('[data-testid="cheapest-supplier-summary-suppliers"]')?.textContent?.trim()).toBe('Coles / Woolworths');
    expect(host.querySelector('[data-testid="cheapest-supplier-summary-tied"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="cheapest-supplier-summary-special"]')).not.toBeNull();
  });

  /**
   * The View link beside each cheapest offer (issue #1098): the server's own `isCheapest` offers,
   * each opening its own product page directly rather than the comparison - never an arbitrary
   * member of a tie, and never fabricated when an offer carries no URL.
   */
  describe('View links (issue #1098)', () => {
    it('opens the cheapest offer\'s own product page in a new tab, named for its supplier and product', async () => {
      const { host } = await render(product());

      const link = host.querySelector<HTMLAnchorElement>('[data-testid="cheapest-supplier-summary-view-1"]');
      expect(link?.textContent?.trim()).toBe('View ↗');
      expect(link?.getAttribute('href')).toBe('https://example.test/coles/espresso');
      expect(link?.getAttribute('target')).toBe('_blank');
      expect(link?.getAttribute('rel')).toBe('noopener noreferrer');
      expect(link?.getAttribute('aria-label')).toBe('Open Coles product page for Boss Iced Double Espresso 237mL');
    });

    it('gives each tied offer its own link to its own URL, named with its own supplier', async () => {
      const { host } = await render(
        product({
          suppliers: [
            offer({ supplierId: 1, supplierName: 'Coles', productUrl: 'https://example.test/coles/espresso' }),
            offer({ supplierId: 2, supplierName: 'Woolworths', productUrl: 'https://example.test/woolworths/espresso' })
          ]
        })
      );

      const coles = host.querySelector<HTMLAnchorElement>('[data-testid="cheapest-supplier-summary-view-1"]');
      const woolworths = host.querySelector<HTMLAnchorElement>('[data-testid="cheapest-supplier-summary-view-2"]');
      expect(coles?.textContent?.trim()).toBe('View Coles ↗');
      expect(coles?.getAttribute('href')).toBe('https://example.test/coles/espresso');
      expect(woolworths?.textContent?.trim()).toBe('View Woolworths ↗');
      expect(woolworths?.getAttribute('href')).toBe('https://example.test/woolworths/espresso');
    });

    it('omits the link for a cheapest offer with no product URL, rather than linking anywhere', async () => {
      const { host } = await render(product({ suppliers: [offer({ productUrl: null })] }));

      expect(host.querySelector('[data-testid="cheapest-supplier-summary-links"]')).toBeNull();
    });

    it('omits a tied offer\'s own link only for the one with no URL, keeping the other\'s', async () => {
      const { host } = await render(
        product({
          suppliers: [
            offer({ supplierId: 1, supplierName: 'Coles', productUrl: null }),
            offer({ supplierId: 2, supplierName: 'Woolworths', productUrl: 'https://example.test/woolworths/espresso' })
          ]
        })
      );

      expect(host.querySelector('[data-testid="cheapest-supplier-summary-view-1"]')).toBeNull();
      expect(host.querySelector('[data-testid="cheapest-supplier-summary-view-2"]')?.textContent?.trim()).toBe('View Woolworths ↗');
    });

    it('never links a stale or non-cheapest offer', async () => {
      const { host } = await render(
        product({
          cheapestUnitPrice: null,
          suppliers: [offer({ unitPrice: 2.1, isCheapest: false, ineligibility: SupplierOfferIneligibility.Stale })]
        })
      );

      expect(host.querySelector('[data-testid^="cheapest-supplier-summary-view-"]')).toBeNull();
    });

    it('does not open the comparison popup when a View link is activated', async () => {
      const { fixture, host, dialog } = await render(product());

      host.querySelector<HTMLAnchorElement>('[data-testid="cheapest-supplier-summary-view-1"]')!.click();
      fixture.detectChanges();

      expect(dialog()).toBeNull();
    });
  });

  it('shows the stale offer\'s check in the business time zone', async () => {
    const { host } = await render(
      product({
        cheapestUnitPrice: null,
        suppliers: [offer({ supplierName: 'Woolworths', unitPrice: 2.1, isCheapest: false, ineligibility: SupplierOfferIneligibility.Stale })]
      })
    );

    expect(host.querySelector('[data-testid="cheapest-supplier-summary-stale"]')?.textContent?.trim()).toBe('Stale price');
    expect(host.querySelector('[data-testid="cheapest-supplier-summary-freshness"]')?.textContent?.trim()).toBe(
      'Checked 04/11/2026, 09:00 am (Woolworths)'
    );
  });

  it('offers Compare with the number of suppliers, and opens the shared comparison as a modal', async () => {
    const showModal = jest.spyOn(Object.getPrototypeOf(document.createElement('dialog')), 'showModal');
    const { fixture, compare, dialog } = await render(product({ suppliers: [offer(), offer({ supplierId: 2, supplierName: 'Woolworths', isCheapest: false })] }));

    expect(compare()?.textContent?.trim()).toBe('Compare (2) ›');
    expect(compare()?.getAttribute('aria-haspopup')).toBe('dialog');
    expect(dialog()).toBeNull();

    compare()!.click();
    fixture.detectChanges();

    expect(showModal).toHaveBeenCalledTimes(1);
    expect(dialog()?.textContent).toContain('SKU: SNT-BOSS-ESP237');
    expect(dialog()?.textContent).toContain('Category: Drinks');
    expect(compare()?.getAttribute('aria-expanded')).toBe('true');
  });

  it('returns focus to Compare when the comparison closes', async () => {
    const { fixture, compare, dialog } = await render(product());

    compare()!.click();
    fixture.detectChanges();
    dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();

    expect(dialog()).toBeNull();
    expect(document.activeElement).toBe(compare());
  });

  it('offers no Compare while the read is loading or unavailable, or with nothing to list', async () => {
    expect((await render(product(), 'loading')).compare()).toBeNull();
    TestBed.resetTestingModule();
    expect((await render(product(), 'failed')).compare()).toBeNull();
    TestBed.resetTestingModule();
    expect((await render(product({ trackingStatus: null, suppliers: [] }))).compare()).toBeNull();
  });
});
