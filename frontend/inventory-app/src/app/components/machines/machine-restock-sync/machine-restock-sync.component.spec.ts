import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { SimpleChange } from '@angular/core';
import { MachineRestockSyncComponent } from './machine-restock-sync.component';
import { MachineService } from '../../../services/machine.service';
import { ProductService } from '../../../services/product.service';
import { ToastService } from '../../../services/toast.service';
import {
  currentDateInTimeZone,
  shiftCalendarDate,
  startOfDayUtc
} from '../../../formatting/business-time-zone';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';
import { AccessService } from '../../../access/access.service';
import { NayaxConnectionService, NayaxConnectionStatusName } from '../../../services/nayax-connection.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

function supportSessionStub(active: Observable<boolean> = of(false)): SupportSessionService {
  return { active$: active } as unknown as SupportSessionService;
}
import {
  NayaxApplyProductReplacementRequest,
  NayaxDuplicateResolution,
  NayaxDuplicateResolutionChoice,
  NayaxMachineAlertEventCodes,
  NayaxMachineStockApplyResponse,
  NayaxProductReplacementPreview,
  NayaxReplacementOutgoingDisposition,
  NayaxReplacementSuggestionSource,
  NayaxMachineStockSyncPreview,
  NayaxStockEventApplyOutcome,
  NayaxStockEventApplyResult,
  NayaxStockEventMatchStatus,
  NayaxStockEventPreview,
  NayaxStockEventProcessingStatus,
  Product
} from '../../../models/models';

/** A business's active product (issue #1327), as the product-change row's autocompletes offer it. */
function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 300,
    name: 'Red Bull Red Edition 250mL',
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: 12,
    maxStockInMachine: 0,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 0,
    restockTo: 0,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides
  } as Product;
}

/**
 * The zone these tests run as: the business the application already has. It was a constant in
 * production code until issue #499 made the zone per business, and it stays here as the test's own
 * choice of business, so every From date expectation below means exactly what it did before.
 */
const BUSINESS_TIME_ZONE = 'Australia/Sydney';

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
    duplicateResolution: NayaxDuplicateResolution.None,
    possibleDuplicateQuantity: null,
    possibleDuplicateEffectiveAt: null,
    otherPossibleDuplicateCount: 0,
    eventCode: NayaxMachineAlertEventCodes.StockAdjustForMachine,
    productReplacement: null,
    isPossibleReplacementDoubleCount: false,
    replacementDoubleCountNotes: null,
    ...overrides
  };
}

/** A parsed Nayax product replacement (Event 510, issue #1325), as the backend previews it. */
function replacementEvent(
  overrides: Partial<NayaxStockEventPreview> = {},
  replacement: Partial<NayaxProductReplacementPreview> = {}
): NayaxStockEventPreview {
  return event({
    id: 9,
    eventCode: NayaxMachineAlertEventCodes.MachineProductChanged,
    rawEventData: "[operator] 'has replaced product V Energy 250ML with product Redbull Red Edition | Product MDB: 58",
    parsedMdb: 58,
    parsedProductName: 'Redbull Red Edition',
    parsedQuantity: null,
    matchedProductId: null,
    matchedProductName: null,
    matchStatus: NayaxStockEventMatchStatus.NeedsReview,
    needsReviewReason: null,
    availableStorageQuantity: null,
    productReplacement: {
      outgoingProductName: 'V Energy 250ML',
      incomingProductName: 'Redbull Red Edition',
      suggestedOutgoingProductId: 301,
      suggestedOutgoingProductName: 'V Energy 250ML',
      outgoingSuggestionSource: NayaxReplacementSuggestionSource.ExactName,
      outgoingSuggestionReason: null,
      suggestedIncomingProductId: 300,
      suggestedIncomingProductName: 'Red Bull Red Edition 250mL',
      incomingSuggestionSource: NayaxReplacementSuggestionSource.LiveSlot,
      incomingSuggestionReason: null,
      suggestedIncomingQuantity: 7,
      suggestedIncomingQuantityReason: null,
      ...replacement
    },
    ...overrides
  });
}

/** The business wall-clock rendering the dialog uses for an instant, for the comparison line. */
function inBusinessTime(instant: string): string {
  return new Intl.DateTimeFormat('en-AU', {
    timeZone: BUSINESS_TIME_ZONE,
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    hour12: true
  }).format(new Date(instant));
}

function preview(
  events: NayaxStockEventPreview[],
  message: string | null = null,
  hiddenReconciledCount = 0
): NayaxMachineStockSyncPreview {
  return {
    machineId: 7,
    newEventCount: events.length,
    events,
    productImpacts: [],
    hiddenReconciledCount,
    message
  };
}

interface Harness {
  component: MachineRestockSyncComponent;
  syncRestock: jest.Mock;
  applySyncRestock: jest.Mock;
  resolveSyncRestockDuplicate: jest.Mock;
  resolveSyncRestockManually: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock };
  applied: number;
}

function createHarness(
  syncRestock: jest.Mock = jest.fn(() => of(preview([]))),
  applySyncRestock: jest.Mock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse)),
  resolveSyncRestockDuplicate: jest.Mock = jest.fn(() =>
    of({ eventId: 1, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null } as NayaxStockEventApplyResult)),
  resolveSyncRestockManually: jest.Mock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse)),
  sessionActive: Observable<boolean> = of(false)
): Harness {
  const machineService = {
    syncRestock, applySyncRestock, resolveSyncRestockDuplicate, resolveSyncRestockManually
  } as unknown as MachineService;
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
  // The business time zone the shell loads at sign-in (issue #499), already resolved: the From
  // date filter is a calendar day in it, and this harness runs as the Sydney business these
  // expectations were written for.
  const businessTimeZone = new BusinessTimeZoneService();
  businessTimeZone.publish(BUSINESS_TIME_ZONE);
  const productService = { getAll: jest.fn(() => of([])) } as unknown as ProductService;
  const component = new MachineRestockSyncComponent(
    machineService, toast as unknown as ToastService, businessTimeZone, supportSessionStub(sessionActive), productService);
  component.machineId = 7;

  const harness: Harness = {
    component, syncRestock, applySyncRestock, resolveSyncRestockDuplicate, resolveSyncRestockManually, toast, applied: 0
  };
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

  it('never offers an unresolved possible duplicate for apply', () => {
    const { component } = createHarness();
    const duplicate = event({ isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.None });
    expect(component.isReadyToApply(duplicate)).toBe(false);
    expect(component.isUnresolvedDuplicate(duplicate)).toBe(true);
  });

  it('never re-offers a duplicate already resolved as a separate restock for apply again', () => {
    const { component } = createHarness();
    const resolved = event({ isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.AppliedAsSeparateRestock });
    expect(component.isUnresolvedDuplicate(resolved)).toBe(false);
    expect(component.isReadyToApply(resolved)).toBe(false);
  });

  it('never offers an event already reconciled as recorded manually for apply again', () => {
    const { component } = createHarness();
    const reconciled = event({ isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.ReconciledManually });
    expect(component.isUnresolvedDuplicate(reconciled)).toBe(false);
    expect(component.isReadyToApply(reconciled)).toBe(false);
  });
});

describe('MachineRestockSyncComponent isEligibleForManualResolution (issue #242)', () => {
  it('is eligible for a ready-to-apply matched event', () => {
    const { component } = createHarness();
    expect(component.isEligibleForManualResolution(event({}))).toBe(true);
  });

  it('is eligible for a needs-review event the ordinary Apply path would refuse', () => {
    const { component } = createHarness();
    const needsReview = event({ matchStatus: NayaxStockEventMatchStatus.NeedsReview, needsReviewReason: 'Unknown MDB' });
    expect(component.isEligibleForManualResolution(needsReview)).toBe(true);
  });

  it('is eligible for a negative discrepancy', () => {
    const { component } = createHarness();
    expect(component.isEligibleForManualResolution(event({ parsedQuantity: -3, isDiscrepancy: true }))).toBe(true);
  });

  it('is eligible for an insufficient-storage event', () => {
    const { component } = createHarness();
    const short = event({ parsedQuantity: 5, availableStorageQuantity: 4, isInsufficientStock: true, unaccountedDifference: 1 });
    expect(component.isEligibleForManualResolution(short)).toBe(true);
  });

  it('is eligible for an unresolved possible duplicate, even though the app suggested it - the suggestion is a hint, not a precondition', () => {
    const { component } = createHarness();
    const duplicate = event({ isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.None });
    expect(component.isEligibleForManualResolution(duplicate)).toBe(true);
  });

  it('is never eligible for an event already reconciled as recorded manually', () => {
    const { component } = createHarness();
    const reconciled = event({ duplicateResolution: NayaxDuplicateResolution.ReconciledManually });
    expect(component.isEligibleForManualResolution(reconciled)).toBe(false);
  });

  it('is never eligible for an event already applied as a separate restock', () => {
    const { component } = createHarness();
    const resolved = event({ duplicateResolution: NayaxDuplicateResolution.AppliedAsSeparateRestock });
    expect(component.isEligibleForManualResolution(resolved)).toBe(false);
  });
});

