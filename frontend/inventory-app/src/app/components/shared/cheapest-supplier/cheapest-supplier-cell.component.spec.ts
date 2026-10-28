import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  CheapestSupplierOffer,
  CheapestSupplierProduct,
  SupplierOfferIneligibility,
  SupplierPriceBasis,
  SupplierStockStatus
} from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import {
  AUTOMATIC_MATCH_NOTE,
  CheapestSupplierCellComponent,
  OBSERVED_PRICES_NOTE,
  cheapestSupplierDisplay
} from './cheapest-supplier-cell.component';
import { CheapestSuppliersState } from './cheapest-suppliers.component';

function offer(overrides: Partial<CheapestSupplierOffer> = {}): CheapestSupplierOffer {
  return {
    supplierId: 1,
    supplierName: 'JB Metro',
    hasListing: true,
    hasPrice: true,
    packQuantity: 24,
    packPrice: 22.8,
    packPriceBasis: SupplierPriceBasis.InclusiveOfGst,
    unitPrice: 0.95,
    unitPriceExGst: 0.8636,
    unitPriceIncGst: 0.95,
    isSpecial: false,
    promotionEndsAtUtc: null,
    stockStatus: SupplierStockStatus.InStock,
    lastCheckedAtUtc: '2026-10-28T04:00:00Z',
    productUrl: 'https://example.test/jb-metro/cola',
    ineligibility: null,
    isCheapest: true,
    ...overrides
  };
}

function product(overrides: Partial<CheapestSupplierProduct> = {}): CheapestSupplierProduct {
  return {
    productId: 7,
    trackingStatus: ProductPriceTrackingStatus.Matched,
    cheapestUnitPrice: 0.95,
    suppliers: [offer()],
    ...overrides
  };
}

describe('cheapestSupplierDisplay (issue #930)', () => {
  const basis = SupplierPriceBasis.InclusiveOfGst;

  it('says the read is still going rather than answering about the product', () => {
    expect(cheapestSupplierDisplay('loading', undefined, basis)).toBe('Loading');
  });

  it.each<CheapestSuppliersState>(['failed', 'unavailable'])(
    'says the column is unavailable when the read %s, never that there is no price',
    (state) => {
      expect(cheapestSupplierDisplay(state, product(), basis)).toBe('Unavailable');
    }
  );

  it('says a product the business does not track is not tracked', () => {
    expect(cheapestSupplierDisplay('loaded', product({ trackingStatus: null }), basis)).toBe('Not tracked');
    expect(cheapestSupplierDisplay('loaded', undefined, basis)).toBe('Not tracked');
  });

  it.each([ProductPriceTrackingStatus.Requested, ProductPriceTrackingStatus.AwaitingProductDetails])(
    'says a tracked product nothing has matched yet is awaiting a match (status %s)',
    (trackingStatus) => {
      expect(
        cheapestSupplierDisplay('loaded', product({ trackingStatus, suppliers: [], cheapestUnitPrice: null }), basis)
      ).toBe('Awaiting match');
    }
  );

  it('keeps "looked for and not found" a separate answer from "still waiting"', () => {
    expect(
      cheapestSupplierDisplay(
        'loaded',
        product({ trackingStatus: ProductPriceTrackingStatus.NotFound, suppliers: [], cheapestUnitPrice: null }),
        basis
      )
    ).toBe('No match found');
  });

  it('says a matched product whose offers could none of them be compared has no eligible price', () => {
    const outOfStock = offer({ isCheapest: false, ineligibility: SupplierOfferIneligibility.OutOfStock });

    expect(cheapestSupplierDisplay('loaded', product({ cheapestUnitPrice: null, suppliers: [outOfStock] }), basis)).toBe(
      'No eligible price'
    );
  });

  it('names the cheapest supplier, its unit price and the basis it is on', () => {
    expect(cheapestSupplierDisplay('loaded', product(), basis)).toBe('JB Metro $0.95/unit inc GST');
  });

  it('names every supplier in a tie, because the server reported both as cheapest', () => {
    const tied = product({
      suppliers: [offer(), offer({ supplierId: 2, supplierName: 'Coles' })]
    });

    expect(cheapestSupplierDisplay('loaded', tied, basis)).toBe('JB Metro / Coles $0.95/unit inc GST');
  });

  it('says when the cheapest price is a special', () => {
    const special = product({ suppliers: [offer({ isSpecial: true })] });

    expect(cheapestSupplierDisplay('loaded', special, basis)).toBe('JB Metro $0.95/unit inc GST (special)');
  });
});

