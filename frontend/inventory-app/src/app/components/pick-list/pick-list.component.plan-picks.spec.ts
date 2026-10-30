import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, Subject, of, throwError } from 'rxjs';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  DISCARD_PLAN_ON_REFRESH_CONFIRMATION,
  DISCARD_PLAN_ON_RESET_CONFIRMATION,
  PickListComponent,
  UNRESOLVED_SAVE_LEAVE_WARNING,
  UNSAVED_WORK_LEAVE_WARNING,
  discardPlanMachineConfirmation
} from './pick-list.component';
import { confirmPickListLeave } from './pick-list-leave.guard';
import { refillCellKey } from './pick-list-refill-errors';
import { MAXIMUM_PICK_QUANTITY, PICK_QUANTITY_INVALID_MESSAGE, PICK_QUANTITY_TOO_LARGE_MESSAGE, parsePickQuantity } from './plan-picks';
import { LocalMachineService } from '../../services/local-machine.service';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { PickListService } from '../../services/pick-list.service';
import { SiteService } from '../../services/site.service';
import { ToastService } from '../../services/toast.service';
import { Machine, PickListResult, Product, SiteListItem } from '../../models/models';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { AccessService } from '../../access/access.service';
import { NayaxConnectionService } from '../../services/nayax-connection.service';
import { providerAvailabilityDouble } from '../../provider/provider-availability.spec-helpers';

/**
 * Plan picks (issue #996): editable planning overrides, completion, refresh/retry, stale-response
 * protection, filters, machine focus, site grouping and the departure guard. Everything here is
 * local planning state; none of it may call anything but the read-only pick list projection.
 *
 * Issue #1008 replaced the pencil and its confirm/cancel with an always-visible quantity box
 * between a minus and a plus button: every valid keystroke is the override, and text the plan
 * cannot use is held as pending work beside the last valid quantity rather than being coerced.
 */

function machine(machineID: number, machineName: string, overrides: Partial<Machine> = {}): Machine {
  return {
    machineID,
    machineName,
    machineNumber: null,
    todayDirectProfit: null,
    twoWeeksAgoDirectProfit: null,
    lastWeekDirectProfit: null,
    currentWeekDirectProfit: null,
    previousComparableWeekDirectProfit: null,
    monthToDateDirectProfit: null,
    ...overrides
  };
}

function product(id: number, name: string, categoryId: number | null = null): Product {
  return {
    id,
    name,
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
    category: categoryId === null ? null : { id: categoryId, name: categoryId === 1 ? 'Drinks' : 'Snacks' }
  };
}

/**
 * Coke: 4 to pick at machine 10, nothing at 20, storage 6. Chips: 5 at 10 and 3 at 20, storage 7, so
 * the plan is short by 1 until an override changes it. Water: nothing to pick anywhere.
 */
function snapshot(): PickListResult {
  return {
    products: [
      {
        productId: 1, productName: 'Coke', mdbCode: 2, storageQuantityInStock: 6, totalQuantityToPick: 4, storageShortageQuantity: 0,
        machineQuantities: [
          { machineId: 10, mdbCode: 2, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 },
          { machineId: 20, mdbCode: 2, currentQuantity: 10, targetQuantity: 10, quantityToPick: 0 }
        ]
      },
      {
        productId: 2, productName: 'Chips', mdbCode: 10, storageQuantityInStock: 7, totalQuantityToPick: 8, storageShortageQuantity: 1,
        machineQuantities: [
          { machineId: 10, mdbCode: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 },
          { machineId: 20, mdbCode: 11, currentQuantity: 2, targetQuantity: 5, quantityToPick: 3 }
        ]
      },
      {
        productId: 3, productName: 'Water', mdbCode: null, storageQuantityInStock: 20, totalQuantityToPick: 0, storageShortageQuantity: 0,
        machineQuantities: [{ machineId: 20, mdbCode: null, currentQuantity: 8, targetQuantity: 8, quantityToPick: 0 }]
      }
    ]
  };
}

interface Harness {
  component: PickListComponent;
  getPickList: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
}

function createHarness(
  options: { getPickList?: jest.Mock; machines?: Machine[]; sites?: Observable<SiteListItem[]>; getSiteList?: jest.Mock } = {}
): Harness {
  const machines = options.machines ?? [machine(10, 'Drinks'), machine(20, 'Snacks')];
  const getPickList = options.getPickList ?? jest.fn(() => of(snapshot()));
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
  const component = new PickListComponent(
    { getAll: jest.fn(() => of(machines)) } as unknown as MachineService,
    { getAll: jest.fn(() => of([product(1, 'Coke', 1), product(2, 'Chips', 2), product(3, 'Water', 1)])) } as unknown as ProductService,
    { get: getPickList } as unknown as PickListService,
    toast as unknown as ToastService,
    providerAvailabilityDouble(),
    { snapshot: { queryParamMap: convertToParamMap({}) } } as unknown as ActivatedRoute,
    { getList: options.getSiteList ?? jest.fn(() => options.sites ?? of([])) } as unknown as SiteService
  );
  component.ngOnInit();
  return { component, getPickList, toast };
}