describe('MachineRestockSyncComponent resolveSelectedAsAlreadyRecorded (issue #242)', () => {
  it('does nothing without a machine id', () => {
    const { component, resolveSyncRestockManually } = createHarness();
    component.machineId = null;
    component.toggleEventSelection(1, true);

    component.resolveSelectedAsAlreadyRecorded();

    expect(resolveSyncRestockManually).not.toHaveBeenCalled();
  });

  it('does nothing when no event is selected', () => {
    const { component, resolveSyncRestockManually } = createHarness();

    component.resolveSelectedAsAlreadyRecorded();

    expect(resolveSyncRestockManually).not.toHaveBeenCalled();
  });

  it('resolves every selected event id and refreshes the preview', () => {
    const resolveSyncRestockManually = jest.fn(() => of({
      results: [
        { eventId: 1, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null },
        { eventId: 2, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null }
      ]
    } as NayaxMachineStockApplyResponse));
    const syncRestock = jest.fn(() => of(preview([])));
    const harness = createHarness(syncRestock, jest.fn(), jest.fn(), resolveSyncRestockManually);
    harness.component.toggleEventSelection(1, true);
    harness.component.toggleEventSelection(2, true);

    harness.component.resolveSelectedAsAlreadyRecorded();

    expect(resolveSyncRestockManually).toHaveBeenCalledWith(7, [1, 2]);
    expect(harness.toast.success).toHaveBeenCalledWith('Reconciled 2 event(s) as already recorded manually.');
    expect(harness.component.selectedEventIds.size).toBe(0);
    expect(harness.component.resolvingManyEvents$.value).toBe(false);
    expect(syncRestock).toHaveBeenCalledTimes(1);
  });

  it('warns about events the backend refused and keeps them selected for review, without silently dropping them', () => {
    const resolveSyncRestockManually = jest.fn(() => of({
      results: [
        { eventId: 1, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null },
        { eventId: 2, outcome: NayaxStockEventApplyOutcome.Error, message: 'Event not found for this machine.', stockAdjustmentId: null }
      ]
    } as NayaxMachineStockApplyResponse));
    const harness = createHarness(jest.fn(() => of(preview([]))), jest.fn(), jest.fn(), resolveSyncRestockManually);
    harness.component.toggleEventSelection(1, true);
    harness.component.toggleEventSelection(2, true);

    harness.component.resolveSelectedAsAlreadyRecorded();

    expect(harness.toast.success).toHaveBeenCalledWith('Reconciled 1 event(s) as already recorded manually.');
    expect(harness.toast.warning).toHaveBeenCalledWith('1 event(s) could not be resolved and remain for review.');
  });

  it('keeps the selection and reports the failure when the request fails', () => {
    const resolveSyncRestockManually = jest.fn(() => throwError(() => new Error('network error')));
    const harness = createHarness(jest.fn(() => of(preview([]))), jest.fn(), jest.fn(), resolveSyncRestockManually);
    harness.component.toggleEventSelection(1, true);

    harness.component.resolveSelectedAsAlreadyRecorded();

    expect(harness.component.resolvingManyEvents$.value).toBe(false);
    expect(harness.component.selectedEventIds.size).toBe(1);
    expect(harness.toast.error).toHaveBeenCalledWith('Failed to resolve the selected events as already recorded manually.');
  });
});

