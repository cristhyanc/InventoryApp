import { HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import {
  Category,
  LocalMachine,
  MultiMachinePickListRefillRequest,
  MultiMachinePickListRefillResponse,
  PickListRefillRequest,
  PickListRefillResponse,
  Product
} from '../../models/models';
import { LocalMachineService } from '../../services/local-machine.service';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  DISCARD_MACHINE_COLUMNS_CONFIRMATION,
  DISCARD_UNATTRIBUTED_ENTRIES_CONFIRMATION,
  INVALID_TAKEN_QUANTITY_MESSAGE,
  MACHINE_ATTRIBUTION_FAILED_MESSAGE,
  MACHINE_RESOLUTION_PENDING_MESSAGE,
  MACHINES_LOAD_FAILED_MESSAGE,
  MAXIMUM_TAKEN_QUANTITY,
  REFILL_CONFLICT_MESSAGE,
  REFILL_FORBIDDEN_MESSAGE,
  REFILL_RECORDED_MESSAGE,
  REFILL_UNCERTAIN_MESSAGE,
  TAKEN_QUANTITY_TOO_LARGE_MESSAGE,
  ManualPickListComponent,
  discardMachineColumnConfirmation,
  multiMachineRefillRecordedMessage
} from './manual-pick-list.component';

function supportSessionStub(active: Observable<boolean>): SupportSessionService {
  return { active$: active } as unknown as SupportSessionService;
}

function category(id: number, name: string): Category {
  return { id, name };
}

function localMachine(overrides: Partial<LocalMachine>): LocalMachine {
  return { id: 1, name: 'Machine', isActive: true, isProviderNamed: false, ...overrides };
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

function recordedResponse(request: PickListRefillRequest): PickListRefillResponse {
  return {
    idempotencyKey: request.idempotencyKey,
    replayed: false,
    machineId: request.machineId ?? null,
    notes: request.notes ?? null,
    recordedAtUtc: '2026-01-01T00:00:00Z',
    rows: request.rows.map((row) => ({ ...row, stockAdjustmentId: row.productId * 100 }))
  };
}

function batchResponse(request: MultiMachinePickListRefillRequest): MultiMachinePickListRefillResponse {
  return {
    idempotencyKey: request.idempotencyKey,
    replayed: false,
    notes: request.notes ?? null,
    recordedAtUtc: '2026-01-01T00:00:00Z',
    allocations: request.allocations.map((allocation) => ({ ...allocation, stockAdjustmentId: allocation.productId * 100 }))
  };
}

function validationError(errors: Record<string, string[]>): HttpErrorResponse {
  return new HttpErrorResponse({ status: 400, error: { title: 'Request validation failed', status: 400, errors } });
}

interface Harness {
  component: ManualPickListComponent;
  recordRefill: jest.Mock;
  recordMultiMachineRefill: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
  getAllMachines: jest.Mock;
}

function createHarness(
  products: Product[] = [product({ id: 1, name: 'Coke', quantityInStock: 20 }), product({ id: 2, name: 'Chips', quantityInStock: 5 })],
  recordRefill: jest.Mock = jest.fn((request: PickListRefillRequest) => of(recordedResponse(request))),
  sessionActive: Observable<boolean> = of(false),
  getAllMachines: jest.Mock = jest.fn(() => of([])),
  preselectedMachineId: number | null = null,
  recordMultiMachineRefill: jest.Mock = jest.fn((request: MultiMachinePickListRefillRequest) => of(batchResponse(request)))
): Harness {
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
  const pickListService = { recordRefill, recordMultiMachineRefill } as unknown as PickListService;
  const localMachineService = { getAll: getAllMachines } as unknown as LocalMachineService;

  const component = new ManualPickListComponent(
    pickListService,
    toast as unknown as ToastService,
    supportSessionStub(sessionActive),
    localMachineService
  );
  component.products = products;
  component.preselectedMachineId = preselectedMachineId;
  component.ngOnInit();

  return { component, recordRefill, recordMultiMachineRefill, toast, getAllMachines };
}

/** The two machines every matrix test selects columns from. */
function twoMachines(): jest.Mock {
  return jest.fn(() => of([localMachine({ id: 5, name: 'Front Lobby' }), localMachine({ id: 6, name: 'Back Room' })]));
}

/** Answers the next `window.confirm` and reports what it was asked, so a discard is never silent. */
function stubConfirm(answer: boolean): jest.SpyInstance<boolean, [message?: string]> {
  return jest.spyOn(window, 'confirm').mockImplementation(() => answer);
}

afterEach(() => jest.restoreAllMocks());

describe('ManualPickListComponent filtering', () => {
  it('derives category options from the products it was given, deduplicated and sorted by name', () => {
    const { component } = createHarness([
      product({ id: 1, name: 'Coke', category: category(2, 'Drinks') }),
      product({ id: 2, name: 'Sprite', category: category(2, 'Drinks') }),
      product({ id: 3, name: 'Chips', category: category(1, 'Snacks') })
    ]);

    expect(component.categories().map((c) => c.name)).toEqual(['Drinks', 'Snacks']);
  });

  it('shows every product when no filter is set', () => {
    const { component } = createHarness();

    expect(component.visibleProducts().map((p) => p.id)).toEqual([1, 2]);
  });

  it('filters by a case-insensitive name search', () => {
    const { component } = createHarness();
    component.search = 'cok';

    expect(component.visibleProducts().map((p) => p.id)).toEqual([1]);
  });

  it('filters by category', () => {
    const { component } = createHarness([
      product({ id: 1, name: 'Coke', category: category(2, 'Drinks') }),
      product({ id: 2, name: 'Chips', category: category(1, 'Snacks') })
    ]);
    component.categoryId = 1;

    expect(component.visibleProducts().map((p) => p.id)).toEqual([2]);
  });
});

describe('ManualPickListComponent machine selection (issues #794, #904)', () => {
  it('offers every active local machine as a selectable column, and starts with none selected', () => {
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());

    expect(component.machineSelectorShown).toBe(true);
    expect(component.machineOptions().map((option) => option.label)).toEqual(['Front Lobby', 'Back Room']);
    expect(component.selectedMachineIds).toEqual([]);
    expect(component.machineColumns).toEqual([]);
  });

  it('gives each selected machine its own clearly labelled quantity column, in selection order', () => {
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());

    component.onSelectedMachineIdsChange([6, 5]);

    expect(component.machineColumns.map((machine) => machine.name)).toEqual(['Back Room', 'Front Lobby']);
    expect(component.cellAriaLabel(component.products[0], component.machineColumns[0])).toBe('Taken: Coke at Back Room');
  });

  it('hides the selector when the business has no active local machines, and saves unattributed', () => {
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, jest.fn(() => of([])));

    expect(component.machineSelectorShown).toBe(false);

    component.onTakenChange(1, '3');
    component.save();

    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBeUndefined();
  });

  it('does not offer a deactivated machine as a column', () => {
    const getAllMachines = jest.fn(() =>
      of([localMachine({ id: 5, name: 'Front Lobby', isActive: true }), localMachine({ id: 6, name: 'Back Room', isActive: false })])
    );
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines);

    expect(component.machineSelectorShown).toBe(true);
    expect(component.activeMachines.map((m) => m.id)).toEqual([5]);

    // A machine the business no longer offers cannot become a column even if something names it.
    component.onSelectedMachineIdsChange([5, 6]);
    expect(component.machineColumns.map((m) => m.id)).toEqual([5]);
  });

  it('shows an explicit message and still allows saving without a machine when the local machine read fails', () => {
    const getAllMachines = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines);

    expect(component.machinesLoadFailed).toBe(true);
    expect(component.machineSelectorShown).toBe(false);
    expect(component.machinesLoadFailedMessage).toBe(MACHINES_LOAD_FAILED_MESSAGE);

    component.onTakenChange(1, '3');
    expect(component.canSave).toBe(true);
    component.save();

    expect(recordRefill).toHaveBeenCalledTimes(1);
    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBeUndefined();
  });
});

