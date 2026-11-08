import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { ProductListComponent } from './product-list.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { ToastService } from '../../services/toast.service';
import { Product, TrackedProduct } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';
import { AccessService } from '../../access/access.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { CheapestSupplierSummary, SupplierPriceBasis } from '../../models/cheapest-supplier';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { ProductFilters } from '../../services/product.service';
import { SEARCH_DEBOUNCE_MS } from '../shared/filter-request-trigger';

/**
 * A search typed while the previous one is still on its way (a slow server, a phone) replaces it:
 * `switchMap` unsubscribes the older request, which then never answers. Before this fix the page
 * kept counting that request as outstanding, so it showed neither the table nor an empty state -
 * a blank card that every later search and Clear filters left blank too, until a reload.
 */
describe('ProductListComponent superseded search requests', () => {
  afterEach(() => TestBed.resetTestingModule());

  function product(id: number, name: string): Product {
    return {
      id,
      name,
      sku: null,
      description: null,
      averageUnitCost: 1,
      costingQuantity: null,
      inventoryValue: null,
      machinePrice: null,
      commissionValue: null,
      suggestedNetValue: null,
      suggestedPriceValue: null,
      quantityInStock: 10,
      maxStockInMachine: 20,
      machineReplenishmentNeed: 0,
      onOrderQuantity: 0,
      lowStockThreshold: 5,
      restockTo: 15,
      needToOrder: 0,
      mdbCode: null,
      unit: 'ea',
      lastEatBefore1: null,
      lastEatBefore2: null,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      categoryId: null,
      category: null,
      isActive: true,
      isLowStock: false,
      isReorderAlert: false,
      canDelete: true,
      deletionRefusal: null
    };
  }

  /** Every `getAll` call gets its own answer the test sends (or never sends) itself. */
  async function render() {
    const requests: { filters: ProductFilters; answer: Subject<Product[]> }[] = [];
    const getAll = jest.fn((filters: ProductFilters) => {
      const answer = new Subject<Product[]>();
      requests.push({ filters, answer });
      return answer;
    });

    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll, getTrackedProducts: () => of([] as TrackedProduct[]) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        provideNayaxConnectionStatus('NotConfigured'),
        { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
        {
          provide: CheapestSupplierService,
          useValue: {
            getSummary: () => of({ displayBasis: SupplierPriceBasis.InclusiveOfGst, products: [] } as CheapestSupplierSummary)
          }
        }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, requests };
  }

  function answer(request: { answer: Subject<Product[]> }, products: Product[]): void {
    request.answer.next(products);
    request.answer.complete();
  }

  const waitForDebounce = () => new Promise((resolve) => setTimeout(resolve, SEARCH_DEBOUNCE_MS + 50));

  it('shows the newest search once a search typed after a pause replaces one still in flight', async () => {
    const { fixture, host, requests } = await render();
    answer(requests[0], [product(1, 'Boss Iced Double Espresso'), product(2, 'Cherry Bar')]);
    fixture.detectChanges();

    // "bo", a pause long enough for the debounce, then "ss" before the first answer is back.
    fixture.componentInstance.onSearchChange('bo');
    await waitForDebounce();
    expect(requests).toHaveLength(2);
    fixture.componentInstance.onSearchChange('boss');
    await waitForDebounce();
    expect(requests).toHaveLength(3);

    answer(requests[2], [product(1, 'Boss Iced Double Espresso')]);
    fixture.detectChanges();

    expect(fixture.componentInstance.loadState.loaded).toBe(true);
    expect(host.querySelector('[data-testid="products-table"]')).not.toBeNull();
    expect(host.textContent).toContain('Boss Iced Double Espresso');
    expect(host.textContent).not.toContain('Cherry Bar');
  });

  it('brings every product back on Clear filters after a replaced search', async () => {
    const { fixture, host, requests } = await render();
    answer(requests[0], [product(1, 'Boss Iced Double Espresso'), product(2, 'Cherry Bar')]);
    fixture.detectChanges();

    fixture.componentInstance.onSearchChange('bo');
    await waitForDebounce();
    fixture.componentInstance.resetFilters();
    expect(requests).toHaveLength(3);

    answer(requests[2], [product(1, 'Boss Iced Double Espresso'), product(2, 'Cherry Bar')]);
    fixture.detectChanges();

    expect(fixture.componentInstance.loadState.loaded).toBe(true);
    expect(host.textContent).toContain('Boss Iced Double Espresso');
    expect(host.textContent).toContain('Cherry Bar');
  });

  /**
   * A phone keyboard leaves a space after a word picked from its suggestions; searching for that
   * space as well would miss a product whose name ends in the word.
   */
  it('searches for the text without the spaces around it', async () => {
    const { requests, fixture } = await render();
    answer(requests[0], []);

    fixture.componentInstance.onSearchChange('  Boss ');
    await waitForDebounce();

    expect(requests[1].filters.search).toBe('Boss');
  });

  it('asks for the whole list when the search holds only spaces', async () => {
    const { requests, fixture } = await render();
    answer(requests[0], []);

    fixture.componentInstance.onSearchChange('   ');
    await waitForDebounce();

    expect(requests[1].filters.search).toBeUndefined();
  });
});