function applyMachines(component: PickListComponent, machineIds: number[]): void {
  component.stagedMachineIds = machineIds;
  component.applyFilters();
}

function row(component: PickListComponent, productId: number) {
  return component.pickListProducts.find((p) => p.productId === productId)!;
}

/** Types text into one cell's always-visible quantity box, as the operator does (issue #1008). */
function type(component: PickListComponent, productId: number, machineId: number, value: string): void {
  component.onQuantityInput(row(component, productId), machineId, value);
}

/** The cell one product occupies at one machine. */
function cellOf(component: PickListComponent, productId: number, machineId: number) {
  return row(component, productId).machineQuantities.find((mq) => mq.machineId === machineId)!;
}

beforeEach(() => jest.useFakeTimers());
afterEach(() => {
  jest.useRealTimers();
  jest.restoreAllMocks();
});

describe('parsePickQuantity (issue #996)', () => {
  it.each([['0', 0], ['7', 7], [' 12 ', 12], [String(MAXIMUM_PICK_QUANTITY), MAXIMUM_PICK_QUANTITY]])('accepts %p as %p', (raw, quantity) => {
    expect(parsePickQuantity(raw)).toEqual({ quantity });
  });

  it.each(['', 'abc', '1.5', '-1', '+3', '1e3'])('refuses %p as malformed', (raw) => {
    expect(parsePickQuantity(raw)).toEqual({ error: PICK_QUANTITY_INVALID_MESSAGE });
  });

  it('refuses a quantity the pick list cannot represent', () => {
    expect(parsePickQuantity(String(MAXIMUM_PICK_QUANTITY + 1))).toEqual({ error: PICK_QUANTITY_TOO_LARGE_MESSAGE });
  });
});

describe('Plan picks refresh and retry (issue #996)', () => {
  it('Refresh reads the applied machines again even though the selection is unchanged', () => {
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    getPickList.mockClear();

    component.refresh();

    expect(getPickList).toHaveBeenCalledTimes(1);
    expect(getPickList).toHaveBeenCalledWith([10, 20]);
  });

  it('after a failed read, Retry reads the same selection again, which Apply alone would not', () => {
    const getPickList = jest.fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValue(of(snapshot()));
    const { component } = createHarness({ getPickList });
    applyMachines(component, [10]);
    expect(component.loadState.loadFailed).toBe(true);

    component.applyFilters();
    expect(getPickList).toHaveBeenCalledTimes(1);

    component.refresh();

    expect(getPickList).toHaveBeenCalledTimes(2);
    expect(getPickList).toHaveBeenLastCalledWith([10]);
    expect(component.loadState.loaded).toBe(true);
    expect(component.runProducts().length).toBeGreaterThan(0);
  });

  it('drops a response that arrives after Reset, so it cannot resurrect a cleared plan', () => {
    const pending = new Subject<PickListResult>();
    const { component } = createHarness({ getPickList: jest.fn(() => pending) });
    applyMachines(component, [10]);

    component.resetAll();
    pending.next(snapshot());
    pending.complete();

    expect(component.appliedMachineIds).toEqual([]);
    expect(component.pickListProducts).toEqual([]);
    expect(component.lastRefreshedAt).toBeNull();
  });

  it('drops an older response that arrives after a newer selection was applied', () => {
    const first = new Subject<PickListResult>();
    const second = new Subject<PickListResult>();
    const getPickList = jest.fn().mockReturnValueOnce(first).mockReturnValueOnce(second);
    const { component } = createHarness({ getPickList });
    applyMachines(component, [10]);
    applyMachines(component, [10, 20]);

    second.next(snapshot());
    second.complete();
    first.next({ products: [] });
    first.complete();

    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.runProducts().map((p) => p.productId)).toEqual([1, 2, 3]);
  });

  it('drops a failure that arrives after the plan was cleared, with no error toast', () => {
    const pending = new Subject<PickListResult>();
    const { component, toast } = createHarness({ getPickList: jest.fn(() => pending) });
    applyMachines(component, [10]);

    component.resetAll();
    pending.error(new HttpErrorResponse({ status: 500 }));

    expect(toast.error).not.toHaveBeenCalled();
    expect(component.pickListProducts).toEqual([]);
  });

  it('asks before Refresh discards planning work; Cancel keeps everything and reads nothing', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, '2');
    component.togglePicked(row(component, 2), 10);
    getPickList.mockClear();

    component.refresh();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLAN_ON_REFRESH_CONFIRMATION);
    expect(getPickList).not.toHaveBeenCalled();
    expect(component.plannedQuantity(1, row(component, 1).machineQuantities[0])).toBe(2);
    expect(component.isPicked(2, 10)).toBe(true);
  });

  it('a confirmed Refresh discards every override and checkmark and plans from the new snapshot', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, '2');
    component.togglePicked(row(component, 2), 10);

    component.refresh();

    expect(getPickList).toHaveBeenCalledTimes(2);
    expect(component.hasPlanningWork()).toBe(false);
    expect(component.totalToPickUnits()).toBe(9);
  });

  it('asks before Reset discards planning work, and Cancel changes nothing', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.togglePicked(row(component, 1), 10);

    component.resetAll();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLAN_ON_RESET_CONFIRMATION);
    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.isPicked(1, 10)).toBe(true);
  });

  it('asks before removing a machine that holds planning work, names it, and keeps everything on Cancel', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    component.togglePicked(row(component, 2), 20);

    component.removeMachine(20);

    expect(confirm).toHaveBeenCalledWith(discardPlanMachineConfirmation('Snacks'));
    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.isPicked(2, 20)).toBe(true);
  });

  it('removes a machine with no planning work without asking, keeping the other machines’ work', () => {
    const confirm = jest.spyOn(window, 'confirm');
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    component.togglePicked(row(component, 2), 10);
    getPickList.mockClear();

    component.removeMachine(20);

    expect(confirm).not.toHaveBeenCalled();
    expect(getPickList).not.toHaveBeenCalled();
    expect(component.isPicked(2, 10)).toBe(true);
    // Chips' total and shortage now count machine 10 only.
    expect(component.productPlannedTotal(row(component, 2))).toBe(5);
    expect(component.productShortage(row(component, 2))).toBe(0);
    // Water had a cell only at the removed machine, so it is no longer part of the run.
    expect(component.runProducts().map((p) => p.productId)).toEqual([1, 2]);
  });
});

