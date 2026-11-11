import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import {
  SuperAdminReceiptScanUsage,
  SuperAdminReceiptScanUsageService
} from '../../../services/super-admin-receipt-scan-usage.service';

/**
 * The **Receipt scans** card of the Super Admin Businesses page, over the one endpoint of issue
 * #1164: how many receipt scans this business has used in its own calendar month, and how many are
 * left.
 *
 * It owns its own load state and error, which is what makes it a distinct workflow per
 * docs/architecture.md § Page composition boundary - the routed page composes it through
 * `[businessId]`/`[businessName]` and reacts only to `(closed)`.
 *
 * **It is read-only, and that is the point.** There is no action on it at all: buying extra scans and
 * resetting a counter are out of scope on issue #1164, and the server has no route that would accept
 * either. What this card is for is seeing whether a business is near the cap - and therefore whether
 * a support request about "it says I have used all my scans" is the cap doing its job.
 *
 * **It decides nothing.** The month, the count, what is left and the cap are all the server's
 * answers; this card shows them. In particular the month is the **business's own** calendar month,
 * derived on the server in that business's time zone, so it is displayed exactly as reported rather
 * than being recomputed from the administrator's browser.
 */
@Component({
  selector: 'app-business-receipt-scan-usage-card',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div
      class="modal-backdrop"
      role="dialog"
      aria-modal="true"
      [attr.aria-label]="'Receipt scans for ' + businessName"
    >
      <div class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg">
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800">
          Receipt scans for {{ businessName }}
        </h2>

        @if (loading) {
          <p class="text-sm value-muted">Loading this business's scan usage…</p>
        } @else if (loadError) {
          <p class="mb-3 text-sm text-red-600" role="alert">{{ loadError }}</p>
        } @else if (usage) {
          <dl class="mb-3 text-md-body text-md-gray-600">
            <div class="flex gap-2">
              <dt class="font-medium">Month</dt>
              <dd data-testid="scan-usage-month">{{ usage.month }}</dd>
            </div>
            <div class="flex gap-2">
              <dt class="font-medium">Scans used this month</dt>
              <dd data-testid="scan-usage-used">{{ usage.used }} of {{ usage.limit }}</dd>
            </div>
            <div class="flex gap-2">
              <dt class="font-medium">Remaining</dt>
              <dd data-testid="scan-usage-remaining">{{ usage.remaining }}</dd>
            </div>
          </dl>

          @if (usage.remaining === 0) {
            <p class="mb-3 text-sm text-md-warning-text" data-testid="scan-usage-full">
              This business has used its whole allowance for {{ usage.month }}. Scanning answers
              "you have used all {{ usage.limit }} receipt scans for this month" until its next
              month begins in its own time zone. Nothing else about the business is affected, and
              purchases can still be recorded by hand.
            </p>
          }

          <p class="mb-3 text-xs value-muted" data-testid="scan-usage-month-note">
            The month is the business's own calendar month, in its own time zone - not yours.
          </p>
        }

        <div class="confirm-actions">
          <button type="button" class="btn btn-secondary btn-sm" (click)="close()">Close</button>
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
        max-width: 480px;
        width: 100%;
      }

      .confirm-actions {
        display: flex;
        justify-content: flex-end;
        gap: 0.75rem;
        flex-wrap: wrap;
      }
    `
  ]
})
export class BusinessReceiptScanUsageCardComponent implements OnInit {
  @Input({ required: true }) businessId!: number;
  @Input({ required: true }) businessName!: string;

  @Output() readonly closed = new EventEmitter<void>();

  loading = false;
  loadError: string | null = null;
  usage: SuperAdminReceiptScanUsage | null = null;

  constructor(private readonly usages: SuperAdminReceiptScanUsageService) {}

  ngOnInit(): void {
    this.load();
  }

  close(): void {
    this.closed.emit();
  }

  private load(): void {
    this.loading = true;
    this.loadError = null;

    this.usages.get(this.businessId).subscribe({
      next: (usage) => {
        this.loading = false;
        this.usage = usage;
      },
      error: (err: unknown) => {
        this.loading = false;
        this.usage = null;
        this.loadError = extractErrorMessage(
          err,
          'Unable to load this business’s receipt scan usage.'
        );
      }
    });
  }
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err instanceof HttpErrorResponse ? err.error : (err as { error?: unknown })?.error) as
    | unknown
    | undefined;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
