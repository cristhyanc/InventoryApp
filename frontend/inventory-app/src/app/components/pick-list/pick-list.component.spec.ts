import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { PickListComponent } from './pick-list.component';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import { Machine, PickListResult, Product } from '../../models/models';

function machine(overrides: Partial<Machine>): Machine {
  return {
    machineID: 1,
    machineName: null,
    machineNumber: null,
    todayGrossRevenue: 0,
    currentWeekGrossRevenue: 0,
    lastWeekGrossRevenue: 0,
    twoWeeksAgoGrossRevenue: 0,
    todayDirectProfit: null,
    twoWeeksAgoDirectProfit: null,
    lastWeekDirectProfit: null,
    currentWeekDirectProfit: null,
    previousComparableWeekGrossRevenue: 0,
    previousComparableWeekDirectProfit: null,
    monthToDateGrossRevenue: 0,
    monthToDateDirectProfit: null,
    profitabilityStatus: null,
    ...overrides
  };
}

function product(overrides: Partial<Product>): Product {
  return {
    id: 1,
    name: 'Product',
    unitPrice: 2,
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 10,
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
    isLowStock: false,
    isReorderAlert: false,
    ...overrides
  };
}

function pickListResult(overrides: Partial<PickListResult> = {}): PickListResult {
  return {
    products: [
      {
        productId: 1,
        productName: 'Coke',
        storageQuantityInStock: 50,
        totalQuantityToPick: 4,
        storageShortageQuantity: 0,
        machineQuantities: [
          { machineId: 10, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 },
          { machineId: 20, currentQuantity: 10, targetQuantity: 10, quantityToPick: 0 }
        ]
      },
      {
        productId: 2,
        productName: 'Chips',
        storageQuantityInStock: 2,
        totalQuantityToPick: 5,
        storageShortageQuantity: 3,
        machineQuantities: [
          { machineId: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 }
        ]
      }
    ],
    ...overrides
  };
}

interface Harness {
  component: PickListComponent;
  getAllMachines: jest.Mock;
  getAllProducts: jest.Mock;
  getPickList: jest.Mock;
  mutationSpies: jest.Mock[];
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
}

function createHarness(
  machines: Machine[] = [machine({ machineID: 10, machineName: 'Machine A' }), machine({ machineID: 20, machineName: 'Machine B' })],
  products: Product[] = [product({ id: 1, name: 'Coke' }), product({ id: 2, name: 'Chips' })],
  pickList: jest.Mock = jest.fn(() => of(pickListResult()))
): Harness {
  const getAllMachines = jest.fn(() => of(machines));
  const getAllProducts = jest.fn(() => of(products));
  const machineUpdate = jest.fn();
  const machineApplySyncRestock = jest.fn();
  const productUpdate = jest.fn();
  const productDelete = jest.fn();

  const machineService = {
    getAll: getAllMachines,
    applySyncRestock: machineApplySyncRestock,
    syncRestock: machineUpdate
  } as unknown as MachineService;
  const productService = {
    getAll: getAllProducts,
    update: productUpdate,
    delete: productDelete
  } as unknown as ProductService;
  const pickListService = { get: pickList } as unknown as PickListService;
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  const component = new PickListComponent(machineService, productService, pickListService, toast as unknown as ToastService);
  component.ngOnInit();

  return {
    component,
    getAllMachines,
    getAllProducts,
    getPickList: pickList,
    mutationSpies: [machineUpdate, machineApplySyncRestock, productUpdate, productDelete],
    toast
  };
}

describe('PickListComponent loading', () => {
  it('loads the machine and product catalogues on init', () => {
    const { component, getAllMachines, getAllProducts } = createHarness();

    expect(getAllMachines).toHaveBeenCalledTimes(1);
    expect(getAllProducts).toHaveBeenCalledTimes(1);
    expect(component.allMachines.map((m) => m.machineID)).toEqual([10, 20]);
    expect(component.allProducts.map((p) => p.id)).toEqual([1, 2]);
  });

  it('shows no pick list data until a machine is selected', () => {
    const { component, getPickList } = createHarness();

    expect(component.selectedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
    expect(getPickList).not.toHaveBeenCalled();
  });
});

describe('PickListComponent add/remove machines', () => {
  it('adding a machine fetches the pick list for it and updates totals', () => {
    const { component, getPickList } = createHarness();

    component.machineToAdd = 10;
    component.addMachine();

    expect(getPickList).toHaveBeenCalledWith([10]);
    expect(component.selectedMachineIds).toEqual([10]);
    expect(component.totalToPickUnits()).toBe(9);
  });

  it('adding a second machine refetches with both ids', () => {
    const { component, getPickList } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    getPickList.mockClear();

    component.machineToAdd = 20;
    component.addMachine();

    expect(getPickList).toHaveBeenCalledWith([10, 20]);
  });

  it('does not add a machine twice', () => {
    const { component, getPickList } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    getPickList.mockClear();

    component.machineToAdd = 10;
    component.addMachine();

    expect(component.selectedMachineIds).toEqual([10]);
    expect(getPickList).not.toHaveBeenCalled();
  });

  it('removing a machine drops it from selection and refetches with the remaining ids', () => {
    const afterRemoval = of(pickListResult({
      products: [{
        productId: 2, productName: 'Chips', storageQuantityInStock: 2, totalQuantityToPick: 5, storageShortageQuantity: 3,
        machineQuantities: [{ machineId: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 }]
      }]
    }));
    const getPickList = jest.fn(() => of(pickListResult()));
    const { component } = createHarness(undefined, undefined, getPickList);
    component.machineToAdd = 10;
    component.addMachine();
    component.machineToAdd = 20;
    component.addMachine();
    getPickList.mockClear();
    getPickList.mockReturnValueOnce(afterRemoval);

    component.removeMachine(20);

    expect(getPickList).toHaveBeenCalledWith([10]);
    expect(component.selectedMachineIds).toEqual([10]);
  });

  it('removing the last machine clears the pick list without calling the backend again', () => {
    const { component, getPickList } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    getPickList.mockClear();

    component.removeMachine(10);

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.selectedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
  });
});

describe('PickListComponent product filter and totals', () => {
  it('an empty product filter shows every product returned for the selected machines', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1, 2]);
    expect(component.totalToPickUnits()).toBe(9);
  });

  it('selecting products restricts the matrix rows and the totals to that selection', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();

    component.selectedProductIds = [2];

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
    expect(component.totalToPickUnits()).toBe(5);
  });
});