describe('Plan picks quantity overrides (issues #996, #1008)', () => {
  it('a cell’s box shows its effective quantity: the suggestion, then the retained override', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    expect(component.quantityInputValue(1, cellOf(component, 1, 10))).toBe('4');

    type(component, 1, 10, '7');

    const cell = cellOf(component, 1, 10);
    expect(component.quantityInputValue(1, cell)).toBe('7');
    expect(component.plannedQuantity(1, cell)).toBe(7);
    // The original suggestion is never rewritten.
    expect(cell.quantityToPick).toBe(4);
    expect(component.isOverridden(1, 10)).toBe(true);
  });

  it('every valid keystroke is the override: no confirm step, and totals follow immediately', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    type(component, 2, 20, '1');

    expect(component.plannedQuantity(2, cellOf(component, 2, 20))).toBe(1);
    expect(component.totalToPickUnits()).toBe(10);
    expect(component.shortageProducts()).toEqual([]);
  });

  it('typing the suggestion again drops the override and hides the Suggested label', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, '7');

    type(component, 1, 10, '4');

    expect(component.isOverridden(1, 10)).toBe(false);
  });

  it('Use suggested puts a cell back on its suggestion', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, '0');

    component.useSuggestion(row(component, 1), 10);

    expect(component.isOverridden(1, 10)).toBe(false);
    expect(component.actionableCellCount()).toBe(2);
  });

  it('never accepts a quantity for a cell with nothing suggested', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    type(component, 1, 20, '5');

    expect(component.isOverridden(1, 20)).toBe(false);
    expect(component.hasInvalidQuantity(1, 20)).toBe(false);
    expect(component.hasPlanningWork()).toBe(false);
  });

  it('a changed effective quantity clears the cell’s completion; entering the same value keeps it', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.togglePicked(row(component, 1), 10);

    type(component, 1, 10, '4');
    expect(component.isPicked(1, 10)).toBe(true);

    type(component, 1, 10, '3');
    expect(component.isPicked(1, 10)).toBe(false);
  });

  it('totals, shortages and progress use the effective quantities across the whole applied run', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    // Coke 4, Chips 5 + 3 = 8 against 7 in storage.
    expect(component.totalToPickUnits()).toBe(12);
    expect(component.shortageProducts().map((p) => p.productId)).toEqual([2]);
    expect(component.productShortage(row(component, 2))).toBe(1);

    type(component, 2, 20, '1');
    type(component, 1, 10, '9');

    expect(component.productShortage(row(component, 1))).toBe(3);
    expect(component.runProgressLabel()).toBe('0 of 3 picks completed');
    component.togglePicked(row(component, 1), 10);
    expect(component.runProgressLabel()).toBe('1 of 3 picks completed');
    expect(component.remainingPickCount()).toBe(2);
  });

  it('a zero is a deliberate skip: not a pick, not checkable, still editable and resettable', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.togglePicked(row(component, 1), 10);

    type(component, 1, 10, '0');

    expect(component.isPicked(1, 10)).toBe(false);
    expect(component.actionableCellCount()).toBe(1);
    component.togglePicked(row(component, 1), 10);
    expect(component.isPicked(1, 10)).toBe(false);
    expect(component.totalToPickUnits()).toBe(5);

    // The box stays on screen and usable at zero, which is the only way back from a skip.
    expect(component.quantityInputValue(1, cellOf(component, 1, 10))).toBe('0');
    type(component, 1, 10, '2');
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(2);
    component.useSuggestion(row(component, 1), 10);
    expect(component.actionableCellCount()).toBe(2);
  });

  it('never calls anything but the read-only projection, whatever is planned', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    type(component, 1, 10, '2');
    type(component, 2, 20, 'oops');
    component.stepQuantity(row(component, 1), 10, 1);
    component.togglePicked(row(component, 2), 20);
    component.refresh();
    component.resetAll();

    expect(getPickList.mock.calls.every((call) => Array.isArray(call[0]))).toBe(true);
  });
});

