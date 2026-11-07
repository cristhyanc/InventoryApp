import { CommonModule, CurrencyPipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import {
  NayaxSaleAvailableDecision,
  NayaxSaleEvidenceKind,
  NayaxSaleFieldComparison,
  NayaxSaleReviewItem,
  NayaxSaleReviewService,
  NayaxSqsVerification,
  decisionLabel,
  decisionRequiresReason,
  verificationLabel
} from '../../services/nayax-sale-review.service';

/** The decision the Owner is filling in for one sale. */
interface PendingDecision {
  saleId: number;
  option: NayaxSaleAvailableDecision;
  reason: string;
}

/**
 * The "Nayax sales to review" page (issue #1105): the business's queue-delivered sales that Nayax's
 * own `lastSales` contradicted or that need investigation, and those an Owner has decided, each with
 * its stored values beside Nayax's evidence and its decision history.
 *
 * Every member with `Dashboard.View` can read it. The decisions appear only for an Owner
 * (`Data.Repair`), and only those the server says the decision table allows for the sale and its
 * evidence. An absence is described as what `lastSales` returned for the machine - never as
 * "missing from Nayax" or "fake". A refused decision shows the server's own sentence; a 409 (the
 * sale changed, or Nayax's product is not linked) reloads the list so the Owner decides against what
 * is true now.
 */
@Component({
  selector: 'app-nayax-sale-review-page',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, BusinessDateTimePipe, CurrencyPipe],
  templateUrl: './nayax-sale-review-page.component.html'
})
export class NayaxSaleReviewPageComponent implements OnInit, OnDestroy {
  readonly decisionLabel = decisionLabel;
  readonly verificationLabel = verificationLabel;
  readonly requiresReason = decisionRequiresReason;
  readonly EvidenceKind = NayaxSaleEvidenceKind;

  items: NayaxSaleReviewItem[] = [];
  state: 'loading' | 'loaded' | 'failed' = 'loading';
  canDecide = false;
  pending: PendingDecision | null = null;
  saving = false;
  message: string | null = null;
  error: string | null = null;

  private readonly subscriptions = new Subscription();

  constructor(private readonly review: NayaxSaleReviewService, access: AccessService) {
    this.subscriptions.add(access.allows('Data.Repair').subscribe((allowed) => (this.canDecide = allowed)));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
  }

  load(): void {
    this.state = 'loading';
    this.subscriptions.add(
      this.review.list().subscribe({
        next: (items) => {
          this.items = items;
          this.state = 'loaded';
        },
        error: () => {
          this.items = [];
          this.state = 'failed';
        }
      })
    );
  }

  /** Whether the sale waits for the Owner (a conflict, or a sale needing investigation). */
  isFlagged(item: NayaxSaleReviewItem): boolean {
    return item.state === NayaxSqsVerification.Conflict || item.state === NayaxSqsVerification.NeedsInvestigation;
  }

  /** The decisions to offer: none unless the member is an Owner. */
  decisionsFor(item: NayaxSaleReviewItem): NayaxSaleAvailableDecision[] {
    return this.canDecide ? item.availableDecisions : [];
  }

  /** How one compared field came out, in words. */
  comparisonLabel(value: NayaxSaleFieldComparison | null): string {
    switch (value) {
      case NayaxSaleFieldComparison.Match:
        return 'matches';
      case NayaxSaleFieldComparison.Mismatch:
        return 'differs';
      default:
        return 'not compared';
    }
  }

  start(item: NayaxSaleReviewItem, option: NayaxSaleAvailableDecision): void {
    this.message = null;
    this.error = null;
    this.pending = { saleId: item.saleId, option, reason: '' };
  }

  cancel(): void {
    this.pending = null;
  }

  /** Whether the pending decision may be sent: a reason is typed where one is required. */
  get canSubmit(): boolean {
    const pending = this.pending;
    return (
      pending !== null
      && !this.saving
      && (!decisionRequiresReason(pending.option.decision) || pending.reason.trim().length > 0)
    );
  }

  submit(item: NayaxSaleReviewItem): void {
    const pending = this.pending;
    if (!pending || pending.saleId !== item.saleId || !this.canSubmit || item.latestEvidenceId === null) {
      return;
    }

    this.saving = true;
    this.error = null;
    this.subscriptions.add(
      this.review
        .decide(item.saleId, {
          presentedState: item.state,
          presentedLatestEvidenceId: item.latestEvidenceId,
          evidenceId: pending.option.evidenceId,
          decision: pending.option.decision,
          reason: pending.reason.trim() === '' ? null : pending.reason.trim()
        })
        .subscribe({
          next: () => {
            this.saving = false;
            this.pending = null;
            this.message = `Saved: ${decisionLabel(pending.option.decision)} for transaction ${item.transactionId}.`;
            this.load();
          },
          error: (err: unknown) => {
            this.saving = false;
            this.error = errorMessage(err);
            if ((err as { status?: number } | undefined)?.status === 409) {
              this.pending = null;
              this.load();
            }
          }
        })
    );
  }
}

/** The server's own sentence for a refusal, or a fallback. */
export function errorMessage(err: unknown): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string' && body.trim() !== '') {
    return body;
  }
  const problem = body as { message?: string; detail?: string; title?: string } | undefined;
  return problem?.message ?? problem?.detail ?? problem?.title ?? 'The decision could not be saved. Try again.';
}