describe('ManualPickListComponent matrix entry and the one batch save (issue #904)', () => {
  it('sends one batch request naming each cell’s own machine, never one request per machine', () => {
    const { component, recordRefill, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);

    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');
    component.onCellChange(2, 6, '2');

    component.save();

    expect(recordMultiMachineRefill).toHaveBeenCalledTimes(1);
    expect(recordRefill).not.toHaveBeenCalled();
    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([
      { machineId: 5, productId: 1, quantityTaken: 3 },
      { machineId: 6, productId: 1, quantityTaken: 4 },
      { machineId: 6, productId: 2, quantityTaken: 2 }
    ]);
    expect(request.idempotencyKey.length).toBeGreaterThan(0);
  });

  it('sends nothing for a blank or explicitly zero cell', () => {
    const { component, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);

    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '0');
    component.onCellChange(2, 5, '');

    component.save();

    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([{ machineId: 5, productId: 1, quantityTaken: 3 }]);
    expect(component.cellError(1, 6)).toBeNull();
  });

  it('totals each row across the selected machines, and the whole pick including rows a filter hides', () => {
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');
    component.onCellChange(2, 5, '5');

    expect(component.rowTotal(1)).toBe(7);
    expect(component.rowTotal(2)).toBe(5);
    expect(component.totalUnitsRequired()).toBe(12);

    // Chips is now hidden, but the storage its entry needs is still part of the pick.
    component.search = 'coke';
    expect(component.visibleProducts().map((p) => p.id)).toEqual([1]);
    expect(component.totalUnitsRequired()).toBe(12);
    expect(component.hiddenUnitsRequired()).toBe(5);
    expect(component.hiddenProductsWithEntries()).toBe(1);
  });

  it('keeps every typed cell when the search or category filter changes', () => {
    const { component, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(2, 6, '2');

    component.search = 'coke';
    expect(component.cellValue(2, 6)).toBe('2');
    component.search = '';
    component.categoryId = 99;
    component.save();

    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([
      { machineId: 5, productId: 1, quantityTaken: 3 },
      { machineId: 6, productId: 2, quantityTaken: 2 }
    ]);
  });

  it('reports the whole batch as saved, clears the cells and refreshes storage once', () => {
    const { component, toast } = createHarness(undefined, undefined, undefined, twoMachines());
    const refreshed = jest.fn();
    component.refillRecorded.subscribe(refreshed);
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');

    component.save();

    expect(toast.success).toHaveBeenCalledWith(multiMachineRefillRecordedMessage(2, 2));
    expect(component.cellValue(1, 5)).toBe('');
    expect(component.cellValue(1, 6)).toBe('');
    expect(refreshed).toHaveBeenCalledTimes(1);
    // The machines stay selected: the columns are the operator's run, not the save's leftovers.
    expect(component.selectedMachineIds).toEqual([5, 6]);
  });
});