describe('PickListComponent picked/unpicked tracking', () => {
  it('toggles a positive-pick cell between picked and unpicked', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    const cokeRow = component.pickListProducts[0];

    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(true);

    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(false);
  });

  it('never toggles a zero-pick cell', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    component.machineToAdd = 20;
    component.addMachine();
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;

    component.togglePicked(cokeRow, 20);

    expect(component.isPicked(1, 20)).toBe(false);
  });

  it('excludes zero-pick cells from the actionable-cell count', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    component.machineToAdd = 20;
    component.addMachine();

    // Coke/machine 20 is a zero-pick cell and Chips has no mapping at all for machine 20, so only
    // Coke/machine 10 (4 to pick) and Chips/machine 10 (5 to pick) are actionable.
    expect(component.actionableCellCount()).toBe(2);
  });

  it('reports picked progress against only the actionable cells', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;

    component.togglePicked(cokeRow, 10);

    expect(component.pickedActionableCellCount()).toBe(1);
    expect(component.actionableCellCount()).toBe(2);
    expect(component.progressPercent()).toBe(50);
  });

  it('drops a stale picked mark once its machine is removed from the selection', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(true);

    component.removeMachine(10);
    component.machineToAdd = 10;
    component.addMachine();

    expect(component.isPicked(1, 10)).toBe(false);
  });

  it('drops a picked mark that is no longer actionable after a refreshed snapshot', () => {
    const getPickList = jest.fn()
      .mockReturnValueOnce(of(pickListResult()))
      .mockReturnValueOnce(of(pickListResult({
        products: [{
          productId: 1, productName: 'Coke', storageQuantityInStock: 50, totalQuantityToPick: 0, storageShortageQuantity: 0,
          machineQuantities: [{ machineId: 10, currentQuantity: 10, targetQuantity: 10, quantityToPick: 0 }]
        }]
      })));
    const { component } = createHarness(undefined, undefined, getPickList);
    component.machineToAdd = 10;
    component.addMachine();
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(true);

    component.refresh();

    expect(component.isPicked(1, 10)).toBe(false);
  });
});

describe('PickListComponent reset', () => {
  it('clears machine selection, product filter, and picked state', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    component.selectedProductIds = [1];

    component.resetAll();

    expect(component.selectedMachineIds).toEqual([]);
    expect(component.selectedProductIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
    expect(component.isPicked(1, 10)).toBe(false);
  });
});

describe('PickListComponent storage shortages', () => {
  it('identifies only the products whose combined pick exceeds physical storage', () => {
    const { component } = createHarness();
    component.machineToAdd = 10;
    component.addMachine();

    expect(component.shortageProducts().map((p) => p.productId)).toEqual([2]);
  });
});

describe('PickListComponent never mutates data', () => {
  it('calls no mutation-style method on any injected service across a full interaction', () => {
    const { component, mutationSpies } = createHarness();

    component.machineToAdd = 10;
    component.addMachine();
    component.machineToAdd = 20;
    component.addMachine();
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    component.togglePicked(cokeRow, 10);
    component.selectedProductIds = [1];
    component.removeMachine(20);
    component.refresh();
    component.resetAll();

    for (const spy of mutationSpies) {
      expect(spy).not.toHaveBeenCalled();
    }
  });
});

