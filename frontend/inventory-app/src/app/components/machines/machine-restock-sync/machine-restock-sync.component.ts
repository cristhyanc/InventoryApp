import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BehaviorSubject } from 'rxjs';
import { MachineService } from '../../../services/machine.service';
import { ToastService } from '../../../services/toast.service';
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
 * reconciliation workflow - the reconciliation dialog's open state, preview state, syncing/applying
 * state, event selection, eligibility, the two API calls and its own notifications - so the
 * machine-detail page only has to supply the machine identity and react to a successful apply.
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
  imports: [CommonModule],
  templateUrl: './machine-restock-sync.component.html',
  styles: [
    `
      .sync-modal-backdrop {
        position: fixed;
        inset: 0;
        background: rgba(15, 23, 42, 0.5);
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
        border-radius: 12px;
        box-shadow: 0 18px 60px rgba(15, 23, 42, 0.18);
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
        border-bottom: 1px solid #e2e8f0;
      }

      .sync-modal-footer {
        flex-wrap: wrap;
        justify-content: flex-end;
        border-top: 1px solid #e2e8f0;
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
        color: #64748b;
        font-size: 1.5rem;
        line-height: 1;
        padding: 0.25rem 0.5rem;
        border-radius: 6px;
        cursor: pointer;
      }

      .sync-modal-close:hover:not(:disabled) {
        background: #f1f5f9;
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
export class MachineRestockSyncComponent {
  @Input() machineId: number | null | undefined = null;

  /** Emitted only after at least one event was actually applied, so the page can reload products. */
  @Output() readonly restockApplied = new EventEmitter<void>();

  modalOpen$ = new BehaviorSubject(false);
  syncPreview$ = new BehaviorSubject<NayaxMachineStockSyncPreview | null>(null);
  syncing$ = new BehaviorSubject(false);
  applying$ = new BehaviorSubject(false);
  syncError$ = new BehaviorSubject<string | null>(null);
  applyError$ = new BehaviorSubject<string | null>(null);
  resolvingEventId$ = new BehaviorSubject<number | null>(null);
  selectedEventIds = new Set<number>();

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
    private machineService: MachineService,
    private toastService: ToastService
  ) {}

  isReadyToApply(event: NayaxStockEventPreview): boolean {
    return event.matchStatus === NayaxStockEventMatchStatus.Matched &&
      (event.parsedQuantity ?? 0) > 0 &&
      !event.isInsufficientStock &&
      !this.isUnresolvedDuplicate(event);
  }

  /** A flagged possible duplicate the operator has not yet explicitly resolved. */
  isUnresolvedDuplicate(event: NayaxStockEventPreview): boolean {
    return event.isPossibleDuplicate && event.duplicateResolution === NayaxDuplicateResolution.None;
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

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.modalOpen$.value) {
      this.closeModal();
    }
  }

  /** Closes the dialog unless an apply is in flight, and returns focus to the Sync Restock button. */
  closeModal(): void {
    if (!this.modalOpen$.value || this.applying$.value) {
      return;
    }

    this.modalOpen$.next(false);
    this.syncPreview$.next(null);
    this.syncError$.next(null);
    this.applyError$.next(null);
    this.resolvingEventId$.next(null);
    this.selectedEventIds.clear();
    this.syncTrigger?.nativeElement.focus();
  }

  /** Opens the reconciliation dialog (if it is not already open) and fetches a fresh preview into it. */
  syncRestock(): void {
    const machineId = this.machineId;
    if (!machineId) {
      return;
    }

    this.modalOpen$.next(true);
    this.syncError$.next(null);
    this.syncing$.next(true);
    this.machineService.syncRestock(machineId).subscribe({
      next: (preview) => {
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
        this.syncing$.next(false);
        this.syncError$.next('Failed to sync Nayax stock-adjustment alerts.');
        this.toastService.error('Failed to sync Nayax stock-adjustment alerts.');
      }
    });
  }

  applySelectedEvents(): void {
    const machineId = this.machineId;
    if (!machineId || this.selectedEventIds.size === 0) {
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
        this.syncRestock();
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
    if (!machineId) {
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
        this.syncRestock();
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
}
