import { TestBed } from '@angular/core/testing';
import { CheapestSupplierOffer, SupplierOfferIneligibility, SupplierPriceBasis, SupplierStockStatus } from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import { SupplierComparisonDialogComponent, comparisonBasisNote } from './supplier-comparison-dialog.component';

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
    isSpecial: true,
    promotionEndsAtUtc: null,
    stockStatus: SupplierStockStatus.InStock,
    lastCheckedAtUtc: null,
    productUrl: 'https://example.test/coles/espresso',
    ineligibility: null,
    isCheapest: true,
    ...overrides
  };
}

/** The one shared supplier comparison (issue #1093): its columns, and what it shows for missing data. */
describe('SupplierComparisonDialogComponent (issue #1093)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(suppliers: CheapestSupplierOffer[], displayBasis = SupplierPriceBasis.InclusiveOfGst) {
    await TestBed.configureTestingModule({ imports: [SupplierComparisonDialogComponent] }).compileComponents();
    const fixture = TestBed.createComponent(SupplierComparisonDialogComponent);
    fixture.componentRef.setInput('product', {
      productId: 4,
      trackingStatus: ProductPriceTrackingStatus.Matched,
      cheapestUnitPrice: 1.84,
      suppliers
    });
    fixture.componentRef.setInput('displayBasis', displayBasis);
    fixture.componentRef.setInput('productName', 'Boss Iced Double Espresso 237mL');
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, rows: () => Array.from(host.querySelectorAll('[data-testid="cheapest-supplier-row"]')) };
  }

  it('has exactly the Supplier, Pack size, Pack price, Unit price, Status & freshness and Link columns', async () => {
    const { host } = await render([offer()]);

    expect(Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim())).toEqual([
      'Supplier',
      'Pack size',
      'Pack price',
      'Unit price',
      'Status & freshness',
      'Link'
    ]);
  });

  it('shows each pack and price as the server published it, with the basis beside each', async () => {
    const { rows } = await render([offer()]);
    const row = rows()[0];

    expect(row.querySelector('[data-testid="supplier-comparison-pack-size"]')?.textContent?.trim()).toBe('12 pack');
    expect(row.querySelector('[data-testid="supplier-comparison-pack-price"]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('$22.08 inc GST');
    expect(row.textContent).toContain('$1.84 inc GST');
    expect(row.textContent).toContain('$1.67 ex GST');
    expect(row.textContent).toContain('Special');
    expect(row.textContent).toContain('Lowest unit price');
    expect(row.querySelector('a')?.getAttribute('href')).toBe('https://example.test/coles/espresso');
  });

  it('carries the required check-before-buying notice above the table, before any supplier row', async () => {
    const { host } = await render([offer()]);

    const notice = host.querySelector('[data-testid="supplier-comparison-notice"]');
    expect(notice?.querySelector('p')?.textContent?.trim()).toBe(
      'Check before buying: Prices and availability may change and can vary by location, membership or '
      + 'promotion. Comparisons use recorded unit prices from supported suppliers and exclude delivery '
      + 'and additional fees. Confirm the product, pack size and final total on the supplier’s '
      + 'website before purchasing.'
    );

    const table = host.querySelector('table');
    expect(notice === null || table === null).toBe(false);
    expect(notice!.compareDocumentPosition(table!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('explains the Lowest unit price badge once, in visible text, when a row carries it', async () => {
    const { host } = await render([offer()]);

    expect(host.querySelector('[data-testid="supplier-comparison-badge-explainer"]')?.textContent).toBe(
      'Lowest recorded unit price among the eligible offers shown, excluding delivery and additional '
      + 'fees. Check the supplier’s final price before buying.'
    );
  });

  it('does not explain the badge when no offer carries it', async () => {
    const { host } = await render([offer({ isCheapest: false, ineligibility: SupplierOfferIneligibility.Stale })]);

    expect(host.querySelector('[data-testid="supplier-comparison-badge-explainer"]')).toBeNull();
  });

  it('shows unavailable pack and price data as unavailable, never inferred or zero', async () => {
    const { rows } = await render([
      offer({ packQuantity: null, packPrice: null, packPriceBasis: null, unitPrice: null, unitPriceExGst: null, unitPriceIncGst: null, productUrl: null, isCheapest: false })
    ]);
    const row = rows()[0];

    expect(row.querySelector('[data-testid="supplier-comparison-pack-size"]')?.textContent?.trim()).toBe('—');
    expect(row.querySelector('[data-testid="supplier-comparison-pack-price"]')?.textContent?.trim()).toBe('—');
    expect(row.textContent).not.toContain('$0.00');
    expect(row.querySelector('a')).toBeNull();
  });

  it('says which GST basis the unit prices are compared on, from the server\'s basis', async () => {
    expect(comparisonBasisNote(SupplierPriceBasis.ExclusiveOfGst)).toContain('ex GST');
    expect(comparisonBasisNote(null)).toBeNull();

    const { host } = await render([offer()], SupplierPriceBasis.ExclusiveOfGst);
    expect(host.querySelector('[data-testid="supplier-comparison-note"]')?.textContent).toContain('compared ex GST');
  });

  it('reports closed once it has closed itself from Close', async () => {
    const { fixture, host } = await render([offer()]);
    const closed = jest.fn();
    fixture.componentInstance.closed.subscribe(closed);
    const dialog = host.querySelector('dialog') as HTMLDialogElement;

    Array.from(dialog.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Close')!.click();

    expect(closed).toHaveBeenCalledTimes(1);
    expect(dialog.hasAttribute('open')).toBe(false);
  });
});
