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

/** Stages a machine selection and applies it - the normal "pick machines, then Apply" flow. */
function applyMachines(component: PickListComponent, machineIds: number[]): void {
  component.stagedMachineIds = machineIds;
  component.applyFilters();
}

describe('PickListComponent loading', () => {
  it('loads the machine and product catalogues on init and defaults the product filter to every product', () => {
    const { component, getAllMachines, getAllProducts } = createHarness();

    expect(getAllMachines).toHaveBeenCalledTimes(1);
    expect(getAllProducts).toHaveBeenCalledTimes(1);
    expect(component.allMachines.map((m) => m.machineID)).toEqual([10, 20]);
    expect(component.allProducts.map((p) => p.id)).toEqual([1, 2]);
    expect(component.stagedProductIds).toEqual([1, 2]);
    expect(component.appliedProductIds).toEqual([1, 2]);
  });

  it('shows no pick list data until a machine selection is applied', () => {
    const { component, getPickList } = createHarness();

    expect(component.appliedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
    expect(getPickList).not.toHaveBeenCalled();
  });
});

describe('PickListComponent staged vs applied filters', () => {
  it('staging a machine selection does not fetch or change the applied selection until Apply', () => {
    const { component, getPickList } = createHarness();

    component.stagedMachineIds = [10];

    expect(component.appliedMachineIds).toEqual([]);
    expect(getPickList).not.toHaveBeenCalled();
  });

  it('staging a product selection does not change the applied selection or displayed rows until Apply', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    component.stagedProductIds = [2];

    expect(component.appliedProductIds).toEqual([1, 2]);
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1, 2]);
  });

  it('Apply applies both staged Products and Machines selections together', () => {
    const { component, getPickList } = createHarness();
    component.stagedMachineIds = [10];
    component.stagedProductIds = [1];

    component.applyFilters();

    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.appliedProductIds).toEqual([1]);
    expect(getPickList).toHaveBeenCalledWith([10]);
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1]);
  });

  it('Apply with a changed machine selection performs a fresh Pick List fetch for the newly applied ids', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();

    applyMachines(component, [10, 20]);

    expect(getPickList).toHaveBeenCalledTimes(1);
    expect(getPickList).toHaveBeenCalledWith([10, 20]);
    expect(component.appliedMachineIds).toEqual([10, 20]);
  });

  it('Apply with only a product-filter change does not refetch the backend projection', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();
    component.stagedProductIds = [2];

    component.applyFilters();

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.appliedProductIds).toEqual([2]);
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
    expect(component.totalToPickUnits()).toBe(5);
  });

  it('Apply with an unchanged machine selection (re-applying the same ids) does not refetch', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    getPickList.mockClear();

    applyMachines(component, [20, 10]);

    expect(getPickList).not.toHaveBeenCalled();
  });

  it('Applying an empty staged machine selection clears the matrix without calling the backend', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();

    applyMachines(component, []);

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
  });
});

describe('PickListComponent Selected Machines chip removal', () => {
  it('removes the machine from both the applied and staged selections and refetches with the remaining ids', () => {
    const afterRemoval = of(pickListResult({
      products: [{
        productId: 2, productName: 'Chips', storageQuantityInStock: 2, totalQuantityToPick: 5, storageShortageQuantity: 3,
        machineQuantities: [{ machineId: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 }]
      }]
    }));
    const getPickList = jest.fn(() => of(pickListResult()));
    const { component } = createHarness(undefined, undefined, getPickList);
    applyMachines(component, [10, 20]);
    getPickList.mockClear();
    getPickList.mockReturnValueOnce(afterRemoval);

    component.removeMachine(20);

    expect(getPickList).toHaveBeenCalledWith([10]);
    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.stagedMachineIds).toEqual([10]);
  });

  it('removing the last machine clears the pick list without calling the backend again', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();

    component.removeMachine(10);

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([]);
    expect(component.stagedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
  });
});

describe('PickListComponent product filter and totals', () => {
  it('the default (all-products) filter shows every product returned for the applied machines', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1, 2]);
    expect(component.totalToPickUnits()).toBe(9);
  });

  it('applying a restricted product selection limits the matrix rows and totals to that selection', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.stagedProductIds = [2];

    component.applyFilters();

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
    expect(component.totalToPickUnits()).toBe(5);
  });
});

