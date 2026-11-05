import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProductListComponent, PRODUCT_DEFAULT_PAGE_SIZE } from './product-list.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { ToastService } from '../../services/toast.service';
import { Product } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';
import { AccessService } from '../../access/access.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { CheapestSupplierSummary, SupplierPriceBasis } from '../../models/cheapest-supplier';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';

function product(id: number, overrides: Partial<Product> = {}): Product {
  return {
    id,
    name: `Product ${String(id).padStart(2, '0')}`,
    sku: `SKU-${id}`,
    description: null,
    averageUnitCost: 1,
    averageUnitCostForDisplay: 1.5,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: 10,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 15,
    needToOrder: 0,
    mdbCode: null,
    unit: 'ea',
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

function costSummary(productId: number): ProductPurchaseCostSummary {
  return {
    productId,
    lastUnitCost: 2.94,
    lastSupplierName: 'Coles',
    lastPurchaseDate: '2026-10-24T00:00:00',
    lowestUnitCost: 2.2,
    lowestSupplierName: 'Woolworths',
    lowestPurchaseDate: '2026-09-12T00:00:00'
  };
}

/**
 * The Products redesign (issue #1093, with the always-visible purchase history fields of its
 * owner-corrected follow-up #1098): client-side paging over the one list already read, the
 * narrow-screen cards and the toolbar's More actions. Every assertion is on what the operator sees
 * or what the page requests, never on markup for its own sake.
 */
describe('ProductListComponent redesign (issue #1093)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(
    products: Product[],
    options: { financials?: boolean; summaries?: ProductPurchaseCostSummary[]; getAll?: jest.Mock } = {}
  ) {
    const getAll = options.getAll ?? jest.fn(() => of(products));
    const getSummaries = jest.fn(() => of(options.summaries ?? []));
    const getSummary = jest.fn(() => of({ displayBasis: SupplierPriceBasis.InclusiveOfGst, products: [] } as CheapestSupplierSummary));
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll, update: jest.fn(() => of(undefined)), getTrackedProducts: () => of([]) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: { allows: () => of(options.financials ?? true) } },
        provideNayaxConnectionStatus('Ready'),
        { provide: ProductPurchaseCostService, useValue: { getSummaries } },
        { provide: CheapestSupplierService, useValue: { getSummary } }
      ]
    }).compileComponents();

    const fixture: ComponentFixture<ProductListComponent> = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, component: fixture.componentInstance, getAll, getSummaries, getSummary };
  }

  const headers = (host: HTMLElement) => Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim());
  const tableNames = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('tbody tr')).map((row) => row.querySelector('td a')?.textContent?.trim());
  const cardNames = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('[data-testid="product-card"]')).map((card) => card.querySelector('a')?.textContent?.trim());
  const many = (count: number) => Array.from({ length: count }, (_, index) => product(index + 1));

  /**
   * The purchase history fields (issue #1098, an owner correction to #1093): both are always shown
   * by default where the role may see them, so there is no Columns control left to turn either on,
   * and no hidden preference can suppress one.
   */
  describe('purchase history fields', () => {
    it('shows Last and Cheapest purchase by default, alongside the default columns, with no Columns control anywhere', async () => {
      const { host } = await render([product(1)], { summaries: [costSummary(1)] });

      expect(headers(host)).toEqual([
        'Product',
        'Category',
        'Storage stock',
        'Average cost',
        'Last purchase',
        'Cheapest purchase',
        'Cheapest supplier',
        'Actions'
      ]);
      expect(host.querySelector('[data-testid="products-columns"]')).toBeNull();
      expect(host.querySelector('[data-testid="products-columns-panel"]')).toBeNull();
      // Each shows the existing historical price, its supplier and the purchase's own date.
      const last = host.querySelector('tbody [data-testid="product-last-purchase"]')?.textContent?.replace(/\s+/g, ' ').trim();
      expect(last).toBe('$2.94 Coles 24 Oct 2026');
      const cheapest = host.querySelector('tbody [data-testid="product-cheapest-purchase"]')?.textContent?.replace(/\s+/g, ' ').trim();
      expect(cheapest).toBe('$2.20 Woolworths 12 Sep 2026');
    });

    it('shows an em dash, never $0.00, for a product with no purchase history', async () => {
      const { host } = await render([product(1)], { summaries: [] });

      expect(host.querySelector('tbody [data-testid="product-last-purchase"]')?.textContent?.trim()).toBe('—');
    });

    it('offers no Columns control, and no history anywhere, to a role without Dashboard.ViewFinancials', async () => {
      const { host, getSummaries } = await render([product(1)], { financials: false });

      expect(host.querySelector('[data-testid="products-columns"]')).toBeNull();
      expect(host.textContent).not.toContain('Last purchase');
      expect(host.textContent).not.toContain('Cheapest purchase');
      expect(getSummaries).not.toHaveBeenCalled();
      // Average cost is unaffected and always shown.
      expect(headers(host)).toContain('Average cost');
    });
  });

  describe('paging', () => {
    it('shows ten products per page by default and offers exactly 5, 10, 20, 50 and 100', async () => {
      const { host } = await render(many(23));

      expect(PRODUCT_DEFAULT_PAGE_SIZE).toBe(10);
      expect(tableNames(host)).toHaveLength(10);
      const select = host.querySelector('[data-testid="list-pagination-size"]') as HTMLSelectElement;
      expect(Array.from(select.options).map((option) => Number(option.value))).toEqual([5, 10, 20, 50, 100]);
      expect(host.querySelector('[data-testid="list-pagination-range"]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'Showing 1–10 of 23 products'
      );
      expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent?.trim()).toBe('Page 1 of 3');
    });

    it('turns pages without missing or repeating a product, and without requesting anything', async () => {
      const products = many(23);
      const { fixture, host, getAll, getSummaries, getSummary } = await render(products);
      const before = [getAll.mock.calls.length, getSummaries.mock.calls.length, getSummary.mock.calls.length];
      const seen: (string | undefined)[] = [...tableNames(host)];

      for (let page = 1; page < 3; page += 1) {
        (host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).click();
        fixture.detectChanges();
        seen.push(...tableNames(host));
      }

      expect(seen).toEqual(products.map((p) => p.name));
      expect((host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).disabled).toBe(true);
      expect([getAll.mock.calls.length, getSummaries.mock.calls.length, getSummary.mock.calls.length]).toEqual(before);
      // The supplier and cost reads were asked once, for every listed product, not per page.
      expect(getSummary).toHaveBeenCalledWith(products.map((p) => p.id));
    });

    it('returns to page 1 when the page size changes', async () => {
      const { fixture, host, component } = await render(many(23));
      component.onPageIndexChange(2);
      fixture.detectChanges();

      const select = host.querySelector('[data-testid="list-pagination-size"]') as HTMLSelectElement;
      select.value = '5';
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      expect(component.pageIndex).toBe(0);
      expect(tableNames(host)).toEqual(['Product 01', 'Product 02', 'Product 03', 'Product 04', 'Product 05']);
      expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent?.trim()).toBe('Page 1 of 5');
    });

    it('clamps to the last page when a refresh shortens the list', async () => {
      const getAll = jest.fn().mockReturnValueOnce(of(many(23))).mockReturnValueOnce(of(many(12)));
      const { fixture, host, component } = await render([], { getAll });
      component.onPageIndexChange(2);
      fixture.detectChanges();

      component.applyFilters();
      fixture.detectChanges();

      expect(component.pageIndex).toBe(1);
      expect(tableNames(host)).toEqual(['Product 11', 'Product 12']);
    });

    it('returns to page 1 when a filter changes', async () => {
      const { fixture, component } = await render(many(23));
      component.onPageIndexChange(2);

      component.onStatusFilterChange('all');
      fixture.detectChanges();

      expect(component.pageIndex).toBe(0);
    });
  });

  describe('narrow-screen cards', () => {
    it('render the same page of products, in the same order, as the table', async () => {
      const { fixture, host, component } = await render(many(23));

      component.sortBy('name');
      component.sortBy('name');
      component.onPageIndexChange(1);
      fixture.detectChanges();

      expect(cardNames(host)).toEqual(tableNames(host));
      expect(cardNames(host)).toHaveLength(10);
    });

    it('shows storage stock, average cost, supplier, the purchase history fields, Adjust stock and the row menu', async () => {
      const { host } = await render([product(1, { quantityInStock: 2, isLowStock: true })], { summaries: [costSummary(1)] });
      const card = () => host.querySelector('[data-testid="product-card"]') as HTMLElement;

      expect(card().textContent).toContain('SKU: SKU-1');
      expect(card().querySelector('.badge')?.classList).toContain('badge-danger');
      expect(card().textContent).toContain('$1.50');
      expect(card().querySelector('[data-testid="cheapest-supplier-summary"]')).not.toBeNull();
      expect(card().querySelector('a[href="/products/1/stock"]')?.textContent?.trim()).toBe('Adjust stock');
      expect(card().querySelector('[data-testid="product-card-menu"]')).not.toBeNull();
      expect(card().textContent).toContain('Last purchase:');
      expect(card().textContent).toContain('$2.94');
    });

    it('shows no purchase history field to a role without Dashboard.ViewFinancials', async () => {
      const { host } = await render([product(1)], { financials: false, summaries: [costSummary(1)] });
      const card = () => host.querySelector('[data-testid="product-card"]') as HTMLElement;

      expect(card().textContent).not.toContain('Last purchase');
      expect(card().textContent).not.toContain('Cheapest purchase');
    });
  });

  describe('More actions', () => {
    it('contains only Import products and Set GST rule in bulk', async () => {
      const { fixture, host } = await render([product(1)]);

      (host.querySelector('[data-testid="products-more-actions"]') as HTMLButtonElement).click();
      fixture.detectChanges();

      const menu = host.querySelector('[data-testid="products-more-actions-panel"]') as HTMLElement;
      expect(Array.from(menu.querySelectorAll('[role="menuitem"]')).map((item) => item.textContent?.trim())).toEqual([
        'Import products',
        'Set GST rule in bulk'
      ]);
    });
  });
});