describe('ManualPickListComponent per-cell validation (issue #904)', () => {
  it('blocks Save with a message naming the product and the machine of a malformed cell', () => {
    const { component, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(2, 6, 'abc');

    expect(component.cellError(2, 6)).toBe(INVALID_TAKEN_QUANTITY_MESSAGE);
    expect(component.cellError(1, 5)).toBeNull();
    expect(component.invalidEntryMessages).toEqual([`Chips at Back Room: ${INVALID_TAKEN_QUANTITY_MESSAGE}`]);
    expect(component.canSave).toBe(false);

    component.save();
    expect(recordMultiMachineRefill).not.toHaveBeenCalled();
  });

  it.each([
    ['a negative quantity', '-2'],
    ['a fractional quantity', '1.5'],
    ['a nonnumeric quantity', 'two']
  ])('refuses %s in a cell', (_description, typed) => {
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5]);

    component.onCellChange(1, 5, typed);

    expect(component.cellError(1, 5)).toBe(INVALID_TAKEN_QUANTITY_MESSAGE);
    expect(component.canSave).toBe(false);
  });

  it('refuses a quantity larger than the server can record, rather than sending it', () => {
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5]);

    component.onCellChange(1, 5, `${MAXIMUM_TAKEN_QUANTITY + 1}`);

    expect(component.cellError(1, 5)).toBe(TAKEN_QUANTITY_TOO_LARGE_MESSAGE);
    expect(component.canSave).toBe(false);

    component.onCellChange(1, 5, `${MAXIMUM_TAKEN_QUANTITY}`);
    expect(component.cellError(1, 5)).toBeNull();
  });

  it('blocks Save for a malformed cell on a row the current filter hides', () => {
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5]);
    component.onCellChange(2, 5, 'abc');
    component.onCellChange(1, 5, '3');

    component.search = 'coke';

    expect(component.visibleProducts().map((p) => p.id)).toEqual([1]);
    expect(component.canSave).toBe(false);
    expect(component.invalidEntryMessages).toEqual([`Chips at Front Lobby: ${INVALID_TAKEN_QUANTITY_MESSAGE}`]);
  });

  it('puts each per-allocation refusal from a 400 back on its own cell', () => {
    const recordMultiMachineRefill = jest.fn((_request: MultiMachinePickListRefillRequest) =>
      throwError(() =>
        validationError({
          'allocations[1]': ['The allocations for this product take 12 units, but only 10 are in stock.'],
          notes: ['A note may be at most 500 characters.']
        })
      )
    );
    const { component } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '6');
    component.onCellChange(1, 6, '6');

    component.save();

    expect(component.cellError(1, 6)).toContain('take 12 units, but only 10 are in stock');
    expect(component.cellError(1, 5)).toBeNull();
    expect(component.requestErrors).toEqual(['A note may be at most 500 characters.']);
    // A definitive refusal recorded nothing, so the drafts stay editable.
    expect(component.cellValue(1, 5)).toBe('6');
    expect(component.unresolvedSubmission).toBe(false);
  });
});

describe('ManualPickListComponent switching between No machine and machine columns (issue #904)', () => {
  it('asks before discarding typed No machine quantities, and never copies them into every machine', () => {
    const confirmed = stubConfirm(true);
    const { component, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onTakenChange(1, '3');

    component.onSelectedMachineIdsChange([5, 6]);

    expect(confirmed).toHaveBeenCalledWith(DISCARD_UNATTRIBUTED_ENTRIES_CONFIRMATION);
    expect(component.selectedMachineIds).toEqual([5, 6]);
    expect(component.takenValue(1)).toBe('');
    expect(component.cellValue(1, 5)).toBe('');
    expect(component.cellValue(1, 6)).toBe('');
    expect(component.canSave).toBe(false);

    component.onCellChange(1, 5, '3');
    component.save();
    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([{ machineId: 5, productId: 1, quantityTaken: 3 }]);
  });

  it('keeps everything when the discard of typed No machine quantities is cancelled', () => {
    stubConfirm(false);
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onTakenChange(1, '3');

    component.onSelectedMachineIdsChange([5]);

    expect(component.selectedMachineIds).toEqual([]);
    expect(component.takenValue(1)).toBe('3');

    component.save();
    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.rows).toEqual([{ productId: 1, quantityTaken: 3 }]);
  });

  it('switches into machine columns without asking when nothing is typed', () => {
    const confirmed = stubConfirm(true);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());

    component.onSelectedMachineIdsChange([5]);

    expect(confirmed).not.toHaveBeenCalled();
    expect(component.selectedMachineIds).toEqual([5]);
  });

  it('asks before clearing the machine selection while cells hold quantities, and Cancel keeps the matrix', () => {
    const cancelled = stubConfirm(false);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');

    component.onSelectedMachineIdsChange([]);

    expect(cancelled).toHaveBeenCalledWith(DISCARD_MACHINE_COLUMNS_CONFIRMATION);
    expect(component.selectedMachineIds).toEqual([5, 6]);
    expect(component.cellValue(1, 5)).toBe('3');
  });

  it('discards the matrix entries once clearing the selection is confirmed', () => {
    stubConfirm(true);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');

    component.onSelectedMachineIdsChange([]);

    expect(component.selectedMachineIds).toEqual([]);
    expect(component.cellValue(1, 5)).toBe('');
    expect(component.takenValue(1)).toBe('');
  });

  it('asks before removing one machine column that holds quantities, and Cancel keeps everything', () => {
    const cancelled = stubConfirm(false);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');

    component.removeMachineColumn(6);

    expect(cancelled).toHaveBeenCalledWith(discardMachineColumnConfirmation('Back Room'));
    expect(component.selectedMachineIds).toEqual([5, 6]);
    expect(component.cellValue(1, 6)).toBe('4');
  });

  it('drops only the removed column’s quantities once the removal is confirmed', () => {
    stubConfirm(true);
    const { component, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');

    component.removeMachineColumn(6);

    expect(component.selectedMachineIds).toEqual([5]);
    expect(component.cellValue(1, 5)).toBe('3');
    expect(component.cellValue(1, 6)).toBe('');

    component.save();
    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([{ machineId: 5, productId: 1, quantityTaken: 3 }]);
  });

  it('removes an empty machine column without asking', () => {
    const confirmed = stubConfirm(true);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');

    component.removeMachineColumn(6);

    expect(confirmed).not.toHaveBeenCalled();
    expect(component.selectedMachineIds).toEqual([5]);
    expect(component.cellValue(1, 5)).toBe('3');
  });

  it('re-adding a machine column brings back an empty column, never the discarded quantities', () => {
    stubConfirm(true);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines());
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 6, '4');
    component.removeMachineColumn(6);

    component.onSelectedMachineIdsChange([5, 6]);

    expect(component.cellValue(1, 6)).toBe('');
  });
});

