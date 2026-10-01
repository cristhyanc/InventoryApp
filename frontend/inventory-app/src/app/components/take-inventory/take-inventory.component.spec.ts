import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { TakeInventoryComponent } from './take-inventory.component';
import { ProductService } from '../../services/product.service';
import { InventoryCountService } from '../../services/inventory-count.service';
import { ToastService } from '../../services/toast.service';
import { InventoryCountApplyOutcome, InventoryCountApplyResult, Product } from '../../models/models';

function product(overrides: Partial<Product>): Product {
  return {
    id: 1,
    name: 'Coke',
    unitPrice: 2,
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 17,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 2,
    restockTo: 10,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    ...overrides
  } as Product;
}

interface Harness {
  component: TakeInventoryComponent;
  getAllProducts: jest.Mock;
  getProduct: jest.Mock;
  apply: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
}

function createHarness(products: Product[] = [product({ id: 1, name: 'Coke', quantityInStock: 17 })]): Harness {
  const getAllProducts = jest.fn(() => of(products));
  const getProduct = jest.fn((id: number) => of(products.find((p) => p.id === id)!));
  const apply = jest.fn();
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  const productService = { getAll: getAllProducts, get: getProduct } as unknown as ProductService;
  const inventoryCountService = { apply } as unknown as InventoryCountService;

  const component = new TakeInventoryComponent(productService, inventoryCountService, toast as unknown as ToastService);
  component.ngOnInit();

  return { component, getAllProducts, getProduct, apply, toast };
}

function applyResult(overrides: Partial<InventoryCountApplyResult> = {}): InventoryCountApplyResult {
  return {
    outcome: InventoryCountApplyOutcome.Applied,
    quantityChange: 0,
    currentStock: 17,
    stockAdjustmentId: 1,
    ...overrides
  };
}

describe('TakeInventoryComponent loading', () => {
  it('loads products into rows with an empty counted stock and no complete state', () => {
    const { component } = createHarness([
      product({ id: 1, name: 'Coke', quantityInStock: 17 }),
      product({ id: 2, name: 'Chips', quantityInStock: 5 })
    ]);

    expect(component.rows.map((r) => r.product.id)).toEqual([1, 2]);
    expect(component.rows.every((r) => r.countedStock === null)).toBe(true);
    expect(component.rows.every((r) => !r.complete)).toBe(true);
  });
});

describe('TakeInventoryComponent confirm current stock', () => {
  it('marks the row green/complete without calling Apply or mutating stock', () => {
    const { component, apply } = createHarness();
    const row = component.rows[0];

    component.confirmCurrentStock(row);

    expect(row.complete).toBe(true);
    expect(row.countedStock).toBe(17);
    expect(apply).not.toHaveBeenCalled();
  });

  it('clears the complete state when the operator edits Counted Stock afterwards', () => {
    const { component } = createHarness();
    const row = component.rows[0];
    component.confirmCurrentStock(row);

    row.countedStock = 20;
    component.onCountedStockChange(row);

    expect(row.complete).toBe(false);
  });
});

describe('TakeInventoryComponent difference', () => {
  it('is null until a counted stock is entered', () => {
    const { component } = createHarness();
    expect(component.difference(component.rows[0])).toBeNull();
  });

  it('is Counted minus Current', () => {
    const { component } = createHarness();
    const row = component.rows[0];
    row.countedStock = 22;
    expect(component.difference(row)).toBe(5);
  });
});

describe('TakeInventoryComponent apply validation', () => {
  it('refuses to apply when no counted stock has been entered', () => {
    const { component, apply } = createHarness();
    const row = component.rows[0];

    component.apply(row);

    expect(apply).not.toHaveBeenCalled();
    expect(row.error).toBeTruthy();
  });

  it('refuses to apply a negative counted stock', () => {
    const { component, apply } = createHarness();
    const row = component.rows[0];
    row.countedStock = -1;

    component.apply(row);

    expect(apply).not.toHaveBeenCalled();
    expect(row.error).toBeTruthy();
  });
});

describe('TakeInventoryComponent apply - positive difference', () => {
  it('sends the counted and expected current stock, and marks the row complete on success', () => {
    const { component, apply } = createHarness([product({ id: 1, quantityInStock: 17 })]);
    apply.mockReturnValue(of(applyResult({ outcome: InventoryCountApplyOutcome.Applied, quantityChange: 5, currentStock: 22 })));
    const row = component.rows[0];
    row.countedStock = 22;

    component.apply(row);

    expect(apply).toHaveBeenCalledWith(1, { countedStock: 22, expectedCurrentStock: 17 });
    expect(row.complete).toBe(true);
    expect(row.applying).toBe(false);
    expect(row.product.quantityInStock).toBe(22);
    expect(row.error).toBeNull();
  });
});

