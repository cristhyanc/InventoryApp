import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BehaviorSubject } from 'rxjs';
import { MachineService } from '../../../services/machine.service';
import { ToastService } from '../../../services/toast.service';
import {
  NayaxMachineStockSyncPreview,
  NayaxStockEventApplyOutcome,
  NayaxStockEventMatchStatus,
  NayaxStockEventPreview
} from '../../../models/models';

/**
 * Machine-level Sync Restock panel (issue #183). It owns the whole Nayax stock-adjustment
 * reconciliation workflow - preview state, syncing/applying state, event selection, eligibility,
 * the two API calls and its own notifications - so the machine-detail page only has to supply the
 * machine identity and react to a successful apply.
 *
 * It never decides what is applied: `isReadyToApply` only pre-selects and enables the checkboxes
 * an operator can accept. The backend apply use case remains the sole authority over whether an
 * event actually moves storage inventory.
 */
@Component({
  selector: 'app-machine-restock-sync',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './machine-restock-sync.component.html'
})
export class MachineRestockSyncComponent {
  @Input() machineId: number | null | undefined = null;

  /** Emitted only after at least one event was actually applied, so the page can reload products. */
  @Output() readonly restockApplied = new EventEmitter<void>();

  syncPreview$ = new BehaviorSubject<NayaxMachineStockSyncPreview | null>(null);
  syncing$ = new BehaviorSubject(false);
  applying$ = new BehaviorSubject(false);
  selectedEventIds = new Set<number>();

  readonly MatchStatus = NayaxStockEventMatchStatus;

  constructor(
    private machineService: MachineService,
    private toastService: ToastService
  ) {}

  isReadyToApply(event: NayaxStockEventPreview): boolean {
    return event.matchStatus === NayaxStockEventMatchStatus.Matched &&
      (event.parsedQuantity ?? 0) > 0 &&
      !event.isInsufficientStock;
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

  syncRestock(): void {
    const machineId = this.machineId;
    if (!machineId) {
      return;
    }

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
        this.toastService.error('Failed to sync Nayax stock-adjustment alerts.');
      }
    });
  }

  applySelectedEvents(): void {
    const machineId = this.machineId;
    if (!machineId || this.selectedEventIds.size === 0) {
      return;
    }

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
        this.toastService.error('Failed to apply the selected Nayax stock-adjustment events.');
      }
    });
  }
}
