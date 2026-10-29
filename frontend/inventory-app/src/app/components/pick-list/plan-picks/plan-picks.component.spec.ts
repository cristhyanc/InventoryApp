import { SimpleChange } from '@angular/core';
import { Subject, of, throwError } from 'rxjs';
import { Machine, PickListProduct, PickListResult, Product, SiteListItem } from '../../../models/models';
import { PickListService } from '../../../services/pick-list.service';
import { PLANNED_QUANTITY_INVALID_MESSAGE, PLANNED_QUANTITY_TOO_LARGE_MESSAGE } from '../pick-list-quantity';
import {
  DISCARD_OPEN_EDIT_ON_APPLY,
  DISCARD_PLANNING_WORK_ON_REFRESH,
  DISCARD_PLANNING_WORK_ON_RESET,
  PlanPicksComponent,
  discardPlanningWorkOnMachineRemoval
} from './plan-picks.component';

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

/**
 * The shared dataset: Coke needs 4 at machine 10 and nothing at machine 20, with plenty in storage;
 * Chips needs 5 at machine 10 against only 2 in storage, so the backend already reports a shortage
 * of 3 - which is what lets an override be checked against the server's own figure.
 */
function pickListResult(overrides: Partial<PickListResult> = {}): PickListResult {
  return {
    products: [
      {
        productId: 1,
        productName: 'Coke',
        mdbCode: 2,
        storageQuantityInStock: 50,
        totalQuantityToPick: 4,
        storageShortageQuantity: 0,
        machineQuantities: [
          { machineId: 10, mdbCode: 2, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 },
          { machineId: 20, mdbCode: 2, currentQuantity: 10, targetQuantity: 10, quantityToPick: 0 }
        ]
      },
      {
        productId: 2,
        productName: 'Chips',
        mdbCode: 10,
        storageQuantityInStock: 2,
        totalQuantityToPick: 5,
        storageShortageQuantity: 3,
        machineQuantities: [{ machineId: 10, mdbCode: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 }]
      }
    ],
    ...overrides
  };
}

interface PickListProductOverrides {
  productId: number;
  productName: string;
  mdbCode: number | null;
}

/** A minimal row for the sort tests; machine 10 always has a 1-to-pick cell so the row is actionable. */
function pickListProduct(overrides: PickListProductOverrides): PickListProduct {
  return {
    productId: overrides.productId,
    productName: overrides.productName,
    mdbCode: overrides.mdbCode,
    storageQuantityInStock: 50,
    totalQuantityToPick: 1,
    storageShortageQuantity: 0,
    machineQuantities: [{ machineId: 10, mdbCode: overrides.mdbCode, currentQuantity: 0, targetQuantity: 1, quantityToPick: 1 }]
  };
}

/**
 * Five products in a backend order distinct from every sort order below, covering: a
 * case-insensitive name tie ("apple"/"Apple", ids 2 and 3), two missing MDB codes whose relative
 * order must stay name/id order in either sort direction (ids 3 and 5), and numeric MDB codes
 * that are not in string order (2, 5, 10) so a lexicographic bug would be caught.
 */
function sortDataset(): PickListResult {
  return {
    products: [
      pickListProduct({ productId: 1, productName: 'Banana', mdbCode: 5 }),
      pickListProduct({ productId: 2, productName: 'apple', mdbCode: 2 }),
      pickListProduct({ productId: 3, productName: 'Apple', mdbCode: null }),
      pickListProduct({ productId: 4, productName: 'Cherry', mdbCode: 10 }),
      pickListProduct({ productId: 5, productName: 'Date', mdbCode: null })
    ]
  };
}

const defaultMachines = [
  machine({ machineID: 10, machineName: 'Machine A' }),
  machine({ machineID: 20, machineName: 'Machine B' })
];

const defaultProducts = [product({ id: 1, name: 'Coke' }), product({ id: 2, name: 'Chips' })];

interface Harness {
  component: PlanPicksComponent;
  getPickList: jest.Mock;
  planningWork: boolean[];
}

