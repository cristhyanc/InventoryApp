import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SuperAdminBusinessDecision, SuperAdminBusinessesService } from '../../../services/super-admin-businesses.service';

/**
 * The Reject dialog of the Super Admin Businesses page (issue #547), over
 * `POST /api/super-admin/businesses/{businessId}/reject` (issue #544).
 *
 * It owns its own reason text, submission state and error, which is what makes it a distinct
 * workflow per docs/architecture.md § Page composition boundary - the routed page composes it
 * through `[businessId]`/`[businessName]` and reacts only to `(rejected)`/`(canceled)`. The reason
 * is optional on purpose: a rejection with no reason is still a rejection.
 */
@Component({
  selector: 'app-business-reject-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="modal-backdrop" role="dialog" aria-modal="true" [attr.aria-label]="'Reject ' + businessName">
      <div class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg">
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800">Reject application</h2>
        <p class="mb-3 text-md-body text-md-gray-600">
          Reject {{ businessName }}'s application? The applicant is released and may apply again.
        </p>
        <label class="mb-1 block text-sm font-medium text-md-gray-700" for="reject-reason">Reason (optional)</label>
        <textarea
          id="reject-reason"
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
          <button type="button" class="btn btn-danger btn-sm" [disabled]="submitting" (click)="submit()">
            {{ submitting ? 'Rejecting…' : 'Confirm' }}
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
export class BusinessRejectDialogComponent {
  @Input({ required: true }) businessId!: number;
  @Input({ required: true }) businessName!: string;

  @Output() readonly rejected = new EventEmitter<SuperAdminBusinessDecision>();
  @Output() readonly canceled = new EventEmitter<void>();

  reason = '';
  submitting = false;
  errorMessage: string | null = null;

  constructor(private readonly businesses: SuperAdminBusinessesService) {}

  cancel(): void {
    if (this.submitting) {
      return;
    }
    this.canceled.emit();
  }

  submit(): void {
    this.submitting = true;
    this.errorMessage = null;

    this.businesses.reject(this.businessId, this.reason).subscribe({
      next: (decision) => {
        this.submitting = false;
        this.businesses.notifyChanged();
        this.rejected.emit(decision);
      },
      error: (err: unknown) => {
        this.submitting = false;
        this.errorMessage = extractErrorMessage(err, 'Unable to reject this application.');
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