describe('ManualPickListComponent while a batch save is in flight (issue #904)', () => {
  it('blocks selection changes, column removal and cell edits until the save lands', () => {
    const pending = new Subject<MultiMachinePickListRefillResponse>();
    const recordMultiMachineRefill = jest.fn(() => pending.asObservable());
    stubConfirm(true);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');

    component.save();
    expect(component.saving).toBe(true);

    component.onSelectedMachineIdsChange([5]);
    component.removeMachineColumn(6);
    component.onCellChange(1, 5, '9');

    expect(component.selectedMachineIds).toEqual([5, 6]);
    expect(component.cellValue(1, 5)).toBe('3');

    pending.next(batchResponse({ idempotencyKey: 'k', allocations: [{ machineId: 5, productId: 1, quantityTaken: 3 }] }));
    pending.complete();

    expect(component.saving).toBe(false);
    component.onSelectedMachineIdsChange([5]);
    expect(component.selectedMachineIds).toEqual([5]);
  });

  it('does not resend while a batch save is already in flight', () => {
    const pending = new Subject<MultiMachinePickListRefillResponse>();
    const recordMultiMachineRefill = jest.fn(() => pending.asObservable());
    const { component } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5]);
    component.onCellChange(1, 5, '3');

    component.save();
    component.save();

    expect(recordMultiMachineRefill).toHaveBeenCalledTimes(1);
  });
});

describe('ManualPickListComponent uncertain batch outcome (issue #904)', () => {
  const uncertain = () =>
    jest.fn((_request: MultiMachinePickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 503 })));

  it('keeps the exact payload and key, and the explicit Retry resends precisely that request', () => {
    const recordMultiMachineRefill = uncertain();
    const { component, toast } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');

    component.save();

    expect(component.unresolvedSubmission).toBe(true);
    expect(toast.error).toHaveBeenCalledWith(REFILL_UNCERTAIN_MESSAGE);
    expect(component.canRetry).toBe(true);
    const first: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];

    component.retry();

    const retried: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[1][0];
    expect(retried.idempotencyKey).toBe(first.idempotencyKey);
    expect(retried.allocations).toEqual(first.allocations);
    expect(component.cellValue(1, 5)).toBe('3');
  });

  it('mints no new key while the outcome is uncertain, whatever is edited, removed or reordered', () => {
    const recordMultiMachineRefill = uncertain();
    stubConfirm(true);
    const { component } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');

    component.save();
    const first: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];

    // An edited cell, a removed column and a reordered selection are all things an operator may do
    // while waiting; none of them may turn this same physical pick into a second deduction.
    component.onCellChange(1, 5, '9');
    component.removeMachineColumn(6);
    component.onSelectedMachineIdsChange([6, 5]);

    component.save();

    const second: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[1][0];
    expect(second.idempotencyKey).toBe(first.idempotencyKey);
    expect(second.allocations).toEqual(first.allocations);
  });

  it('resolves the uncertainty once a retry is answered, and only then accepts a different pick', () => {
    let failing = true;
    const recordMultiMachineRefill = jest.fn((request: MultiMachinePickListRefillRequest) =>
      failing ? throwError(() => new HttpErrorResponse({ status: 0 })) : of(batchResponse(request))
    );
    const { component } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5]);
    component.onCellChange(1, 5, '3');
    component.save();
    const firstKey = (recordMultiMachineRefill.mock.calls[0][0] as MultiMachinePickListRefillRequest).idempotencyKey;

    failing = false;
    component.retry();
    expect(component.unresolvedSubmission).toBe(false);
    expect(component.canRetry).toBe(false);

    component.onCellChange(1, 5, '7');
    component.save();

    const third: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[2][0];
    expect(third.idempotencyKey).not.toBe(firstKey);
    expect(third.allocations).toEqual([{ machineId: 5, productId: 1, quantityTaken: 7 }]);
  });

  it('reuses the key for a reordered resubmission of the same pick after a definitive refusal', () => {
    const recordMultiMachineRefill = jest.fn((_request: MultiMachinePickListRefillRequest) =>
      throwError(() => validationError({ 'allocations[0]': ['Not enough stock.'] }))
    );
    const { component } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '4');
    component.save();
    const first: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];

    component.onSelectedMachineIdsChange([6, 5]);
    component.save();

    const second: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[1][0];
    expect(second.idempotencyKey).toBe(first.idempotencyKey);
    expect(second.allocations).toEqual(first.allocations);
  });

  it('reports a 403 on a batch as a definite refusal and mints a fresh key afterwards', () => {
    const refused = new HttpErrorResponse({ status: 403, error: { title: 'Forbidden', status: 403 } });
    const recordMultiMachineRefill = jest
      .fn()
      .mockImplementationOnce(() => throwError(() => refused))
      .mockImplementation((request: MultiMachinePickListRefillRequest) => of(batchResponse(request)));
    const { component, toast } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5]);
    component.onCellChange(1, 5, '3');

    component.save();
    expect(component.unresolvedSubmission).toBe(false);
    expect(toast.error).toHaveBeenCalledWith(REFILL_FORBIDDEN_MESSAGE);

    component.save();
    const firstKey = (recordMultiMachineRefill.mock.calls[0][0] as MultiMachinePickListRefillRequest).idempotencyKey;
    const secondKey = (recordMultiMachineRefill.mock.calls[1][0] as MultiMachinePickListRefillRequest).idempotencyKey;
    expect(secondKey).not.toBe(firstKey);
  });

  it('mints a fresh key after a 409 on a batch, because that key is spent on another request', () => {
    const recordMultiMachineRefill = jest.fn((_request: MultiMachinePickListRefillRequest) =>
      throwError(() => new HttpErrorResponse({ status: 409, error: { detail: 'conflict' } }))
    );
    const { component, toast } = createHarness(undefined, undefined, undefined, twoMachines(), null, recordMultiMachineRefill);
    component.onSelectedMachineIdsChange([5]);
    component.onCellChange(1, 5, '3');

    component.save();
    component.save();

    expect(toast.error).toHaveBeenCalledWith('conflict');
    const firstKey = (recordMultiMachineRefill.mock.calls[0][0] as MultiMachinePickListRefillRequest).idempotencyKey;
    const secondKey = (recordMultiMachineRefill.mock.calls[1][0] as MultiMachinePickListRefillRequest).idempotencyKey;
    expect(secondKey).not.toBe(firstKey);
  });

  it('refuses to save a batch while a support session is active', () => {
    const { component, recordMultiMachineRefill } = createHarness(
      undefined,
      undefined,
      of(true),
      twoMachines(),
      null,
      jest.fn((request: MultiMachinePickListRefillRequest) => of(batchResponse(request)))
    );
    component.onSelectedMachineIdsChange([5]);
    component.onCellChange(1, 5, '3');

    expect(component.canSave).toBe(false);
    component.save();

    expect(recordMultiMachineRefill).not.toHaveBeenCalled();
  });
});

