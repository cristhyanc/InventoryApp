import { HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { Category, LocalMachine, PickListRefillRequest, PickListRefillResponse, Product } from '../../models/models';
import { LocalMachineService } from '../../services/local-machine.service';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  INVALID_TAKEN_QUANTITY_MESSAGE,
  MACHINE_ATTRIBUTION_FAILED_MESSAGE,
  MACHINES_LOAD_FAILED_MESSAGE,
  REFILL_CONFLICT_MESSAGE,
  REFILL_FORBIDDEN_MESSAGE,
  REFILL_RECORDED_MESSAGE,
  REFILL_UNCERTAIN_MESSAGE,
  ManualPickListComponent
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

function validationError(errors: Record<string, string[]>): HttpErrorResponse {
  return new HttpErrorResponse({ status: 400, error: { title: 'Request validation failed', status: 400, errors } });
}

interface Harness {
  component: ManualPickListComponent;
  recordRefill: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
  getAllMachines: jest.Mock;
}

function createHarness(
  products: Product[] = [product({ id: 1, name: 'Coke', quantityInStock: 20 }), product({ id: 2, name: 'Chips', quantityInStock: 5 })],
  recordRefill: jest.Mock = jest.fn((request: PickListRefillRequest) => of(recordedResponse(request))),
  sessionActive: Observable<boolean> = of(false),
  getAllMachines: jest.Mock = jest.fn(() => of([])),
  preselectedMachineId: number | null = null
): Harness {
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
  const pickListService = { recordRefill } as unknown as PickListService;
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

  return { component, recordRefill, toast, getAllMachines };
}

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

describe('ManualPickListComponent machine selector (issue #794)', () => {
  it('shows the selector and submits the chosen machine as the local machineId when active local machines exist', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 5, name: 'Front Lobby' })]));
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines);

    expect(component.machineSelectorShown).toBe(true);
    expect(component.activeMachines.map((m) => m.id)).toEqual([5]);

    component.selectedMachineId = 5;
    component.onTakenChange(1, '3');
    component.save();

    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBe(5);
  });

  it('hides the selector and submits no machineId when the business has no active local machines', () => {
    const getAllMachines = jest.fn(() => of([]));
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines);

    expect(component.machineSelectorShown).toBe(false);

    component.onTakenChange(1, '3');
    component.save();

    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBeUndefined();
  });

  it('does not offer a deactivated machine in the selector', () => {
    const getAllMachines = jest.fn(() =>
      of([localMachine({ id: 5, name: 'Front Lobby', isActive: true }), localMachine({ id: 6, name: 'Back Room', isActive: false })])
    );
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines);

    expect(component.machineSelectorShown).toBe(true);
    expect(component.activeMachines.map((m) => m.id)).toEqual([5]);
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

  it('gives a changed machine choice a new idempotency key, and an unchanged retry reuses both the key and the machine', () => {
    const getAllMachines = jest.fn(() =>
      of([localMachine({ id: 5, name: 'Front Lobby' }), localMachine({ id: 6, name: 'Back Room' })])
    );
    const recordRefill = jest.fn((_request: PickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 0 })));
    const { component } = createHarness(undefined, recordRefill, undefined, getAllMachines);
    component.onTakenChange(1, '3');
    component.selectedMachineId = 5;

    component.save();
    const firstRequest: PickListRefillRequest = recordRefill.mock.calls[0][0];

    component.save();
    const secondRequest: PickListRefillRequest = recordRefill.mock.calls[1][0];
    expect(secondRequest.idempotencyKey).toBe(firstRequest.idempotencyKey);
    expect(secondRequest.machineId).toBe(5);

    component.selectedMachineId = 6;
    component.save();
    const thirdRequest: PickListRefillRequest = recordRefill.mock.calls[2][0];
    expect(thirdRequest.idempotencyKey).not.toBe(firstRequest.idempotencyKey);
    expect(thirdRequest.machineId).toBe(6);
  });
});

describe('ManualPickListComponent machine preselection from a link (issue #744 machine-page consolidation)', () => {
  it('selects the local machine the link named, once it is confirmed active', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 5, name: 'Front Lobby' })]));
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.selectedMachineId).toBe(5);
    expect(component.machineAttributionFailed).toBe(false);

    component.onTakenChange(1, '3');
    component.save();

    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBe(5);
  });

  it('reports attribution as failed, and leaves the selection at "No machine", when the linked machine is not an active local machine', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 6, name: 'Back Room' })]));
    const { component, recordRefill } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.selectedMachineId).toBeNull();
    expect(component.machineAttributionFailed).toBe(true);
    expect(component.machineAttributionFailedMessage).toBe(MACHINE_ATTRIBUTION_FAILED_MESSAGE);

    // Not silent: the dropdown still reflects the true, unattributed selection and a save against
    // it is an explicit "No machine" choice, not one disguised as the linked machine.
    component.onTakenChange(1, '3');
    component.save();
    const request: PickListRefillRequest = recordRefill.mock.calls[0][0];
    expect(request.machineId).toBeUndefined();
  });

  it('reports attribution as failed when the local machine list itself could not be read', () => {
    const getAllMachines = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.machineAttributionFailed).toBe(true);
    expect(component.selectedMachineId).toBeNull();
  });

  it('does not report a failure when no machine was linked at all', () => {
    const getAllMachines = jest.fn(() => of([localMachine({ id: 5, name: 'Front Lobby' })]));
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines, null);

    expect(component.machineAttributionFailed).toBe(false);
    expect(component.selectedMachineId).toBeNull();
  });

  it('treats a deactivated machine named by the link the same as one that no longer exists', () => {
    const getAllMachines = jest.fn(() =>
      of([localMachine({ id: 5, name: 'Front Lobby', isActive: false })])
    );
    const { component } = createHarness(undefined, undefined, undefined, getAllMachines, 5);

    expect(component.selectedMachineId).toBeNull();
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

    it('mints a new key once the submission actually changes before the retry', () => {
      const recordRefill = jest.fn((_request: PickListRefillRequest) => throwError(() => new HttpErrorResponse({ status: 0 })));
      const { component } = createHarness(undefined, recordRefill);
      component.onTakenChange(1, '3');
      component.save();
      const firstKey = (recordRefill.mock.calls[0][0] as PickListRefillRequest).idempotencyKey;

      component.onTakenChange(1, '7');
      component.save();
      const secondRequest: PickListRefillRequest = recordRefill.mock.calls[1][0];

      expect(secondRequest.idempotencyKey).not.toBe(firstKey);
      expect(secondRequest.rows).toEqual([{ productId: 1, quantityTaken: 7 }]);
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