function createHarness(
  options: {
    machines?: Machine[];
    products?: Product[];
    sites?: SiteListItem[];
    pickList?: jest.Mock;
  } = {}
): Harness {
  const getPickList = options.pickList ?? jest.fn(() => of(pickListResult()));
  const component = new PlanPicksComponent({ get: getPickList } as unknown as PickListService);
  component.machines = options.machines ?? defaultMachines;
  component.sites = options.sites ?? [];
  component.products = options.products ?? defaultProducts;
  component.ngOnChanges({ products: new SimpleChange(undefined, component.products, true) });

  const planningWork: boolean[] = [];
  component.planningWorkChange.subscribe((present) => planningWork.push(present));

  return { component, getPickList, planningWork };
}

/** Stages a machine selection and applies it - the normal "pick machines, then Apply" flow. */
function applyMachines(component: PlanPicksComponent, machineIds: number[]): void {
  component.stagedMachineIds = machineIds;
  component.applyFilters();
}

function cellOf(component: PlanPicksComponent, productId: number, machineId: number) {
  const row = component.pickListProducts.find((p) => p.productId === productId)!;
  return component.cellFor(row, machineId)!;
}

function rowOf(component: PlanPicksComponent, productId: number): PickListProduct {
  return component.pickListProducts.find((p) => p.productId === productId)!;
}

/** Types into the open editor exactly as the template's `(input)` binding does. */
function typeInEditor(component: PlanPicksComponent, value: string): void {
  component.onEditValueInput({ target: { value } } as unknown as Event);
}

/** Opens the editor on a cell and types a value into it without committing anything. */
function startDraft(component: PlanPicksComponent, productId: number, machineId: number, value: string): void {
  component.startEdit(productId, cellOf(component, productId, machineId));
  typeInEditor(component, value);
}

afterEach(() => jest.restoreAllMocks());

describe('PlanPicksComponent selection', () => {
  it('defaults the product filter to every product the catalogue answered with', () => {
    const { component } = createHarness();

    expect(component.stagedProductIds).toEqual([1, 2]);
    expect(component.appliedProductIds).toEqual([1, 2]);
  });

  it('shows no plan until a machine selection is applied', () => {
    const { component, getPickList } = createHarness();

    expect(component.appliedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
    expect(getPickList).not.toHaveBeenCalled();
  });

  it('staging a machine selection does not fetch or change the applied selection until Apply', () => {
    const { component, getPickList } = createHarness();

    component.stagedMachineIds = [10];

    expect(component.appliedMachineIds).toEqual([]);
    expect(getPickList).not.toHaveBeenCalled();
  });

  it('Apply applies both staged selections together and fetches for the applied machines', () => {
    const { component, getPickList } = createHarness();
    component.stagedMachineIds = [10];
    component.stagedProductIds = [1];

    component.applyFilters();

    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.appliedProductIds).toEqual([1]);
    expect(getPickList).toHaveBeenCalledWith([10]);
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1]);
  });

  it('Apply with only a product-filter change does not refetch the projection', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();
    component.stagedProductIds = [2];

    component.applyFilters();

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.appliedProductIds).toEqual([2]);
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
    expect(component.totalUnits()).toBe(9);
  });

  it('Apply with an unchanged machine selection does not refetch', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    getPickList.mockClear();

    applyMachines(component, [20, 10]);

    expect(getPickList).not.toHaveBeenCalled();
  });

  it('Applying an empty machine selection clears the plan without calling the backend', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();

    applyMachines(component, []);

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
    expect(component.lastRefreshedAt).toBeNull();
  });

  it('removing a machine chip updates both selections and refetches with the remaining ids', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    getPickList.mockClear();

    component.removeMachine(20);

    expect(getPickList).toHaveBeenCalledWith([10]);
    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.stagedMachineIds).toEqual([10]);
  });

  it('removing the last machine clears the plan without calling the backend again', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();

    component.removeMachine(10);

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.pickListProducts).toEqual([]);
  });
});