describe('ManualPickListComponent machine preselection from a link (issue #744 machine-page consolidation)', () => {
  it('preselects exactly the local machine the link named, once it is confirmed active', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 5, name: 'Front Lobby' }), localMachine({ id: 6, name: 'Back Room' })]));
    const { component, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.selectedMachineIds).toEqual([5]);
    expect(component.machineAttributionFailed).toBe(false);

    component.onCellChange(1, 5, '3');
    component.save();

    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([{ machineId: 5, productId: 1, quantityTaken: 3 }]);
  });

  it('lets the operator add more machines to the preselected one', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 5, name: 'Front Lobby' }), localMachine({ id: 6, name: 'Back Room' })]));
    const { component, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    component.onSelectedMachineIdsChange([5, 6]);
    component.onCellChange(1, 5, '3');
    component.onCellChange(1, 6, '2');
    component.save();

    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([
      { machineId: 5, productId: 1, quantityTaken: 3 },
      { machineId: 6, productId: 1, quantityTaken: 2 }
    ]);
  });

  it('reports attribution as failed, and leaves the selection at "No machine", when the linked machine is not an active local machine', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 6, name: 'Back Room' })]));
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.selectedMachineIds).toEqual([]);
    expect(component.machineAttributionFailed).toBe(true);
    expect(component.machineAttributionFailedMessage).toBe(MACHINE_ATTRIBUTION_FAILED_MESSAGE);

    // Not silent: the selection still reflects the true, unattributed pick and a save against it is
    // an explicit "No machine" choice, not one disguised as the linked machine.
    component.onTakenChange(1, '3');
    component.save();
    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBeUndefined();
  });

  it('reports attribution as failed when the local machine list itself could not be read', () => {
    const getAllMachines = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.machineAttributionFailed).toBe(true);
    expect(component.selectedMachineIds).toEqual([]);
  });

  it('does not report a failure when no machine was linked at all', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 5, name: 'Front Lobby' })]));
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines, null);

    expect(component.machineAttributionFailed).toBe(false);
    expect(component.selectedMachineIds).toEqual([]);
  });

  it('refuses Save while the linked machine is still being resolved, and attributes the refill once the read answers', () => {
    // The catalogue is the page's own already-fetched list, so it can be ready long before this
    // component's machine read answers. Without the pending check the worker could type quantities
    // and save an unattributed refill in that window.
    const machines = new Subject<LocalMachine[]>();
    const getAllMachines = jest.fn(() => machines.asObservable());
    const { component, recordRefill, recordMultiMachineRefill } = createHarness(undefined, undefined, undefined, getAllMachines, 42);

    component.onTakenChange(1, '3');
    expect(component.machineResolutionPending).toBe(true);
    expect(component.machineResolutionPendingMessage).toBe(MACHINE_RESOLUTION_PENDING_MESSAGE);
    expect(component.canSave).toBe(false);

    component.save();
    expect(recordRefill).not.toHaveBeenCalled();
    expect(recordMultiMachineRefill).not.toHaveBeenCalled();

    machines.next([localMachine({ id: 42, name: 'Front Lobby' })]);
    machines.complete();

    expect(component.machineResolutionPending).toBe(false);
    expect(component.selectedMachineIds).toEqual([42]);

    // A quantity typed while the link's machine was still being confirmed belongs to that one
    // machine - the link promised attribution to it and the pending notice says so - so it moves
    // into that machine's column rather than being dropped or left unattributed.
    expect(component.cellValue(1, 42)).toBe('3');
    expect(component.takenValue(1)).toBe('');
    expect(component.canSave).toBe(true);

    component.save();
    expect(recordMultiMachineRefill).toHaveBeenCalledTimes(1);
    const request: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(request.allocations).toEqual([{ machineId: 42, productId: 1, quantityTaken: 3 }]);
  });

  it('never waits on the machine read for ordinary entry with no linked machine', () => {
    const machines = new Subject<LocalMachine[]>();
    const getAllMachines = jest.fn(() => machines.asObservable());
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines, null);

    component.onTakenChange(1, '3');
    expect(component.machineResolutionPending).toBe(false);
    expect(component.canSave).toBe(true);

    component.save();
    expect(recordRefill).toHaveBeenCalledTimes(1);
    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBeUndefined();
  });

  it('cannot let a late machine answer turn an uncertain attempt into a second deduction', () => {
    const machines = new Subject<LocalMachine[]>();
    const getAllMachines = jest.fn(() => machines.asObservable());
    const recordMultiMachineRefill = jest.fn((_request: MultiMachinePickListRefillRequest) =>
      throwError(() => new HttpErrorResponse({ status: 503 }))
    );
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines, 42, recordMultiMachineRefill);

    // No request may leave while the attribution is unresolved, so there is no uncertain attempt
    // for a late answer to change underneath.
    component.onTakenChange(1, '3');
    component.save();
    expect(recordRefill).not.toHaveBeenCalled();
    expect(recordMultiMachineRefill).not.toHaveBeenCalled();

    machines.next([localMachine({ id: 42, name: 'Front Lobby' })]);
    machines.complete();

    component.save();
    expect(recordMultiMachineRefill).toHaveBeenCalledTimes(1);
    expect(component.unresolvedSubmission).toBe(true);
    const first: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(first.allocations).toEqual([{ machineId: 42, productId: 1, quantityTaken: 3 }]);

    // The retry is the identical request: the same key and the same allocations, so a first attempt
    // that did commit is replayed rather than deducting storage again.
    component.retry();
    expect(recordMultiMachineRefill).toHaveBeenCalledTimes(2);
    const retried: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[1][0];
    expect(retried.idempotencyKey).toBe(first.idempotencyKey);
    expect(retried.allocations).toEqual(first.allocations);
  });

  it('treats a deactivated machine named by the link the same as one that no longer exists', () => {
    const getAllMachines = jest.fn(() =>
      of([localMachine({ id: 5, name: 'Front Lobby', isActive: false })])
    );
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.selectedMachineIds).toEqual([]);
    expect(component.machineAttributionFailed).toBe(true);
  });
});

