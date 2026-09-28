import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
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
    applySyncRestock: jest.Mock = jest.fn(() => of({ results: [] } as NayaxMachineStockApplyResponse))
  ) {
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn() };
    await TestBed.configureTestingModule({
      imports: [MachineRestockSyncComponent],
      providers: [
        { provide: MachineService, useValue: { syncRestock, applySyncRestock } },
        { provide: ToastService, useValue: toast }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineRestockSyncComponent);
    fixture.componentRef.setInput('machineId', 7);
    let appliedCount = 0;
    fixture.componentInstance.restockApplied.subscribe(() => appliedCount++);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const dialog = () => host.querySelector<HTMLElement>('[role="dialog"]');
    const button = (label: string) =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim().startsWith(label));
    const click = (element: HTMLElement | null | undefined) => {
      element!.click();
      fixture.detectChanges();
    };
    const pressEscape = () => {
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
    };
    const openDialog = () => click(button('Sync Restock'));

    return {
      fixture, host, toast, syncRestock, applySyncRestock,
      dialog, button, click, pressEscape, openDialog,
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
    const { dialog, openDialog, syncRestock } = await render();

    openDialog();

    expect(syncRestock).toHaveBeenCalledWith(7);
    const panel = dialog();
    expect(panel).not.toBeNull();
    expect(panel!.getAttribute('aria-modal')).toBe('true');
    const titleId = panel!.getAttribute('aria-labelledby')!;
    expect(panel!.querySelector(`#${titleId}`)?.textContent?.trim()).toBe('Sync Restock');
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
    const { host, dialog, openDialog } = await render(jest.fn(() => of(withImpacts)));

    openDialog();

    const table = host.querySelector('table');
    expect(table).not.toBeNull();
    expect(dialog()!.contains(table)).toBe(true);
    expect(table!.querySelectorAll('tbody tr').length).toBe(4);
    const text = dialog()!.textContent!;
    expect(text).toContain('Pending refill: 2 · Available in storage: 10');
    expect(text).toContain('Insufficient storage: needs 5, has 4 (short 1)');
    expect(text).toContain('Possible duplicate');
    expect(text).toContain('Needs review: Unknown MDB');
    expect(text).toContain('Ready to apply');
  });

  it('closes with the Close button, clears the preview, and returns focus to Sync Restock', async () => {
    const { host, dialog, button, click, openDialog, fixture } = await render(jest.fn(() => of(preview([event({})]))));
    openDialog();

    click(button('Close'));

    expect(dialog()).toBeNull();
    expect(host.querySelector('table')).toBeNull();
    expect(fixture.componentInstance.syncPreview$.value).toBeNull();
    expect(document.activeElement).toBe(button('Sync Restock'));
  });

  it('closes with the header close control, Escape, and a backdrop click, but not a click inside the dialog', async () => {
    const { host, dialog, click, pressEscape, openDialog } = await render();

    openDialog();
    click(host.querySelector<HTMLButtonElement>('[aria-label="Close Sync Restock"]'));
    expect(dialog()).toBeNull();

    openDialog();
    pressEscape();
    expect(dialog()).toBeNull();

    openDialog();
    click(dialog());
    expect(dialog()).not.toBeNull();
    click(host.querySelector<HTMLElement>('.sync-modal-backdrop'));
    expect(dialog()).toBeNull();
  });

  it('cannot be closed while an apply is in flight', async () => {
    const pendingApply = new Subject<NayaxMachineStockApplyResponse>();
    const { dialog, button, click, pressEscape, openDialog } = await render(
      jest.fn(() => of(preview([event({ id: 1 })]))),
      jest.fn(() => pendingApply)
    );
    openDialog();
    click(button('Apply selected'));

    expect(button('Close')!.disabled).toBe(true);
    pressEscape();
    expect(dialog()).not.toBeNull();
  });

  it('lets the operator change the selection with the checkboxes', async () => {
    const ready = event({ id: 1 });
    const needsReview = event({ id: 2, matchStatus: NayaxStockEventMatchStatus.NeedsReview, needsReviewReason: 'Unknown MDB' });
    const { host, button, click, openDialog, fixture } = await render(jest.fn(() => of(preview([ready, needsReview]))));
    openDialog();

    const [readyBox, reviewBox] = Array.from(host.querySelectorAll<HTMLInputElement>('tbody input[type="checkbox"]'));
    expect(readyBox.checked).toBe(true);
    expect(reviewBox.disabled).toBe(true);
    expect(button('Apply selected')!.textContent).toContain('(1)');

    click(readyBox);

    expect(fixture.componentInstance.isEventSelected(1)).toBe(false);
    expect(button('Apply selected')!.textContent).toContain('(0)');
    expect(button('Apply selected')!.disabled).toBe(true);
  });

  it('applies the selected events, emits restockApplied, and keeps the dialog open without the applied event', async () => {
    const syncRestock = jest.fn()
      .mockReturnValueOnce(of(preview([event({ id: 1 })])))
      .mockReturnValueOnce(of(preview([], 'No new Nayax stock-adjustment alerts to review.')));
    const { host, dialog, button, click, openDialog, toast, applySyncRestock, applied } = await render(
      syncRestock,
      jest.fn(() => of(appliedResponse(1)))
    );
    openDialog();

    click(button('Apply selected'));

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
    const { dialog, openDialog, toast } = await render(jest.fn(() => throwError(() => new Error('upstream unavailable'))));

    openDialog();

    expect(dialog()!.querySelector('[role="alert"]')?.textContent).toContain('Failed to sync Nayax stock-adjustment alerts.');
    expect(toast.error).toHaveBeenCalledWith('Failed to sync Nayax stock-adjustment alerts.');
  });

  it('shows an apply failure inside the dialog and keeps the events actionable', async () => {
    const { host, dialog, button, click, openDialog, applied } = await render(
      jest.fn(() => of(preview([event({ id: 1 })]))),
      jest.fn(() => throwError(() => new Error('network error')))
    );
    openDialog();

    click(button('Apply selected'));

    expect(dialog()!.querySelector('[role="alert"]')?.textContent)
      .toContain('Failed to apply the selected Nayax stock-adjustment events.');
    expect(applied()).toBe(0);
    expect(host.querySelectorAll('tbody tr').length).toBe(1);
    expect(button('Apply selected')!.disabled).toBe(false);
  });

  it('shows the empty state inside the dialog with no apply action', async () => {
    const { host, dialog, button, openDialog } = await render(
      jest.fn(() => of(preview([], 'No new Nayax stock-adjustment alerts to review.')))
    );

    openDialog();

    const empty = host.querySelector('[data-testid="sync-restock-empty"]');
    expect(dialog()!.contains(empty)).toBe(true);
    expect(empty!.textContent).toContain('No new Nayax stock-adjustment alerts to review.');
    expect(host.querySelector('table')).toBeNull();
    expect(button('Apply selected')).toBeUndefined();
    expect(button('Close')).toBeTruthy();
  });
});