describe('PlanPicksComponent Refresh and Retry (issue #996)', () => {
  it('re-reads the same machine selection that is already applied, which Apply would not', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    getPickList.mockClear();

    component.refresh();

    expect(getPickList).toHaveBeenCalledTimes(1);
    expect(getPickList).toHaveBeenCalledWith([10]);
  });

  it('does nothing when no machine is applied, because there is no selection to re-read', () => {
    const { component, getPickList } = createHarness();

    component.refresh();

    expect(getPickList).not.toHaveBeenCalled();
  });

  it('reports a failed read as a failure to retry, not as an empty plan', () => {
    const getPickList = jest.fn(() => throwError(() => new Error('offline')));
    const { component } = createHarness({ pickList: getPickList });

    applyMachines(component, [10]);

    expect(component.loadFailed).toBe(true);
    expect(component.loading).toBe(false);
    expect(component.pickListProducts).toEqual([]);
  });

  it('retries that failed read through Refresh and shows the snapshot it returns', () => {
    const getPickList = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValueOnce(of(pickListResult()));
    const { component } = createHarness({ pickList: getPickList });
    applyMachines(component, [10]);

    component.refresh();

    expect(component.loadFailed).toBe(false);
    expect(component.pickListProducts).toHaveLength(2);
    expect(component.lastRefreshedAt).not.toBeNull();
  });

  it('asks before a refresh discards planning work, and changes nothing when refused', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '7';
    component.commitEdit(1, cellOf(component, 1, 10));
    getPickList.mockClear();

    component.refresh();

    expect(confirm).toHaveBeenCalled();
    expect(getPickList).not.toHaveBeenCalled();
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(7);
  });

  it('discards the overrides and marks it warned about once the refresh is confirmed', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '7';
    component.commitEdit(1, cellOf(component, 1, 10));
    component.toggleCompleted(2, cellOf(component, 2, 10));

    component.refresh();

    expect(component.hasPlanningWork).toBe(false);
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(4);
    expect(component.isCompleted(2, 10)).toBe(false);
  });

  it('asks before Reset discards planning work and changes nothing when refused', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.toggleCompleted(1, cellOf(component, 1, 10));

    component.resetAll();

    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.isCompleted(1, 10)).toBe(true);
  });

  it('asks before a removed machine column discards planning work and keeps everything on refusal', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    component.toggleCompleted(1, cellOf(component, 1, 10));
    getPickList.mockClear();

    component.removeMachine(10);

    expect(getPickList).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.isCompleted(1, 10)).toBe(true);
  });

  it('never asks about removing a machine column that holds no planning work', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    component.removeMachine(20);

    expect(confirm).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([10]);
  });
});

describe('PlanPicksComponent stale responses (issue #996)', () => {
  it('never lets a slow response replace a newer selection’s plan', () => {
    const slow = new Subject<PickListResult>();
    const getPickList = jest
      .fn()
      .mockReturnValueOnce(slow)
      .mockReturnValueOnce(of(pickListResult({ products: [rowFor(2, 'Chips')] })));
    const { component } = createHarness({ pickList: getPickList });
    applyMachines(component, [10]);
    applyMachines(component, [10, 20]);

    slow.next(pickListResult());
    slow.complete();

    expect(component.pickListProducts.map((p) => p.productId)).toEqual([2]);
  });

  it('never resurrects a plan that was cleared while its request was in flight', () => {
    const slow = new Subject<PickListResult>();
    const { component } = createHarness({ pickList: jest.fn(() => slow) });
    applyMachines(component, [10]);

    applyMachines(component, []);
    slow.next(pickListResult());
    slow.complete();

    expect(component.pickListProducts).toEqual([]);
    expect(component.lastRefreshedAt).toBeNull();
    expect(component.loading).toBe(false);
  });

  it('never resurrects a plan that Reset cleared while its request was in flight', () => {
    const slow = new Subject<PickListResult>();
    const { component } = createHarness({ pickList: jest.fn(() => slow) });
    applyMachines(component, [10]);

    component.resetAll();
    slow.next(pickListResult());
    slow.complete();

    expect(component.pickListProducts).toEqual([]);
    expect(component.appliedMachineIds).toEqual([]);
  });

  it('never reports a stale failure against a newer selection', () => {
    const slow = new Subject<PickListResult>();
    const getPickList = jest.fn().mockReturnValueOnce(slow).mockReturnValueOnce(of(pickListResult()));
    const { component } = createHarness({ pickList: getPickList });
    applyMachines(component, [10]);
    applyMachines(component, [10, 20]);

    slow.error(new Error('offline'));

    expect(component.loadFailed).toBe(false);
    expect(component.pickListProducts).toHaveLength(2);
  });
});

