import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ProductPriceHistoryComponent } from './product-price-history.component';
import { ProductService } from '../../../services/product.service';
import { ProductPriceComparison, ProductPriceHistoryEntry } from '../../../models/models';

function entry(overrides: Partial<ProductPriceHistoryEntry>): ProductPriceHistoryEntry {
  return {
    purchaseItemId: 1,
    purchaseId: 10,
    purchaseTitle: 'January order',
    purchaseDate: '2026-01-05T00:00:00Z',
    supplierId: 1,
    supplierName: 'Acme Supplies',
    unitCost: 2,
    ...overrides
  };
}

function comparison(overrides: Partial<ProductPriceComparison> = {}): ProductPriceComparison {
  const lowest = entry({ purchaseItemId: 2, purchaseId: 11, purchaseTitle: 'March order', purchaseDate: '2026-03-05T00:00:00Z', supplierName: 'Best Wholesale', unitCost: 1.5 });
  const latest = entry({ purchaseItemId: 3, purchaseId: 12, purchaseTitle: 'June order', purchaseDate: '2026-06-05T00:00:00Z', unitCost: 1.8 });
  return {
    lowest,
    latest,
    absoluteDifference: 0.3,
    percentageDifference: 20,
    percentageIsMeaningful: true,
    historyNewestFirst: [latest, lowest],
    ...overrides
  };
}

async function render(getPriceComparison: jest.Mock = jest.fn(() => of(comparison()))) {
  await TestBed.configureTestingModule({
    imports: [ProductPriceHistoryComponent],
    providers: [{ provide: ProductService, useValue: { getPriceComparison } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductPriceHistoryComponent);
  fixture.componentRef.setInput('productId', 42);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const dialog = () => host.querySelector<HTMLElement>('[role="dialog"]');
  const button = (label: string) =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim().startsWith(label));
  const requireButton = (label: string): HTMLButtonElement => {
    const found = button(label);
    if (found === undefined) {
      throw new Error(`Expected a button labelled "${label}".`);
    }
    return found;
  };
  const click = (element: HTMLElement) => {
    element.click();
    fixture.detectChanges();
  };
  const openHistory = () => click(requireButton('View price history'));

  return { fixture, host, dialog, button, requireButton, click, openHistory, getPriceComparison };
}

afterEach(() => TestBed.resetTestingModule());

describe('ProductPriceHistoryComponent', () => {
  it('fetches and shows the lowest and latest recorded cost with their supplier and date', async () => {
    const { host } = await render();

    const text = host.textContent ?? '';
    expect(text).toContain('Best Wholesale');
    expect(text).toContain('March order');
    expect(text).toContain('Acme Supplies');
    expect(text).toContain('June order');
  });

  it('shows the absolute and percentage difference between latest and lowest cost', async () => {
    const { host } = await render();

    const diff = host.querySelector('[data-testid="price-history-difference"]')?.textContent ?? '';
    expect(diff).toContain('20');
  });

  it('shows an explicit empty state when the product has no purchase history', async () => {
    const { host } = await render(jest.fn(() => of(comparison({ lowest: null, latest: null, absoluteDifference: null, percentageDifference: null, percentageIsMeaningful: false, historyNewestFirst: [] }))));

    expect(host.querySelector('[data-testid="price-history-empty"]')?.textContent).toContain('No purchases have been recorded');
    expect(host.querySelector('button')).toBeNull();
  });

  it('shows a purchase with no supplier as "None" rather than omitting it', async () => {
    const noSupplier = entry({ purchaseItemId: 5, supplierId: null, supplierName: null, purchaseTitle: 'No supplier recorded' });
    const { host } = await render(jest.fn(() => of(comparison({ lowest: noSupplier, latest: noSupplier, historyNewestFirst: [noSupplier] }))));

    expect(host.textContent).toContain('None');
  });

  it('does not report a misleading percentage when the lowest recorded cost is zero', async () => {
    const zero = entry({ purchaseItemId: 6, unitCost: 0 });
    const { host } = await render(jest.fn(() => of(comparison({
      lowest: zero,
      absoluteDifference: 1.8,
      percentageDifference: null,
      percentageIsMeaningful: false
    }))));

    const diff = host.querySelector('[data-testid="price-history-difference"]')?.textContent ?? '';
    expect(diff).toContain('not meaningful');
  });

  it('reports an upstream failure', async () => {
    const { host } = await render(jest.fn(() => throwError(() => new Error('network error'))));

    expect(host.textContent).toContain('Failed to load price history.');
  });

  it('opens an accessible dialog listing every recorded purchase newest-first', async () => {
    const { openHistory, dialog, host } = await render();

    expect(dialog()).toBeNull();
    openHistory();

    const panel = dialog();
    expect(panel).not.toBeNull();
    expect(panel!.getAttribute('aria-modal')).toBe('true');

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('June order');
    expect(rows[1].textContent).toContain('March order');
  });

  it('closes the dialog with the Close button and with Escape', async () => {
    const { fixture, openHistory, dialog, requireButton, click } = await render();
    openHistory();

    click(requireButton('Close'));
    expect(dialog()).toBeNull();

    openHistory();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(dialog()).toBeNull();
  });

  it('re-fetches when the product id changes', async () => {
    const getPriceComparison = jest.fn(() => of(comparison()));
    const { fixture } = await render(getPriceComparison);

    fixture.componentRef.setInput('productId', 99);
    fixture.detectChanges();

    expect(getPriceComparison).toHaveBeenCalledWith(42);
    expect(getPriceComparison).toHaveBeenCalledWith(99);
  });
});