describe('ManualPickListComponent entry and validation', () => {
  it('treats a blank "Taken" box as nothing to save, with no row error', () => {
    const { component } = createHarness();

    expect(component.rowError(1)).toBeNull();
    expect(component.canSave).toBe(false);
  });

  it('treats an explicit 0 the same as blank', () => {
    const { component } = createHarness();
    component.onTakenChange(1, '0');

    expect(component.rowError(1)).toBeNull();
    expect(component.canSave).toBe(false);
  });

  it('shows a validation message for a non-numeric entry and blocks Save', () => {
    const { component } = createHarness();
    component.onTakenChange(1, 'abc');

    expect(component.rowError(1)).toBe(INVALID_TAKEN_QUANTITY_MESSAGE);
    expect(component.canSave).toBe(false);
  });

  it('shows a validation message for a negative entry', () => {
    const { component } = createHarness();
    component.onTakenChange(1, '-1');

    expect(component.rowError(1)).toBe(INVALID_TAKEN_QUANTITY_MESSAGE);
  });

  it('shows a validation message for a fractional entry', () => {
    const { component } = createHarness();
    component.onTakenChange(1, '1.5');

    expect(component.rowError(1)).toBe(INVALID_TAKEN_QUANTITY_MESSAGE);
  });

  it('allows Save once at least one row has a positive whole number and nothing is malformed', () => {
    const { component } = createHarness();
    component.onTakenChange(1, '3');

    expect(component.canSave).toBe(true);
  });

  it('blocks Save while any entry anywhere is malformed, even if another row is valid', () => {
    const { component } = createHarness();
    component.onTakenChange(1, '3');
    component.onTakenChange(2, 'abc');

    expect(component.canSave).toBe(false);
  });

  it('clears a row’s validation message once it is edited to a valid value', () => {
    const { component } = createHarness();
    component.onTakenChange(1, 'abc');
    component.onTakenChange(1, '3');

    expect(component.rowError(1)).toBeNull();
  });
});

