import { of, throwError } from 'rxjs';
import { MachineRestockSyncComponent } from './machine-restock-sync.component';
import { MachineService } from '../../../services/machine.service';
import { ToastService } from '../../../services/toast.service';
import {
  NayaxMachineStockApplyResponse,
  NayaxMachineStockSyncPreview,
  NayaxStockEventApplyOutcome,
  NayaxStockEventMatchStatus,
  NayaxStockEventPreview,
  NayaxStockEventProcessingStatus
} from '../../../models/models';

function event(overrides: Partial<NayaxStockEventPreview>): NayaxStockEventPreview {
  return {
    id: 1,
    nayaxEventLogId: 1001,
    machineId: 7,
    eventDateTimeGmt: '2026-09-27T10:00:00Z',
    eventDateTimeVmc: '2026-09-27T20:00:00',
    rawEventData: "Eunhye Chung 'Adjusted Stock, Product MDB: 13 | Beef Jerky | 2",
    parsedMdb: 13,
    parsedProductName: 'Beef Jerky',
    parsedQuantity: 2,
    matchedProductId: 55,
    matchedProductName: 'Beef Jerky',
    matchStatus: NayaxStockEventMatchStatus.Matched,
    needsReviewReason: null,
    processingStatus: NayaxStockEventProcessingStatus.Unprocessed,
    availableStorageQuantity: 10,
    isInsufficientStock: false,
    unaccountedDifference: null,
    isDiscrepancy: false,
    isPossibleDuplicate: false,
    possibleDuplicateNotes: null,
    ...overrides
  };
}

function preview(events: NayaxStockEventPreview[], message: string | null = null): NayaxMachineStockSyncPreview {
  return {
    machineId: 7,
    newEventCount: events.length,
    events,
    productImpacts: [],
    message
  };
}

interface Harness {
  component: MachineRestockSyncComponent;
  syncRestock: jest.Mock;
  applySyncRestock: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock };
  applied: number;
}

function createHarness(
  syncRestock: jest.Mock = jest.fn(() => of(preview([]))),
  applySyncRestock: jest.Mock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse))
): Harness {
  const machineService = { syncRestock, applySyncRestock } as unknown as MachineService;
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
  const component = new MachineRestockSyncComponent(machineService, toast as unknown as ToastService);
  component.machineId = 7;

  const harness: Harness = { component, syncRestock, applySyncRestock, toast, applied: 0 };
  component.restockApplied.subscribe(() => harness.applied++);
  return harness;
}

describe('MachineRestockSyncComponent eligibility', () => {
  it('treats a matched positive event with enough storage as ready to apply', () => {
    const { component } = createHarness();
    expect(component.isReadyToApply(event({}))).toBe(true);
  });

  it('never offers a needs-review event for apply', () => {
    const { component } = createHarness();
    const needsReview = event({ matchStatus: NayaxStockEventMatchStatus.NeedsReview, needsReviewReason: 'Unknown MDB' });
    expect(component.isReadyToApply(needsReview)).toBe(false);
  });

  it('never offers a negative discrepancy for apply', () => {
    const { component } = createHarness();
    expect(component.isReadyToApply(event({ parsedQuantity: -3, isDiscrepancy: true }))).toBe(false);
  });

  it('never offers an insufficient-storage event for apply', () => {
    const { component } = createHarness();
    const short = event({ parsedQuantity: 5, availableStorageQuantity: 4, isInsufficientStock: true, unaccountedDifference: 1 });
    expect(component.isReadyToApply(short)).toBe(false);
  });

  it('never offers an unparsed event for apply', () => {
    const { component } = createHarness();
    expect(component.isReadyToApply(event({ parsedQuantity: null }))).toBe(false);
  });
});

describe('MachineRestockSyncComponent sync', () => {
  it('does nothing without a machine id', () => {
    const { component, syncRestock } = createHarness();
    component.machineId = null;

    component.syncRestock();

    expect(syncRestock).not.toHaveBeenCalled();
  });

  it('pre-selects only the events that are ready to apply', () => {
    const ready = event({ id: 1 });
    const needsReview = event({ id: 2, matchStatus: NayaxStockEventMatchStatus.NeedsReview });
    const short = event({ id: 3, parsedQuantity: 5, availableStorageQuantity: 4, isInsufficientStock: true });
    const { component } = createHarness(jest.fn(() => of(preview([ready, needsReview, short]))));

    component.syncRestock();

    expect([...component.selectedEventIds]).toEqual([1]);
    expect(component.syncing$.value).toBe(false);
    expect(component.syncPreview$.value?.events.length).toBe(3);
  });

  it('shows the backend empty-state message when there is nothing to reconcile', () => {
    const { component, toast } = createHarness(jest.fn(() => of(preview([], 'No new Nayax stock-adjustment alerts.'))));

    component.syncRestock();

    expect(toast.success).toHaveBeenCalledWith('No new Nayax stock-adjustment alerts.');
    expect(component.selectedEventIds.size).toBe(0);
  });

  it('reports an upstream failure and clears the syncing state', () => {
    const { component, toast } = createHarness(jest.fn(() => throwError(() => new Error('upstream unavailable'))));

    component.syncRestock();

    expect(component.syncing$.value).toBe(false);
    expect(toast.error).toHaveBeenCalledWith('Failed to sync Nayax stock-adjustment alerts.');
  });
});