describe('PlanPicksComponent planning overrides (issue #996)', () => {
  it('starts an edit on the current planned quantity and commits a new one', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    component.startEdit(1, cellOf(component, 1, 10));
    expect(component.editValue).toBe('4');

    component.editValue = '6';
    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(6);
    expect(component.isOverridden(1, 10)).toBe(true);
    expect(component.isEditing(1, 10)).toBe(false);
  });

  it('preserves the backend suggestion and says what it was', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '6';
    component.commitEdit(1, cellOf(component, 1, 10));

    expect(cellOf(component, 1, 10).quantityToPick).toBe(4);
    expect(component.suggestionShown(1, cellOf(component, 1, 10))).toBe(true);
  });

  it('restores the previous quantity when the edit is cancelled', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '6';

    component.cancelEdit();

    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(4);
    expect(component.isOverridden(1, 10)).toBe(false);
  });

  it('commits on Enter and restores on Escape', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '9';

    component.onEditKeydown(new KeyboardEvent('keydown', { key: 'Enter' }), 1, cellOf(component, 1, 10));
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(9);

    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '11';
    component.onEditKeydown(new KeyboardEvent('keydown', { key: 'Escape' }), 1, cellOf(component, 1, 10));
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(9);
  });

  it.each([
    ['', PLANNED_QUANTITY_INVALID_MESSAGE],
    ['abc', PLANNED_QUANTITY_INVALID_MESSAGE],
    ['2.5', PLANNED_QUANTITY_INVALID_MESSAGE],
    ['-1', PLANNED_QUANTITY_INVALID_MESSAGE],
    ['1 2', PLANNED_QUANTITY_INVALID_MESSAGE],
    ['2147483648', PLANNED_QUANTITY_TOO_LARGE_MESSAGE]
  ])('refuses "%s" inline and commits nothing', (typed, message) => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = typed;

    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.editError).toBe(message);
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.isOverridden(1, 10)).toBe(false);
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(4);
  });

  it('accepts the largest storable quantity', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '2147483647';

    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(2147483647);
  });

  it('treats a committed zero as a deliberate skip: not a pick, still editable and resettable', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '0';
    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(0);
    expect(component.isActionable(1, cellOf(component, 1, 10))).toBe(false);
    expect(component.isEditable(1, cellOf(component, 1, 10))).toBe(true);
    // Chips/machine 10 is the only pick left, so the run is one pick rather than two.
    expect(component.totalPickCount()).toBe(1);

    component.resetOverride(1, 10);
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(4);
    expect(component.totalPickCount()).toBe(2);
  });

  it('clears the completion mark when a changed quantity is committed', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.toggleCompleted(1, cellOf(component, 1, 10));
    expect(component.isCompleted(1, 10)).toBe(true);

    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '5';
    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.isCompleted(1, 10)).toBe(false);
  });

  it('keeps the completion mark when the committed quantity is the one already planned', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.toggleCompleted(1, cellOf(component, 1, 10));

    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '4';
    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.isCompleted(1, 10)).toBe(true);
    expect(component.isOverridden(1, 10)).toBe(false);
  });

  it('never offers an edit on a cell the backend suggests no pick for', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    expect(component.isEditable(1, cellOf(component, 1, 20))).toBe(false);
    component.startEdit(1, cellOf(component, 1, 20));
    expect(component.isEditing(1, 20)).toBe(false);
  });

  it('never completes a cell with nothing to pick', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    component.toggleCompleted(1, cellOf(component, 1, 20));

    expect(component.isCompleted(1, 20)).toBe(false);
  });

  it('tells the page when planning work starts and stops', () => {
    const { component, planningWork } = createHarness();
    applyMachines(component, [10]);

    component.toggleCompleted(1, cellOf(component, 1, 10));
    component.toggleCompleted(1, cellOf(component, 1, 10));

    expect(planningWork).toEqual([true, false]);
  });

  it('calls no mutation endpoint across a whole planning interaction', () => {
    const getPickList = jest.fn(() => of(pickListResult()));
    const recordRefill = jest.fn();
    const recordMultiMachineRefill = jest.fn();
    const component = new PlanPicksComponent({
      get: getPickList,
      recordRefill,
      recordMultiMachineRefill
    } as unknown as PickListService);
    component.machines = defaultMachines;
    component.products = defaultProducts;
    component.ngOnChanges({ products: new SimpleChange(undefined, defaultProducts, true) });
    jest.spyOn(window, 'confirm').mockReturnValue(true);

    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '0';
    component.commitEdit(1, cellOf(component, 1, 10));
    component.toggleCompleted(2, cellOf(component, 2, 10));
    component.refresh();
    component.resetAll();

    expect(recordRefill).not.toHaveBeenCalled();
    expect(recordMultiMachineRefill).not.toHaveBeenCalled();
  });
});