/**
 * The minus and plus buttons beside the box (issue #1008): exactly one unit each, bounded at zero
 * and at the largest quantity the pick list can represent, and refused entirely while the box holds
 * text the plan cannot use.
 */
describe('Plan picks quantity steppers (issue #1008)', () => {
  it('adjusts by exactly one in each direction and updates the plan at once', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    component.stepQuantity(row(component, 1), 10, 1);
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(5);

    component.stepQuantity(row(component, 1), 10, -1);
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(4);
    expect(component.isOverridden(1, 10)).toBe(false);
  });

  it('stops minus at zero and plus at the largest supported quantity', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, '0');

    expect(component.canDecreaseQuantity(1, cellOf(component, 1, 10))).toBe(false);
    component.stepQuantity(row(component, 1), 10, -1);
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(0);

    type(component, 1, 10, String(MAXIMUM_PICK_QUANTITY));

    expect(component.canIncreaseQuantity(1, cellOf(component, 1, 10))).toBe(false);
    component.stepQuantity(row(component, 1), 10, 1);
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(MAXIMUM_PICK_QUANTITY);
  });

  it('stepping off a completed quantity clears that cell’s completion', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.togglePicked(row(component, 1), 10);

    component.stepQuantity(row(component, 1), 10, 1);

    expect(component.isPicked(1, 10)).toBe(false);
  });

  it('is disabled and does nothing while the box holds text the plan cannot use', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, '2.5');

    expect(component.canDecreaseQuantity(1, cellOf(component, 1, 10))).toBe(false);
    expect(component.canIncreaseQuantity(1, cellOf(component, 1, 10))).toBe(false);

    component.stepQuantity(row(component, 1), 10, 1);

    expect(component.quantityInputValue(1, cellOf(component, 1, 10))).toBe('2.5');
    expect(component.isOverridden(1, 10)).toBe(false);
  });
});

/**
 * Invalid or incomplete text is validated whole and kept whole (issue #1008): it is never truncated
 * to the digits it starts with, never read as a zero, and never left looking like a completed pick.
 * The last valid effective quantity keeps carrying the totals until the operator corrects the box.
 */
describe('Plan picks refuses text the plan cannot use (issue #1008)', () => {
  it.each(['abc', '2.5', '-1', '+3', '1e3', '12x', String(MAXIMUM_PICK_QUANTITY + 1)])(
    'keeps %p exactly as typed, refuses it with a message and changes no quantity',
    (raw) => {
      const { component } = createHarness();
      applyMachines(component, [10]);
      type(component, 1, 10, '6');

      type(component, 1, 10, raw);

      expect(component.quantityInputValue(1, cellOf(component, 1, 10))).toBe(raw);
      expect(component.quantityError(1, 10)).toBeTruthy();
      // The last valid quantity is what the run still counts, and the cell says so.
      expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(6);
      expect(component.totalToPickUnits()).toBe(11);
    }
  );

  it('treats a blank box as incomplete input rather than an implicit zero', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    type(component, 1, 10, '');

    expect(component.hasInvalidQuantity(1, 10)).toBe(true);
    expect(component.quantityError(1, 10)).toBe(PICK_QUANTITY_INVALID_MESSAGE);
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(4);
    expect(component.actionableCellCount()).toBe(2);
  });

  it('refuses an overflowing quantity whole instead of clamping it to the maximum', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    type(component, 1, 10, String(MAXIMUM_PICK_QUANTITY + 1));

    expect(component.quantityError(1, 10)).toBe(PICK_QUANTITY_TOO_LARGE_MESSAGE);
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(4);
  });

  it('clears completion as soon as the box differs from the last valid value, and refuses to mark it complete', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.togglePicked(row(component, 1), 10);

    type(component, 1, 10, '');

    expect(component.isPicked(1, 10)).toBe(false);
    component.togglePicked(row(component, 1), 10);
    expect(component.isPicked(1, 10)).toBe(false);
    expect(component.canComplete(1, cellOf(component, 1, 10))).toBe(false);
  });

  it('a corrected box clears the message and takes effect at once', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, 'x');

    type(component, 1, 10, '9');

    expect(component.hasInvalidQuantity(1, 10)).toBe(false);
    expect(component.quantityError(1, 10)).toBeNull();
    expect(component.plannedQuantity(1, cellOf(component, 1, 10))).toBe(9);
    expect(component.canComplete(1, cellOf(component, 1, 10))).toBe(true);
  });

  it('Use suggested rescues a cell whose box holds text the plan cannot use', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, 'oops');

    component.useSuggestion(row(component, 1), 10);

    expect(component.hasInvalidQuantity(1, 10)).toBe(false);
    expect(component.quantityInputValue(1, cellOf(component, 1, 10))).toBe('4');
  });
});

/**
 * Text the plan cannot use yet is planning work in its own right (issue #1008, replacing #996's
 * open-editor rule): every transition that would throw it away asks first, exactly as it does for a
 * valid override, and it is never silently converted to a quantity or dropped.
 */