describe('ManualPickListComponent save', () => {
  it('sends only the rows with a positive quantity, in catalogue order, with no machineId', () => {
    const { component, recordRefill } = createHarness();
    component.onTakenChange(2, '4');
    component.onTakenChange(1, '2');

    component.save();

    expect(recordRefill).toHaveBeenCalledTimes(1);
    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.rows).toEqual([{ productId: 1, quantityTaken: 2 }, { productId: 2, quantityTaken: 4 }]);
    expect(request.machineId).toBeUndefined();
    expect(request.idempotencyKey.length).toBeGreaterThan(0);
  });

  it('clears the entered quantities and reports success once the save is recorded', () => {
    const { component, toast } = createHarness();
    component.onTakenChange(1, '3');

    component.save();

    expect(component.takenValue(1)).toBe('');
    expect(toast.success).toHaveBeenCalledWith(REFILL_RECORDED_MESSAGE);
  });

  it('emits refillRecorded once the save is recorded, so the page can refresh storage quantities', () => {
    const { component } = createHarness();
    const emitted = jest.fn();
    component.refillRecorded.subscribe(emitted);
    component.onTakenChange(1, '3');

    component.save();

    expect(emitted).toHaveBeenCalledTimes(1);
  });

  it('gives every new submission a different idempotency key', () => {
    const { component, recordRefill } = createHarness();
    component.onTakenChange(1, '3');
    component.save();
    const firstKey = (recordRefill.mock.calls[0][0] as PickListRefillRequest).idempotencyKey;

    component.onTakenChange(1, '5');
    component.save();
    const secondKey = (recordRefill.mock.calls[1][0] as PickListRefillRequest).idempotencyKey;

    expect(secondKey).not.toBe(firstKey);
  });

  it('shows each row’s own refusal from a 400, naming the product the submitted index pointed at', () => {
    const recordRefill = jest.fn(() =>
      throwError(() => validationError({ 'rows[1]': ['Quantity taken must be a whole number greater than 0.'] }))
    );
    const { component } = createHarness(undefined, recordRefill);
    component.onTakenChange(1, '1');
    component.onTakenChange(2, '2');

    component.save();

    expect(component.rowError(2)).toContain('Quantity taken must be a whole number greater than 0.');
    expect(component.rowError(1)).toBeNull();
  });

  it('shows a request-level refusal the response named', () => {
    const recordRefill = jest.fn((_request: PickListRefillRequest) =>
      throwError(() => validationError({ idempotencyKey: ['An idempotency key is required.'] }))
    );
    const { component } = createHarness(undefined, recordRefill);
    component.onTakenChange(1, '1');

    component.save();

    expect(component.requestErrors).toEqual(['An idempotency key is required.']);
  });

  describe('retry reuse of the idempotency key (issue #744)', () => {
    it('reuses the same key and exact rows when retrying an unchanged submission after an uncertain failure', () => {
      const recordRefill = jest.fn((_request: PickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 0 })));
      const { component, toast } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');

      component.save();
      const firstRequest: PickListRefillRequest = recordRefill.mock.calls[0][0];
      expect(toast.error).toHaveBeenCalledWith(REFILL_UNCERTAIN_MESSAGE);

      component.save();
      const secondRequest: PickListRefillRequest = recordRefill.mock.calls[1][0];

      expect(secondRequest.idempotencyKey).toBe(firstRequest.idempotencyKey);
      expect(secondRequest.rows).toEqual(firstRequest.rows);
    });

    it('keeps the entered quantities on screen after an uncertain failure, so a retry has something to resend', () => {
      const recordRefill = jest.fn((_request: PickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 0 })));
      const { component } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');

      component.save();

      expect(component.takenValue(1)).toBe('3');
    });

    /**
     * Issue #904 tightened this rule. Before it, editing a quantity after an uncertain outcome
     * minted a new key - which is a second deduction whenever the first attempt had in fact been
     * recorded. While the outcome is unknown the kept request is the only one that may be sent.
     */
    it('mints no new key while the outcome is uncertain, even after the entries are edited', () => {
      const recordRefill = jest.fn((_request: PickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 0 })));
      const { component } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');
      component.save();
      const first: PickListRefillRequest = recordRefill.mock.calls[0][0];

      component.onTakenChange(1, '7');
      component.save();
      const secondRequest: PickListRefillRequest = recordRefill.mock.calls[1][0];

      expect(secondRequest.idempotencyKey).toBe(first.idempotencyKey);
      expect(secondRequest.rows).toEqual([{ productId: 1, quantityTaken: 3 }]);
    });

    it('mints a new key for an edited pick once the uncertain attempt has been resolved', () => {
      let uncertain = true;
      const recordRefill = jest.fn((request: PickListRefillRequest) =>
        uncertain ? throwError(() => new HttpErrorResponse({ status: 0 })) : of(recordedResponse(request))
      );
      const { component } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');
      component.save();
      const firstKey = (recordRefill.mock.calls[0][0] as PickListRefillRequest).idempotencyKey;

      uncertain = false;
      component.retry();
      expect(component.unresolvedSubmission).toBe(false);

      component.onTakenChange(1, '7');
      component.save();
      const thirdRequest: PickListRefillRequest = recordRefill.mock.calls[2][0];

      expect(thirdRequest.idempotencyKey).not.toBe(firstKey);
      expect(thirdRequest.rows).toEqual([{ productId: 1, quantityTaken: 7 }]);
    });

    it('mints a new key once an edit changes a pick a 400 definitively refused', () => {
      const recordRefill = jest.fn((_request: PickListRefillRequest) =>
        throwError(() => validationError({ 'rows[0]': ['Not enough stock.'] }))
      );
      const { component } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');
      component.save();
      const firstKey = (recordRefill.mock.calls[0][0] as PickListRefillRequest).idempotencyKey;

      component.onTakenChange(1, '2');
      component.save();
      const secondRequest: PickListRefillRequest = recordRefill.mock.calls[1][0];

      expect(secondRequest.idempotencyKey).not.toBe(firstKey);
      expect(secondRequest.rows).toEqual([{ productId: 1, quantityTaken: 2 }]);
    });

    it('mints a new key for the next save after a 409 key conflict, rather than reusing a spent key', () => {
      const recordRefill = jest.fn((_request: PickListRefillRequest) =>
        throwError(() => new HttpErrorResponse({ status: 409, error: { detail: 'conflict' } }))
      );
      const { component, toast } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');

      component.save();
      const firstKey = (recordRefill.mock.calls[0][0] as PickListRefillRequest).idempotencyKey;
      expect(toast.error).toHaveBeenCalledWith('conflict');

      component.save();
      const secondKey = (recordRefill.mock.calls[1][0] as PickListRefillRequest).idempotencyKey;

      expect(secondKey).not.toBe(firstKey);
    });

    it('falls back to a generic conflict message when the 409 carries none', () => {
      const recordRefill = jest.fn((_request: PickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 409, error: {} })));
      const { component, toast } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');

      component.save();

      expect(toast.error).toHaveBeenCalledWith(REFILL_CONFLICT_MESSAGE);
    });

    it('does not resend while a save is already in flight', () => {
      const pending = new Subject<PickListRefillResponse>();
      const recordRefill = jest.fn(() => pending.asObservable());
      const { component } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');

      component.save();
      component.save();

      expect(recordRefill).toHaveBeenCalledTimes(1);
    });
  });

  /**
   * The kept rows and key are what stop a retry double-deducting, so an uncertain attempt has to
   * stay visible until it is resolved, and the page has to know a save is outstanding so it can
   * refuse to destroy this component while one is.
   */
  describe('reporting an unresolved submission (issue #744)', () => {
    it('raises an unresolved submission for an uncertain failure and clears it once a retry succeeds', () => {
      let uncertain = true;
      const recordRefill = jest.fn((request: PickListRefillRequest) =>
        uncertain ? throwError(() => new HttpErrorResponse({ status: 0 })) : of(recordedResponse(request))
      );
      const { component } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');

      component.save();
      expect(component.unresolvedSubmission).toBe(true);

      uncertain = false;
      component.save();

      expect(component.unresolvedSubmission).toBe(false);
    });

    it('leaves a 400 resolved: a per-row refusal is a definite answer, not an unknown outcome', () => {
      const rejected = jest.fn((_request: PickListRefillRequest) =>
        throwError(() => validationError({ 'rows[0].quantityTaken': ['Too many.'] }))
      );
      const { component } = createHarness(undefined, rejected);
      component.onTakenChange(1, '3');

      component.save();

      expect(component.unresolvedSubmission).toBe(false);
    });

    it('leaves a 409 resolved: a spent key is a definite answer too', () => {
      const conflicted = jest.fn((_request: PickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 409, error: {} })));
      const { component } = createHarness(undefined, conflicted);
      component.onTakenChange(1, '3');

      component.save();

      expect(component.unresolvedSubmission).toBe(false);
    });

    it('tells the page when a save starts and when it finishes', () => {
      const pending = new Subject<PickListRefillResponse>();
      const recordRefill = jest.fn(() => pending.asObservable());
      const { component } = createHarness(undefined, recordRefill);
      const inFlight: boolean[] = [];
      component.saveInFlightChange.subscribe((value) => inFlight.push(value));
      component.onTakenChange(1, '3');

      component.save();
      expect(inFlight).toEqual([true]);

      pending.next(recordedResponse({ idempotencyKey: 'key', rows: [{ productId: 1, quantityTaken: 3 }] }));
      pending.complete();

      expect(inFlight).toEqual([true, false]);
    });
  });
});

