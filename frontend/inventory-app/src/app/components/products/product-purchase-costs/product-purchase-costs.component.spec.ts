import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { ProductPurchaseCostSummary } from '../../../models/product-purchase-cost-summary';
import { ProductPurchaseCostService } from '../../../services/product-purchase-cost.service';
import {
  PURCHASE_COSTS_FAILED_MESSAGE,
  ProductPurchaseCosts,
  ProductPurchaseCostsComponent,
  purchaseCostDisplay,
  purchaseCostSortValue,
  purchaseCostSupplierDisplay
} from './product-purchase-costs.component';

function summary(productId: number, overrides: Partial<ProductPurchaseCostSummary> = {}): ProductPurchaseCostSummary {
  return {
    productId,
    lastUnitCost: 2.4,
    lastSupplierName: 'Acme Supplies',
    lastPurchaseDate: '2026-03-01T00:00:00',
    lowestUnitCost: 1.1,
    lowestSupplierName: 'Bravo Co',
    lowestPurchaseDate: '2026-01-01T00:00:00',
    ...overrides
  };
}

function costs(state: ProductPurchaseCosts['state'], summaries: ProductPurchaseCostSummary[] = []): ProductPurchaseCosts {
  return { state, summaries: new Map(summaries.map((entry) => [entry.productId, entry])) };
}

describe('ProductPurchaseCostsComponent (issue #791)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(options: { getSummaries?: jest.Mock; allowed?: boolean } = {}) {
    const getSummaries = options.getSummaries ?? jest.fn(() => of([]));
    await TestBed.configureTestingModule({
      imports: [ProductPurchaseCostsComponent],
      providers: [
        { provide: ProductPurchaseCostService, useValue: { getSummaries } },
        { provide: AccessService, useValue: { allows: () => of(options.allowed ?? true) } }
      ]
    }).compileComponents();

    const fixture: ComponentFixture<ProductPurchaseCostsComponent> = TestBed.createComponent(ProductPurchaseCostsComponent);
    const published: ProductPurchaseCosts[] = [];
    fixture.componentInstance.costsChange.subscribe((answer) => published.push(answer));

    const show = (productIds: readonly number[]) => {
      fixture.componentRef.setInput('productIds', productIds);
      fixture.detectChanges();
    };

    return { fixture, host: fixture.nativeElement as HTMLElement, getSummaries, published, show };
  }

  function retryButton(host: HTMLElement): HTMLButtonElement | undefined {
    return Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Retry');
  }

  it('reads the summaries once for the ids it is given and publishes them keyed by product id', async () => {
    const getSummaries = jest.fn(() => of([summary(1), summary(2, { lastUnitCost: 0, lastSupplierName: null })]));
    const { published, show } = await render({ getSummaries });

    show([1, 2]);

    expect(getSummaries).toHaveBeenCalledTimes(1);
    expect(getSummaries).toHaveBeenCalledWith([1, 2]);

    const answer = published[published.length - 1];
    expect(answer.state).toBe('loaded');
    expect(answer.summaries.get(1)?.lastUnitCost).toBe(2.4);
    expect(answer.summaries.get(2)?.lastUnitCost).toBe(0);
    // "Loading" is published before the answer, so a page never shows a cost cell as settled while
    // the read is still in flight.
    expect(published[0].state).toBe('loading');
  });

  it('publishes a settled, empty answer without a request when there are no products to ask about', async () => {
    const { getSummaries, published, show } = await render();

    show([]);

    expect(getSummaries).not.toHaveBeenCalled();
    expect(published).toEqual([{ state: 'loaded', summaries: new Map() }]);
  });

  it('makes no request at all without Dashboard.ViewFinancials, and reports that as settled rather than failed', async () => {
    const getSummaries = jest.fn(() => of([summary(1)]));
    const { host, published, show } = await render({ getSummaries, allowed: false });

    show([1]);

    expect(getSummaries).not.toHaveBeenCalled();
    expect(published[published.length - 1]).toEqual({ state: 'loaded', summaries: new Map() });
    expect(retryButton(host)).toBeUndefined();
  });

  it('owns the failure message and the Retry control that re-enters the read for the same ids', async () => {
    const getSummaries = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('boom')))
      .mockReturnValueOnce(of([summary(1)]));
    const { fixture, host, published, show } = await render({ getSummaries });

    show([1]);

    expect(published[published.length - 1].state).toBe('failed');
    expect(host.textContent).toContain(PURCHASE_COSTS_FAILED_MESSAGE);

    retryButton(host)!.click();
    fixture.detectChanges();

    expect(getSummaries).toHaveBeenCalledTimes(2);
    expect(getSummaries).toHaveBeenLastCalledWith([1]);
    expect(published[published.length - 1].summaries.get(1)?.lastUnitCost).toBe(2.4);
    expect(retryButton(host)).toBeUndefined();
  });

  it('drops an outstanding read for an earlier product set, so its late answer never lands under a newer one', async () => {
    const firstAnswer = new Subject<ProductPurchaseCostSummary[]>();
    const getSummaries = jest
      .fn()
      .mockReturnValueOnce(firstAnswer.asObservable())
      .mockReturnValueOnce(of([summary(2)]));
    const { published, show } = await render({ getSummaries });

    show([1]);
    show([2]);

    firstAnswer.next([summary(1, { lastUnitCost: 99 })]);
    firstAnswer.complete();

    const answer = published[published.length - 1];
    expect(answer.state).toBe('loaded');
    expect(answer.summaries.has(1)).toBe(false);
    expect(answer.summaries.get(2)?.lastUnitCost).toBe(2.4);
  });

  it('stops reading once the page that composed it is destroyed', async () => {
    const pending = new Subject<ProductPurchaseCostSummary[]>();
    const { fixture, published, show } = await render({ getSummaries: jest.fn(() => pending.asObservable()) });

    show([1]);
    fixture.destroy();
    const publishedBeforeTheAnswer = published.length;

    pending.next([summary(1)]);
    pending.complete();

    expect(published).toHaveLength(publishedBeforeTheAnswer);
  });
});