describe('Plan picks guards pending invalid text (issue #1008)', () => {
  /** Leaves the Chips box at machine 20 (suggestion 3) holding text that is not a usable quantity. */
  function typeInvalid(component: PickListComponent, value = '1x'): void {
    type(component, 2, 20, value);
  }

  it('a box left on its effective quantity is not planning work', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    type(component, 2, 20, '3');

    expect(component.hasPlanningWork()).toBe(false);
    expect(component.leaveWarning()).toBeNull();
  });

  it('pending invalid text is planning work', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    expect(component.hasPlanningWork()).toBe(true);
  });

  it('asks before Refresh discards the pending text; Cancel keeps the text and reads nothing', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);
    getPickList.mockClear();

    component.refresh();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLAN_ON_REFRESH_CONFIRMATION);
    expect(getPickList).not.toHaveBeenCalled();
    expect(component.quantityInputValue(2, cellOf(component, 2, 20))).toBe('1x');
  });

  it('a confirmed Refresh discards the pending text', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    component.refresh();

    expect(getPickList).toHaveBeenCalledTimes(2);
    expect(component.hasInvalidQuantity(2, 20)).toBe(false);
    expect(component.isOverridden(2, 20)).toBe(false);
  });

  it('Refresh with every box on its effective quantity asks nothing', () => {
    const confirm = jest.spyOn(window, 'confirm');
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);

    component.refresh();

    expect(confirm).not.toHaveBeenCalled();
    expect(getPickList).toHaveBeenCalledTimes(2);
  });

  it('asks before Reset discards the pending text; Cancel keeps it, and confirming clears it', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    component.resetAll();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLAN_ON_RESET_CONFIRMATION);
    expect(component.quantityInputValue(2, cellOf(component, 2, 20))).toBe('1x');
    expect(component.appliedMachineIds).toEqual([10, 20]);

    confirm.mockReturnValue(true);
    component.resetAll();

    expect(component.hasInvalidQuantity(2, 20)).toBe(false);
    expect(component.appliedMachineIds).toEqual([]);
  });

  it('asks before removing the machine whose cell holds pending text, by name; Cancel keeps it', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    component.removeMachine(20);

    expect(confirm).toHaveBeenCalledWith(discardPlanMachineConfirmation('Snacks'));
    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.quantityInputValue(2, cellOf(component, 2, 20))).toBe('1x');

    confirm.mockReturnValue(true);
    component.removeMachine(20);

    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.hasInvalidQuantity(2, 20)).toBe(false);
  });

  it('removing a different machine neither asks nor touches the pending text', () => {
    const confirm = jest.spyOn(window, 'confirm');
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    component.removeMachine(10);

    expect(confirm).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([20]);
    expect(component.quantityInputValue(2, cellOf(component, 2, 20))).toBe('1x');
  });

  it('the same check applies when Apply removes the machine', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    applyMachines(component, [10]);

    expect(confirm).toHaveBeenCalledWith(discardPlanMachineConfirmation('Snacks'));
    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.quantityInputValue(2, cellOf(component, 2, 20))).toBe('1x');
  });

  it('guards leaving the page and reloading or closing the tab, and a cancelled departure keeps the text', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    expect(component.confirmLeave()).toBe(false);
    expect(confirm).toHaveBeenCalledWith(UNSAVED_WORK_LEAVE_WARNING);
    expect(component.quantityInputValue(2, cellOf(component, 2, 20))).toBe('1x');

    const unload = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    component.onBeforeUnload(unload);
    expect(unload.defaultPrevented).toBe(true);
  });

  it('survives filtering, sorting, the focused machine and a trip through Record refill', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeInvalid(component);

    component.search = 'zzz';
    component.statusFilter = 'shortages';
    component.sortBy('mdbCode');
    component.focusMachine(10);
    component.switchMode('manual');
    component.switchMode('calculated');
    component.search = '';
    component.statusFilter = 'all';

    expect(component.quantityInputValue(2, cellOf(component, 2, 20))).toBe('1x');
    expect(component.quantityError(2, 20)).toBe(PICK_QUANTITY_INVALID_MESSAGE);
  });
});

describe('Plan picks filters and machine focus preserve the plan (issue #996)', () => {
  it('Only remaining shows rows with an unchecked pick; Only shortages shows rows storage cannot cover', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    component.togglePicked(row(component, 1), 10);

    component.statusFilter = 'remaining';
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);

    component.statusFilter = 'shortages';
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
  });

  it('search and category narrow the rows without changing the whole-run summary', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    component.search = 'chi';
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([2]);
    component.search = '';
    component.categoryId = 1;
    expect(component.visibleProducts().map((p) => p.productId)).toEqual([1, 3]);

    expect(component.totalToPickUnits()).toBe(12);
    expect(component.actionableCellCount()).toBe(3);
  });

  it('keeps overrides and checkmarks across filtering, sorting and switching the focused machine', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    type(component, 2, 20, '2');
    component.togglePicked(row(component, 1), 10);

    component.search = 'zzz';
    component.statusFilter = 'shortages';
    component.sortBy('mdbCode');
    component.focusMachine(20);
    component.focusMachine(10);
    component.search = '';
    component.statusFilter = 'all';

    expect(component.plannedQuantity(2, row(component, 2).machineQuantities[1])).toBe(2);
    expect(component.isPicked(1, 10)).toBe(true);
  });

  it('a phone shows the focused machine’s cards and its own progress beside the whole run', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    expect(component.focusedMachine()).toBe(10);
    expect(component.focusedProducts().map((p) => p.productId)).toEqual([1, 2]);
    component.focusMachine(20);
    expect(component.focusedProducts().map((p) => p.productId)).toEqual([1, 2, 3]);
    component.togglePicked(row(component, 2), 20);
    expect(component.machineProgressLabel(20)).toBe('1 of 1 pick completed');
    expect(component.runProgressLabel()).toBe('1 of 3 picks completed');
  });
});

