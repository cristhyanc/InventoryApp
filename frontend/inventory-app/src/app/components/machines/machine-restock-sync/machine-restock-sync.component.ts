import {
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { BehaviorSubject, Subscription } from 'rxjs';
import { MachineService } from '../../../services/machine.service';
import { ToastService } from '../../../services/toast.service';
import {
  currentDateInTimeZone,
  shiftCalendarDate,
  startOfDayUtc
} from '../../../formatting/business-time-zone';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import { NayaxStatusBannerComponent } from '../../shared/nayax-status-banner/nayax-status-banner.component';
import { SupportSessionService } from '../../../support-session/support-session.service';
import {
  NayaxDuplicateResolution,
  NayaxDuplicateResolutionChoice,
  NayaxMachineStockSyncPreview,
  NayaxStockEventApplyOutcome,
  NayaxStockEventMatchStatus,
  NayaxStockEventPreview
} from '../../../models/models';

/**
 * Machine-level Sync Restock action (issue #183). It owns the whole Nayax stock-adjustment
 * reconciliation workflow - the reconciliation dialog's open state, preview state, syncing/applying/
 * bulk-resolving state, event selection, eligibility, its API calls and its own notifications - so
 * the machine-detail page only has to supply the machine identity and react to a successful apply.
 * The bulk "Already recorded manually" action (issue #242) reconciles every explicitly selected
 * event in one call, whether or not the app flagged it as a possible duplicate.
 *
 * The preview is shown in a dialog rather than inline, following the application's existing
 * `ConfirmationDialogComponent` pattern (an `*ngIf` backdrop that closes on an outside click).
 * After an apply the dialog stays open and re-syncs, so applied events drop out of the list and
 * only still-actionable events remain.
 *
 * It never decides what is applied: `isReadyToApply` only pre-selects and enables the checkboxes
 * an operator can accept. The backend apply use case remains the sole authority over whether an
 * event actually moves storage inventory.
 */
@Component({
  selector: 'app-machine-restock-sync',
  standalone: true,
  imports: [CommonModule, BusinessDateTimePipe, NayaxStatusBannerComponent],
  templateUrl: './machine-restock-sync.component.html',
  styles: [
    `
      /*
       * Plain CSS, not run through the Tailwind/PostCSS pipeline, so the #410 tokens cannot be
       * referenced with @apply here. Every colour/radius/shadow literal below is instead copied
       * from the matching tailwind.config.js token (md-gray-100/200/500, md-dialog, md-lg) so the
       * dialog still only uses values the visual specification defines.
       */
      .sync-modal-backdrop {
        position: fixed;
        inset: 0;
        background: rgba(38, 38, 38, 0.5);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 50;
        padding: 1rem;
      }

      .sync-modal {
        display: flex;
        flex-direction: column;
        width: 100%;
        max-width: 64rem;
        max-height: calc(100vh - 2rem);
        max-height: calc(100dvh - 2rem);
        background: #fff;
        border-radius: 0.75rem;
        box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.1), 0 4px 6px -2px rgba(0, 0, 0, 0.05);
        outline: none;
      }

      .sync-modal-header,
      .sync-modal-footer {
        flex-shrink: 0;
        display: flex;
        gap: 0.75rem;
        padding: 1rem 1.5rem;
      }

      .sync-modal-header {
        align-items: flex-start;
        justify-content: space-between;
        border-bottom: 1px solid #e5e5e5;
      }

      .sync-modal-footer {
        flex-wrap: wrap;
        justify-content: flex-end;
        border-top: 1px solid #e5e5e5;
      }

      .sync-modal-body {
        flex: 1 1 auto;
        min-height: 0;
        overflow-y: auto;
        padding: 1rem 1.5rem;
      }

      .sync-modal-close {
        flex-shrink: 0;
        border: none;
        background: transparent;
        color: #737373;
        font-size: 1.5rem;
        line-height: 1;
        padding: 0.25rem 0.5rem;
        border-radius: 0.375rem;
        cursor: pointer;
      }

      .sync-modal-close:hover:not(:disabled) {
        background: #f5f5f5;
      }

      .sync-modal-close:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }

      .sync-sr-only {
        position: absolute;
        width: 1px;
        height: 1px;
        padding: 0;
        margin: -1px;
        overflow: hidden;
        clip: rect(0, 0, 0, 0);
        white-space: nowrap;
        border: 0;
      }

      @media (max-width: 640px) {
        .sync-modal-backdrop {
          padding: 0.5rem;
        }

        .sync-modal {
          max-height: calc(100vh - 1rem);
          max-height: calc(100dvh - 1rem);
        }

        .sync-modal-header,
        .sync-modal-footer,
        .sync-modal-body {
          padding-left: 1rem;
          padding-right: 1rem;
        }
      }
    `
  ]
})
export class MachineRestockSyncComponent implements OnChanges, OnDestroy {
  @Input() machineId: number | null | undefined = null;

  /**
   * The oldest pending restock date the Dashboard's restock-sync reminder linked here with
   * (`?restockFrom=yyyy-MM-dd`, issue #948). When it is a real date earlier than the usual From
   * default, the dialog opens at it instead, so every event the reminder counted is on screen
   * without editing the date. Anything else - absent, malformed, or not earlier - leaves the
   * default exactly as it was, and the operator can still change or clear the date. Since issue
   * #1011 that link also opens the dialog by itself (see `openOnArrivalFor`).
   */
  @Input() pendingFromDate: string | null | undefined = null;

  /**
   * The machine the Dashboard reminder's "Open Sync Restock" link asked this panel to open for
   * (`?syncRestock=open`, issue #1011), or `null` for an ordinary visit. The dialog opens by
   * itself, through the same `syncRestock()` the button uses, once `machineId` is that machine and
   * the business time zone is known - never for another machine, never during a support session,
   * and at most once per request, so a later page update, or the operator closing it, never
   * reopens it.
   */
  @Input() openOnArrivalFor: number | null | undefined = null;

  /** Emitted only after at least one event was actually applied, so the page can reload products. */
  @Output() readonly restockApplied = new EventEmitter<void>();

  /**
   * Whether a support session is active (issue #658): Sync Restock and every action inside its
   * dialog are disabled while it is, because a session grants `InventoryOperate` - the capability
   * every one of these writes would otherwise be gated on - and the backend refuses every one of
   * them itself only by its read-only method rule (including the preview fetch, which is a `POST`),
   * never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;
  private readonly timeZoneSubscription: Subscription;

  /** The `openOnArrivalFor` request already acted on (issue #1011), so it is acted on only once. */
  private openOnArrivalHandledFor: number | null = null;

  /**
   * The current preview request (issue #1011). Every new request, a move to another machine and
   * the component's destruction supersede the one before - cancelling it and bumping
   * `previewGeneration` - and a response is applied only while its own generation is still the
   * current one. So a superseded response can never clear a newer request's loading state or show
   * a preview, error, selection or toast, even after the page has moved back to the machine it was
   * asked for.
   */
  private previewSubscription?: Subscription;
  private previewGeneration = 0;

  modalOpen$ = new BehaviorSubject(false);
  syncPreview$ = new BehaviorSubject<NayaxMachineStockSyncPreview | null>(null);
  syncing$ = new BehaviorSubject(false);
  applying$ = new BehaviorSubject(false);
  syncError$ = new BehaviorSubject<string | null>(null);
  applyError$ = new BehaviorSubject<string | null>(null);
  resolvingEventId$ = new BehaviorSubject<number | null>(null);
  /** The bulk "Already recorded manually" action is in flight (issue #242). */
  resolvingManyEvents$ = new BehaviorSubject(false);
  selectedEventIds = new Set<number>();

  /**
   * The Sync Restock From date filter (issue #206), as a `yyyy-MM-dd` local-date input value. It
   * is empty until the dialog is opened, because its default is a date in the business's own
   * timezone and that zone is not known before the business has been read (issue #499).
   */
  fromDate$ = new BehaviorSubject<string>('');
  /**
   * The dialog was opened before the business timezone was known, so its From date default could
   * not be dated yet (issue #499). An empty From date otherwise means the operator cleared it, so
   * this is what keeps a later refresh from treating "not yet defaulted" as "no lower bound".
   */
  private fromDateDefaultPending = false;
  /** Show reconciled (issue #206): off by default, so reconciled-manually events stay hidden. */
  showReconciled$ = new BehaviorSubject<boolean>(false);

  readonly MatchStatus = NayaxStockEventMatchStatus;
  readonly DuplicateResolution = NayaxDuplicateResolution;
  readonly DuplicateResolutionChoice = NayaxDuplicateResolutionChoice;

  @ViewChild('syncTrigger') private syncTrigger?: ElementRef<HTMLButtonElement>;

  /** Moves keyboard focus into the dialog as soon as it is rendered. */
  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLElement> | undefined) {
    panel?.nativeElement.focus();
  }

  constructor(
    private readonly machineService: MachineService,
    private readonly toastService: ToastService,
    private readonly businessTimeZone: BusinessTimeZoneService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => {
      this.sessionActive = active;
      this.openOnArrivalIfRequested();
    });
    // A requested open waits for the zone rather than opening into "Loading the business time
    // zone" (issue #1011): the From date default is a day in that zone (issue #499).
    this.timeZoneSubscription = businessTimeZone.timeZoneId$.subscribe(() => this.openOnArrivalIfRequested());
  }

  ngOnChanges(changes: SimpleChanges): void {
    const machineChange = changes['machineId'];
    if (machineChange && !machineChange.firstChange && machineChange.previousValue !== machineChange.currentValue) {
      // The page is reused when the route moves to another machine, so the previous machine's
      // dialog and preview are dropped here rather than shown under the new machine (issue #1011).
      this.supersedePreviewRequest();
      this.resetDialog();
    }
    if (changes['openOnArrivalFor']) {
      this.openOnArrivalHandledFor = null;
    }
    this.openOnArrivalIfRequested();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
    this.timeZoneSubscription.unsubscribe();
    this.supersedePreviewRequest();
  }

  /** Cancels the preview request in flight, if any, so nothing it answers is applied (issue #1011). */
  private supersedePreviewRequest(): void {
    this.previewGeneration++;
    this.previewSubscription?.unsubscribe();
    this.previewSubscription = undefined;
    this.syncing$.next(false);
  }

  /**
   * Opens the dialog for the Dashboard reminder's "Open Sync Restock" link (issue #1011) once the
   * page can: the request names this panel's machine and the business time zone is known. A
   * support session consumes the request without opening anything, because Sync Restock is not
   * available in one (issue #658).
   */
  private openOnArrivalIfRequested(): void {
    const requestedFor = this.openOnArrivalFor;
    if (!requestedFor || requestedFor !== this.machineId || this.openOnArrivalHandledFor === requestedFor) {
      return;
    }
    if (!this.sessionActive && this.businessTimeZone.timeZoneId === null) {
      return;
    }

    this.openOnArrivalHandledFor = requestedFor;
    if (!this.sessionActive && !this.modalOpen$.value) {
      this.syncRestock();
    }
  }

  /**
   * Whether this event is directly safe/applicable through the normal Apply path (issue #183),
   * including through the "Select all applicable" bulk control (issue #206). A reconciled or an
   * unresolved possible duplicate is never ready to apply - the former is already settled with no
   * movement, the latter must go through explicit resolution first.
   */
  isReadyToApply(event: NayaxStockEventPreview): boolean {
    return event.matchStatus === NayaxStockEventMatchStatus.Matched &&
      (event.parsedQuantity ?? 0) > 0 &&
      !event.isInsufficientStock &&
      event.duplicateResolution === NayaxDuplicateResolution.None &&
      !this.isUnresolvedDuplicate(event);
  }

  /** A flagged possible duplicate the operator has not yet explicitly resolved. */
  isUnresolvedDuplicate(event: NayaxStockEventPreview): boolean {
    return event.isPossibleDuplicate && event.duplicateResolution === NayaxDuplicateResolution.None;
  }

  /**
   * Whether this event may be explicitly selected and marked **Already recorded manually** through
   * the bulk action (issue #242). The app's possible-duplicate detection is only a suggestion for
   * human resolution, not a precondition: any unresolved event is eligible, whether or not it is
   * ready to apply or flagged as a possible duplicate. An already-reconciled event stays ineligible
   * so it is never re-selected for the same resolution.
   */
  isEligibleForManualResolution(event: NayaxStockEventPreview): boolean {
    return event.duplicateResolution === NayaxDuplicateResolution.None;
  }

  isEventSelected(eventId: number): boolean {
    return this.selectedEventIds.has(eventId);
  }

  toggleEventSelection(eventId: number, checked: boolean): void {
    if (checked) {
      this.selectedEventIds.add(eventId);
    } else {
      this.selectedEventIds.delete(eventId);
    }
  }

  /** Whether every currently visible/applicable event is selected, for the "Select all applicable" control. */
  isAllApplicableSelected(preview: NayaxMachineStockSyncPreview): boolean {
    const eligibleIds = preview.events.filter((e) => this.isReadyToApply(e)).map((e) => e.id);
    return eligibleIds.length > 0 && eligibleIds.every((id) => this.selectedEventIds.has(id));
  }

  /**
   * "Select all applicable" (issue #206): selects exactly the currently visible events that are
   * directly safe/applicable through the normal Apply path - never an already-applied, reconciled,
   * Needs Review, or unresolved-duplicate event. Clearing it clears the bulk selection without
   * changing any event's persisted state.
   */
  toggleSelectAllApplicable(preview: NayaxMachineStockSyncPreview, checked: boolean): void {
    this.selectedEventIds = checked
      ? new Set(preview.events.filter((e) => this.isReadyToApply(e)).map((e) => e.id))
      : new Set();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.modalOpen$.value) {
      this.closeModal();
    }
  }

  /**
   * Closes the dialog unless an apply or a bulk resolve is in flight, and returns focus to the Sync
   * Restock button.
   */
  closeModal(): void {
    if (!this.modalOpen$.value || this.applying$.value || this.resolvingManyEvents$.value) {
      return;
    }

    this.resetDialog();
    this.syncTrigger?.nativeElement.focus();
  }

  /** Closes the dialog and drops its preview, errors and selection. */
  private resetDialog(): void {
    this.modalOpen$.next(false);
    this.syncPreview$.next(null);
    this.syncError$.next(null);
    this.applyError$.next(null);
    this.resolvingEventId$.next(null);
    this.selectedEventIds.clear();
  }

  /**
   * Opens the reconciliation dialog (if it is not already open), resets the From date/Show
   * reconciled filters to their opening defaults (issue #206), and fetches a fresh preview.
   */
  syncRestock(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (!this.machineId || this.sessionActive) {
      return;
    }

    const defaultFromDate = this.defaultFromDate();
    this.fromDateDefaultPending = defaultFromDate === '';
    this.fromDate$.next(defaultFromDate);
    this.showReconciled$.next(false);
    this.modalOpen$.next(true);
    this.refreshPreview();
  }

  /** The From date filter changed (issue #206): fetches a preview bounded by the new date. */
  onFromDateChange(value: string): void {
    this.fromDateDefaultPending = false;
    this.fromDate$.next(value);
    this.refreshPreview();
  }

  /** Show reconciled changed (issue #206): never rewrites any event, only what the preview returns. */
  onShowReconciledChange(checked: boolean): void {
    this.showReconciled$.next(checked);
    this.refreshPreview();
  }

  /**
   * Fetches the preview for the current From date/Show reconciled filters. Reused by the initial
   * open, a filter change, and the post-apply/post-resolve refresh, so every one of them is
   * bounded/filtered consistently and the selection is reconciled with whatever comes back -
   * never leaving a hidden or now-ineligible event selected (issue #206).
   */
  private refreshPreview(): void {
    const machineId = this.machineId;
    if (!machineId) {
      return;
    }

    this.syncError$.next(null);

    // The From date filter is a business-timezone calendar day, so without that zone there is no
    // correct window to ask for (issue #499). Reporting it beats asking for an unbounded one.
    if (this.businessTimeZone.timeZoneId === null) {
      this.syncError$.next('Loading the business time zone. Please try again in a moment.');
      return;
    }

    if (this.fromDateDefaultPending) {
      // The zone has arrived since the dialog opened: apply the default the operator would have
      // seen, rather than asking for an unbounded window.
      this.fromDateDefaultPending = false;
      this.fromDate$.next(this.defaultFromDate());
    }

    this.supersedePreviewRequest();
    const generation = this.previewGeneration;
    const isCurrent = () => generation === this.previewGeneration;

    this.syncing$.next(true);
    const fromDateIso = this.toUtcInstant(this.fromDate$.value);
    const request$ = this.machineService.syncRestock(machineId, fromDateIso, this.showReconciled$.value);
    this.previewSubscription = request$.subscribe({
      next: (preview) => {
        if (!isCurrent()) {
          return;
        }
        this.syncing$.next(false);
        this.syncPreview$.next(preview);
        this.selectedEventIds = new Set(
          preview.events.filter((e) => this.isReadyToApply(e)).map((e) => e.id)
        );
        if (preview.message) {
          this.toastService.success(preview.message);
        } else {
          this.toastService.success(`${preview.newEventCount} new Nayax alert(s) found.`);
        }
      },
      error: () => {
        if (!isCurrent()) {
          return;
        }
        this.syncing$.next(false);
        this.syncError$.next('Failed to sync Nayax stock-adjustment alerts.');
        this.toastService.error('Failed to sync Nayax stock-adjustment alerts.');
      }
    });
  }

  /**
   * Three calendar days before the operator's current business date (issue #206; timezone
   * corrected by issue #218; shortened from seven days by issue #242), as `yyyy-MM-dd` - or the
   * earlier `pendingFromDate` the Dashboard reminder linked here with (issue #948). Empty
   * while the business timezone is still unknown (issue #499), which is the loading state
   * `refreshPreview` reports rather than dating the filter in a zone nobody confirmed.
   */
  private defaultFromDate(): string {
    const timeZone = this.businessTimeZone.timeZoneId;
    if (timeZone === null) {
      return '';
    }
    const today = currentDateInTimeZone(new Date(), timeZone);
    const { year, month, day } = shiftCalendarDate(today, -3);
    const pad = (value: number) => value.toString().padStart(2, '0');
    const standardDefault = `${year}-${pad(month)}-${pad(day)}`;

    // `yyyy-MM-dd` strings order the same way as the dates they name.
    const pendingFrom = this.pendingFromDate;
    return isCalendarDate(pendingFrom) && pendingFrom < standardDefault ? pendingFrom : standardDefault;
  }

  /**
   * The operator's chosen calendar date as the UTC instant of its midnight in the business's own
   * timezone (issue #218; per business since issue #499) - not the browser's local timezone - so
   * the backend compares it against the canonical (UTC) EventDateTimeGMT without a second,
   * separate event-date interpretation. Resolved from the IANA timezone database, so that zone's
   * daylight-saving transitions are applied automatically rather than a fixed UTC offset.
   */
  private toUtcInstant(dateInputValue: string): string | null {
    const timeZone = this.businessTimeZone.timeZoneId;
    if (!dateInputValue || timeZone === null) {
      return null;
    }
    const [year, month, day] = dateInputValue.split('-').map(Number);
    return startOfDayUtc(year, month, day, timeZone).toISOString();
  }

  applySelectedEvents(): void {
    const machineId = this.machineId;
    if (!machineId || this.selectedEventIds.size === 0 || this.sessionActive) {
      return;
    }

    this.applyError$.next(null);
    this.applying$.next(true);
    this.machineService.applySyncRestock(machineId, [...this.selectedEventIds]).subscribe({
      next: (response) => {
        this.applying$.next(false);
        const appliedCount = response.results.filter((r) => r.outcome === NayaxStockEventApplyOutcome.Applied).length;
        const failedCount = response.results.length - appliedCount;
        if (appliedCount > 0) {
          this.toastService.success(`Applied ${appliedCount} Nayax stock-adjustment event(s).`);
        }
        if (failedCount > 0) {
          this.toastService.warning(`${failedCount} event(s) could not be applied and remain for review.`);
        }
        this.selectedEventIds.clear();
        this.refreshPreview();
        if (appliedCount > 0) {
          this.restockApplied.emit();
        }
      },
      error: () => {
        this.applying$.next(false);
        this.applyError$.next('Failed to apply the selected Nayax stock-adjustment events.');
        this.toastService.error('Failed to apply the selected Nayax stock-adjustment events.');
      }
    });
  }

  /**
   * The operator's explicit resolution for one event flagged as a possible duplicate (issue #196):
   * "already recorded manually" reconciles it without any movement, "apply as separate restock" is
   * an explicit, auditable override that applies it once. Either way the dialog re-syncs so the
   * preview and machine inventory stay consistent.
   */
  resolveDuplicate(event: NayaxStockEventPreview, resolution: NayaxDuplicateResolutionChoice): void {
    const machineId = this.machineId;
    if (!machineId || this.sessionActive) {
      return;
    }

    this.applyError$.next(null);
    this.resolvingEventId$.next(event.id);
    this.machineService.resolveSyncRestockDuplicate(machineId, event.id, resolution).subscribe({
      next: (result) => {
        this.resolvingEventId$.next(null);
        this.selectedEventIds.delete(event.id);
        const applied = result.outcome === NayaxStockEventApplyOutcome.Applied;
        if (result.outcome === NayaxStockEventApplyOutcome.Reconciled) {
          this.toastService.success('Reconciled as already recorded manually; no Nayax movement was applied.');
        } else if (applied) {
          this.toastService.success('Applied as a separate restock.');
        } else {
          this.toastService.warning(result.message);
        }
        this.refreshPreview();
        if (applied) {
          this.restockApplied.emit();
        }
      },
      error: () => {
        this.resolvingEventId$.next(null);
        this.applyError$.next('Failed to resolve the possible duplicate.');
        this.toastService.error('Failed to resolve the possible duplicate.');
      }
    });
  }

  /**
   * The bulk "Already recorded manually" action (issue #242): resolves every explicitly selected
   * event as already represented by an existing manual restock in one action, whether or not the
   * app flagged it as a possible duplicate. Each event keeps its own outcome; the dialog re-syncs
   * afterwards so the preview and selection stay consistent with what was actually resolved.
   */
  resolveSelectedAsAlreadyRecorded(): void {
    const machineId = this.machineId;
    if (!machineId || this.selectedEventIds.size === 0 || this.sessionActive) {
      return;
    }

    this.applyError$.next(null);
    this.resolvingManyEvents$.next(true);
    this.machineService.resolveSyncRestockManually(machineId, [...this.selectedEventIds]).subscribe({
      next: (response) => {
        this.resolvingManyEvents$.next(false);
        const reconciledCount = response.results.filter((r) => r.outcome === NayaxStockEventApplyOutcome.Reconciled).length;
        const failedCount = response.results.length - reconciledCount;
        if (reconciledCount > 0) {
          this.toastService.success(`Reconciled ${reconciledCount} event(s) as already recorded manually.`);
        }
        if (failedCount > 0) {
          this.toastService.warning(`${failedCount} event(s) could not be resolved and remain for review.`);
        }
        this.selectedEventIds.clear();
        this.refreshPreview();
      },
      error: () => {
        this.resolvingManyEvents$.next(false);
        this.applyError$.next('Failed to resolve the selected events as already recorded manually.');
        this.toastService.error('Failed to resolve the selected events as already recorded manually.');
      }
    });
  }
}

/** Whether a value is a real calendar date written as `yyyy-MM-dd` (so not `2026-02-30`). */
function isCalendarDate(value: string | null | undefined): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return false;
  }
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}