describe('PickListComponent picked/unpicked tracking', () => {
  it('toggles a positive-pick cell between picked and unpicked', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    const cokeRow = component.pickListProducts[0];

    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(true);

    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(false);
  });

  it('never toggles a zero-pick cell', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;

    component.togglePicked(cokeRow, 20);

    expect(component.isPicked(1, 20)).toBe(false);
  });

  it('excludes zero-pick cells from the actionable-cell count', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    // Coke/machine 20 is a zero-pick cell and Chips has no mapping at all for machine 20, so only
    // Coke/machine 10 (4 to pick) and Chips/machine 10 (5 to pick) are actionable.
    expect(component.actionableCellCount()).toBe(2);
  });

  it('reports picked progress against only the actionable cells', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;

    component.togglePicked(cokeRow, 10);

    expect(component.pickedActionableCellCount()).toBe(1);
    expect(component.actionableCellCount()).toBe(2);
    expect(component.progressPercent()).toBe(50);
  });

  it('does not disturb picked state for a staged-but-not-applied filter edit', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);

    component.stagedProductIds = [2];
    component.stagedMachineIds = [20];

    expect(component.isPicked(1, 10)).toBe(true);
  });

  it('drops a stale picked mark once its machine is removed from the applied selection', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(true);

    component.removeMachine(10);
    applyMachines(component, [10]);

    expect(component.isPicked(1, 10)).toBe(false);
  });

  it('drops a picked mark that is no longer actionable after Apply refreshes the projection', () => {
    const getPickList = jest.fn()
      .mockReturnValueOnce(of(pickListResult()))
      .mockReturnValueOnce(of(pickListResult({
        products: [{
          productId: 1, productName: 'Coke', storageQuantityInStock: 50, totalQuantityToPick: 0, storageShortageQuantity: 0,
          machineQuantities: [{ machineId: 10, currentQuantity: 10, targetQuantity: 10, quantityToPick: 0 }]
        }]
      })));
    const { component } = createHarness(undefined, undefined, getPickList);
    applyMachines(component, [10]);
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    expect(component.isPicked(1, 10)).toBe(true);

    applyMachines(component, [10, 20]);

    expect(component.isPicked(1, 10)).toBe(false);
  });
});

describe('PickListComponent reset', () => {
  it('clears staged and applied machine/product selections, matrix data, picked state, and snapshot', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    component.stagedProductIds = [1];
    component.applyFilters();

    component.resetAll();

    expect(component.stagedMachineIds).toEqual([]);
    expect(component.appliedMachineIds).toEqual([]);
    expect(component.stagedProductIds).toEqual([1, 2]);
    expect(component.appliedProductIds).toEqual([1, 2]);
    expect(component.pickListProducts).toEqual([]);
    expect(component.lastRefreshedAt).toBeNull();
    expect(component.isPicked(1, 10)).toBe(false);
  });
});

describe('PickListComponent storage shortages', () => {
  it('identifies only the products whose combined pick exceeds physical storage', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    expect(component.shortageProducts().map((p) => p.productId)).toEqual([2]);
  });
});