describe('Plan picks site grouping (issue #996)', () => {
  const sited = () => [
    machine(10, 'Drinks', { siteId: 1 }),
    machine(20, 'Snacks', { siteId: 1 }),
    machine(30, 'Combo', { siteId: 2 }),
    machine(40, 'Local', { siteId: null })
  ];
  const sites = of([{ siteId: 1, name: 'Harbour Point', machineCount: 2 }, { siteId: 2, name: 'Pavilion', machineCount: 1 }]);

  it('groups the machine choices by site, with machines of no known site listed last under no site of their own', () => {
    const { component } = createHarness({ machines: sited(), sites });

    expect(component.machineOptions()).toEqual([
      { id: 10, label: 'Drinks', group: 'Harbour Point' },
      { id: 20, label: 'Snacks', group: 'Harbour Point' },
      { id: 30, label: 'Combo', group: 'Pavilion' },
      { id: 40, label: 'Local', group: 'Other machines' }
    ]);
  });

  it('puts machines at the same site in adjacent columns under one shared header, inventing no site', () => {
    const { component } = createHarness({ machines: sited(), sites });
    applyMachines(component, [10, 40, 30, 20]);

    expect(component.columnGroups()).toEqual([
      { siteId: 1, siteName: 'Harbour Point', machineIds: [10, 20] },
      { siteId: 2, siteName: 'Pavilion', machineIds: [30] },
      { siteId: null, siteName: null, machineIds: [40] }
    ]);
    expect(component.columnMachineIds()).toEqual([10, 20, 30, 40]);
  });

  it('keeps two different sites apart even when they share a name, and shows that name on each header', () => {
    const machines = [machine(10, 'Drinks', { siteId: 1 }), machine(20, 'Snacks', { siteId: 2 }), machine(30, 'Combo', { siteId: 1 })];
    const sameName = of([{ siteId: 1, name: 'Westfield', machineCount: 2 }, { siteId: 2, name: 'Westfield', machineCount: 1 }]);
    const { component } = createHarness({ machines, sites: sameName });
    applyMachines(component, [10, 20, 30]);

    expect(component.columnGroups()).toEqual([
      { siteId: 1, siteName: 'Westfield', machineIds: [10, 30] },
      { siteId: 2, siteName: 'Westfield', machineIds: [20] }
    ]);
    expect(component.siteHeadersShown).toBe(true);
  });

  it('treats a machine whose site the list does not know as having no known site', () => {
    const machines = [machine(10, 'Drinks', { siteId: 1 }), machine(20, 'Snacks', { siteId: 99 })];
    const { component } = createHarness({ machines, sites });
    applyMachines(component, [20, 10]);

    expect(component.columnGroups()).toEqual([
      { siteId: 1, siteName: 'Harbour Point', machineIds: [10] },
      { siteId: null, siteName: null, machineIds: [20] }
    ]);
  });

  it('lists machines flat when no machine has a known site, and never reads the site list for them', () => {
    const getList = jest.fn(() => of([]));
    const { component } = createHarness({ getSiteList: getList });

    expect(component.machineOptions()).toEqual([{ id: 10, label: 'Drinks' }, { id: 20, label: 'Snacks' }]);
    expect(component.siteHeadersShown).toBe(false);
    expect(getList).not.toHaveBeenCalled();
  });

  it('still lists every machine, ungrouped, when the site list cannot be read', () => {
    const { component } = createHarness({ machines: sited(), sites: throwError(() => new HttpErrorResponse({ status: 500 })) });

    expect(component.machineOptions().map((o) => o.id)).toEqual([10, 20, 30, 40]);
    expect(component.machineOptions().every((o) => o.group === undefined)).toBe(true);
  });
});

describe('Plan picks suggestions for Record refill (issue #996)', () => {
  it('offers the snapshot’s original suggestions keyed by local machine id, never a planned override', () => {
    const { component } = createHarness({
      machines: [machine(10, 'Drinks', { localMachineId: 5 }), machine(20, 'Snacks', { localMachineId: null })]
    });
    applyMachines(component, [10, 20]);
    type(component, 1, 10, '9');

    expect(component.refillSuggestions.get(refillCellKey(1, 5))).toBe(4);
    expect(component.refillSuggestions.get(refillCellKey(2, 5))).toBe(5);
    // Machine 20 has no local record, so nothing is suggested for it.
    expect([...component.refillSuggestions.keys()].every((key) => key.endsWith(':5'))).toBe(true);
  });
});

