import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * The "View as this business" dialog of the Super Admin Businesses page (issue #553), over
 * `POST /api/super-admin/support-sessions/{businessId}` (issue #550).
 *
 * Unlike the Reject dialog beside it, the reason here is required: a support session is read
 * access to somebody else's business, so the trail has to say why (the same rule the API itself
 * enforces - a blank reason is refused there regardless of what this dialog does). Submitting
 * starts the session; `SupportSessionService.start` is what then clears the cached business data,
 * re-reads the capabilities and navigates to the dashboard, so this dialog only has to close
 * itself once that call has succeeded.
 */
@Component({
  selector: 'app-view-as-business-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="modal-backdrop" role="dialog" aria-modal="true" [attr.aria-label]="'View as ' + businessName">
      <div class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg">
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800">View as {{ businessName }}</h2>
        <p class="mb-3 text-md-body text-md-gray-600">
          This opens a read-only, time-limited view of {{ businessName }}'s data. Say why you need to look.
        </p>
        <label class="mb-1 block text-sm font-medium text-md-gray-700" for="view-as-reason">Reason (required)</label>
        <textarea
          id="view-as-reason"
          class="mb-3 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
          rows="3"
          maxlength="500"
          [disabled]="submitting"
          [(ngModel)]="reason"
        ></textarea>
        @if (errorMessage) {
          <p class="mb-3 text-sm text-red-600" role="alert">{{ errorMessage }}</p>
        }
        <div class="confirm-actions">
          <button type="button" class="btn btn-secondary btn-sm" [disabled]="submitting" (click)="cancel()">
            Cancel
          </button>
          <button type="button" class="btn btn-primary btn-sm" [disabled]="!canSubmit" (click)="submit()">
            {{ submitting ? 'Starting…' : 'Start session' }}
          </button>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      .modal-backdrop {
        position: fixed;
        inset: 0;
        background: rgba(15, 23, 42, 0.5);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 50;
        padding: 1rem;
      }

      .confirm-modal {
        max-width: 420px;
        width: 100%;
      }

      .confirm-actions {
        display: flex;
        justify-content: flex-end;
        gap: 0.75rem;
      }
    `
  ]
})
export class ViewAsBusinessDialogComponent {
  @Input({ required: true }) businessId!: number;
  @Input({ required: true }) businessName!: string;

  @Output() readonly started = new EventEmitter<void>();
  @Output() readonly canceled = new EventEmitter<void>();

  reason = '';
  submitting = false;
  errorMessage: string | null = null;

  constructor(private readonly supportSession: SupportSessionService) {}

  get canSubmit(): boolean {
    return !this.submitting && this.reason.trim().length > 0;
  }

  cancel(): void {
    if (this.submitting) {
      return;
    }
    this.canceled.emit();
  }

  submit(): void {
    if (!this.canSubmit) {
      return;
    }
    this.submitting = true;
    this.errorMessage = null;

    this.supportSession.start(this.businessId, this.reason.trim()).subscribe({
      next: () => {
        this.submitting = false;
        this.started.emit();
      },
      error: (err: unknown) => {
        this.submitting = false;
        this.errorMessage = extractErrorMessage(err, 'Unable to start a support session for this business.');
      }
    });
  }
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