describe('PickListComponent never mutates data', () => {
  it('calls no mutation-style method on any injected service across a full interaction', () => {
    const { component, mutationSpies } = createHarness();

    applyMachines(component, [10]);
    applyMachines(component, [10, 20]);
    const cokeRow = component.pickListProducts.find((p) => p.productId === 1)!;
    component.togglePicked(cokeRow, 10);
    component.togglePicked(cokeRow, 10);
    component.stagedProductIds = [1];
    component.applyFilters();
    component.removeMachine(20);
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
    const dropdownTrigger = (labelPrefix: string): HTMLButtonElement => {
      const found = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) =>
        b.getAttribute('aria-label')?.startsWith(`${labelPrefix}:`)
      );
      if (found === undefined) {
        throw new Error(`Expected a dropdown trigger button labelled "${labelPrefix}".`);
      }
      return found;
    };
    const openDropdown = (labelPrefix: string): void => click(dropdownTrigger(labelPrefix));
    const checkOption = (optionLabel: string): void => {
      const labels = Array.from(host.querySelectorAll('[role="group"] label'));
      const target = labels.find((l) => l.textContent?.trim() === optionLabel);
      if (target === undefined) {
        throw new Error(`Expected an open dropdown option labelled "${optionLabel}".`);
      }
      click(target.querySelector('input')!);
    };
    const applyFilters = (): void => click(requireButton('Apply'));

    return { fixture, host, requireElement, button, requireButton, click, dropdownTrigger, openDropdown, checkOption, applyFilters };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('shows the header, top filter panel with Products/Machines dropdowns, and an empty-selection message', async () => {
    const { host, dropdownTrigger } = await render();

    expect(host.querySelector('h1')?.textContent).toContain('Pick List (Restock Planning)');
    expect(host.textContent).toContain('read-only');
    expect(host.textContent).toContain('Snapshot');
    expect(dropdownTrigger('Products').textContent).toContain('All products');
    expect(dropdownTrigger('Machines').textContent).toContain('No machines selected');
    expect(host.textContent).toContain('Selected Machines (0)');
    expect(host.textContent).toContain('Select at least one machine');
    expect(host.querySelector('table')).toBeNull();
  });

  it('renders the snapshot "As of" time as Australia/Canberra local time during AEST (UTC+10), not raw UTC (issue #232)', async () => {
    jest.useFakeTimers({ now: new Date('2026-06-15T00:00:00Z') });
    try {
      const { host, openDropdown, checkOption, applyFilters } = await render();
      openDropdown('Machines');
      checkOption('Machine A');
      applyFilters();

      expect(host.querySelector('[data-testid="pick-list-snapshot"]')?.textContent).toContain('As of 15/06/2026, 10:00 am');
    } finally {
      jest.useRealTimers();
    }
  });

  it('renders the snapshot "As of" time as Australia/Canberra local time during AEDT (UTC+11), not raw UTC (issue #232)', async () => {
    jest.useFakeTimers({ now: new Date('2026-01-15T00:00:00Z') });
    try {
      const { host, openDropdown, checkOption, applyFilters } = await render();
      openDropdown('Machines');
      checkOption('Machine A');
      applyFilters();

      expect(host.querySelector('[data-testid="pick-list-snapshot"]')?.textContent).toContain('As of 15/01/2026, 11:00 am');
    } finally {
      jest.useRealTimers();
    }
  });

  it('stages a multi-machine selection from the Machines dropdown without changing the matrix until Apply', async () => {
    const { host, openDropdown, checkOption, dropdownTrigger } = await render();

    openDropdown('Machines');
    checkOption('Machine A');
    checkOption('Machine B');

    expect(dropdownTrigger('Machines').textContent).toContain('All machines');
    expect(host.textContent).toContain('Selected Machines (0)');
    expect(host.querySelector('table')).toBeNull();
  });

  it('Apply renders the staged machine selection as matrix columns and Selected Machines chips', async () => {
    const { host, openDropdown, checkOption, applyFilters } = await render();

    openDropdown('Machines');
    checkOption('Machine A');
    checkOption('Machine B');
    applyFilters();

    const headers = Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    expect(headers[0]).toBe('Product');
    expect(headers[1]).toBe('Total to Pick');
    expect(headers).toContain('Machine A');
    expect(headers).toContain('Machine B');
    expect(host.textContent).toContain('Selected Machines (2)');

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Coke');
    expect(rows[0].textContent).toContain('Pick: 4');
    expect(rows[0].textContent).toContain('Current: 6 of 10');
  });

  it('keeps the whole matrix header row sticky inside a scrollable table area', async () => {
    const { host, requireElement, openDropdown, checkOption, applyFilters } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    checkOption('Machine B');
    applyFilters();

    // A bounded height with overflow on both axes: rows scroll vertically under the pinned header,
    // and many machine columns still scroll horizontally in the same scroll area.
    const scrollArea = requireElement('[data-testid="pick-list-table-scroll"]');
    expect(scrollArea.contains(requireElement('table'))).toBe(true);
    expect(Array.from(scrollArea.classList)).toEqual(expect.arrayContaining(['max-h-[70vh]', 'overflow-auto']));

    const headers = Array.from(host.querySelectorAll<HTMLTableCellElement>('thead th'));
    expect(headers.map((th) => th.textContent?.trim())).toEqual(['Product', 'Total to Pick', 'Machine A', 'Machine B']);
    for (const th of headers) {
      // Every header cell, not just the first two, is pinned and opaque so rows cannot show through.
      expect(Array.from(th.classList)).toEqual(expect.arrayContaining(['sticky', 'top-0', 'z-10', 'bg-md-gray-100']));
      // The opaque head surface is the separator: #410 defines no shadow token for a sticky header
      // rule, so no header cell may carry an arbitrary shadow utility with a raw colour value.
      expect(Array.from(th.classList).filter((cssClass) => cssClass.startsWith('shadow'))).toEqual([]);
    }
  });

  it('a product-only Apply filters the visible rows without an extra Pick List request', async () => {
    const pickList = jest.fn(() => of(pickListResult()));
    const { openDropdown, checkOption, applyFilters, host } = await render(undefined, undefined, pickList);
    openDropdown('Machines');
    checkOption('Machine A');
    applyFilters();
    pickList.mockClear();

    openDropdown('Products');
    checkOption('Coke'); // Products defaults to all-selected, so unchecking Coke leaves only Chips staged.
    applyFilters();

    expect(pickList).not.toHaveBeenCalled();
    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('Chips');
  });

  it('shows a removable chip per applied machine and removes it, updating the dropdown and matrix', async () => {
    const { host, openDropdown, checkOption, applyFilters, requireElement, click, dropdownTrigger } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    applyFilters();

    expect(host.textContent).toContain('Selected Machines (1)');
    const chipRemove = requireElement<HTMLButtonElement>('[aria-label="Remove Machine A"]');

    click(chipRemove);

    expect(host.textContent).toContain('Selected Machines (0)');
    expect(host.textContent).toContain('No machines selected yet.');
    expect(dropdownTrigger('Machines').textContent).toContain('No machines selected');
    expect(host.querySelector('table')).toBeNull();
  });

  it('toggles a positive-pick cell to the picked visual state and back on click', async () => {
    const { host, openDropdown, checkOption, applyFilters, click } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    applyFilters();

    const pickButtons = Array.from(host.querySelectorAll<HTMLButtonElement>('tbody button'));
    const cokeCell = pickButtons.find((b) => b.textContent?.includes('Pick: 4'));
    if (cokeCell === undefined) {
      throw new Error('Expected a positive-pick cell for Coke.');
    }
    expect(cokeCell.className).toContain('bg-white');

    click(cokeCell);

    expect(cokeCell.className).toContain('bg-md-success/15');
    expect(cokeCell.textContent).toContain('✓');

    click(cokeCell);

    expect(cokeCell.className).toContain('bg-white');
  });

  it('never renders a button for a zero-pick cell', async () => {
    const { host, openDropdown, checkOption, applyFilters } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    checkOption('Machine B');
    applyFilters();

    const cells = Array.from(host.querySelectorAll('tbody td'));
    const zeroCell = cells.find((td) => td.textContent?.includes('Pick: 0'));
    if (zeroCell === undefined) {
      throw new Error('Expected a zero-pick cell to be rendered.');
    }
    expect(zeroCell.querySelector('button')).toBeNull();
  });

  it('shows a visible shortage badge for a product whose pick exceeds physical storage', async () => {
    const { host, openDropdown, checkOption, applyFilters } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    applyFilters();

    const badge = host.querySelector('[data-testid="storage-shortage-badge"]');
    expect(badge).not.toBeNull();
    expect(badge?.closest('tr')?.textContent).toContain('Chips');
  });

  it('shows the bottom summary with total units and actionable-cell progress', async () => {
    const { host, openDropdown, checkOption, applyFilters } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    applyFilters();

    expect(host.querySelector('[data-testid="pick-list-total-units"]')?.textContent).toContain('9');
    expect(host.querySelector('[data-testid="pick-list-progress-count"]')?.textContent).toContain('0/2');
    expect(host.querySelector('[data-testid="pick-list-progress-percent"]')?.textContent).toContain('0%');
  });

  it('renders no product or machine images anywhere on the page', async () => {
    const { host, openDropdown, checkOption, applyFilters } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    checkOption('Machine B');
    applyFilters();

    expect(host.querySelectorAll('img').length).toBe(0);
  });

  it('Reset clears the dropdown selections, chips, and matrix back to their initial state', async () => {
    const { host, openDropdown, checkOption, applyFilters, requireButton, click, dropdownTrigger } = await render();
    openDropdown('Machines');
    checkOption('Machine A');
    applyFilters();
    const pickButton = Array.from(host.querySelectorAll<HTMLButtonElement>('tbody button')).find((b) => b.textContent?.includes('Pick: 4'))!;
    click(pickButton);

    click(requireButton('Reset'));

    expect(host.textContent).toContain('Selected Machines (0)');
    expect(host.textContent).toContain('Select at least one machine');
    expect(dropdownTrigger('Machines').textContent).toContain('No machines selected');
    expect(dropdownTrigger('Products').textContent).toContain('All products');
    expect(host.querySelector('table')).toBeNull();
  });
});