describe('Pick List departure guard (issue #996)', () => {
  it('lets the operator leave without asking when nothing would be lost', () => {
    const confirm = jest.spyOn(window, 'confirm');
    const { component } = createHarness();
    applyMachines(component, [10]);

    expect(component.confirmLeave()).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('asks before leaving planning work, and a cancelled departure keeps it', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10]);
    type(component, 1, 10, '2');

    expect(component.confirmLeave()).toBe(false);
    expect(confirm).toHaveBeenCalledWith(UNSAVED_WORK_LEAVE_WARNING);
    expect(component.isOverridden(1, 10)).toBe(true);
  });

  it('warns that leaving an in-flight or unconfirmed save loses its retry', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    component.onManualSaveInFlightChange(true);

    expect(component.leaveWarning()).toBe(UNRESOLVED_SAVE_LEAVE_WARNING);
  });

  it('asks the browser to confirm a reload or close only while there is work to lose', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    const quiet = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    component.onBeforeUnload(quiet);
    expect(quiet.defaultPrevented).toBe(false);

    component.togglePicked(row(component, 1), 10);
    const guarded = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    component.onBeforeUnload(guarded);
    expect(guarded.defaultPrevented).toBe(true);
  });

  it('the route guard defers to the page', () => {
    const run = (answer: boolean) =>
      confirmPickListLeave({ confirmLeave: () => answer }, undefined as never, undefined as never, undefined as never);

    expect(run(true)).toBe(true);
    expect(run(false)).toBe(false);
  });
});

