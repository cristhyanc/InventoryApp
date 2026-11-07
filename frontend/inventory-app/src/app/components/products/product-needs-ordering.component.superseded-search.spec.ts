import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { ProductNeedsOrderingComponent } from './product-needs-ordering.component';
import { SEARCH_DEBOUNCE_MS } from '../shared/filter-request-trigger';
import { AccessService } from '../../access/access.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { ProductFilters, ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { ToastService } from '../../services/toast.service';
import { Product, ReorderAlertListing } from '../../models/models';
import { CheapestSupplierSummary, SupplierPriceBasis } from '../../models/cheapest-supplier';

/**
 * The Needs ordering tab replaces a search still on its way exactly as All products does (see
 * `product-list.component.superseded-search.spec.ts`): the replaced request never answers, and the
 * page must not keep waiting for it.
 */
describe('ProductNeedsOrderingComponent superseded search requests', () => {
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
      quantityInStock: 1,
      maxStockInMachine: 20,
      machineReplenishmentNeed: 0,
      onOrderQuantity: 0,
      lowStockThreshold: 5,
      restockTo: 15,
      needToOrder: 3,
      mdbCode: null,
      unit: 'ea',
      lastEatBefore1: null,
      lastEatBefore2: null,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      categoryId: null,
      category: null,
      isActive: true,
      isLowStock: true,
      isReorderAlert: true
    };
  }

  async function render() {
    const requests: { filters: ProductFilters; answer: Subject<ReorderAlertListing> }[] = [];
    const getLowStock = jest.fn((filters: ProductFilters) => {
      const answer = new Subject<ReorderAlertListing>();
      requests.push({ filters, answer });
      return answer;
    });

    await TestBed.configureTestingModule({
      imports: [ProductNeedsOrderingComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getLowStock, getAll: () => of([]), update: jest.fn() } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: SupplierService, useValue: { getAll: () => of([]) } },
        { provide: SupplierOrderService, useValue: { create: jest.fn() } },
        { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
        {
          provide: CheapestSupplierService,
          useValue: {
            getSummary: () => of({ displayBasis: SupplierPriceBasis.InclusiveOfGst, products: [] } as CheapestSupplierSummary)
          }
        },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: { allows: () => of(true) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductNeedsOrderingComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, requests };
  }

  function answer(request: { answer: Subject<ReorderAlertListing> }, products: Product[]): void {
    request.answer.next({ products, mode: 'MachineAware', machineNeedUnavailableReason: null });
    request.answer.complete();
  }

  const waitForDebounce = () => new Promise((resolve) => setTimeout(resolve, SEARCH_DEBOUNCE_MS + 50));

  it('shows the newest search once a search typed after a pause replaces one still in flight', async () => {
    const { fixture, host, requests } = await render();
    answer(requests[0], [product(1, 'Boss Iced Double Espresso'), product(2, 'Cherry Bar')]);
    fixture.detectChanges();

    fixture.componentInstance.onSearchChange('bo');
    await waitForDebounce();
    fixture.componentInstance.onSearchChange('boss');
    await waitForDebounce();
    expect(requests).toHaveLength(3);

    answer(requests[2], [product(1, 'Boss Iced Double Espresso')]);
    fixture.detectChanges();

    expect(fixture.componentInstance.loadState.loaded).toBe(true);
    expect(host.textContent).toContain('Boss Iced Double Espresso');
    expect(host.textContent).not.toContain('Cherry Bar');
  });

  it('searches for the text without the spaces around it', async () => {
    const { fixture, requests } = await render();
    answer(requests[0], []);

    fixture.componentInstance.onSearchChange(' Boss ');
    await waitForDebounce();

    expect(requests[1].filters.search).toBe('Boss');
  });
});