describe('MachineRestockSyncComponent resolveDuplicate', () => {
  it('does nothing without a machine id', () => {
    const { component, resolveSyncRestockDuplicate } = createHarness();
    component.machineId = null;

    component.resolveDuplicate(event({ id: 3 }), NayaxDuplicateResolutionChoice.AlreadyRecordedManually);

    expect(resolveSyncRestockDuplicate).not.toHaveBeenCalled();
  });

  it('reconciles as already recorded manually and re-syncs the preview', () => {
    const syncRestock = jest.fn(() => of(preview([])));
    const resolveSyncRestockDuplicate = jest.fn(() =>
      of({ eventId: 3, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null } as NayaxStockEventApplyResult));
    const { component, toast } = createHarness(syncRestock, jest.fn(), resolveSyncRestockDuplicate);

    component.resolveDuplicate(event({ id: 3 }), NayaxDuplicateResolutionChoice.AlreadyRecordedManually);

    expect(resolveSyncRestockDuplicate).toHaveBeenCalledWith(7, 3, NayaxDuplicateResolutionChoice.AlreadyRecordedManually);
    expect(toast.success).toHaveBeenCalledWith('Reconciled as already recorded manually; no Nayax movement was applied.');
    expect(syncRestock).toHaveBeenCalledTimes(1);
    expect(component.resolvingEventId$.value).toBeNull();
  });

  it('applies as a separate restock and tells the page to refresh products', () => {
    const resolveSyncRestockDuplicate = jest.fn(() =>
      of({ eventId: 3, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied', stockAdjustmentId: 55 } as NayaxStockEventApplyResult));
    const harness = createHarness(jest.fn(() => of(preview([]))), jest.fn(), resolveSyncRestockDuplicate);

    harness.component.resolveDuplicate(event({ id: 3 }), NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock);

    expect(resolveSyncRestockDuplicate).toHaveBeenCalledWith(7, 3, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock);
    expect(harness.toast.success).toHaveBeenCalledWith('Applied as a separate restock.');
    expect(harness.applied).toBe(1);
  });

  it('reports a refused resolution as a warning rather than a success', () => {
    const resolveSyncRestockDuplicate = jest.fn(() =>
      of({ eventId: 3, outcome: NayaxStockEventApplyOutcome.NotApplicable, message: 'Not a duplicate.', stockAdjustmentId: null } as NayaxStockEventApplyResult));
    const harness = createHarness(jest.fn(() => of(preview([]))), jest.fn(), resolveSyncRestockDuplicate);

    harness.component.resolveDuplicate(event({ id: 3 }), NayaxDuplicateResolutionChoice.AlreadyRecordedManually);

    expect(harness.toast.warning).toHaveBeenCalledWith('Not a duplicate.');
    expect(harness.applied).toBe(0);
  });

  it('reports an upstream failure and keeps the event actionable', () => {
    const resolveSyncRestockDuplicate = jest.fn(() => throwError(() => new Error('network error')));
    const harness = createHarness(jest.fn(() => of(preview([]))), jest.fn(), resolveSyncRestockDuplicate);

    harness.component.resolveDuplicate(event({ id: 3 }), NayaxDuplicateResolutionChoice.AlreadyRecordedManually);

    expect(harness.component.resolvingEventId$.value).toBeNull();
    expect(harness.toast.error).toHaveBeenCalledWith('Failed to resolve the possible duplicate.');
  });

  // Issue #1248: the other resolution won the race, so the server answered 409 having written
  // nothing. The server's own sentence says which way the event went, and the list is reloaded so
  // the row stops offering a decision that has already been taken.
  it('reports a 409 conflict with the server sentence and re-syncs the preview', () => {
    const conflict = new HttpErrorResponse({
      status: 409,
      error: {
        eventId: 3,
        outcome: NayaxStockEventApplyOutcome.ResolutionConflict,
        message: 'This event has already been applied as a Nayax inventory movement and cannot be marked as already recorded manually.',
        stockAdjustmentId: null
      } as NayaxStockEventApplyResult
    });
    const syncRestock = jest.fn(() => of(preview([])));
    const resolveSyncRestockDuplicate = jest.fn(() => throwError(() => conflict));
    const harness = createHarness(syncRestock, jest.fn(), resolveSyncRestockDuplicate);

    harness.component.resolveDuplicate(event({ id: 3 }), NayaxDuplicateResolutionChoice.AlreadyRecordedManually);

    expect(harness.toast.warning).toHaveBeenCalledWith(
      'This event has already been applied as a Nayax inventory movement and cannot be marked as already recorded manually.');
    expect(harness.toast.error).not.toHaveBeenCalled();
    expect(syncRestock).toHaveBeenCalledTimes(1);
    expect(harness.component.resolvingEventId$.value).toBeNull();
    expect(harness.applied).toBe(0);
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

  it('opens the reconciliation dialog when a sync starts', () => {
    const { component } = createHarness();

    component.syncRestock();

    expect(component.modalOpen$.value).toBe(true);
  });

  it('reports an upstream failure in the dialog and clears the syncing state', () => {
    const { component, toast } = createHarness(jest.fn(() => throwError(() => new Error('upstream unavailable'))));

    component.syncRestock();

    expect(component.syncing$.value).toBe(false);
    expect(component.modalOpen$.value).toBe(true);
    expect(component.syncError$.value).toBe('Failed to sync Nayax stock-adjustment alerts.');
    expect(toast.error).toHaveBeenCalledWith('Failed to sync Nayax stock-adjustment alerts.');
  });

  it('defaults the From date to three business-timezone calendar days before today and shows reconciled off', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const { component } = createHarness(syncRestock);
    const today = currentDateInTimeZone(new Date(), BUSINESS_TIME_ZONE);
    const expected = shiftCalendarDate(today, -3);
    const pad = (n: number) => n.toString().padStart(2, '0');
    const expectedValue = `${expected.year}-${pad(expected.month)}-${pad(expected.day)}`;

    component.syncRestock();

    expect(component.fromDate$.value).toBe(expectedValue);
    expect(component.showReconciled$.value).toBe(false);
    const [, fromDateIso, includeReconciled] = syncRestock.mock.calls[0];
    expect(fromDateIso).toBe(startOfDayUtc(expected.year, expected.month, expected.day, BUSINESS_TIME_ZONE).toISOString());
    expect(includeReconciled).toBe(false);
  });

  /**
   * The loading state (issue #499): the From date filter is a calendar day in the business's own
   * timezone, so until that zone is known there is no correct window to ask for. Asking for an
   * unbounded one instead would quietly change which events the operator is shown.
   */
  it('reports the still-loading business timezone instead of syncing an unbounded window', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const machineService = { syncRestock } as unknown as MachineService;
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
    const productService = { getAll: jest.fn(() => of([])) } as unknown as ProductService;
    // Deliberately unresolved: GET /api/business/current has not answered yet.
    const component = new MachineRestockSyncComponent(
      machineService, toast as unknown as ToastService, new BusinessTimeZoneService(), supportSessionStub(), productService);
    component.machineId = 7;

    component.syncRestock();

    expect(syncRestock).not.toHaveBeenCalled();
    expect(component.fromDate$.value).toBe('');
    expect(component.syncing$.value).toBe(false);
    expect(component.syncError$.value).toContain('business time zone');
  });

  it('applies the default From date once the zone arrives, rather than syncing an unbounded window', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const machineService = { syncRestock } as unknown as MachineService;
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
    const businessTimeZone = new BusinessTimeZoneService();
    const productService = { getAll: jest.fn(() => of([])) } as unknown as ProductService;
    const component = new MachineRestockSyncComponent(
      machineService, toast as unknown as ToastService, businessTimeZone, supportSessionStub(), productService);
    component.machineId = 7;
    component.syncRestock();

    businessTimeZone.publish(BUSINESS_TIME_ZONE);
    component.onShowReconciledChange(true);

    const expected = shiftCalendarDate(currentDateInTimeZone(new Date(), BUSINESS_TIME_ZONE), -3);
    const [, fromDateIso] = syncRestock.mock.calls[0];
    expect(fromDateIso).toBe(startOfDayUtc(expected.year, expected.month, expected.day, BUSINESS_TIME_ZONE).toISOString());
  });

  it('resets the From date to three business-timezone calendar days back on every fresh sync, not just the first', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const { component } = createHarness(syncRestock);
    const today = currentDateInTimeZone(new Date(), BUSINESS_TIME_ZONE);
    const expected = shiftCalendarDate(today, -3);
    const pad = (n: number) => n.toString().padStart(2, '0');
    const expectedValue = `${expected.year}-${pad(expected.month)}-${pad(expected.day)}`;

    component.syncRestock();
    component.onFromDateChange('2020-01-01');
    component.closeModal();
    component.syncRestock();

    expect(component.fromDate$.value).toBe(expectedValue);
  });

  it('computes the three-day default across an AEDT (UTC+11) Canberra midnight boundary, not a fixed UTC+10 offset', () => {
    // 2026-06-18T13:30:00Z is 2026-06-18 23:30 AEST (UTC+10, winter): three Canberra calendar days
    // back is 2026-06-15.
    jest.useFakeTimers().setSystemTime(new Date('2026-06-18T13:30:00Z'));
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const { component } = createHarness(syncRestock);

    component.syncRestock();

    expect(component.fromDate$.value).toBe('2026-06-15');
    jest.useRealTimers();
  });

  /**
   * The Dashboard reminder's link (issue #948) hands over the oldest pending date it counted; the
   * dialog opens there only when that is a real date earlier than its own default.
   */
  describe('the pending From date the Dashboard reminder links with', () => {
    afterEach(() => jest.useRealTimers());

    function openedFromDate(pendingFromDate: string | null): string {
      jest.useFakeTimers().setSystemTime(new Date('2026-10-28T01:00:00Z')); // 28 Oct in Sydney
      const { component } = createHarness(jest.fn(() => of(preview([]))));
      component.pendingFromDate = pendingFromDate;
      component.syncRestock();
      return component.fromDate$.value;
    }

    it('opens at an earlier pending date and asks for that window', () => {
      const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
      jest.useFakeTimers().setSystemTime(new Date('2026-10-28T01:00:00Z'));
      const { component } = createHarness(syncRestock);
      component.pendingFromDate = '2026-10-16';

      component.syncRestock();

      expect(component.fromDate$.value).toBe('2026-10-16');
      const [, fromDateIso] = syncRestock.mock.calls[0];
      expect(fromDateIso).toBe(startOfDayUtc(2026, 10, 16, BUSINESS_TIME_ZONE).toISOString());
    });

    it('keeps the three-day default when there is no pending date', () => {
      expect(openedFromDate(null)).toBe('2026-10-25');
    });

    it('keeps the three-day default when the pending date is not earlier than it', () => {
      expect(openedFromDate('2026-10-25')).toBe('2026-10-25');
      expect(openedFromDate('2026-10-27')).toBe('2026-10-25');
    });

    it('ignores a malformed or impossible pending date', () => {
      expect(openedFromDate('16/10/2026')).toBe('2026-10-25');
      expect(openedFromDate('2026-02-30')).toBe('2026-10-25');
      expect(openedFromDate('')).toBe('2026-10-25');
    });

    it('still lets the operator change the date after opening at it', () => {
      jest.useFakeTimers().setSystemTime(new Date('2026-10-28T01:00:00Z'));
      const { component } = createHarness(jest.fn(() => of(preview([]))));
      component.pendingFromDate = '2026-10-16';
      component.syncRestock();

      component.onFromDateChange('');

      expect(component.fromDate$.value).toBe('');
    });
  });
});

/**
 * Superseded Sync Restock responses (issue #1011): the page reuses this panel when it moves between
 * machines, so a response for a machine the panel has since left - or a request a newer one
 * replaced - must change nothing: not the loading state, preview, error, selection or toasts.
 */
describe('MachineRestockSyncComponent superseded sync responses (issue #1011)', () => {
  function moveTo(component: MachineRestockSyncComponent, machineId: number): void {
    const previous = component.machineId;
    component.machineId = machineId;
    component.ngOnChanges({ machineId: new SimpleChange(previous, machineId, false) });
  }

  function requests() {
    const responses: Subject<NayaxMachineStockSyncPreview>[] = [];
    const syncRestock: jest.Mock = jest.fn((_machineId: number) => {
      const response = new Subject<NayaxMachineStockSyncPreview>();
      responses.push(response);
      return response.asObservable();
    });
    return { responses, syncRestock };
  }

  it('ignores machine A\'s late answer while machine B\'s newer request is outstanding', () => {
    const { responses, syncRestock } = requests();
    const { component, toast } = createHarness(syncRestock);

    component.syncRestock(); // machine 7
    moveTo(component, 8);
    component.syncRestock(); // machine 8
    expect(syncRestock).toHaveBeenCalledTimes(2);
    expect(syncRestock.mock.calls[1][0]).toBe(8);

    responses[0].next(preview([event({ id: 70 })]));
    responses[0].error(new Error('late failure for machine 7'));

    expect(component.syncing$.value).toBe(true);
    expect(component.syncPreview$.value).toBeNull();
    expect(component.syncError$.value).toBeNull();
    expect(component.selectedEventIds.size).toBe(0);
    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.error).not.toHaveBeenCalled();

    responses[1].next(preview([event({ id: 80, machineId: 8 })]));

    expect(component.syncing$.value).toBe(false);
    expect(component.syncPreview$.value?.events.map((e) => e.id)).toEqual([80]);
    expect([...component.selectedEventIds]).toEqual([80]);
    expect(toast.success).toHaveBeenCalledTimes(1);
  });

  it('ignores an obsolete answer for machine A after moving A to B and back to A', () => {
    const { responses, syncRestock } = requests();
    const { component, toast } = createHarness(syncRestock);

    component.syncRestock(); // first request for machine 7
    moveTo(component, 8);
    moveTo(component, 7);
    component.syncRestock(); // the current request for machine 7

    responses[0].error(new Error('obsolete failure'));
    responses[0].next(preview([event({ id: 1 })]));

    expect(component.syncing$.value).toBe(true);
    expect(component.syncError$.value).toBeNull();
    expect(component.syncPreview$.value).toBeNull();
    expect(toast.error).not.toHaveBeenCalled();
    expect(toast.success).not.toHaveBeenCalled();

    responses[1].next(preview([event({ id: 2 })]));

    expect(component.syncing$.value).toBe(false);
    expect(component.syncPreview$.value?.events.map((e) => e.id)).toEqual([2]);
  });

  it('cancels the in-flight request when the machine changes or the panel is destroyed', () => {
    const { responses, syncRestock } = requests();
    const { component } = createHarness(syncRestock);

    component.syncRestock();
    expect(responses[0].observed).toBe(true);
    moveTo(component, 8);
    expect(responses[0].observed).toBe(false);
    expect(component.syncing$.value).toBe(false);

    component.syncRestock();
    component.ngOnDestroy();
    expect(responses[1].observed).toBe(false);
  });

  it('lets only the newest request answer when a filter change replaces one in flight', () => {
    const { responses, syncRestock } = requests();
    const { component } = createHarness(syncRestock);

    component.syncRestock();
    component.onShowReconciledChange(true);

    responses[0].next(preview([event({ id: 1 })]));
    expect(component.syncPreview$.value).toBeNull();
    expect(component.syncing$.value).toBe(true);

    responses[1].next(preview([event({ id: 2 })]));
    expect(component.syncPreview$.value?.events.map((e) => e.id)).toEqual([2]);
    expect(component.syncing$.value).toBe(false);
  });
});