/**
 * A quantity that has been typed into an open editor but not committed is planning work: every
 * transition that would throw it away asks first, a refusal keeps the draft exactly as it was, and
 * the page is told so its route and `beforeunload` guards cover it. An editor that still holds the
 * quantity it was opened on is not work, and an explicit Cancel is a discard the operator just
 * asked for, so neither produces a warning.
 */
describe('PlanPicksComponent an uncommitted quantity draft (issue #996)', () => {
  it('reports a changed but uncommitted quantity to the page as planning work', () => {
    const { component, planningWork } = createHarness();
    applyMachines(component, [10]);

    component.startEdit(1, cellOf(component, 1, 10));
    expect(component.hasPlanningWork).toBe(false);
    expect(planningWork).toEqual([]);

    typeInEditor(component, '7');

    expect(component.hasDirtyEditor).toBe(true);
    expect(component.hasPlanningWork).toBe(true);
    expect(planningWork).toEqual([true]);
    // Nothing is committed by typing: the planned quantity is still the backend's suggestion.
    expect(component.isOverridden(1, 10)).toBe(false);
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(4);
  });

  it.each(['4', '04', ' 4 '])('is no work while the editor still holds the planned quantity as "%s"', (typed) => {
    const { component, planningWork } = createHarness();
    applyMachines(component, [10]);

    startDraft(component, 1, 10, typed);

    expect(component.hasDirtyEditor).toBe(false);
    expect(component.hasPlanningWork).toBe(false);
    expect(planningWork).toEqual([]);
  });

  it('reports a draft typed back to the quantity it opened on as work that has gone again', () => {
    const { component, planningWork } = createHarness();
    applyMachines(component, [10]);

    startDraft(component, 1, 10, '7');
    typeInEditor(component, '4');

    expect(component.hasPlanningWork).toBe(false);
    expect(planningWork).toEqual([true, false]);
  });

  it('counts a value that cannot be committed, because it is still what was typed', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    startDraft(component, 1, 10, 'abc');
    expect(component.hasPlanningWork).toBe(true);

    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.editError).toBe(PLANNED_QUANTITY_INVALID_MESSAGE);
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.hasPlanningWork).toBe(true);
  });

  it('discards the draft on an explicit Cancel, without asking, and reports the work as gone', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component, planningWork } = createHarness();
    applyMachines(component, [10]);

    startDraft(component, 1, 10, '7');
    component.cancelEdit();

    expect(confirm).not.toHaveBeenCalled();
    expect(component.isEditing(1, 10)).toBe(false);
    expect(component.hasPlanningWork).toBe(false);
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(4);
    expect(planningWork).toEqual([true, false]);
  });

  it('stops reporting the draft once it is committed, as the override it became', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    startDraft(component, 1, 10, '7');
    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.hasDirtyEditor).toBe(false);
    expect(component.hasPlanningWork).toBe(true);
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(7);
  });

  it('asks before a refresh discards the draft, and a refusal keeps it in the open editor', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');
    getPickList.mockClear();

    component.refresh();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLANNING_WORK_ON_REFRESH);
    expect(getPickList).not.toHaveBeenCalled();
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.editValue).toBe('7');
    expect(component.hasPlanningWork).toBe(true);
  });

  it('discards the draft once the refresh is confirmed', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');
    getPickList.mockClear();

    component.refresh();

    expect(getPickList).toHaveBeenCalledWith([10]);
    expect(component.isEditing(1, 10)).toBe(false);
    expect(component.hasPlanningWork).toBe(false);
    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(4);
  });

  it('asks before Reset discards the draft, and a refusal keeps it with the selection', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');

    component.resetAll();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLANNING_WORK_ON_RESET);
    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.editValue).toBe('7');
  });

  it('discards the draft once Reset is confirmed', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');

    component.resetAll();

    expect(component.appliedMachineIds).toEqual([]);
    expect(component.editingKey).toBeNull();
    expect(component.editValue).toBe('');
    expect(component.hasPlanningWork).toBe(false);
  });

  it('asks before removing the machine column the draft is in, and a refusal keeps everything', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    startDraft(component, 1, 10, '7');
    getPickList.mockClear();

    component.removeMachine(10);

    expect(confirm).toHaveBeenCalledWith(discardPlanningWorkOnMachineRemoval('Machine A'));
    expect(getPickList).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.editValue).toBe('7');
  });

  it('discards the draft with the column once its removal is confirmed', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    startDraft(component, 1, 10, '7');

    component.removeMachine(10);

    expect(component.appliedMachineIds).toEqual([20]);
    expect(component.editingKey).toBeNull();
    expect(component.hasPlanningWork).toBe(false);
  });

  it('never asks about, and never drops, a draft in a column that is staying', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    startDraft(component, 1, 10, '7');

    component.removeMachine(20);

    expect(confirm).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.editValue).toBe('7');
    expect(component.hasPlanningWork).toBe(true);
  });

  it('asks before an applied Products selection leaves the draft’s row out, and a refusal changes nothing', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');
    component.stagedProductIds = [2];

    component.applyFilters();

    expect(confirm).toHaveBeenCalledWith(DISCARD_OPEN_EDIT_ON_APPLY);
    expect(component.appliedProductIds).toEqual([1, 2]);
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.editValue).toBe('7');
  });

  it('discards the draft once that Products selection is confirmed', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');
    component.stagedProductIds = [2];

    component.applyFilters();

    expect(component.appliedProductIds).toEqual([2]);
    expect(component.editingKey).toBeNull();
    expect(component.hasPlanningWork).toBe(false);
  });

  it('keeps the draft through an Apply that still shows its row', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');
    component.stagedProductIds = [1];

    component.applyFilters();

    expect(confirm).not.toHaveBeenCalled();
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.editValue).toBe('7');
  });

  it('stops reporting a draft whose cell the next snapshot no longer holds', () => {
    const getPickList = jest
      .fn()
      .mockReturnValueOnce(of(pickListResult()))
      .mockReturnValueOnce(of(pickListResult({ products: [rowFor(2, 'Chips')] })));
    const { component, planningWork } = createHarness({ pickList: getPickList });
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    applyMachines(component, [10, 20]);
    startDraft(component, 1, 10, '7');

    // Machine 20 holds no work of its own, so this is not a confirmed discard of the draft: the
    // refetch it triggers simply comes back without the row the draft was in.
    component.removeMachine(20);

    expect(component.editingKey).toBeNull();
    expect(component.hasPlanningWork).toBe(false);
    expect(planningWork).toEqual([true, false]);
  });

  it('tells the page the draft is gone when the view is destroyed', () => {
    const { component, planningWork } = createHarness();
    applyMachines(component, [10]);
    startDraft(component, 1, 10, '7');

    component.ngOnDestroy();

    expect(planningWork).toEqual([true, false]);
  });
});

