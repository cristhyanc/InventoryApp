import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  GRANTABLE_PLANS,
  GrantablePlan,
  SuperAdminPlanGrant,
  SuperAdminPlanGrantsService
} from '../../../services/super-admin-plan-grants.service';

/**
 * The Plan dialog of the Super Admin Businesses page, over the three manual-grant endpoints of issue
 * #982.
 *
 * It owns its own load, form state, submission state and error, which is what makes it a distinct
 * workflow per docs/architecture.md § Page composition boundary - the routed page composes it through
 * `[businessId]`/`[businessName]` and reacts only to `(saved)`/`(closed)`.
 *
 * **It decides nothing.** Which plans may be granted, whether a reason is required, what an end date
 * means, whether the grant currently applies and whether the caller's view is stale are all the
 * server's answers; this dialog shows them and shows the server's own refusal sentence when one
 * comes back. In particular the `revision` it submits is the one the last response carried, and a
 * `409` is surfaced as the reload-and-try-again message the server sent rather than retried with a
 * guessed number.
 *
 * **It can grant a plan and nothing else.** There is no payment, billing or subscription field here,
 * because the endpoint behind it has none: a grant and a paid subscription are two records and
 * neither writes the other.
 */
@Component({
  selector: 'app-business-plan-grant-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="modal-backdrop" role="dialog" aria-modal="true" [attr.aria-label]="'Plan for ' + businessName">
      <div class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg">
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800">Plan for {{ businessName }}</h2>

        @if (loading) {
          <p class="text-sm value-muted">Loading the current grant…</p>
        } @else if (loadError) {
          <p class="mb-3 text-sm text-red-600" role="alert">{{ loadError }}</p>
        } @else {
          <p class="mb-3 text-md-body text-md-gray-600" data-testid="current-grant">
            {{ currentGrantSummary }}
          </p>

          <label class="mb-1 block text-sm font-medium text-md-gray-700" for="grant-plan">Plan</label>
          <select
            id="grant-plan"
            class="mb-3 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
            [disabled]="submitting"
            [(ngModel)]="plan"
          >
            @for (option of plans; track option) {
              <option [value]="option">{{ option }}</option>
            }
          </select>

          <label class="mb-1 block text-sm font-medium text-md-gray-700" for="grant-ends-on">
            Last day (UTC, optional)
          </label>
          <input
            id="grant-ends-on"
            type="date"
            class="mb-3 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
            [disabled]="submitting"
            [(ngModel)]="endsOn"
          />

          <label class="mb-1 block text-sm font-medium text-md-gray-700" for="grant-reason">Reason</label>
          <textarea
            id="grant-reason"
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
            <button type="button" class="btn btn-secondary btn-sm" [disabled]="submitting" (click)="close()">
              Cancel
            </button>
            @if (active) {
              <button
                type="button"
                class="btn btn-danger btn-sm"
                data-testid="end-grant"
                [disabled]="submitting"
                (click)="end()"
              >
                {{ submitting ? 'Working…' : 'End grant' }}
              </button>
            }
            <button
              type="button"
              class="btn btn-primary btn-sm"
              data-testid="save-grant"
              [disabled]="submitting"
              (click)="save()"
            >
              {{ submitting ? 'Working…' : 'Save grant' }}
            </button>
          </div>
        }
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
      }
    `
  ]
})
export class BusinessPlanGrantDialogComponent implements OnInit {
  @Input({ required: true }) businessId!: number;
  @Input({ required: true }) businessName!: string;

  /** Emitted after a committed grant, change or end, so the page can re-read what it shows. */
  @Output() readonly saved = new EventEmitter<SuperAdminPlanGrant>();
  @Output() readonly closed = new EventEmitter<void>();

  readonly plans = GRANTABLE_PLANS;

  plan: GrantablePlan = 'Starter';
  endsOn = '';
  reason = '';

  loading = false;
  loadError: string | null = null;
  submitting = false;
  errorMessage: string | null = null;

  /** The revision the server last reported - never a number this dialog chose. */
  private revision = 0;
  private grant: SuperAdminPlanGrant | null = null;

  constructor(private readonly grants: SuperAdminPlanGrantsService) {}

  ngOnInit(): void {
    this.load();
  }

  /** Whether the business holds a grant that currently applies, as the server reported it. */
  get active(): boolean {
    return this.grant?.active === true;
  }

  get currentGrantSummary(): string {
    if (!this.grant?.hasGrant) {
      return 'This business holds no manual grant. Its plan comes from its subscription, or Free.';
    }
    const plan = this.grant.plan ?? 'an unknown plan';
    const until = this.grant.endsOnUtc ? ` until ${this.grant.endsOnUtc} (UTC)` : ' with no end date';
    return this.grant.active
      ? `Granted ${plan}${until}.`
      : `A ${plan} grant was ended and no longer applies.`;
  }

  close(): void {
    if (this.submitting) {
      return;
    }
    this.closed.emit();
  }

  save(): void {
    this.submitting = true;
    this.errorMessage = null;

    this.grants
      .set(this.businessId, this.plan, this.endsOn === '' ? null : this.endsOn, this.reason, this.revision)
      .subscribe({
        next: (grant) => this.onSaved(grant),
        error: (err: unknown) => this.onFailed(err, 'Unable to save this grant.')
      });
  }

  end(): void {
    this.submitting = true;
    this.errorMessage = null;

    this.grants.end(this.businessId, this.reason, this.revision).subscribe({
      next: (grant) => this.onSaved(grant),
      error: (err: unknown) => this.onFailed(err, 'Unable to end this grant.')
    });
  }

  private load(): void {
    this.loading = true;
    this.loadError = null;

    this.grants.get(this.businessId).subscribe({
      next: (grant) => {
        this.loading = false;
        this.apply(grant);
      },
      error: (err: unknown) => {
        this.loading = false;
        this.loadError = extractErrorMessage(err, 'Unable to load this business’s grant.');
      }
    });
  }

  private onSaved(grant: SuperAdminPlanGrant): void {
    this.submitting = false;
    this.apply(grant);
    this.saved.emit(grant);
  }

  private onFailed(err: unknown, fallback: string): void {
    this.submitting = false;
    this.errorMessage = extractErrorMessage(err, fallback);
  }

  /**
   * Takes the whole state from the server's answer - the plan to offer, the end date, the reason and
   * the revision the next write must present - so a committed decision leaves the form showing what
   * was stored rather than what was typed.
   */
  private apply(grant: SuperAdminPlanGrant): void {
    this.grant = grant;
    this.revision = grant.revision;
    this.plan = grant.plan === 'Pro' ? 'Pro' : 'Starter';
    this.endsOn = grant.endsOnUtc ?? '';
    this.reason = grant.hasGrant ? (grant.reason ?? '') : '';
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
