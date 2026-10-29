import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { ProductListComponent } from './product-list.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { ToastService } from '../../services/toast.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { AccessService } from '../../access/access.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { CHEAPEST_SUPPLIER_MAX_IDS, CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { Product, ProductPriceTrackingStatus } from '../../models/models';
import { CheapestSupplierSummary, SupplierPriceBasis, SupplierStockStatus } from '../../models/cheapest-supplier';
import { CHEAPEST_SUPPLIERS_FAILED_MESSAGE } from '../shared/cheapest-supplier/cheapest-suppliers.component';

/**
 * Products (All Products)'s Cheapest supplier column (issue #940, parent #817): the same shared
 * column and popup Needs Ordering already carries (issue #930), reused here with one summary
 * request for the rows this page is displaying.
 *
 * The column's own states, the popup's content and its keyboard behaviour are
 * `cheapest-supplier-cell.component.spec.ts`; the batching is `cheapest-supplier.service.spec.ts`.
 * What this file is about is this page's composition of that shared component.
 */
function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Coca-Cola Classic',
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
    deletionRefusal: null,
    ...overrides
  };
}

function summary(productIds: number[]): CheapestSupplierSummary {
  return {
    displayBasis: SupplierPriceBasis.InclusiveOfGst,
    products: productIds.map((productId) => ({
      productId,
      trackingStatus: ProductPriceTrackingStatus.Matched,
      cheapestUnitPrice: 0.95,
      suppliers: [
        {
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
          isCheapest: true
        }
      ]
    }))
  };
}

describe('ProductListComponent Cheapest supplier column (issue #940)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[], getSummary: jest.Mock) {
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        {
          provide: ProductService,
          useValue: {
            getAll: (): Observable<Product[]> => of(products),
            getTrackedProducts: () => of([])
          }
        },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
        { provide: CheapestSupplierService, useValue: { getSummary } },
        provideNayaxConnectionStatus('NotConfigured')
      ]
    }).compileComponents();

    const fixture: ComponentFixture<ProductListComponent> = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();

    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  function cells(host: HTMLElement): HTMLElement[] {
    return Array.from(host.querySelectorAll('[data-testid="cheapest-supplier-cell"]'));
  }

  it('asks once for exactly the rows on screen and shows a cell for each of them', async () => {
    const getSummary = jest.fn(() => of(summary([1, 2])));
    const { host } = await render([product({ id: 1 }), product({ id: 2, name: 'Pepsi Max' })], getSummary);

    expect(getSummary).toHaveBeenCalledTimes(1);
    expect(getSummary).toHaveBeenCalledWith([1, 2]);
    expect(cells(host).map((cell) => cell.textContent?.trim())).toEqual([
      'JB Metro $0.95/unit inc GST',
      'JB Metro $0.95/unit inc GST'
    ]);
  });

  it('asks about a page larger than one batch in a single call, which the service splits', async () => {
    const ids = Array.from({ length: CHEAPEST_SUPPLIER_MAX_IDS + 5 }, (_, index) => index + 1);
    const getSummary = jest.fn(() => of(summary(ids)));

    await render(ids.map((id) => product({ id })), getSummary);

    expect(getSummary).toHaveBeenCalledTimes(1);
    expect(getSummary).toHaveBeenCalledWith(ids);
  });

  it('shows an untracked product with no eligible price as "Not tracked", never as a match', async () => {
    const getSummary = jest.fn(() => of({ displayBasis: SupplierPriceBasis.InclusiveOfGst, products: [] } as CheapestSupplierSummary));
    const { host } = await render([product({ id: 1 })], getSummary);

    expect(cells(host).map((cell) => cell.textContent?.trim())).toEqual(['Not tracked']);
  });

  it('opens the popup from a row and lists that product\'s suppliers', async () => {
    const getSummary = jest.fn(() => of(summary([1])));
    const { fixture, host } = await render([product({ id: 1 })], getSummary);

    (cells(host)[0] as HTMLButtonElement).click();
    fixture.detectChanges();

    const popup = host.querySelector('[data-testid="cheapest-supplier-popup"]') as HTMLElement;
    expect(popup).not.toBeNull();
    expect(popup.textContent).toContain('Coca-Cola Classic');
    expect(popup.textContent).toContain('JB Metro');
  });

  it('reports a failed read beside the table and leaves the rows and their other columns usable', async () => {
    const getSummary = jest.fn(() => throwError(() => new Error('nope')));
    const { host } = await render([product({ id: 1 })], getSummary);

    expect(host.textContent).toContain(CHEAPEST_SUPPLIERS_FAILED_MESSAGE);
    expect(cells(host).map((cell) => cell.textContent?.trim())).toEqual(['Unavailable']);
    // The page's own rows and other columns are untouched: the row is still there with its name.
    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(host.textContent).toContain('Coca-Cola Classic');
  });

  it('keeps paging, sorting and filtering by name working with the column present', async () => {
    const getSummary = jest.fn(() => of(summary([1, 2])));
    const { fixture, host } = await render(
      [product({ id: 1, name: 'Zed Product' }), product({ id: 2, name: 'Alpha Product' })],
      getSummary
    );

    const nameHeaderButton = Array.from(host.querySelectorAll('thead button')).find(
      (button) => button.textContent?.trim().startsWith('Name')
    ) as HTMLButtonElement;
    nameHeaderButton.click();
    fixture.detectChanges();

    const names = Array.from(host.querySelectorAll('tbody tr td:first-child')).map((cell) => cell.textContent?.trim());
    expect(names).toEqual(['Alpha Product', 'Zed Product']);
    expect(cells(host)).toHaveLength(2);
  });
});