describe('PlanPicksComponent totals, shortages and progress (issue #996)', () => {
  it('totals the whole applied run, overrides included, never only the visible rows', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    expect(component.totalUnits()).toBe(9);

    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '6';
    component.commitEdit(1, cellOf(component, 1, 10));
    component.search = 'Chips';

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
    expect(component.totalUnits()).toBe(11);
  });

  it('reports progress in plain language against the actionable picks only', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    expect(component.progressText()).toBe('0 of 2 picks completed');

    component.toggleCompleted(1, cellOf(component, 1, 10));

    expect(component.progressText()).toBe('1 of 2 picks completed');
    expect(component.remainingPickCount()).toBe(1);
  });

  it('matches the backend shortage while nothing is overridden', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    expect(component.shortageFor(rowOf(component, 2))).toBe(rowOf(component, 2).storageShortageQuantity);
    expect(component.shortageFor(rowOf(component, 1))).toBe(0);
    expect(component.shortageProductCount()).toBe(1);
  });

  it('recomputes the shortage from a committed override', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    component.startEdit(2, cellOf(component, 2, 10));
    component.editValue = '2';
    component.commitEdit(2, cellOf(component, 2, 10));

    expect(component.shortageFor(rowOf(component, 2))).toBe(0);
    expect(component.shortageProductCount()).toBe(0);
  });

  it('raises a shortage an override creates', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '60';
    component.commitEdit(1, cellOf(component, 1, 10));

    expect(component.shortageFor(rowOf(component, 1))).toBe(10);
  });

  it('filters to only the remaining picks, and to only the shortages', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.toggleCompleted(1, cellOf(component, 1, 10));

    component.onlyRemaining = true;
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);

    component.onlyRemaining = false;
    component.onlyShortages = true;
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
  });

  it('keeps overrides and marks across filtering and sorting', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '6';
    component.commitEdit(1, cellOf(component, 1, 10));
    component.toggleCompleted(2, cellOf(component, 2, 10));

    component.search = 'Coke';
    component.sortBy('mdbCode');
    component.search = '';
    component.onlyShortages = true;
    component.onlyShortages = false;

    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(6);
    expect(component.isCompleted(2, 10)).toBe(true);
  });
});

