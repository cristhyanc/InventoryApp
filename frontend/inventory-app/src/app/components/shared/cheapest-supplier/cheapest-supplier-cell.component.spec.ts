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
import {
  AUTOMATIC_MATCH_NOTE,
  CheapestSupplierCellComponent,
  OBSERVED_PRICES_NOTE,
  STOCK_MAY_DIFFER_NOTE,
  cheapestStockNote,
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

/**
 * The prototype `<dialog>`'s methods live on, taken from an element rather than from the global so
 * the spec works whichever interface jsdom maps `dialog` to. `setup-jest.ts` supplies the
 * open/close state machine jsdom 20 is missing; what is spied on here is which of the two opening
 * calls the component makes, because `showModal()` is the one that makes the page behind the panel
 * inert and `show()` is the one that does not.
 */
const dialogPrototype = Object.getPrototypeOf(document.createElement('dialog')) as HTMLDialogElement;

/** A Tab, Shift+Tab or Escape press on whatever currently has focus, as the browser reports one. */
function press(key: string, shiftKey = false): void {
  (document.activeElement ?? document.body).dispatchEvent(
    new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true })
  );
}

describe('CheapestSupplierCellComponent (issue #930)', () => {
  let showModal: jest.SpyInstance;
  let show: jest.SpyInstance;

  beforeEach(() => {
    showModal = jest.spyOn(dialogPrototype, 'showModal');
    show = jest.spyOn(dialogPrototype, 'show');
  });

  afterEach(() => {
    jest.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  async function render(
    overrides: Partial<CheapestSupplierProduct> | null = {},
    state: CheapestSuppliersState = 'loaded',
    timeZoneId?: string
  ) {
    await TestBed.configureTestingModule({ imports: [CheapestSupplierCellComponent] }).compileComponents();

    // The business's own zone, as `GET /api/business/current` publishes it. Left unpublished it
    // stays "not known yet", which is the state the popup must render no instant at all in.
    if (timeZoneId !== undefined) {
      TestBed.inject(BusinessTimeZoneService).publish(timeZoneId);
    }

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
      popup: () => host.querySelector('[data-testid="cheapest-supplier-popup"]') as HTMLDialogElement | null,
      rows: () => Array.from(host.querySelectorAll('[data-testid="cheapest-supplier-row"]')),
      lastChecked: () =>
        Array.from(host.querySelectorAll('[data-testid="cheapest-supplier-last-checked"]')).map(
          (cell) => cell.textContent?.trim() ?? ''
        ),
      open: () => {
        (host.querySelector('[data-testid="cheapest-supplier-cell"]') as HTMLButtonElement).click();
        fixture.detectChanges();
        return host.querySelector('[data-testid="cheapest-supplier-popup"]') as HTMLDialogElement;
      },
      tabStops: () =>
        Array.from(
          (host.querySelector('[data-testid="cheapest-supplier-popup"]') as HTMLElement).querySelectorAll<HTMLElement>(
            'a[href], button:not([disabled])'
          )
        )
    };
  }

  it('opens a labelled native modal dialog from the cell and focuses it', async () => {
    const { fixture, cell, popup } = await render();

    const trigger = cell() as HTMLButtonElement;
    expect(trigger.tagName).toBe('BUTTON');
    expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');

    trigger.click();
    fixture.detectChanges();

    const dialog = popup()!;
    // A native `<dialog>` opened with `showModal()`, which is what maps it to the modal-dialog role
    // and makes the rest of the page inert; `role="dialog"`/`aria-modal="true"` on a plain div
    // claimed that without it, which is the finding this replaces.
    expect(dialog.tagName).toBe('DIALOG');
    expect(showModal).toHaveBeenCalledTimes(1);
    expect(show).not.toHaveBeenCalled();
    expect(dialog.hasAttribute('open')).toBe(true);
    const labelledBy = dialog.getAttribute('aria-labelledby')!;
    expect(dialog.querySelector(`#${labelledBy}`)?.textContent).toContain('Coca-Cola Classic');
    expect(document.activeElement).toBe(dialog);
    expect((cell() as HTMLButtonElement).getAttribute('aria-expanded')).toBe('true');
  });

  it('closes on Escape and gives focus back to the cell that opened it', async () => {
    const { fixture, cell, popup, open } = await render();

    open();
    press('Escape');
    fixture.detectChanges();

    expect(popup()).toBeNull();
    expect(document.activeElement).toBe(cell());
    expect((cell() as HTMLButtonElement).getAttribute('aria-expanded')).toBe('false');
  });

  it('closes on the browser\'s own close request and leaves the cell reporting itself closed', async () => {
    const { fixture, cell, popup, open } = await render();

    const dialog = open();
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    fixture.detectChanges();

    expect(popup()).toBeNull();
    expect(document.activeElement).toBe(cell());
    expect((cell() as HTMLButtonElement).getAttribute('aria-expanded')).toBe('false');
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
          isSpecial: true,
          ineligibility: SupplierOfferIneligibility.PromotionEnded
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
    expect(text[0]).toContain('The special has ended');
    expect(text[1]).toContain('Not checked yet');
    expect(text[1]).toContain('Never');
  });

  it('says under a cheapest price that stock may differ, even when the cheapest supplier reports out of stock', async () => {
    const { host } = await render({
      suppliers: [offer({ stockStatus: SupplierStockStatus.OutOfStock })]
    });

    const note = host.querySelector('[data-testid="cheapest-supplier-stock-note"]');
    expect(note?.textContent?.trim()).toBe(STOCK_MAY_DIFFER_NOTE);
    expect(note?.textContent?.trim()).toBe('Stock may differ');
  });

  it('adds no stock note where the cell shows no price', async () => {
    const { host } = await render({ cheapestUnitPrice: null, suppliers: [offer({ isCheapest: false })] });

    expect(host.querySelector('[data-testid="cheapest-supplier-stock-note"]')).toBeNull();
    expect(cheapestStockNote('loading', product())).toBeNull();
    expect(cheapestStockNote('loaded', undefined)).toBeNull();
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

  /**
   * The popup says it is modal, so the keyboard has to stay inside it and the page behind it has
   * to be out of reach. The review finding these cover is that the panel declared `aria-modal`
   * while Tab walked straight out of it into the page underneath.
   *
   * Two halves prove it, and only one of them belongs to this component. The platform's half - the
   * top layer, the inert page, the `::backdrop` - is `showModal()`, asserted above as the call the
   * component makes; jsdom implements none of it, so asserting it here would only be asserting
   * `setup-jest.ts`'s stub. This component's own half is the Tab cycle, and that is exercised for
   * real below: real `keydown` events, real `focus()` calls and `document.activeElement` read back
   * each time, with a live control on the page behind the popup that the cycle must never reach.
   */
  describe('keeps focus inside the open popup', () => {
    let background: HTMLButtonElement;

    beforeEach(() => {
      background = document.createElement('button');
      background.textContent = 'A control on the page behind the popup';
      document.body.appendChild(background);
    });

    afterEach(() => background.remove());

    it('moves Tab from the panel itself to its first control, not into the page behind it', async () => {
      const { open, tabStops } = await render();

      open();
      expect(document.activeElement).not.toBe(background);

      press('Tab');

      expect(document.activeElement).toBe(tabStops()[0]);
    });

    it('wraps Tab from the last control back to the first', async () => {
      const { open, tabStops } = await render();

      const dialog = open();
      const stops = tabStops();
      expect(stops.length).toBeGreaterThan(1);
      stops[stops.length - 1].focus();

      press('Tab');

      expect(document.activeElement).toBe(stops[0]);
      expect(dialog.contains(document.activeElement)).toBe(true);
    });

    it('wraps Shift+Tab from the first control back to the last, and from the panel to the last', async () => {
      const { open, tabStops } = await render();

      open();
      const stops = tabStops();
      const last = stops[stops.length - 1];

      // From the panel itself, backwards is the end of its own cycle, never the page behind it.
      press('Tab', true);
      expect(document.activeElement).toBe(last);

      stops[0].focus();
      press('Tab', true);
      expect(document.activeElement).toBe(last);
    });

    it('never lands on a control outside the popup, Tab or Shift+Tab, however long it is held', async () => {
      const { open, tabStops } = await render();

      const dialog = open();
      const stops = tabStops();
      const visited: HTMLElement[] = [];

      for (let step = 0; step < stops.length * 2 + 1; step += 1) {
        press('Tab');
        expect(document.activeElement).not.toBe(background);
        expect(dialog.contains(document.activeElement)).toBe(true);
        visited.push(document.activeElement as HTMLElement);
      }

      // Forward cycling visits every one of the popup's controls and nothing else.
      expect(new Set(visited)).toEqual(new Set(stops));

      for (let step = 0; step < stops.length * 2 + 1; step += 1) {
        press('Tab', true);
        expect(document.activeElement).not.toBe(background);
        expect(dialog.contains(document.activeElement)).toBe(true);
      }
    });

    it('closes on a click beside the panel and not on one inside it, returning focus either way', async () => {
      const { fixture, cell, popup, open } = await render();

      const dialog = open();
      // A click on the content is not a dismissal: the `::backdrop` click is the one reported with
      // the dialog itself as its target.
      dialog.querySelector('table')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      fixture.detectChanges();
      expect(popup()).not.toBeNull();

      dialog.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      fixture.detectChanges();

      expect(popup()).toBeNull();
      expect(document.activeElement).toBe(cell());
    });

    it('closes from its own Close button and returns focus to the cell', async () => {
      const { fixture, cell, popup, open } = await render();

      const dialog = open();
      Array.from(dialog.querySelectorAll('button'))
        .find((button) => button.textContent?.trim() === 'Close')!
        .click();
      fixture.detectChanges();

      expect(popup()).toBeNull();
      expect(document.activeElement).toBe(cell());
    });
  });

  /**
   * "Last checked" is a true instant, so it follows the business's own configured zone and not the
   * zone the operator's browser happens to be in (AGENTS.md § Reporting dates and filters). Each
   * case asserts an exact wall-clock reading in a named zone, and the two zones are far enough
   * apart that no single browser zone can satisfy both - which is what makes these cases evidence
   * that the business's zone is read rather than the host's. Both zones' daylight-saving
   * transitions are included: one instant either side of each, where an hour of UTC does not move
   * the business clock by an hour.
   */
  describe('shows Last checked in the business time zone', () => {
    it.each([
      // Australian Eastern Daylight Time, UTC+11.
      ['Australia/Sydney', '2026-10-28T04:00:00Z', '28/10/2026, 03:00 pm'],
      // The same instant for a business in New York: UTC-4, and the day before.
      ['America/New_York', '2026-10-28T04:00:00Z', '28/10/2026, 12:00 am'],
      // Sydney's daylight saving ends 3:00 am AEDT on 5 April 2026, so the clock goes back to
      // 2:00 am AEST and one UTC minute moves it back 59.
      ['Australia/Sydney', '2026-04-04T15:59:00Z', '05/04/2026, 02:59 am'],
      ['Australia/Sydney', '2026-04-04T16:00:00Z', '05/04/2026, 02:00 am'],
      // It starts 2:00 am AEST on 4 October 2026, so the clock jumps to 3:00 am AEDT.
      ['Australia/Sydney', '2026-10-03T15:59:00Z', '04/10/2026, 01:59 am'],
      ['Australia/Sydney', '2026-10-03T16:00:00Z', '04/10/2026, 03:00 am'],
      // New York's ends 2:00 am EDT on 1 November 2026, back to 1:00 am EST.
      ['America/New_York', '2026-11-01T05:59:00Z', '01/11/2026, 01:59 am'],
      ['America/New_York', '2026-11-01T06:00:00Z', '01/11/2026, 01:00 am'],
      // ...and starts 2:00 am EST on 8 March 2026, jumping to 3:00 am EDT.
      ['America/New_York', '2026-03-08T06:59:00Z', '08/03/2026, 01:59 am'],
      ['America/New_York', '2026-03-08T07:00:00Z', '08/03/2026, 03:00 am']
    ])('renders %s\'s own wall clock for %s', async (timeZoneId, lastCheckedAtUtc, expected) => {
      const { open, lastChecked } = await render({ suppliers: [offer({ lastCheckedAtUtc })] }, 'loaded', timeZoneId);

      open();

      expect(lastChecked()).toEqual([expected]);
    });

    it('renders no instant at all while the business time zone is unknown, rather than the browser\'s', async () => {
      const { open, lastChecked } = await render({ suppliers: [offer()] });

      open();

      // Blank, and in particular not the browser-local rendering of the instant and not "Never",
      // which is the answer for a listing nobody has checked.
      expect(lastChecked()).toEqual(['']);
    });

    it('still says Never for a listing nobody has checked, in a known zone', async () => {
      const { open, lastChecked } = await render(
        { suppliers: [offer({ lastCheckedAtUtc: null })] },
        'loaded',
        'Australia/Sydney'
      );

      open();

      expect(lastChecked()).toEqual(['Never']);
    });
  });
});
