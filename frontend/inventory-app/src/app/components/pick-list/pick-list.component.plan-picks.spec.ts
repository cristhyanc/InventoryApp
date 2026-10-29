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

/** Edits one cell and commits the typed text, as the pencil, the box and Enter do. */
function commit(component: PickListComponent, productId: number, machineId: number, value: string): void {
  component.startEdit(row(component, productId), machineId);
  component.onEditInput(value);
  component.commitEdit(row(component, productId), machineId);
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
    commit(component, 1, 10, '2');
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
    commit(component, 1, 10, '2');
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

describe('Plan picks quantity overrides (issue #996)', () => {
  it('commits an edited quantity as a local override and keeps the original suggestion beside it', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    commit(component, 1, 10, '7');

    const cell = row(component, 1).machineQuantities[0];
    expect(component.plannedQuantity(1, cell)).toBe(7);
    expect(cell.quantityToPick).toBe(4);
    expect(component.isOverridden(1, 10)).toBe(true);
    expect(component.edit).toBeNull();
  });

  it('starts the box from the committed quantity and focuses and selects it', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    const input = document.createElement('input');
    input.id = component.editInputId(1, 10);
    document.body.appendChild(input);
    const select = jest.spyOn(input, 'select');

    component.startEdit(row(component, 1), 10);
    jest.runAllTimers();

    expect(component.edit?.value).toBe('4');
    expect(document.activeElement).toBe(input);
    expect(select).toHaveBeenCalled();
    input.remove();
  });

  it('cancel restores the committed quantity', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    commit(component, 1, 10, '6');

    component.startEdit(row(component, 1), 10);
    component.onEditInput('99');
    component.cancelEdit();

    expect(component.plannedQuantity(1, row(component, 1).machineQuantities[0])).toBe(6);
    expect(component.edit).toBeNull();
  });

  it.each(['abc', '2.5', '-1', '', String(MAXIMUM_PICK_QUANTITY + 1)])('refuses %p inline and keeps the box open', (raw) => {
    const { component } = createHarness();
    applyMachines(component, [10]);

    component.startEdit(row(component, 1), 10);
    component.onEditInput(raw);
    component.commitEdit(row(component, 1), 10);

    expect(component.edit?.error).toBeTruthy();
    expect(component.isEditing(1, 10)).toBe(true);
    expect(component.isOverridden(1, 10)).toBe(false);
  });

  it('committing the suggestion again drops the override', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    commit(component, 1, 10, '7');

    commit(component, 1, 10, '4');

    expect(component.isOverridden(1, 10)).toBe(false);
  });

  it('Use suggested puts a cell back on its suggestion', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    commit(component, 1, 10, '0');

    component.useSuggestion(row(component, 1), 10);

    expect(component.isOverridden(1, 10)).toBe(false);
    expect(component.actionableCellCount()).toBe(2);
  });

  it('never edits a cell with nothing suggested', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);

    component.startEdit(row(component, 1), 20);

    expect(component.edit).toBeNull();
  });

  it('committing a changed quantity clears the cell’s completion; an unchanged commit keeps it', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.togglePicked(row(component, 1), 10);

    commit(component, 1, 10, '4');
    expect(component.isPicked(1, 10)).toBe(true);

    commit(component, 1, 10, '3');
    expect(component.isPicked(1, 10)).toBe(false);
  });

  it('totals, shortages and progress use committed overrides across the whole applied run', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    // Coke 4, Chips 5 + 3 = 8 against 7 in storage.
    expect(component.totalToPickUnits()).toBe(12);
    expect(component.shortageProducts().map((p) => p.productId)).toEqual([2]);
    expect(component.productShortage(row(component, 2))).toBe(1);

    commit(component, 2, 20, '1');

    expect(component.totalToPickUnits()).toBe(10);
    expect(component.shortageProducts()).toEqual([]);

    commit(component, 1, 10, '9');

    expect(component.productShortage(row(component, 1))).toBe(3);
    expect(component.runProgressLabel()).toBe('0 of 3 picks completed');
    component.togglePicked(row(component, 1), 10);
    expect(component.runProgressLabel()).toBe('1 of 3 picks completed');
    expect(component.remainingPickCount()).toBe(2);
  });

  it('a committed zero is a deliberate skip: not a pick, not checkable, still editable and resettable', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.togglePicked(row(component, 1), 10);

    commit(component, 1, 10, '0');

    expect(component.isPicked(1, 10)).toBe(false);
    expect(component.actionableCellCount()).toBe(1);
    component.togglePicked(row(component, 1), 10);
    expect(component.isPicked(1, 10)).toBe(false);
    expect(component.totalToPickUnits()).toBe(5);

    component.startEdit(row(component, 1), 10);
    expect(component.edit?.value).toBe('0');
    component.cancelEdit();
    component.useSuggestion(row(component, 1), 10);
    expect(component.actionableCellCount()).toBe(2);
  });

  it('touch steppers change the box by one and never go below zero', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    component.startEdit(row(component, 1), 10);

    component.stepEdit(1);
    expect(component.edit?.value).toBe('5');
    component.onEditInput('0');
    component.stepEdit(-1);
    expect(component.edit?.value).toBe('0');
  });

  it('never calls anything but the read-only projection, whatever is planned', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    commit(component, 1, 10, '2');
    component.togglePicked(row(component, 2), 20);
    component.refresh();
    component.resetAll();

    expect(getPickList.mock.calls.every((call) => Array.isArray(call[0]))).toBe(true);
  });
});