describe('PlanPicksComponent machine focus (issue #996)', () => {
  it('focuses the first applied machine and reports its own picks and units apart from the run’s', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    expect(component.focusedMachineId).toBe(10);
    expect(component.focusedMachinePickCount()).toBe(2);
    expect(component.focusedMachineUnits()).toBe(9);
    expect(component.totalUnits()).toBe(9);

    component.onFocusMachineChange(20);

    expect(component.focusedMachineUnits()).toBe(0);
    expect(component.totalUnits()).toBe(9);
  });

  it('keeps every override and mark when the focused machine changes', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    component.startEdit(1, cellOf(component, 1, 10));
    component.editValue = '6';
    component.commitEdit(1, cellOf(component, 1, 10));
    component.toggleCompleted(2, cellOf(component, 2, 10));

    component.onFocusMachineChange(20);

    expect(component.pickQuantity(1, cellOf(component, 1, 10))).toBe(6);
    expect(component.isCompleted(2, 10)).toBe(true);
  });

  it('falls back to a column that still exists when the focused machine is removed', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    component.onFocusMachineChange(20);

    component.removeMachine(20);

    expect(component.focusedMachineId).toBe(10);
  });
});

describe('PlanPicksComponent site grouping (issue #996)', () => {
  const sites: SiteListItem[] = [
    { siteId: 5, name: 'Airport', machineCount: 2 },
    { siteId: 6, name: 'Hospital', machineCount: 1 }
  ];
  const machines = [
    machine({ machineID: 10, machineName: 'Machine A', siteId: 5 }),
    machine({ machineID: 20, machineName: 'Machine B', siteId: 6 }),
    machine({ machineID: 30, machineName: 'Machine C', siteId: 5 }),
    machine({ machineID: 40, machineName: 'Machine D', siteId: null })
  ];

  it('keeps each site’s machine columns adjacent, under one header, with no-site columns last', () => {
    const { component } = createHarness({ machines, sites });
    applyMachines(component, [20, 40, 10, 30]);

    expect(component.machineGroups.map((group) => [group.siteName, [...group.machineIds]])).toEqual([
      ['Airport', [10, 30]],
      ['Hospital', [20]],
      [null, [40]]
    ]);
    expect(component.machineColumnIds).toEqual([10, 30, 20, 40]);
    expect(component.siteHeaderShown).toBe(true);
  });

  it('groups the machine filter’s options by site and leaves an unsited machine ungrouped', () => {
    const { component } = createHarness({ machines, sites });

    expect(component.machineOptions()).toEqual([
      { id: 10, label: 'Machine A', group: 'Airport' },
      { id: 30, label: 'Machine C', group: 'Airport' },
      { id: 20, label: 'Machine B', group: 'Hospital' },
      { id: 40, label: 'Machine D', group: undefined }
    ]);
  });

  it('invents no site when the site read found none: one ungrouped set of usable columns', () => {
    const { component } = createHarness({ machines, sites: [] });
    applyMachines(component, [10, 40]);

    expect(component.siteHeaderShown).toBe(false);
    expect(component.machineColumnIds).toEqual([10, 40]);
    expect(component.machineOptions().every((option) => option.group === undefined)).toBe(true);
  });

  it('leaves a machine whose site the site read did not return under no site header', () => {
    const { component } = createHarness({ machines, sites: [sites[1]] });
    applyMachines(component, [10, 20]);

    expect(component.machineGroups.map((group) => [group.siteName, [...group.machineIds]])).toEqual([
      ['Hospital', [20]],
      [null, [10]]
    ]);
  });
});

