import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { MachineRestockSyncComponent } from './machine-restock-sync.component';
import { MachineService } from '../../../services/machine.service';
import { ToastService } from '../../../services/toast.service';
import {
  NayaxDuplicateResolution,
  NayaxDuplicateResolutionChoice,
  NayaxMachineStockApplyResponse,
  NayaxMachineStockSyncPreview,
  NayaxStockEventApplyOutcome,
  NayaxStockEventApplyResult,
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
    duplicateResolution: NayaxDuplicateResolution.None,
    ...overrides
  };
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
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock };
  applied: number;
}

function createHarness(
  syncRestock: jest.Mock = jest.fn(() => of(preview([]))),
  applySyncRestock: jest.Mock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse)),
  resolveSyncRestockDuplicate: jest.Mock = jest.fn(() =>
    of({ eventId: 1, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null } as NayaxStockEventApplyResult))
): Harness {
  const machineService = { syncRestock, applySyncRestock, resolveSyncRestockDuplicate } as unknown as MachineService;
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
  const component = new MachineRestockSyncComponent(machineService, toast as unknown as ToastService);
  component.machineId = 7;

  const harness: Harness = { component, syncRestock, applySyncRestock, resolveSyncRestockDuplicate, toast, applied: 0 };
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

  it('defaults the From date to seven calendar days before today and shows reconciled off', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const { component } = createHarness(syncRestock);
    const expected = new Date();
    expected.setDate(expected.getDate() - 7);
    const pad = (n: number) => n.toString().padStart(2, '0');
    const expectedValue = `${expected.getFullYear()}-${pad(expected.getMonth() + 1)}-${pad(expected.getDate())}`;

    component.syncRestock();

    expect(component.fromDate$.value).toBe(expectedValue);
    expect(component.showReconciled$.value).toBe(false);
    const [, fromDateIso, includeReconciled] = syncRestock.mock.calls[0];
    expect(fromDateIso).toBe(new Date(expected.getFullYear(), expected.getMonth(), expected.getDate()).toISOString());
    expect(includeReconciled).toBe(false);
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

  it('changing the From date fetches a new preview bounded by the new date as a UTC instant', () => {
    const syncRestock: jest.Mock = jest.fn(() => of(preview([])));
    const { component } = createHarness(syncRestock);
    component.syncRestock();
    syncRestock.mockClear();

    component.onFromDateChange('2026-09-01');

    expect(component.fromDate$.value).toBe('2026-09-01');
    const [machineId, fromDateIso, includeReconciled] = syncRestock.mock.calls[0];
    expect(machineId).toBe(7);
    expect(fromDateIso).toBe(new Date(2026, 8, 1).toISOString());
    expect(includeReconciled).toBe(false);
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
      of({ eventId: 1, outcome: NayaxStockEventApplyOutcome.Reconciled, message: 'Reconciled', stockAdjustmentId: null } as NayaxStockEventApplyResult))
  ) {
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
    await TestBed.configureTestingModule({
      imports: [MachineRestockSyncComponent],
      providers: [
        { provide: MachineService, useValue: { syncRestock, applySyncRestock, resolveSyncRestockDuplicate } },
        { provide: ToastService, useValue: toast }
      ]
    }).compileComponents();

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
      fixture, host, toast, syncRestock, applySyncRestock, resolveSyncRestockDuplicate,
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
    expect(reviewBox.disabled).toBe(true);
    expect(requireButton('Apply selected').textContent).toContain('(1)');

    click(readyBox);

    expect(fixture.componentInstance.isEventSelected(1)).toBe(false);
    expect(requireButton('Apply selected').textContent).toContain('(0)');
    expect(requireButton('Apply selected').disabled).toBe(true);
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

  it('never presents an unresolved possible duplicate as an ordinary selectable Apply item', async () => {
    const duplicate = event({ id: 5, isPossibleDuplicate: true, possibleDuplicateNotes: 'Manual refill' });
    const { host, requireElement, openDialog } = await render(jest.fn(() => of(preview([duplicate]))));

    openDialog();

    const checkbox = requireElement<HTMLInputElement>('tbody input[type="checkbox"]');
    expect(checkbox.disabled).toBe(true);
    expect(host.querySelector('[data-testid="possible-duplicate-badge"]')).not.toBeNull();
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
    expect(syncRestock.mock.calls[1]).toEqual([7, new Date(2026, 8, 1).toISOString(), false]);
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
});