describe('MachineRestockSyncComponent filters', () => {
  it('Select all applicable selects exactly the currently visible/applicable events', () => {
    const ready1 = event({ id: 1 });
    const ready2 = event({ id: 2, matchedProductName: 'Chips' });
    const needsReview = event({ id: 3, matchStatus: NayaxStockEventMatchStatus.NeedsReview });
    const reconciled = event({ id: 4, isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.ReconciledManually });
    const duplicate = event({ id: 5, isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.None });
    const { component } = createHarness();
    const p = preview([ready1, ready2, needsReview, reconciled, duplicate]);

    component.toggleSelectAllApplicable(p, true);

    expect([...component.selectedEventIds].sort()).toEqual([1, 2]);
    expect(component.isAllApplicableSelected(p)).toBe(true);
  });

  it('clears the bulk selection without changing event state when Select all applicable is unchecked', () => {
    const ready = event({ id: 1 });
    const { component } = createHarness();
    const p = preview([ready]);
    component.toggleEventSelection(1, true);

    component.toggleSelectAllApplicable(p, false);

    expect(component.selectedEventIds.size).toBe(0);
  });

  it('reports Select all applicable as unchecked once there is nothing eligible to select', () => {
    const needsReview = event({ id: 1, matchStatus: NayaxStockEventMatchStatus.NeedsReview });
    const { component } = createHarness();

    expect(component.isAllApplicableSelected(preview([needsReview]))).toBe(false);
  });

  it('changing the From date fetches a new preview bounded by the business-timezone midnight of the new date, as a UTC instant', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const { component } = createHarness(syncRestock);
    component.syncRestock();
    syncRestock.mockClear();

    component.onFromDateChange('2026-09-01');

    expect(component.fromDate$.value).toBe('2026-09-01');
    const [machineId, fromDateIso, includeReconciled] = syncRestock.mock.calls[0];
    expect(machineId).toBe(7);
    // 2026-09-01 is in the Australian winter (AEST, UTC+10), so the business's midnight is the previous UTC calendar day.
    expect(fromDateIso).toBe(startOfDayUtc(2026, 9, 1, BUSINESS_TIME_ZONE).toISOString());
    expect(fromDateIso).toBe('2026-08-31T14:00:00.000Z');
    expect(includeReconciled).toBe(false);
  });

  it('interprets a From date at an AEDT (UTC+11) boundary as Canberra midnight, not a fixed UTC+10 offset', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const { component } = createHarness(syncRestock);
    component.syncRestock();
    syncRestock.mockClear();

    component.onFromDateChange('2026-01-15');

    const [, fromDateIso] = syncRestock.mock.calls[0];
    expect(fromDateIso).toBe('2026-01-14T13:00:00.000Z');
  });

  it('toggling Show reconciled fetches reconciled events without implying any state change', () => {
    const reconciled = event({ id: 9, isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.ReconciledManually });
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([], null, 1)))
      .mockReturnValueOnce(of(preview([reconciled], null, 0)));
    const { component } = createHarness(syncRestock);
    component.syncRestock();

    component.onShowReconciledChange(true);

    expect(syncRestock).toHaveBeenLastCalledWith(7, expect.any(String), true);
    expect(component.syncPreview$.value?.events).toEqual([reconciled]);
    expect(component.syncPreview$.value?.hiddenReconciledCount).toBe(0);
  });

  it('reconciles the selection with the newly visible/applicable set after a filter change', () => {
    const ready = event({ id: 1 });
    const short = event({ id: 2, parsedQuantity: 5, availableStorageQuantity: 4, isInsufficientStock: true });
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([ready, short])))
      .mockReturnValueOnce(of(preview([short])));
    const { component } = createHarness(syncRestock);
    component.syncRestock();
    expect([...component.selectedEventIds]).toEqual([1]);

    component.onFromDateChange('2026-09-15');

    // Event 1 is no longer part of the visible/applicable set after the filter change, so it
    // cannot be left selected for Apply.
    expect([...component.selectedEventIds]).toEqual([]);
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