describe('MachineRestockSyncComponent selection', () => {
  it('adds and removes event ids', () => {
    const { component } = createHarness();

    component.toggleEventSelection(4, true);
    expect(component.isEventSelected(4)).toBe(true);

    component.toggleEventSelection(4, false);
    expect(component.isEventSelected(4)).toBe(false);
  });
});

describe('MachineRestockSyncComponent apply', () => {
  it('applies only the selected event ids', () => {
    const applyResponse: NayaxMachineStockApplyResponse = {
      results: [{ eventId: 1, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied', stockAdjustmentId: 90 }]
    };
    const { component, applySyncRestock } = createHarness(
      jest.fn(() => of(preview([]))),
      jest.fn(() => of(applyResponse))
    );
    component.toggleEventSelection(1, true);

    component.applySelectedEvents();

    expect(applySyncRestock).toHaveBeenCalledWith(7, [1]);
  });

  it('tells the page to refresh products and refreshes its own preview after a successful apply', () => {
    const applyResponse: NayaxMachineStockApplyResponse = {
      results: [{ eventId: 1, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied', stockAdjustmentId: 90 }]
    };
    const syncRestock = jest.fn(() => of(preview([])));
    const harness = createHarness(syncRestock, jest.fn(() => of(applyResponse)));
    harness.component.toggleEventSelection(1, true);

    harness.component.applySelectedEvents();

    expect(harness.applied).toBe(1);
    expect(syncRestock).toHaveBeenCalledTimes(1);
    expect(harness.component.selectedEventIds.size).toBe(0);
    expect(harness.component.applying$.value).toBe(false);
    expect(harness.toast.success).toHaveBeenCalledWith('Applied 1 Nayax stock-adjustment event(s).');
  });

  it('warns about events the backend refused and does not claim a restock happened', () => {
    const applyResponse: NayaxMachineStockApplyResponse = {
      results: [
        { eventId: 1, outcome: NayaxStockEventApplyOutcome.InsufficientStock, message: 'Not enough storage', stockAdjustmentId: null }
      ]
    };
    const harness = createHarness(jest.fn(() => of(preview([]))), jest.fn(() => of(applyResponse)));
    harness.component.toggleEventSelection(1, true);

    harness.component.applySelectedEvents();

    expect(harness.applied).toBe(0);
    expect(harness.toast.warning).toHaveBeenCalledWith('1 event(s) could not be applied and remain for review.');
  });

  it('reports both the applied and the refused events in a mixed batch', () => {
    const applyResponse: NayaxMachineStockApplyResponse = {
      results: [
        { eventId: 1, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied', stockAdjustmentId: 90 },
        { eventId: 2, outcome: NayaxStockEventApplyOutcome.Error, message: 'Rolled back', stockAdjustmentId: null }
      ]
    };
    const harness = createHarness(jest.fn(() => of(preview([]))), jest.fn(() => of(applyResponse)));
    harness.component.toggleEventSelection(1, true);
    harness.component.toggleEventSelection(2, true);

    harness.component.applySelectedEvents();

    expect(harness.toast.success).toHaveBeenCalledWith('Applied 1 Nayax stock-adjustment event(s).');
    expect(harness.toast.warning).toHaveBeenCalledWith('1 event(s) could not be applied and remain for review.');
    expect(harness.applied).toBe(1);
  });

  it('does nothing when no event is selected', () => {
    const { component, applySyncRestock } = createHarness();

    component.applySelectedEvents();

    expect(applySyncRestock).not.toHaveBeenCalled();
  });

  it('does nothing without a machine id', () => {
    const { component, applySyncRestock } = createHarness();
    component.machineId = null;
    component.toggleEventSelection(1, true);

    component.applySelectedEvents();

    expect(applySyncRestock).not.toHaveBeenCalled();
  });

  it('keeps the selection and reports the failure when the apply request fails', () => {
    const harness = createHarness(
      jest.fn(() => of(preview([]))),
      jest.fn(() => throwError(() => new Error('network error')))
    );
    harness.component.toggleEventSelection(1, true);

    harness.component.applySelectedEvents();

    expect(harness.component.applying$.value).toBe(false);
    expect(harness.component.selectedEventIds.size).toBe(1);
    expect(harness.applied).toBe(0);
    expect(harness.toast.error).toHaveBeenCalledWith('Failed to apply the selected Nayax stock-adjustment events.');
  });
});