describe('ManualPickListComponent during a read-only support session (issue #744)', () => {
  it('refuses to save while a support session is active, sending nothing', () => {
    const { component, recordRefill, toast } = createHarness(undefined, undefined, of(true));
    component.onTakenChange(1, '3');

    expect(component.canSave).toBe(false);
    component.save();

    expect(recordRefill).not.toHaveBeenCalled();
    expect(component.unresolvedSubmission).toBe(false);
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('stops saving once a support session starts after the page opened', () => {
    const session = new BehaviorSubject(false);
    const { component, recordRefill } = createHarness(undefined, undefined, session);
    component.onTakenChange(1, '3');
    expect(component.canSave).toBe(true);

    session.next(true);
    component.save();

    expect(component.canSave).toBe(false);
    expect(recordRefill).not.toHaveBeenCalled();
  });

  it('reports a 403 as a definite refusal, never as an uncertain write to retry', () => {
    const refused = new HttpErrorResponse({ status: 403, error: { title: 'Forbidden', status: 403 } });
    const recordRefill = jest.fn(() => throwError(() => refused));
    const { component, toast } = createHarness(undefined, recordRefill);
    component.onTakenChange(1, '3');

    component.save();

    expect(component.unresolvedSubmission).toBe(false);
    expect(toast.error).toHaveBeenCalledWith(REFILL_FORBIDDEN_MESSAGE);
    expect(toast.error).not.toHaveBeenCalledWith(REFILL_UNCERTAIN_MESSAGE);
  });

  it('mints a fresh key for a later save after a 403, because nothing was recorded under the refused one', () => {
    const refused = new HttpErrorResponse({ status: 403, error: { title: 'Forbidden', status: 403 } });
    const recordRefill = jest
      .fn()
      .mockImplementationOnce(() => throwError(() => refused))
      .mockImplementation((request: PickListRefillRequest) => of(recordedResponse(request)));
    const { component } = createHarness(undefined, recordRefill);
    component.onTakenChange(1, '3');

    component.save();
    component.save();

    const firstKey = (recordRefill.mock.calls[0][0] as PickListRefillRequest).idempotencyKey;
    const secondKey = (recordRefill.mock.calls[1][0] as PickListRefillRequest).idempotencyKey;
    expect(secondKey).not.toBe(firstKey);
  });

  it('unsubscribes from the support session when destroyed', () => {
    const session = new BehaviorSubject(false);
    const { component } = createHarness(undefined, undefined, session);

    component.ngOnDestroy();

    expect(session.observed).toBe(false);
  });
});
