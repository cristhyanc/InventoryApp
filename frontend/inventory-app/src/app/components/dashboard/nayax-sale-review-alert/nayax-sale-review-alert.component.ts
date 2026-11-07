import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { NayaxSaleReviewService, NayaxSaleReviewSummary } from '../../../services/nayax-sale-review.service';
import { IconComponent } from '../../shared/icon.component';

/**
 * The Dashboard's "Nayax sales need your review" alert (issue #1105): one alert in the Needs
 * attention panel, shown while the business has any queue-delivered sale that Nayax contradicted
 * or that needs investigation. It links to the review page. Every member who can see the Dashboard
 * sees it; only an Owner can decide, on the review page.
 *
 * A failed read shows nothing rather than a false "nothing to review": the review page itself
 * still lists every flagged sale.
 */
@Component({
  selector: 'app-nayax-sale-review-alert',
  standalone: true,
  imports: [CommonModule, RouterLink, IconComponent],
  host: { class: 'contents' },
  template: `
    @if (isShown && summary) {
      <output class="flex items-start gap-3 rounded-lg border border-rr-line bg-white p-3 font-rr" data-testid="nayax-sale-review-alert">
        <span class="mt-0.5 shrink-0 text-rr-warn-text" aria-hidden="true"><app-icon name="receipt" [size]="20" /></span>
        <span class="block min-w-0">
          <span class="block text-sm font-semibold text-rr-navy" data-testid="nayax-sale-review-alert-title">{{ title }}</span>
          <span class="block text-sm text-rr-muted">
            Nayax's own records disagree with, or don't yet show, these sales.
            <a routerLink="/nayax-sales-review" class="font-semibold text-rr-navy underline" data-testid="nayax-sale-review-alert-link">Review sales</a>
          </span>
        </span>
      </output>
    }
  `
})
export class NayaxSaleReviewAlertComponent implements OnInit, OnDestroy {
  summary: NayaxSaleReviewSummary | null = null;

  private subscription: Subscription | null = null;

  constructor(private readonly review: NayaxSaleReviewService) {}

  ngOnInit(): void {
    this.subscription = this.review.summary().subscribe({
      next: (summary) => (this.summary = summary),
      error: () => (this.summary = null)
    });
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

  /** Whether anything waits for the Owner. */
  get isShown(): boolean {
    return (this.summary?.needsReviewCount ?? 0) > 0;
  }

  /** For example "3 Nayax sales need your review · 1 is not being counted". */
  get title(): string {
    const summary = this.summary;
    if (!summary) {
      return '';
    }
    const review =
      summary.needsReviewCount === 1
        ? '1 Nayax sale needs your review'
        : `${summary.needsReviewCount} Nayax sales need your review`;
    if (summary.notCountedCount === 0) {
      return review;
    }
    const counted =
      summary.notCountedCount === 1 ? '1 is not being counted' : `${summary.notCountedCount} are not being counted`;
    return `${review} · ${counted}`;
  }
}