describe('MachineRestockSyncComponent dialog', () => {
  const appliedResponse = (eventId: number): NayaxMachineStockApplyResponse => ({
    results: [{ eventId, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied', stockAdjustmentId: 90 }]
  });

  async function render(
    syncRestock: jest.Mock = jest.fn(() => of(preview([]))),
    applySyncRestock: jest.Mock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse)),
    resolveSyncRestockDuplicate: jest.Mock = jest.fn(() =>
      of({ eventId: 1, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null } as NayaxStockEventApplyResult)),
    resolveSyncRestockManually: jest.Mock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse)),
    nayaxStatus: NayaxConnectionStatusName = 'Ready',
    sessionActive: Observable<boolean> = of(false),
    applySyncRestockReplacement: jest.Mock = jest.fn(() =>
      of({ eventId: 9, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied.', stockAdjustmentId: 90 } as NayaxStockEventApplyResult)),
    products: Product[] = []
  ) {
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
    await TestBed.configureTestingModule({
      imports: [MachineRestockSyncComponent],
      providers: [
        provideRouter([]),
        {
          provide: MachineService,
          useValue: {
            syncRestock, applySyncRestock, resolveSyncRestockDuplicate, resolveSyncRestockManually,
            applySyncRestockReplacement
          }
        },
        { provide: ToastService, useValue: toast },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: nayaxStatus, machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: supportSessionStub(sessionActive) },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of(products)) } }
      ]
    }).compileComponents();

    // The business time zone the shell loads at sign-in (issue #499). The From date filter is a
    // calendar day in it, and the application's existing business is in Sydney - the same offsets
    // the expectations below were written against.
    TestBed.inject(BusinessTimeZoneService).publish(BUSINESS_TIME_ZONE);

    const fixture = TestBed.createComponent(MachineRestockSyncComponent);
    fixture.componentRef.setInput('machineId', 7);
    let appliedCount = 0;
    fixture.componentInstance.restockApplied.subscribe(() => appliedCount++);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;

    // `dialog`/`button` answer "is it there?" and may legitimately return nothing; the `require*`
    // helpers are for the cases that need the element itself. They throw a message naming what was
    // expected (and, for a button, what was actually rendered) so a missing element fails the test
    // where it is missing, instead of a non-null assertion turning it into a TypeError one line later.
    const dialog = () => host.querySelector<HTMLElement>('[role="dialog"]');
    const button = (label: string) =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim().startsWith(label));
    const requireElement = <T extends HTMLElement = HTMLElement>(selector: string): T => {
      const element = host.querySelector<T>(selector);
      if (element === null) {
        throw new Error(`Expected the rendered component to contain an element matching "${selector}", but it did not.`);
      }
      return element;
    };
    const requireDialog = (): HTMLElement => requireElement('[role="dialog"]');
    const requireButton = (label: string): HTMLButtonElement => {
      const found = button(label);
      if (found === undefined) {
        const rendered = Array.from(host.querySelectorAll('button')).map((b) => `"${b.textContent?.trim()}"`).join(', ');
        throw new Error(`Expected a button labelled "${label}", but the rendered buttons were: ${rendered || '(none)'}.`);
      }
      return found;
    };
    const requireAttribute = (element: HTMLElement, name: string): string => {
      const value = element.getAttribute(name);
      if (value === null) {
        throw new Error(`Expected <${element.tagName.toLowerCase()}> to carry a "${name}" attribute, but it did not.`);
      }
      return value;
    };
    const textOf = (element: HTMLElement): string => {
      const text = element.textContent;
      if (text === null) {
        throw new Error(`Expected <${element.tagName.toLowerCase()}> to have text content, but it had none.`);
      }
      return text;
    };
    const click = (element: HTMLElement) => {
      element.click();
      fixture.detectChanges();
    };
    const pressEscape = () => {
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
    };
    const openDialog = () => click(requireButton('Sync Restock'));

    return {
      fixture, host, toast, syncRestock, applySyncRestock, resolveSyncRestockDuplicate, resolveSyncRestockManually,
      applySyncRestockReplacement,
      dialog, button, requireElement, requireDialog, requireButton, requireAttribute, textOf,
      click, pressEscape, openDialog,
      applied: () => appliedCount
    };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('shows only the Sync Restock action on the page, with no inline preview', async () => {
    const { host, dialog, button, syncRestock } = await render();

    expect(button('Sync Restock')).toBeTruthy();
    expect(dialog()).toBeNull();
    expect(host.querySelector('table')).toBeNull();
    expect(syncRestock).not.toHaveBeenCalled();
  });

  it('clicking Sync Restock fetches the preview and opens an accessible dialog', async () => {
    const { requireDialog, requireAttribute, openDialog, syncRestock } = await render();

    openDialog();

    expect(syncRestock).toHaveBeenCalledWith(7, expect.any(String), false);
    const panel = requireDialog();
    expect(panel.getAttribute('aria-modal')).toBe('true');
    const titleId = requireAttribute(panel, 'aria-labelledby');
    expect(panel.querySelector(`#${titleId}`)?.textContent?.trim()).toBe('Sync Restock');
    expect(document.activeElement).toBe(panel);
  });

  it('renders the reconciliation preview inside the dialog', async () => {
    const ready = event({ id: 1 });
    const short = event({
      id: 2, parsedMdb: 14, matchedProductName: 'Cola', parsedQuantity: 5,
      availableStorageQuantity: 4, isInsufficientStock: true, unaccountedDifference: 1
    });
    const duplicate = event({
      id: 3, parsedMdb: 15, matchedProductName: 'Chips', isPossibleDuplicate: true, possibleDuplicateNotes: 'Manual refill'
    });
    const needsReview = event({
      id: 4, parsedMdb: 99, matchStatus: NayaxStockEventMatchStatus.NeedsReview, needsReviewReason: 'Unknown MDB'
    });
    const withImpacts: NayaxMachineStockSyncPreview = {
      ...preview([ready, short, duplicate, needsReview]),
      productImpacts: [{ productId: 55, productName: 'Beef Jerky', availableStorageQuantity: 10, pendingRefillQuantity: 2 }]
    };
    const { requireElement, requireDialog, textOf, openDialog } = await render(jest.fn(() => of(withImpacts)));

    openDialog();

    const table = requireElement<HTMLTableElement>('table');
    expect(requireDialog().contains(table)).toBe(true);
    expect(table.querySelectorAll('tbody tr').length).toBe(4);
    const text = textOf(requireDialog());
    expect(text).toContain('Pending refill: 2 · Available in storage: 10');
    expect(text).toContain('Insufficient storage: needs 5, has 4 (short 1)');
    expect(text).toContain('Possible duplicate');
    expect(text).toContain('Needs review: Unknown MDB');
    expect(text).toContain('Ready to apply');
  });

  it("renders the canonical GMT event time as the business's local time during AEST (UTC+10), not raw UTC", async () => {
    // 2026-06-15T00:00:00Z falls in the Australian winter, outside daylight saving (AEST, UTC+10).
    const aest = event({ id: 1, eventDateTimeGmt: '2026-06-15T00:00:00Z' });
    const { requireElement, openDialog } = await render(jest.fn(() => of(preview([aest]))));

    openDialog();

    const timeCell = requireElement<HTMLTableElement>('table').querySelector('tbody tr td:nth-child(2)');
    expect(timeCell?.textContent?.trim()).toBe('15/06/2026, 10:00 am');
  });

  it("renders the canonical GMT event time as the business's local time during AEDT (UTC+11), not raw UTC", async () => {
    // 2026-01-15T00:00:00Z falls in the Australian summer, inside daylight saving (AEDT, UTC+11).
    const aedt = event({ id: 1, eventDateTimeGmt: '2026-01-15T00:00:00Z' });
    const { requireElement, openDialog } = await render(jest.fn(() => of(preview([aedt]))));

    openDialog();

    const timeCell = requireElement<HTMLTableElement>('table').querySelector('tbody tr td:nth-child(2)');
    expect(timeCell?.textContent?.trim()).toBe('15/01/2026, 11:00 am');
  });

  it("rolls the displayed calendar date forward across the UTC/business day boundary rather than showing the UTC date", async () => {
    // 2026-06-14T14:30:00Z is still 14 June in UTC, but AEST (UTC+10) has already crossed into 15 June.
    const boundary = event({ id: 1, eventDateTimeGmt: '2026-06-14T14:30:00Z' });
    const { requireElement, openDialog } = await render(jest.fn(() => of(preview([boundary]))));

    openDialog();

    const timeCell = requireElement<HTMLTableElement>('table').querySelector('tbody tr td:nth-child(2)');
    expect(timeCell?.textContent?.trim()).toBe('15/06/2026, 12:30 am');
  });

  it('closes with the Close button, clears the preview, and returns focus to Sync Restock', async () => {
    const { host, dialog, requireButton, click, openDialog, fixture } = await render(jest.fn(() => of(preview([event({})]))));
    openDialog();

    click(requireButton('Close'));

    expect(dialog()).toBeNull();
    expect(host.querySelector('table')).toBeNull();
    expect(fixture.componentInstance.syncPreview$.value).toBeNull();
    expect(document.activeElement).toBe(requireButton('Sync Restock'));
  });

  it('closes with the header close control, Escape, and a backdrop click, but not a click inside the dialog', async () => {
    const { dialog, requireDialog, requireElement, click, pressEscape, openDialog } = await render();

    openDialog();
    click(requireElement<HTMLButtonElement>('[aria-label="Close Sync Restock"]'));
    expect(dialog()).toBeNull();

    openDialog();
    pressEscape();
    expect(dialog()).toBeNull();

    openDialog();
    click(requireDialog());
    expect(dialog()).not.toBeNull();
    click(requireElement('.sync-modal-backdrop'));
    expect(dialog()).toBeNull();
  });

  it('cannot be closed while an apply is in flight', async () => {
    const pendingApply = new Subject<NayaxMachineStockApplyResponse>();
    const { dialog, requireButton, click, pressEscape, openDialog } = await render(
      jest.fn(() => of(preview([event({ id: 1 })]))),
      jest.fn(() => pendingApply)
    );
    openDialog();
    click(requireButton('Apply selected'));

    expect(requireButton('Close').disabled).toBe(true);
    pressEscape();
    expect(dialog()).not.toBeNull();
  });

  it('lets the operator change the selection with the checkboxes', async () => {
    const ready = event({ id: 1 });
    const needsReview = event({ id: 2, matchStatus: NayaxStockEventMatchStatus.NeedsReview, needsReviewReason: 'Unknown MDB' });
    const { host, requireButton, click, openDialog, fixture } = await render(jest.fn(() => of(preview([ready, needsReview]))));
    openDialog();

    const [readyBox, reviewBox] = Array.from(host.querySelectorAll<HTMLInputElement>('tbody input[type="checkbox"]'));
    expect(readyBox.checked).toBe(true);
    // A Needs Review event is never pre-selected for Apply, but it is still an eligible unresolved
    // event, so it may be explicitly checked for the bulk "Already recorded manually" action
    // (issue #242).
    expect(reviewBox.checked).toBe(false);
    expect(reviewBox.disabled).toBe(false);
    expect(requireButton('Apply selected').textContent).toContain('(1)');
    expect(requireButton('Already recorded manually').textContent).toContain('(1)');

    click(readyBox);

    expect(fixture.componentInstance.isEventSelected(1)).toBe(false);
    expect(requireButton('Apply selected').textContent).toContain('(0)');
    expect(requireButton('Apply selected').disabled).toBe(true);

    click(reviewBox);

    expect(fixture.componentInstance.isEventSelected(2)).toBe(true);
    expect(requireButton('Already recorded manually').textContent).toContain('(1)');
    expect(requireButton('Already recorded manually').disabled).toBe(false);
  });

  it('applies the selected events, emits restockApplied, and keeps the dialog open without the applied event', async () => {
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([event({ id: 1 })])))
      .mockReturnValueOnce(of(preview([], 'No new Nayax stock-adjustment alerts to review.')));
    const { host, dialog, button, requireButton, click, openDialog, toast, applySyncRestock, applied } = await render(
      syncRestock,
      jest.fn(() => of(appliedResponse(1)))
    );
    openDialog();

    click(requireButton('Apply selected'));

    expect(applySyncRestock).toHaveBeenCalledWith(7, [1]);
    expect(applied()).toBe(1);
    expect(toast.success).toHaveBeenCalledWith('Applied 1 Nayax stock-adjustment event(s).');
    expect(syncRestock).toHaveBeenCalledTimes(2);
    expect(dialog()).not.toBeNull();
    expect(host.querySelector('tbody tr')).toBeNull();
    expect(button('Apply selected')).toBeUndefined();
    expect(host.querySelector('[data-testid="sync-restock-empty"]')?.textContent)
      .toContain('No new Nayax stock-adjustment alerts to review.');
  });

  it('shows a sync failure inside the dialog', async () => {
    const { requireDialog, openDialog, toast } = await render(jest.fn(() => throwError(() => new Error('upstream unavailable'))));

    openDialog();

    expect(requireDialog().querySelector('[role="alert"]')?.textContent).toContain('Failed to sync Nayax stock-adjustment alerts.');
    expect(toast.error).toHaveBeenCalledWith('Failed to sync Nayax stock-adjustment alerts.');
  });

  it('shows an apply failure inside the dialog and keeps the events actionable', async () => {
    const { host, requireDialog, requireButton, click, openDialog, applied } = await render(
      jest.fn(() => of(preview([event({ id: 1 })]))),
      jest.fn(() => throwError(() => new Error('network error')))
    );
    openDialog();

    click(requireButton('Apply selected'));

    expect(requireDialog().querySelector('[role="alert"]')?.textContent)
      .toContain('Failed to apply the selected Nayax stock-adjustment events.');
    expect(applied()).toBe(0);
    expect(host.querySelectorAll('tbody tr').length).toBe(1);
    expect(requireButton('Apply selected').disabled).toBe(false);
  });

  it('shows the empty state inside the dialog with no apply action', async () => {
    const { host, requireElement, requireDialog, button, openDialog } = await render(
      jest.fn(() => of(preview([], 'No new Nayax stock-adjustment alerts to review.')))
    );

    openDialog();

    const empty = requireElement('[data-testid="sync-restock-empty"]');
    expect(requireDialog().contains(empty)).toBe(true);
    expect(empty.textContent).toContain('No new Nayax stock-adjustment alerts to review.');
    expect(host.querySelector('table')).toBeNull();
    expect(button('Apply selected')).toBeUndefined();
    expect(button('Close')).toBeTruthy();
  });

  it('never pre-selects an unresolved possible duplicate as an ordinary Apply item, but it stays checkbox-eligible for bulk manual resolution', async () => {
    const duplicate = event({ id: 5, isPossibleDuplicate: true, possibleDuplicateNotes: 'Manual refill' });
    const { host, requireElement, openDialog } = await render(jest.fn(() => of(preview([duplicate]))));

    openDialog();

    const checkbox = requireElement<HTMLInputElement>('tbody input[type="checkbox"]');
    expect(checkbox.checked).toBe(false);
    // A flagged possible duplicate is only a suggestion for human resolution, not a precondition
    // (issue #242): it stays selectable so it can be included in the bulk "Already recorded
    // manually" action alongside events the app never flagged.
    expect(checkbox.disabled).toBe(false);
    expect(host.querySelector('[data-testid="possible-duplicate-badge"]')).not.toBeNull();
  });

  it('shows both quantities and the matching manual refill time on a flagged possible duplicate', async () => {
    // Issue #1244: packed 20, loaded 15. The operator decides, so the screen has to show the two
    // quantities rather than only asserting that the event looks like a duplicate.
    const duplicate = event({
      id: 5,
      parsedQuantity: 15,
      isPossibleDuplicate: true,
      possibleDuplicateNotes: 'Manual refill',
      possibleDuplicateQuantity: 20,
      possibleDuplicateEffectiveAt: '2026-09-27T09:00:00Z'
    });
    const { requireElement, openDialog } = await render(jest.fn(() => of(preview([duplicate]))));

    openDialog();

    const comparison = requireElement('[data-testid="possible-duplicate-comparison"]');
    expect(comparison.textContent).toContain('Nayax event 15');
    expect(comparison.textContent).toContain('manual refill 20');
    expect(comparison.textContent).toContain(inBusinessTime('2026-09-27T09:00:00Z'));
  });

  it('says how many other manual refills also qualified', async () => {
    const duplicate = event({
      id: 5,
      parsedQuantity: 15,
      isPossibleDuplicate: true,
      possibleDuplicateQuantity: 20,
      possibleDuplicateEffectiveAt: '2026-09-27T09:00:00Z',
      otherPossibleDuplicateCount: 2
    });
    const { requireElement, openDialog } = await render(jest.fn(() => of(preview([duplicate]))));

    openDialog();

    expect(requireElement('[data-testid="possible-duplicate-others"]').textContent)
      .toContain('2 other manual refills also qualified');
  });

  it('says nothing about other manual refills when only one qualified', async () => {
    const alone = event({
      id: 6,
      isPossibleDuplicate: true,
      possibleDuplicateQuantity: 4,
      possibleDuplicateEffectiveAt: '2026-09-27T09:00:00Z'
    });
    const { host, openDialog } = await render(jest.fn(() => of(preview([alone]))));

    openDialog();

    expect(host.querySelector('[data-testid="possible-duplicate-comparison"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="possible-duplicate-others"]')).toBeNull();
  });

  describe('product replacements (issue #1325)', () => {
    it('shows both Nayax names and is never ready to apply through the ordinary Apply path', async () => {
      const replacement = replacementEvent();
      const { fixture, host, requireElement, openDialog } = await render(jest.fn(() => of(preview([replacement]))));

      openDialog();

      expect(requireElement('[data-testid="replacement-names"]').textContent)
        .toContain('V Energy 250ML replaced by Redbull Red Edition');
      expect(requireElement('[data-testid="replacement-badge"]').textContent).toContain('Product replacement');
      // A parsed replacement has no review reason of its own, so no empty "Needs review:" badge.
      expect(host.textContent).not.toContain('Needs review:');
      expect(fixture.componentInstance.isReadyToApply(replacement)).toBe(false);
      expect(fixture.componentInstance.isEligibleForManualResolution(replacement)).toBe(true);
    });

    it('shows an unparsed replacement as needs review with its reason', async () => {
      const unparsed = event({
        id: 10,
        eventCode: NayaxMachineAlertEventCodes.MachineProductChanged,
        parsedMdb: null,
        parsedProductName: null,
        parsedQuantity: null,
        matchedProductId: null,
        matchedProductName: null,
        matchStatus: NayaxStockEventMatchStatus.NeedsReview,
        needsReviewReason: 'EventData does not contain the supported form.'
      });
      const { host, openDialog } = await render(jest.fn(() => of(preview([unparsed]))));

      openDialog();

      expect(host.textContent).toContain('Needs review: EventData does not contain the supported form.');
      expect(host.querySelector('[data-testid="replacement-badge"]')).not.toBeNull();
    });

    it('flags a possible double count on the row that carries it', async () => {
      const flagged = event({
        id: 11,
        isPossibleReplacementDoubleCount: true,
        replacementDoubleCountNotes: 'A pending product replacement for MDB 58 within 24 hours may record the same refill.'
      });
      const { requireElement, openDialog } = await render(jest.fn(() => of(preview([flagged]))));

      openDialog();

      expect(requireElement('[data-testid="replacement-double-count"]').textContent).toContain('Possible double count');
    });

    it('marks a selected replacement as already recorded but never sends it to Apply', async () => {
      const replacement = replacementEvent();
      const resolveSyncRestockManually = jest.fn(() => of({
        results: [{ eventId: 9, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null }]
      } as NayaxMachineStockApplyResponse));
      const applySyncRestock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse));
      const { fixture, requireElement, requireButton, click, openDialog } = await render(
        jest.fn(() => of(preview([replacement]))), applySyncRestock, undefined, resolveSyncRestockManually
      );
      openDialog();

      click(requireElement<HTMLInputElement>('tbody input[type="checkbox"]'));

      expect(requireButton('Apply selected').disabled).toBe(true);
      fixture.componentInstance.applySelectedEvents();
      expect(applySyncRestock).not.toHaveBeenCalled();

      click(requireButton('Already recorded manually'));
      expect(resolveSyncRestockManually).toHaveBeenCalledWith(7, [9]);
    });
  });

  describe('product-change row apply (issue #1327)', () => {
    const INCOMING = product({ id: 300, name: 'Red Bull Red Edition 250mL', quantityInStock: 12 });
    const OUTGOING = product({ id: 301, name: 'V Energy 250ML', quantityInStock: 5 });

    const incomingInput = (host: HTMLElement) =>
      host.querySelector<HTMLInputElement>('[data-testid="replacement-incoming-product-9"]')!;
    const outgoingInput = (host: HTMLElement) =>
      host.querySelector<HTMLInputElement>('[data-testid="replacement-outgoing-product-9"]')!;
    const incomingQuantityInput = (host: HTMLElement) =>
      host.querySelector<HTMLInputElement>('[data-testid="replacement-incoming-quantity-9"]')!;
    const outgoingQuantityInput = (host: HTMLElement) =>
      host.querySelector<HTMLInputElement>('[data-testid="replacement-outgoing-quantity-9"]')!;
    const dispositionSelect = (host: HTMLElement) =>
      host.querySelector<HTMLSelectElement>('[data-testid="replacement-disposition-9"]')!;
    const applyButton = (host: HTMLElement) =>
      host.querySelector<HTMLButtonElement>('[data-testid="replacement-apply-button"]')!;

    it("prefills both autocompletes and the quantity restocked from #1325's suggestion", async () => {
      const replacement = replacementEvent();
      const { host, requireElement, openDialog } = await render(
        jest.fn(() => of(preview([replacement]))), undefined, undefined, undefined, 'Ready', of(false), undefined,
        [INCOMING, OUTGOING]
      );

      openDialog();

      expect(incomingInput(host).value).toBe('Red Bull Red Edition 250mL');
      expect(outgoingInput(host).value).toBe('V Energy 250ML');
      expect(incomingQuantityInput(host).value).toBe('7');
      expect(requireElement('[data-testid="replacement-incoming-storage"]').textContent).toContain('12');
      expect(requireElement('[data-testid="replacement-incoming-quantity-hint"]').textContent)
        .toContain('May be low if the machine has sold some since the replacement.');
      expect(host.querySelector('[data-testid="replacement-incoming-quantity-reason"]')).toBeNull();
      // "None" is the default outgoing disposition, and Quantity removed starts disabled.
      expect(dispositionSelect(host).value).toBe(String(NayaxReplacementOutgoingDisposition.None));
      expect(outgoingQuantityInput(host).disabled).toBe(true);
    });

    it('shows each empty suggestion as an empty autocomplete with its reason', async () => {
      const replacement = replacementEvent({}, {
        suggestedOutgoingProductId: null,
        suggestedOutgoingProductName: null,
        outgoingSuggestionSource: NayaxReplacementSuggestionSource.None,
        outgoingSuggestionReason: "More than one active product is named 'V Energy 250ML'.",
        suggestedIncomingProductId: null,
        suggestedIncomingProductName: null,
        incomingSuggestionSource: NayaxReplacementSuggestionSource.None,
        incomingSuggestionReason: "No active product is named 'Redbull Red Edition'.",
        suggestedIncomingQuantity: null,
        suggestedIncomingQuantityReason: "MDB 58 now reports 'Mother 500mL', not 'Redbull Red Edition'."
      });
      const { host, requireElement, openDialog } = await render(jest.fn(() => of(preview([replacement]))));

      openDialog();

      expect(incomingInput(host).value).toBe('');
      expect(outgoingInput(host).value).toBe('');
      expect(incomingQuantityInput(host).value).toBe('');
      expect(requireElement('[data-testid="replacement-incoming-no-suggestion"]').textContent)
        .toContain('No active product is named');
      expect(requireElement('[data-testid="replacement-outgoing-no-suggestion"]').textContent)
        .toContain('More than one active product');
      expect(requireElement('[data-testid="replacement-incoming-quantity-reason"]').textContent)
        .toContain("now reports 'Mother 500mL'");
      expect(host.querySelector('[data-testid="replacement-incoming-quantity-hint"]')).toBeNull();
    });

    it('disables Quantity removed while None is selected, and clears a typed quantity when None is chosen again', async () => {
      const replacement = replacementEvent();
      const { fixture, host, openDialog } = await render(
        jest.fn(() => of(preview([replacement]))), undefined, undefined, undefined, 'Ready', of(false), undefined,
        [INCOMING, OUTGOING]
      );
      openDialog();

      expect(outgoingQuantityInput(host).disabled).toBe(true);

      dispositionSelect(host).value = String(NayaxReplacementOutgoingDisposition.ReturnedToStorage);
      dispositionSelect(host).dispatchEvent(new Event('change'));
      fixture.detectChanges();
      expect(outgoingQuantityInput(host).disabled).toBe(false);

      outgoingQuantityInput(host).value = '3';
      outgoingQuantityInput(host).dispatchEvent(new Event('input'));
      fixture.detectChanges();
      expect(outgoingQuantityInput(host).value).toBe('3');

      dispositionSelect(host).value = String(NayaxReplacementOutgoingDisposition.None);
      dispositionSelect(host).dispatchEvent(new Event('change'));
      fixture.detectChanges();
      expect(outgoingQuantityInput(host).disabled).toBe(true);
      expect(outgoingQuantityInput(host).value).toBe('');
    });

    it('shows the insufficient-storage warning once the entered quantity exceeds what storage holds', async () => {
      const replacement = replacementEvent({}, { suggestedIncomingQuantity: 20 });
      const { requireElement, openDialog } = await render(
        jest.fn(() => of(preview([replacement]))), undefined, undefined, undefined, 'Ready', of(false), undefined,
        [INCOMING, OUTGOING]
      );
      openDialog();

      expect(requireElement('[data-testid="replacement-incoming-insufficient"]').textContent)
        .toContain('Insufficient storage: needs 20, has 12 (short 8)');
    });

    it('enables Apply only once the incoming product is chosen and every input is valid', async () => {
      const replacement = replacementEvent({}, {
        suggestedIncomingProductId: null,
        suggestedIncomingProductName: null,
        suggestedOutgoingProductId: null,
        suggestedOutgoingProductName: null
      });
      const { fixture, host, openDialog } = await render(
        jest.fn(() => of(preview([replacement]))), undefined, undefined, undefined, 'Ready', of(false), undefined,
        [INCOMING, OUTGOING]
      );
      openDialog();

      // No incoming product chosen yet (the suggestion was empty): Apply is disabled.
      expect(applyButton(host).disabled).toBe(true);

      fixture.componentInstance.setReplacementIncomingProduct(replacement, 300);
      fixture.detectChanges();
      // A suggested quantity of 7 still stands, so the row is now valid.
      expect(applyButton(host).disabled).toBe(false);

      dispositionSelect(host).value = String(NayaxReplacementOutgoingDisposition.Damaged);
      dispositionSelect(host).dispatchEvent(new Event('change'));
      fixture.detectChanges();
      // Damaged requires an outgoing product and a quantity of at least one; neither is set yet.
      expect(applyButton(host).disabled).toBe(true);

      fixture.componentInstance.setReplacementOutgoingProduct(replacement, 301);
      outgoingQuantityInput(host).value = '2';
      outgoingQuantityInput(host).dispatchEvent(new Event('input'));
      fixture.detectChanges();
      expect(applyButton(host).disabled).toBe(false);
    });

    it('shows a short confirmation of what will be written, then applies and re-syncs on confirm', async () => {
      const replacement = replacementEvent();
      const syncRestock = jest.fn()
        .mockReturnValueOnce(of(preview([replacement])))
        .mockReturnValueOnce(of(preview([], 'No new Nayax stock-adjustment alerts to review.')));
      const applySyncRestockReplacement = jest.fn(() =>
        of({ eventId: 9, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied.', stockAdjustmentId: 90 } as NayaxStockEventApplyResult));
      const { fixture, host, requireElement, click, openDialog, applied } = await render(
        syncRestock, undefined, undefined, undefined, 'Ready', of(false), applySyncRestockReplacement,
        [INCOMING, OUTGOING]
      );
      openDialog();

      dispositionSelect(host).value = String(NayaxReplacementOutgoingDisposition.ReturnedToStorage);
      dispositionSelect(host).dispatchEvent(new Event('change'));
      fixture.detectChanges();
      outgoingQuantityInput(host).value = '4';
      outgoingQuantityInput(host).dispatchEvent(new Event('input'));
      fixture.detectChanges();

      click(applyButton(host));

      const confirmation = requireElement('[role="alertdialog"]');
      expect(confirmation.textContent).toContain('Record 7 unit(s) of Red Bull Red Edition 250mL loaded into the machine.');
      expect(confirmation.textContent).toContain('4 unit(s) of V Energy 250ML will be returned to storage.');

      click(requireElement<HTMLButtonElement>('[role="alertdialog"] button.btn-danger'));

      const expectedRequest: NayaxApplyProductReplacementRequest = {
        eventId: 9,
        incomingProductId: 300,
        incomingQuantity: 7,
        outgoingDisposition: NayaxReplacementOutgoingDisposition.ReturnedToStorage,
        outgoingProductId: 301,
        outgoingQuantity: 4
      };
      expect(applySyncRestockReplacement).toHaveBeenCalledWith(7, expectedRequest);
      expect(syncRestock).toHaveBeenCalledTimes(2);
      expect(applied()).toBe(1);
    });

    it("shows the server's refusal on the row without closing the dialog", async () => {
      const replacement = replacementEvent();
      const applySyncRestockReplacement = jest.fn(() =>
        of({
          eventId: 9, outcome: NayaxStockEventApplyOutcome.InsufficientStock,
          message: '7 was entered as loaded but only 3 is available in storage.', stockAdjustmentId: null
        } as NayaxStockEventApplyResult));
      const { host, requireDialog, requireElement, click, openDialog } = await render(
        jest.fn(() => of(preview([replacement]))), undefined, undefined, undefined, 'Ready', of(false), applySyncRestockReplacement,
        [INCOMING, OUTGOING]
      );
      openDialog();

      click(applyButton(host));
      click(requireElement<HTMLButtonElement>('[role="alertdialog"] button.btn-danger'));

      expect(applySyncRestockReplacement).toHaveBeenCalled();
      expect(requireElement('[data-testid="replacement-apply-error"]').textContent)
        .toContain('7 was entered as loaded but only 3 is available in storage.');
      // The refusal is shown on the row; the dialog itself stays open.
      expect(requireDialog()).not.toBeNull();
    });
  });

  it('resolves a possible duplicate as already recorded manually and refreshes the preview', async () => {
    const duplicate = event({ id: 5, isPossibleDuplicate: true, possibleDuplicateNotes: 'Manual refill' });
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([duplicate])))
      .mockReturnValueOnce(of(preview([], 'No new Nayax stock-adjustment alerts to review.')));
    const resolveSyncRestockDuplicate = jest.fn(() =>
      of({ eventId: 5, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null } as NayaxStockEventApplyResult));
    const { host, toast, openDialog, requireButton, click, applied } = await render(
      syncRestock, jest.fn(), resolveSyncRestockDuplicate
    );
    openDialog();

    click(requireButton('Already recorded manually'));

    expect(resolveSyncRestockDuplicate).toHaveBeenCalledWith(7, 5, NayaxDuplicateResolutionChoice.AlreadyRecordedManually);
    expect(toast.success).toHaveBeenCalledWith('Reconciled as already recorded manually; no Nayax movement was applied.');
    expect(syncRestock).toHaveBeenCalledTimes(2);
    expect(applied()).toBe(0);
    expect(host.querySelector('[data-testid="sync-restock-empty"]')).not.toBeNull();
  });

  it('applies a possible duplicate as a separate restock and emits restockApplied', async () => {
    const duplicate = event({ id: 5, isPossibleDuplicate: true, possibleDuplicateNotes: 'Manual refill' });
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([duplicate])))
      .mockReturnValueOnce(of(preview([], 'No new Nayax stock-adjustment alerts to review.')));
    const resolveSyncRestockDuplicate = jest.fn(() =>
      of({ eventId: 5, outcome: NayaxStockEventApplyOutcome.Applied, message: 'Applied', stockAdjustmentId: 90 } as NayaxStockEventApplyResult));
    const { toast, openDialog, requireButton, click, applied } = await render(syncRestock, jest.fn(), resolveSyncRestockDuplicate);
    openDialog();

    click(requireButton('Apply as separate restock'));

    expect(resolveSyncRestockDuplicate).toHaveBeenCalledWith(7, 5, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock);
    expect(toast.success).toHaveBeenCalledWith('Applied as a separate restock.');
    expect(applied()).toBe(1);
  });

  it('resolves multiple selected events as already recorded manually in one bulk action, including a non-suggested one', async () => {
    const readyEvent = event({ id: 1 });
    const needsReview = event({ id: 2, matchStatus: NayaxStockEventMatchStatus.NeedsReview, needsReviewReason: 'Unknown MDB' });
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([readyEvent, needsReview])))
      .mockReturnValueOnce(of(preview([], 'No new Nayax stock-adjustment alerts to review.')));
    const resolveSyncRestockManually = jest.fn(() => of({
      results: [
        { eventId: 1, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null },
        { eventId: 2, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null }
      ]
    } as NayaxMachineStockApplyResponse));
    const { host, toast, requireButton, click, openDialog } = await render(
      syncRestock, jest.fn(), jest.fn(), resolveSyncRestockManually
    );
    openDialog();
    const [, reviewBox] = Array.from(host.querySelectorAll<HTMLInputElement>('tbody input[type="checkbox"]'));
    click(reviewBox);

    click(requireButton('Already recorded manually'));

    expect(resolveSyncRestockManually).toHaveBeenCalledWith(7, [1, 2]);
    expect(toast.success).toHaveBeenCalledWith('Reconciled 2 event(s) as already recorded manually.');
    expect(syncRestock).toHaveBeenCalledTimes(2);
  });

  it('cannot be closed while a bulk manual resolution is in flight', async () => {
    const pendingResolve = new Subject<NayaxMachineStockApplyResponse>();
    const { host, dialog, requireButton, click, pressEscape, openDialog } = await render(
      jest.fn(() => of(preview([event({ id: 1, matchStatus: NayaxStockEventMatchStatus.NeedsReview })]))),
      jest.fn(), jest.fn(), jest.fn(() => pendingResolve)
    );
    openDialog();
    const checkbox = host.querySelector<HTMLInputElement>('tbody input[type="checkbox"]');
    if (checkbox === null) {
      throw new Error('Expected a row checkbox.');
    }
    click(checkbox);

    click(requireButton('Already recorded manually'));

    expect(requireButton('Close').disabled).toBe(true);
    pressEscape();
    expect(dialog()).not.toBeNull();
  });

  it('shows the From date/Show reconciled/Select all applicable controls and a compact status summary', async () => {
    const { host, openDialog } = await render(jest.fn(() => of(preview([event({ id: 1 })], null, 2))));

    openDialog();

    const fromDateInput = host.querySelector<HTMLInputElement>('#sync-restock-from-date');
    if (fromDateInput === null) {
      throw new Error('Expected a From date input.');
    }
    expect(fromDateInput.value).toMatch(/^\d{4}-\d{2}-\d{2}$/);

    const labels = Array.from(host.querySelectorAll('label')).map((label) => label.textContent?.trim());
    expect(labels.some((text) => text?.includes('Show reconciled'))).toBe(true);
    expect(labels.some((text) => text?.includes('Select all applicable'))).toBe(true);

    const status = host.querySelector('[data-testid="sync-restock-status"]');
    expect(status?.textContent).toContain('1 shown');
    expect(status?.textContent).toContain('2 reconciled record(s) hidden');
  });

  it('changing the From date input requests a preview bounded by the new date', async () => {
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([event({ id: 1 })])))
      .mockReturnValueOnce(of(preview([])));
    const { host, fixture, openDialog } = await render(syncRestock);
    openDialog();

    const fromDateInput = host.querySelector<HTMLInputElement>('#sync-restock-from-date');
    if (fromDateInput === null) {
      throw new Error('Expected a From date input.');
    }
    fromDateInput.value = '2026-09-01';
    fromDateInput.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(syncRestock).toHaveBeenCalledTimes(2);
    expect(syncRestock.mock.calls[1]).toEqual([7, startOfDayUtc(2026, 9, 1, BUSINESS_TIME_ZONE).toISOString(), false]);
  });

  it('Select all applicable toggles the bulk selection between every applicable event and none', async () => {
    const ready = event({ id: 1 });
    const needsReview = event({ id: 2, matchStatus: NayaxStockEventMatchStatus.NeedsReview });
    const { host, requireButton, click, openDialog } = await render(jest.fn(() => of(preview([ready, needsReview]))));
    openDialog();

    const selectAll = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'))
      .find((el) => el.closest('label')?.textContent?.includes('Select all applicable'));
    if (selectAll === undefined) {
      throw new Error('Expected a "Select all applicable" checkbox.');
    }
    expect(selectAll.checked).toBe(true);
    expect(requireButton('Apply selected').textContent).toContain('(1)');

    click(selectAll);
    expect(requireButton('Apply selected').textContent).toContain('(0)');

    click(selectAll);
    expect(requireButton('Apply selected').textContent).toContain('(1)');
  });

  it('toggling Show reconciled displays a reconciled event for audit without an ordinary selectable checkbox', async () => {
    const reconciled = event({
      id: 3, isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.ReconciledManually
    });
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([], null, 1)))
      .mockReturnValueOnce(of(preview([reconciled], null, 0)));
    const { host, fixture, openDialog } = await render(syncRestock);
    openDialog();

    const showReconciled = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'))
      .find((el) => el.closest('label')?.textContent?.includes('Show reconciled'));
    if (showReconciled === undefined) {
      throw new Error('Expected a "Show reconciled" checkbox.');
    }
    showReconciled.checked = true;
    showReconciled.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(syncRestock.mock.calls[1]).toEqual([7, expect.any(String), true]);
    const row = host.querySelector<HTMLInputElement>('tbody input[type="checkbox"]');
    expect(row?.disabled).toBe(true);
    expect(host.querySelector('tbody')?.textContent).toContain('Reconciled: already recorded manually');
  });

  it('shows the Nayax status banner next to Sync Restock when the connection is not Ready (issue #539)', async () => {
    const { host } = await render(undefined, undefined, undefined, undefined, 'NotConfigured');

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).not.toBeNull();
  });

  it('hides the Nayax status banner once the connection is Ready (issue #539)', async () => {
    const { host } = await render(undefined, undefined, undefined, undefined, 'Ready');

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).toBeNull();
  });

  /**
   * Sync Restock while a support session is active (issue #658). A session grants
   * `InventoryOperate`, so this write would otherwise still be offered - and even the preview fetch
   * is refused server-side, since it is a `POST` and a session may only `GET`/`HEAD`.
   */
  it('disables Sync Restock while a session is active', async () => {
    const { requireButton } = await render(undefined, undefined, undefined, undefined, 'Ready', of(true));

    expect(requireButton('Sync Restock').disabled).toBe(true);
  });

  it('leaves Sync Restock enabled while no session is active', async () => {
    const { requireButton } = await render(undefined, undefined, undefined, undefined, 'Ready', of(false));

    expect(requireButton('Sync Restock').disabled).toBe(false);
  });
});