describe('purchase-cost display rules (issue #791)', () => {
  it('answers an em dash only after a successful read found no purchase for the product', () => {
    expect(purchaseCostDisplay(costs('loading'), 1, 'lastPurchase')).toBe('Loading');
    expect(purchaseCostDisplay(costs('failed'), 1, 'lastPurchase')).toBe('Unavailable');
    expect(purchaseCostDisplay(costs('loaded'), 1, 'lastPurchase')).toBe('—');
  });

  it('shows a genuine recorded zero as $0.00 rather than as a missing cost', () => {
    const answer = costs('loaded', [summary(1, { lastUnitCost: 0, lowestUnitCost: 0 })]);

    expect(purchaseCostDisplay(answer, 1, 'lastPurchase')).toBe('$0.00');
    expect(purchaseCostDisplay(answer, 1, 'cheapestPurchase')).toBe('$0.00');
  });

  it('shows each column its own cost', () => {
    const answer = costs('loaded', [summary(1)]);

    expect(purchaseCostDisplay(answer, 1, 'lastPurchase')).toBe('$2.40');
    expect(purchaseCostDisplay(answer, 1, 'cheapestPurchase')).toBe('$1.10');
  });

  it('names an unrecorded supplier None, independently of the cost, and shows none at all without a cost', () => {
    const answer = costs('loaded', [summary(1, { lastSupplierName: null })]);

    expect(purchaseCostSupplierDisplay(answer, 1, 'lastPurchase')).toBe('None');
    expect(purchaseCostSupplierDisplay(answer, 1, 'cheapestPurchase')).toBe('Bravo Co');
    expect(purchaseCostSupplierDisplay(costs('loaded'), 1, 'lastPurchase')).toBeNull();
    expect(purchaseCostSupplierDisplay(costs('failed', [summary(1)]), 1, 'lastPurchase')).toBeNull();
    expect(purchaseCostSupplierDisplay(costs('loading', [summary(1)]), 1, 'lastPurchase')).toBeNull();
  });

  it('sorts on the cost figure, and has nothing to sort on while the read is unfinished or failed', () => {
    const answer = costs('loaded', [summary(1)]);

    expect(purchaseCostSortValue(answer, 1, 'lastPurchase')).toBe(2.4);
    expect(purchaseCostSortValue(answer, 1, 'cheapestPurchase')).toBe(1.1);
    expect(purchaseCostSortValue(answer, 2, 'lastPurchase')).toBeNull();
    expect(purchaseCostSortValue(costs('loading', [summary(1)]), 1, 'lastPurchase')).toBeNull();
    expect(purchaseCostSortValue(costs('failed', [summary(1)]), 1, 'lastPurchase')).toBeNull();
  });
});