describe('PlanPicksComponent sorting', () => {
  const sortProducts = [1, 2, 3, 4, 5].map((id) => product({ id, name: `Catalogue ${id}` }));

  function createSortHarness(): Harness {
    return createHarness({ products: sortProducts, pickList: jest.fn(() => of(sortDataset())) });
  }

  it('keeps the backend order and reports aria-sort none on both columns until a header is clicked', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1, 2, 3, 4, 5]);
    expect(component.ariaSort('name')).toBe('none');
    expect(component.ariaSort('mdbCode')).toBe('none');
    expect(component.sortLabel('Product', 'name')).toBe('Product');
    expect(component.sortLabel('MDB Code', 'mdbCode')).toBe('MDB Code');
  });

  it('sorts by product name ascending, case-insensitively, breaking a name tie by product id', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);

    component.sortBy('name');

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2, 3, 1, 4, 5]);
    expect(component.ariaSort('name')).toBe('ascending');
    expect(component.sortLabel('Product', 'name')).toBe('Product (ascending)');
  });

  it('sorts by product name descending on a second click of the same header', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);

    component.sortBy('name');
    component.sortBy('name');

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([5, 4, 1, 3, 2]);
    expect(component.ariaSort('name')).toBe('descending');
  });

  it('sorts by MDB Code numerically ascending, with 2 before 10, and missing codes last', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);

    component.sortBy('mdbCode');

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2, 1, 4, 3, 5]);
    expect(component.ariaSort('mdbCode')).toBe('ascending');
  });

  it('sorts by MDB Code descending on a second click, with missing codes still last', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);

    component.sortBy('mdbCode');
    component.sortBy('mdbCode');

    expect(component.visibleProducts().map((p) => p.productId)).toEqual([4, 1, 2, 3, 5]);
    expect(component.ariaSort('mdbCode')).toBe('descending');
  });

  it('breaks a tie between two missing MDB codes by product name, then product id', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);

    component.sortBy('mdbCode');

    const order = component.visibleProducts().map((p) => p.productId);
    expect(order.indexOf(3)).toBeLessThan(order.indexOf(5));
  });

  it('clicking the other sortable header makes it active ascending and clears the previous header', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);
    component.sortBy('name');
    component.sortBy('name');

    component.sortBy('mdbCode');

    expect(component.ariaSort('mdbCode')).toBe('ascending');
    expect(component.ariaSort('name')).toBe('none');
  });

  it('keeps the active sort across a product-filter Apply and a machine-change refetch', () => {
    const getPickList = jest.fn(() => of(sortDataset()));
    const { component } = createHarness({ products: sortProducts, pickList: getPickList });
    applyMachines(component, [10]);
    component.sortBy('name');

    component.stagedProductIds = [1, 2, 3];
    component.applyFilters();
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2, 3, 1]);

    component.stagedProductIds = [1, 2, 3, 4, 5];
    applyMachines(component, [10, 20]);
    expect(getPickList).toHaveBeenCalledWith([10, 20]);
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2, 3, 1, 4, 5]);
  });

  it('leaves marks, totals and progress unaffected by sorting', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);
    component.toggleCompleted(1, cellOf(component, 1, 10));
    const totalBefore = component.totalUnits();
    const progressBefore = component.progressText();

    component.sortBy('mdbCode');

    expect(component.isCompleted(1, 10)).toBe(true);
    expect(component.totalUnits()).toBe(totalBefore);
    expect(component.progressText()).toBe(progressBefore);
  });

  it('Reset clears the active sort so the next loaded plan appears in backend order', () => {
    const { component } = createSortHarness();
    applyMachines(component, [10]);
    component.sortBy('name');

    component.resetAll();
    applyMachines(component, [10]);

    expect(component.ariaSort('name')).toBe('none');
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1, 2, 3, 4, 5]);
  });
});

/** One row of the shared dataset, by product, for the stale-response tests. */
function rowFor(productId: number, productName: string): PickListProduct {
  return {
    productId,
    productName,
    mdbCode: 10,
    storageQuantityInStock: 2,
    totalQuantityToPick: 5,
    storageShortageQuantity: 3,
    machineQuantities: [{ machineId: 10, mdbCode: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 }]
  };
}