describe('PickListComponent rendered structure', () => {
  async function render(
    machines: Machine[] = [machine({ machineID: 10, machineName: 'Machine A' }), machine({ machineID: 20, machineName: 'Machine B' })],
    products: Product[] = [product({ id: 1, name: 'Coke' }), product({ id: 2, name: 'Chips' })],
    pickList: jest.Mock = jest.fn(() => of(pickListResult()))
  ) {
    await TestBed.configureTestingModule({
      imports: [PickListComponent],
      providers: [
        { provide: MachineService, useValue: { getAll: jest.fn(() => of(machines)) } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of(products)) } },
        { provide: PickListService, useValue: { get: pickList } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(PickListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const requireElement = <T extends HTMLElement = HTMLElement>(selector: string): T => {
      const element = host.querySelector<T>(selector);
      if (element === null) {
        throw new Error(`Expected the rendered component to contain an element matching "${selector}", but it did not.`);
      }
      return element;
    };
    const button = (label: string) =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim().startsWith(label));
    const requireButton = (label: string): HTMLButtonElement => {
      const found = button(label);
      if (found === undefined) {
        const rendered = Array.from(host.querySelectorAll('button')).map((b) => `"${b.textContent?.trim()}"`).join(', ');
        throw new Error(`Expected a button labelled "${label}", but the rendered buttons were: ${rendered || '(none)'}.`);
      }
      return found;
    };
    const click = (element: HTMLElement) => {
      element.click();
      fixture.detectChanges();
    };
    const addMachine = (machineId: number) => {
      fixture.componentInstance.machineToAdd = machineId;
      fixture.detectChanges();
      click(requireButton('+ Add Machines'));
    };

    return { fixture, host, requireElement, button, requireButton, click, addMachine };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('shows the header, filter panel, selected-machines panel and an empty-selection message', async () => {
    const { host } = await render();

    expect(host.querySelector('h1')?.textContent).toContain('Pick List (Restock Planning)');
    expect(host.textContent).toContain('read-only');
    expect(host.textContent).toContain('Snapshot');
    expect(host.textContent).toContain('Selected Machines (0)');
    expect(host.textContent).toContain('Select at least one machine');
    expect(host.querySelector('table')).toBeNull();
  });

  it('renders products as rows and selected machines as columns, with Product and Total to Pick first', async () => {
    const { host, addMachine } = await render();

    addMachine(10);
    addMachine(20);

    const headers = Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    expect(headers[0]).toBe('Product');
    expect(headers[1]).toBe('Total to Pick');
    expect(headers).toContain('Machine A');
    expect(headers).toContain('Machine B');

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Coke');
    expect(rows[0].textContent).toContain('Pick: 4');
    expect(rows[0].textContent).toContain('Current: 6 of 10');
  });

  it('shows a removable chip per selected machine and removes it on click', async () => {
    const { host, addMachine, requireElement, click } = await render();
    addMachine(10);

    expect(host.textContent).toContain('Selected Machines (1)');
    const chipRemove = requireElement<HTMLButtonElement>('[aria-label="Remove Machine A"]');

    click(chipRemove);

    expect(host.textContent).toContain('Selected Machines (0)');
    expect(host.textContent).toContain('No machines selected yet.');
  });

  it('toggles a positive-pick cell to the picked visual state and back on click', async () => {
    const { host, addMachine, click } = await render();
    addMachine(10);

    const pickButtons = Array.from(host.querySelectorAll<HTMLButtonElement>('tbody button'));
    const cokeCell = pickButtons.find((b) => b.textContent?.includes('Pick: 4'));
    if (cokeCell === undefined) {
      throw new Error('Expected a positive-pick cell for Coke.');
    }
    expect(cokeCell.className).toContain('bg-white');

    click(cokeCell);

    expect(cokeCell.className).toContain('bg-green-100');
    expect(cokeCell.textContent).toContain('✓');

    click(cokeCell);

    expect(cokeCell.className).toContain('bg-white');
  });

  it('never renders a button for a zero-pick cell', async () => {
    const { host, addMachine } = await render();
    addMachine(10);
    addMachine(20);

    const cells = Array.from(host.querySelectorAll('tbody td'));
    const zeroCell = cells.find((td) => td.textContent?.includes('Pick: 0'));
    if (zeroCell === undefined) {
      throw new Error('Expected a zero-pick cell to be rendered.');
    }
    expect(zeroCell.querySelector('button')).toBeNull();
  });

  it('shows a visible shortage badge for a product whose pick exceeds physical storage', async () => {
    const { host, addMachine } = await render();
    addMachine(10);

    const badge = host.querySelector('[data-testid="storage-shortage-badge"]');
    expect(badge).not.toBeNull();
    expect(badge?.closest('tr')?.textContent).toContain('Chips');
  });

  it('shows the bottom summary with total units and actionable-cell progress', async () => {
    const { host, addMachine } = await render();
    addMachine(10);

    expect(host.querySelector('[data-testid="pick-list-total-units"]')?.textContent).toContain('9');
    expect(host.querySelector('[data-testid="pick-list-progress-count"]')?.textContent).toContain('0/2');
    expect(host.querySelector('[data-testid="pick-list-progress-percent"]')?.textContent).toContain('0%');
  });

  it('renders no product or machine images anywhere on the page', async () => {
    const { host, addMachine } = await render();
    addMachine(10);
    addMachine(20);

    expect(host.querySelectorAll('img').length).toBe(0);
  });
});