/**
 * Every write-triggering handler guarded against issuing a request while a support session is
 * active (issue #658), invoked directly rather than through the (now-disabled) buttons - the
 * endpoint refuses every one of these the same way, so the guard must as well.
 */
describe('MachineRestockSyncComponent write controls during a support session (issue #658)', () => {
  it('does not fetch a preview while a session is active, even if syncRestock() is invoked directly', () => {
    const { component, syncRestock } = createHarness(undefined, undefined, undefined, undefined, of(true));

    component.syncRestock();

    expect(syncRestock).not.toHaveBeenCalled();
    expect(component.modalOpen$.value).toBe(false);
  });

  it('does not apply the selected events while a session is active, even if applySelectedEvents() is invoked directly', () => {
    const { component, applySyncRestock } = createHarness(undefined, undefined, undefined, undefined, of(true));
    component.toggleEventSelection(1, true);

    component.applySelectedEvents();

    expect(applySyncRestock).not.toHaveBeenCalled();
  });

  it('does not resolve a duplicate while a session is active, even if resolveDuplicate() is invoked directly', () => {
    const { component, resolveSyncRestockDuplicate } = createHarness(undefined, undefined, undefined, undefined, of(true));

    component.resolveDuplicate(event({ id: 3 }), NayaxDuplicateResolutionChoice.AlreadyRecordedManually);

    expect(resolveSyncRestockDuplicate).not.toHaveBeenCalled();
  });

  it('does not bulk-resolve while a session is active, even if resolveSelectedAsAlreadyRecorded() is invoked directly', () => {
    const { component, resolveSyncRestockManually } = createHarness(undefined, undefined, undefined, undefined, of(true));
    component.toggleEventSelection(1, true);

    component.resolveSelectedAsAlreadyRecorded();

    expect(resolveSyncRestockManually).not.toHaveBeenCalled();
  });

  it('leaves every write available while no session is active', () => {
    const { component, syncRestock } = createHarness(undefined, undefined, undefined, undefined, of(false));

    component.syncRestock();

    expect(syncRestock).toHaveBeenCalled();
  });
});