describe('TakeInventoryComponent apply - negative difference', () => {
  it('applies the Correction and marks the row complete on success', () => {
    const { component, apply } = createHarness([product({ id: 1, quantityInStock: 32 })]);
    apply.mockReturnValue(of(applyResult({ outcome: InventoryCountApplyOutcome.Applied, quantityChange: -4, currentStock: 28 })));
    const row = component.rows[0];
    row.countedStock = 28;

    component.apply(row);

    expect(apply).toHaveBeenCalledWith(1, { countedStock: 28, expectedCurrentStock: 32 });
    expect(row.complete).toBe(true);
    expect(row.product.quantityInStock).toBe(28);
  });
});

describe('TakeInventoryComponent apply - equal counted and current', () => {
  it('still calls Apply and marks the row complete with no error', () => {
    const { component, apply } = createHarness([product({ id: 1, quantityInStock: 46 })]);
    apply.mockReturnValue(of(applyResult({ outcome: InventoryCountApplyOutcome.Confirmed, quantityChange: 0, currentStock: 46 })));
    const row = component.rows[0];
    row.countedStock = 46;

    component.apply(row);

    expect(apply).toHaveBeenCalledWith(1, { countedStock: 46, expectedCurrentStock: 46 });
    expect(row.complete).toBe(true);
    expect(row.error).toBeNull();
  });
});

describe('TakeInventoryComponent apply failure/refresh', () => {
  it('shows the server error and refreshes the row current stock instead of silently keeping stale state', () => {
    const { component, apply, getProduct } = createHarness([product({ id: 1, quantityInStock: 17 })]);
    apply.mockReturnValue(throwError(() => ({ error: { message: "Current stock for 'Coke' has changed to 19. Refresh and recount." } })));
    getProduct.mockReturnValue(of(product({ id: 1, quantityInStock: 19 })));
    const row = component.rows[0];
    row.countedStock = 22;

    component.apply(row);

    expect(row.applying).toBe(false);
    expect(row.complete).toBe(false);
    expect(row.error).toContain('19');
    expect(getProduct).toHaveBeenCalledWith(1);
    expect(row.product.quantityInStock).toBe(19);
  });
});

describe('TakeInventoryComponent current stock control sizing', () => {
  async function render(products: Product[]) {
    const apply = jest.fn();

    await TestBed.configureTestingModule({
      imports: [TakeInventoryComponent],
      providers: [
        {
          provide: ProductService,
          useValue: { getAll: jest.fn(() => of(products)), get: jest.fn((id: number) => of(products.find((p) => p.id === id)!)) }
        },
        { provide: InventoryCountService, useValue: { apply } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(TakeInventoryComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const buttons = () => Array.from(host.querySelectorAll<HTMLButtonElement>('[data-testid="confirm-current-stock"]'));

    return { fixture, host, buttons, apply };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('gives every current stock button a centred 44x44 CSS pixel minimum target, regardless of digit count', async () => {
    const { buttons } = await render([
      product({ id: 1, name: 'Coke', quantityInStock: 8 }),
      product({ id: 2, name: 'Chips', quantityInStock: 12 }),
      product({ id: 3, name: 'Water', quantityInStock: 1234 })
    ]);

    const rendered = buttons();
    expect(rendered.length).toBe(3);

    for (const button of rendered) {
      const classes = Array.from(button.classList);
      expect(classes).toEqual(expect.arrayContaining(['min-h-[44px]', 'min-w-[44px]', 'inline-flex', 'items-center', 'justify-center']));
    }

    const sizingClasses = rendered.map((b) =>
      Array.from(b.classList)
        .filter((c) => /^(min-[hw]-|[pm][xytblr]?-|inline-flex$|items-|justify-|w-|h-)/.test(c))
        .sort()
        .join(' ')
    );
    expect(new Set(sizingClasses).size).toBe(1);
    expect(rendered[2].textContent).toContain('1234');
  });

  it('keeps the confirm behaviour and completed appearance when the enlarged button is clicked', async () => {
    const { fixture, buttons, apply } = await render([product({ id: 1, name: 'Coke', quantityInStock: 17 })]);

    buttons()[0].click();
    fixture.detectChanges();

    const button = buttons()[0];
    expect(apply).not.toHaveBeenCalled();
    expect(fixture.componentInstance.rows[0].complete).toBe(true);
    expect(button.textContent).toContain('\u2713');
    expect(Array.from(button.classList)).toEqual(expect.arrayContaining(['bg-green-100', 'min-h-[44px]', 'min-w-[44px]']));
  });
});