/**
 * Text typed into an open quantity editor is planning work too, until it is committed or cancelled:
 * every transition that would throw it away asks first, exactly as it does for a committed override.
 * An editor that was opened but not changed, or that was explicitly cancelled, loses nothing and
 * never asks.
 */
describe('Plan picks guards an open, changed quantity editor (issue #996)', () => {
  /** Opens the Chips cell at machine 20 (suggestion 3) and types a different, uncommitted quantity. */
  function typeWithoutCommitting(component: PickListComponent, value = '1'): void {
    component.startEdit(row(component, 2), 20);
    component.onEditInput(value);
  }

  it('an editor that was opened but not changed is not planning work', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    component.startEdit(row(component, 2), 20);

    expect(component.editIsDirty()).toBe(false);
    expect(component.hasPlanningWork()).toBe(false);
    expect(component.leaveWarning()).toBeNull();
  });

  it('typing back the quantity the editor opened with is not a change', () => {
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component, '7');
    component.onEditInput(' 3 ');

    expect(component.editIsDirty()).toBe(false);
  });

  it('changed, uncommitted text is planning work; an explicit Cancel discards it without any warning', () => {
    const confirm = jest.spyOn(window, 'confirm');
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);

    expect(component.hasPlanningWork()).toBe(true);

    component.cancelEdit();

    expect(component.hasPlanningWork()).toBe(false);
    expect(component.confirmLeave()).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('asks before Refresh discards the typed text; Cancel keeps the editor and its text and reads nothing', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);
    getPickList.mockClear();

    component.refresh();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLAN_ON_REFRESH_CONFIRMATION);
    expect(getPickList).not.toHaveBeenCalled();
    expect(component.isEditing(2, 20)).toBe(true);
    expect(component.edit?.value).toBe('1');
  });

  it('a confirmed Refresh discards the typed text and closes the editor', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);

    component.refresh();

    expect(getPickList).toHaveBeenCalledTimes(2);
    expect(component.edit).toBeNull();
    expect(component.isOverridden(2, 20)).toBe(false);
  });

  it('Refresh with an untouched editor open asks nothing', () => {
    const confirm = jest.spyOn(window, 'confirm');
    const { component, getPickList } = createHarness();
    applyMachines(component, [10, 20]);
    component.startEdit(row(component, 2), 20);

    component.refresh();

    expect(confirm).not.toHaveBeenCalled();
    expect(getPickList).toHaveBeenCalledTimes(2);
  });

  it('asks before Reset discards the typed text; Cancel keeps it, and confirming clears it', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);

    component.resetAll();

    expect(confirm).toHaveBeenCalledWith(DISCARD_PLAN_ON_RESET_CONFIRMATION);
    expect(component.edit?.value).toBe('1');
    expect(component.appliedMachineIds).toEqual([10, 20]);

    confirm.mockReturnValue(true);
    component.resetAll();

    expect(component.edit).toBeNull();
    expect(component.appliedMachineIds).toEqual([]);
  });

  it('asks before removing the machine whose cell holds typed text, by name; Cancel keeps it', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);

    component.removeMachine(20);

    expect(confirm).toHaveBeenCalledWith(discardPlanMachineConfirmation('Snacks'));
    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.edit?.value).toBe('1');

    confirm.mockReturnValue(true);
    component.removeMachine(20);

    expect(component.appliedMachineIds).toEqual([10]);
    expect(component.edit).toBeNull();
  });

  it('removing a different machine neither asks nor touches the typed text', () => {
    const confirm = jest.spyOn(window, 'confirm');
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);

    component.removeMachine(10);

    expect(confirm).not.toHaveBeenCalled();
    expect(component.appliedMachineIds).toEqual([20]);
    expect(component.edit?.value).toBe('1');
  });

  it('the same check applies when Apply removes the machine', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);

    applyMachines(component, [10]);

    expect(confirm).toHaveBeenCalledWith(discardPlanMachineConfirmation('Snacks'));
    expect(component.appliedMachineIds).toEqual([10, 20]);
    expect(component.edit?.value).toBe('1');
  });

  it('guards leaving the page and reloading or closing the tab, and a cancelled departure keeps the text', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();
    applyMachines(component, [10, 20]);
    typeWithoutCommitting(component);

    expect(component.confirmLeave()).toBe(false);
    expect(confirm).toHaveBeenCalledWith(UNSAVED_WORK_LEAVE_WARNING);
    expect(component.edit?.value).toBe('1');

    const unload = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    component.onBeforeUnload(unload);
    expect(unload.defaultPrevented).toBe(true);
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
    commit(component, 2, 20, '2');
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
    commit(component, 1, 10, '9');

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
    commit(component, 1, 10, '2');

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

describe('Plan picks rendered (issue #996)', () => {
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

  it('edits a pick from its pencil: Enter commits, and the cell shows the suggestion beside the new quantity', async () => {
    const { fixture, host } = await render();
    host.querySelector<HTMLButtonElement>('button[aria-label="Edit pick quantity: Coke at Drinks"]')!.click();
    fixture.detectChanges();

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Pick quantity: Coke at Drinks"]')!;
    input.value = '6';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();

    const cell = host.querySelector('button[aria-label="Edit pick quantity: Coke at Drinks"]')!.closest('[data-testid="pick-cell"]')!;
    expect(cell.querySelector('[data-testid="pick-cell-quantity"]')?.textContent?.trim()).toBe('Pick 6');
    expect(cell.querySelector('[data-testid="pick-cell-suggested"]')?.textContent?.trim()).toBe('Suggested: 4');
    expect(cell.textContent).toContain('Current: 6 / Target: 10');
  });

  it('Escape abandons an edit, and a malformed value is refused with an accessible inline message', async () => {
    const { fixture, host } = await render();
    host.querySelector<HTMLButtonElement>('button[aria-label="Edit pick quantity: Coke at Drinks"]')!.click();
    fixture.detectChanges();
    const input = host.querySelector<HTMLInputElement>('input[aria-label="Pick quantity: Coke at Drinks"]')!;
    input.value = '2.5';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();

    const error = host.querySelector('[data-testid="pick-cell-error"]')!;
    expect(error.getAttribute('role')).toBe('alert');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe(error.id);

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="pick-cell-editor"]')).toBeNull();
    expect(host.textContent).toContain('Pick 4');
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
});