describe('Plan picks rendered (issues #996, #1008)', () => {
  async function render(compact = false) {
    jest.useRealTimers();
    window.matchMedia = jest.fn((query: string) => ({
      matches: compact,
      media: query,
      addEventListener: jest.fn(),
      removeEventListener: jest.fn()
    })) as unknown as typeof window.matchMedia;
    await TestBed.configureTestingModule({
      imports: [PickListComponent],
      providers: [
        provideRouter([]),
        { provide: MachineService, useValue: { getAll: jest.fn(() => of([machine(10, 'Drinks'), machine(20, 'Snacks')])) } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of([product(1, 'Coke'), product(2, 'Chips'), product(3, 'Water')])) } },
        { provide: PickListService, useValue: { get: jest.fn(() => of(snapshot())) } },
        { provide: SiteService, useValue: { getList: jest.fn(() => of([])) } },
        { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of([])) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: { active$: of(false) } }
      ]
    }).compileComponents();
    TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');
    const fixture = TestBed.createComponent(PickListComponent);
    fixture.detectChanges();
    fixture.componentInstance.stagedMachineIds = [10, 20];
    fixture.componentInstance.applyFilters();
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    delete (window as { matchMedia?: unknown }).matchMedia;
  });

  /** The always-visible quantity box of one cell, found by the label that names its product and machine. */
  function quantityBox(host: HTMLElement, product: string, machineName: string): HTMLInputElement {
    return host.querySelector<HTMLInputElement>(`input[aria-label="Pick quantity: ${product} at ${machineName}"]`)!;
  }

  function typeInto(fixture: { detectChanges(): void }, input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('shows minus, the quantity box, plus and the completion checkbox in that order, each labelled by product and machine', async () => {
    const { host } = await render();
    const cell = quantityBox(host, 'Coke', 'Drinks').closest('[data-testid="pick-cell"]')!;

    const controls = Array.from(cell.querySelectorAll('button, input')).map((el) => el.getAttribute('aria-label') ?? el.getAttribute('data-testid'));
    expect(controls).toEqual([
      'Decrease pick quantity: Coke at Drinks',
      'Pick quantity: Coke at Drinks',
      'Increase pick quantity: Coke at Drinks',
      'pick-cell-checkbox'
    ]);
    expect(quantityBox(host, 'Coke', 'Drinks').getAttribute('inputmode')).toBe('numeric');
    // No pencil, confirm or cancel survives the redesign.
    expect(host.querySelector('[data-testid="pick-cell-editor"]')).toBeNull();
    expect(host.querySelector('button[aria-label^="Edit pick quantity"]')).toBeNull();
    expect(host.querySelector('button[aria-label^="Confirm pick quantity"]')).toBeNull();
  });

  it('typing a quantity updates the plan with no confirm click, showing the suggestion beside it', async () => {
    const { fixture, host } = await render();

    typeInto(fixture, quantityBox(host, 'Coke', 'Drinks'), '6');

    const cell = quantityBox(host, 'Coke', 'Drinks').closest('[data-testid="pick-cell"]')!;
    expect(cell.querySelector('[data-testid="pick-cell-suggested"]')?.textContent?.trim()).toBe('Suggested: 4');
    expect(cell.textContent).toContain('Current: 6 / Target: 10');
    expect(host.querySelector('[data-testid="pick-list-total-units"]')?.textContent).toContain('14 units');

    typeInto(fixture, quantityBox(host, 'Coke', 'Drinks'), '4');
    expect(host.querySelector('[data-testid="pick-cell-suggested"]')).toBeNull();
  });

  it('the steppers adjust by one and stop at zero', async () => {
    const { fixture, host } = await render();
    const minus = host.querySelector<HTMLButtonElement>('button[aria-label="Decrease pick quantity: Coke at Drinks"]')!;
    const plus = host.querySelector<HTMLButtonElement>('button[aria-label="Increase pick quantity: Coke at Drinks"]')!;

    plus.click();
    fixture.detectChanges();
    expect(quantityBox(host, 'Coke', 'Drinks').value).toBe('5');

    for (let i = 0; i < 5; i++) {
      minus.click();
      fixture.detectChanges();
    }
    expect(quantityBox(host, 'Coke', 'Drinks').value).toBe('0');
    expect(minus.disabled).toBe(true);
  });

  it('refuses text the plan cannot use with an accessible message, names the quantity still used, and locks the cell', async () => {
    const { fixture, host } = await render();
    const input = quantityBox(host, 'Coke', 'Drinks');

    typeInto(fixture, input, '2.5');

    const error = host.querySelector('[data-testid="pick-cell-error"]')!;
    expect(error.getAttribute('role')).toBe('alert');
    expect(error.textContent).toContain(PICK_QUANTITY_INVALID_MESSAGE);
    expect(error.textContent).toContain('Using 4');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe(error.id);
    expect(input.value).toBe('2.5');
    // Neither stepper nor the completion checkbox may act on a cell the plan cannot read.
    expect(host.querySelector<HTMLButtonElement>('button[aria-label="Decrease pick quantity: Coke at Drinks"]')!.disabled).toBe(true);
    expect(host.querySelector<HTMLButtonElement>('button[aria-label="Increase pick quantity: Coke at Drinks"]')!.disabled).toBe(true);
    const checkbox = input.closest('[data-testid="pick-cell"]')!.querySelector<HTMLInputElement>('[data-testid="pick-cell-checkbox"]')!;
    expect(checkbox.disabled).toBe(true);
    expect(host.querySelector('[data-testid="pick-list-total-units"]')?.textContent).toContain('12 units');

    typeInto(fixture, input, '3');
    expect(host.querySelector('[data-testid="pick-cell-error"]')).toBeNull();
    expect(host.querySelector('[data-testid="pick-list-total-units"]')?.textContent).toContain('11 units');
  });

  it('never leaves an invalid cell looking complete', async () => {
    const { fixture, host } = await render();
    const input = quantityBox(host, 'Coke', 'Drinks');
    const checkbox = input.closest('[data-testid="pick-cell"]')!.querySelector<HTMLInputElement>('[data-testid="pick-cell-checkbox"]')!;
    checkbox.click();
    fixture.detectChanges();
    expect(checkbox.checked).toBe(true);

    typeInto(fixture, input, '');

    expect(input.closest('[data-testid="pick-cell"]')!.querySelector<HTMLInputElement>('[data-testid="pick-cell-checkbox"]')!.checked).toBe(false);
    expect(host.querySelector('[data-testid="pick-list-progress-count"]')?.textContent).toBe('0 of 3 picks completed');
  });

  it('shows the whole-run summary and the shortage, and Refresh is a separate action from Apply', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="pick-list-progress-count"]')?.textContent).toBe('0 of 3 picks completed');
    expect(host.querySelector('[data-testid="pick-list-total-units"]')?.textContent).toContain('12 units');
    expect(host.querySelector('[data-testid="pick-list-shortage-count"]')?.textContent).toContain('1 short in storage');
    expect(host.querySelector('[data-testid="pick-list-refresh"]')?.textContent?.trim()).toBe('Refresh');
    expect(host.textContent).toContain('Only remaining (2)');
    expect(host.textContent).toContain('Only shortages (1)');
  });

  it('on a phone shows product cards for one focused machine instead of the matrix, keeping Current/Target on a completed card', async () => {
    const { fixture, host } = await render(true);

    expect(host.querySelector('table')).toBeNull();
    expect(host.querySelectorAll('[data-testid="pick-list-card"]')).toHaveLength(2);
    host.querySelector<HTMLInputElement>('[data-testid="pick-cell-checkbox"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="pick-list-card"]')?.textContent).toContain('Current: 6 / Target: 10');

    const snacks = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.includes('Snacks'))!;
    snacks.click();
    fixture.detectChanges();

    expect(host.querySelectorAll('[data-testid="pick-list-card"]')).toHaveLength(3);
    expect(host.querySelector('[data-testid="pick-list-mobile-summary"]')?.textContent).toContain('Whole run: 1 of 3 picks completed');
    expect(fixture.componentInstance.isPicked(1, 10)).toBe(true);
  });

  it('offers the identical minus, box, plus and checkbox on a phone card, with Current/Target beneath', async () => {
    const { fixture, host } = await render(true);
    const card = host.querySelector('[data-testid="pick-list-card"]')!;

    expect(card.querySelector<HTMLInputElement>('input[aria-label="Pick quantity: Coke at Drinks"]')!.value).toBe('4');

    card.querySelector<HTMLButtonElement>('button[aria-label="Increase pick quantity: Coke at Drinks"]')!.click();
    fixture.detectChanges();

    expect(card.querySelector<HTMLInputElement>('input[aria-label="Pick quantity: Coke at Drinks"]')!.value).toBe('5');
    expect(card.querySelector('[data-testid="pick-cell-checkbox"]')).not.toBeNull();
    expect(card.textContent).toContain('Current: 6 / Target: 10');
  });
});