describe('CheapestSupplierCellComponent (issue #930)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(overrides: Partial<CheapestSupplierProduct> | null = {}, state: CheapestSuppliersState = 'loaded') {
    await TestBed.configureTestingModule({ imports: [CheapestSupplierCellComponent] }).compileComponents();

    const fixture: ComponentFixture<CheapestSupplierCellComponent> = TestBed.createComponent(CheapestSupplierCellComponent);
    fixture.componentRef.setInput('product', overrides === null ? undefined : product(overrides));
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('displayBasis', SupplierPriceBasis.InclusiveOfGst);
    fixture.componentRef.setInput('productName', 'Coca-Cola Classic');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;

    return {
      fixture,
      host,
      cell: () => host.querySelector('[data-testid="cheapest-supplier-cell"]') as HTMLElement,
      popup: () => host.querySelector('[data-testid="cheapest-supplier-popup"]') as HTMLElement | null,
      rows: () => Array.from(host.querySelectorAll('[data-testid="cheapest-supplier-row"]'))
    };
  }

  it('opens an accessible, labelled dialog from the cell and focuses it', async () => {
    const { fixture, cell, popup } = await render();

    const trigger = cell() as HTMLButtonElement;
    expect(trigger.tagName).toBe('BUTTON');
    expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');

    trigger.click();
    fixture.detectChanges();

    const dialog = popup()!;
    expect(dialog.getAttribute('role')).toBe('dialog');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    const labelledBy = dialog.getAttribute('aria-labelledby')!;
    expect(dialog.querySelector(`#${labelledBy}`)?.textContent).toContain('Coca-Cola Classic');
    expect(document.activeElement).toBe(dialog);
    expect((cell() as HTMLButtonElement).getAttribute('aria-expanded')).toBe('true');
  });

  it('closes on Escape and gives focus back to the cell that opened it', async () => {
    const { fixture, cell, popup } = await render();

    (cell() as HTMLButtonElement).click();
    fixture.detectChanges();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(popup()).toBeNull();
    expect(document.activeElement).toBe(cell());
  });

  it('lists every supplier in the order the server published them, with the cheapest marked', async () => {
    const { fixture, cell, rows } = await render({
      suppliers: [
        offer(),
        offer({ supplierId: 2, supplierName: 'Coles', packQuantity: 1, packPrice: 1.27, unitPrice: 1.27, isCheapest: false }),
        offer({
          supplierId: 3,
          supplierName: 'Woolworths',
          hasListing: false,
          hasPrice: false,
          packQuantity: null,
          packPrice: null,
          packPriceBasis: null,
          unitPrice: null,
          unitPriceExGst: null,
          unitPriceIncGst: null,
          stockStatus: null,
          lastCheckedAtUtc: null,
          productUrl: null,
          isCheapest: false
        })
      ]
    });

    (cell() as HTMLButtonElement).click();
    fixture.detectChanges();

    const text = rows().map((row) => row.textContent ?? '');
    expect(text.map((row) => row.trim().startsWith('JB Metro') || row.includes('JB Metro'))).toEqual([true, false, false]);
    expect(text[0]).toContain('Cheapest');
    expect(text[0]).toContain('24 pack');
    expect(text[0]).toContain('$22.80');
    expect(text[0]).toContain('$0.95 inc GST');
    expect(text[0]).toContain('$0.86 ex GST');
    expect(text[0]).toContain('In stock');
    expect(text[1]).toContain('Coles');
    expect(text[2]).toContain('Woolworths');
    expect(text[2]).toContain('No match');
  });

  it('says that the prices are observed guidance rather than recorded costs', async () => {
    const { fixture, cell, host } = await render();

    (cell() as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(host.textContent).toContain(OBSERVED_PRICES_NOTE);
  });

  it('says when the match was made automatically and has not been checked', async () => {
    const { fixture, cell, host } = await render({ trackingStatus: ProductPriceTrackingStatus.AutoMatched });

    (cell() as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(host.textContent).toContain(AUTOMATIC_MATCH_NOTE);
  });

  it('does not say so for a match somebody decided', async () => {
    const { fixture, cell, host } = await render();

    (cell() as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(host.textContent).not.toContain(AUTOMATIC_MATCH_NOTE);
  });

  it('says why an offer was left out of the comparison instead of leaving it off the list', async () => {
    const { fixture, cell, rows } = await render({
      cheapestUnitPrice: null,
      suppliers: [
        offer({
          supplierName: 'BIG W',
          isCheapest: false,
          stockStatus: SupplierStockStatus.OutOfStock,
          ineligibility: SupplierOfferIneligibility.OutOfStock
        }),
        offer({
          supplierId: 2,
          supplierName: 'Coles',
          hasPrice: false,
          packPrice: null,
          unitPrice: null,
          unitPriceExGst: null,
          unitPriceIncGst: null,
          stockStatus: null,
          lastCheckedAtUtc: null,
          isCheapest: false
        })
      ]
    });

    (cell() as HTMLButtonElement).click();
    fixture.detectChanges();

    const text = rows().map((row) => row.textContent ?? '');
    expect(text[0]).toContain('Out of stock');
    expect(text[1]).toContain('Not checked yet');
    expect(text[1]).toContain('Never');
  });

  it('offers nothing to open when there is nothing to list', async () => {
    const { cell, popup } = await render({
      trackingStatus: null,
      cheapestUnitPrice: null,
      suppliers: []
    });

    expect(cell().tagName).toBe('SPAN');
    expect(cell().textContent?.trim()).toBe('Not tracked');
    expect(popup()).toBeNull();
  });

  it('offers nothing to open while the read has not answered', async () => {
    const { cell } = await render(null, 'loading');

    expect(cell().tagName).toBe('SPAN');
    expect(cell().textContent?.trim()).toBe('Loading');
  });
});
