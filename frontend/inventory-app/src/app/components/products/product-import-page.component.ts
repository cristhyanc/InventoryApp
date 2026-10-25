import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SupportSessionService } from '../../support-session/support-session.service';
import { CatalogueReviewComponent } from '../admin/imports/catalogue-review/catalogue-review.component';

/**
 * The dedicated Import products page (issue #888): `/products/import`, the one place the product
 * catalogue import is reached from.
 *
 * <p>Importing the catalogue <strong>is</strong> the catalogue review (issue #739, parent #329
 * decision C2), which until this change was a panel Admin &rarr; Imports opened below its three
 * import cards. It is a distinct workflow with its own two-step state, its own actions and its own
 * loading and error lifecycle, so it belongs on its own page rather than inside another one's - and
 * this page composes it and owns nothing of its workflow (AGENTS.md &sect; Frontend rules, and
 * docs/architecture.md &sect; Page composition boundary). Nothing about the review itself changed:
 * same API calls, same steps, same decisions, same applied result.</p>
 *
 * <p>The page carries no provider guard of its own. A business whose Nayax connection cannot serve
 * the catalogue reaches the review's own unavailable/error handling, which is where that answer has
 * always been given; the sidebar and the All Products button hide the entry points instead.</p>
 */
@Component({
  selector: 'app-product-import-page',
  standalone: true,
  imports: [CommonModule, RouterLink, CatalogueReviewComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/products" class="btn-link text-sm">&larr; Back to Products</a>
          <h1 class="page-title mt-2">Import products</h1>
          <p class="page-subtitle">
            Review the Nayax catalogue against your own products and categories, then link, create or
            update them per entry. Nothing is written until you apply the review.
          </p>
        </div>
      </header>

      @if (sessionActive) {
        <div class="alert alert-info" role="status" data-testid="import-support-session-notice">
          <p class="alert-title">Imports are unavailable during a support session.</p>
        </div>
      } @else {
        <app-catalogue-review (closed)="close()" />
      }
    </div>
  `
})
export class ProductImportPageComponent implements OnDestroy {
  /**
   * Whether a support session is active (issue #661, kept from Admin &rarr; Imports): a session
   * grants `Imports.Run` - the capability this page and the review's endpoints are gated on - and
   * the backend refuses the write only by its read-only method rule, so the review is withheld
   * here rather than rendered and refused. It is withheld rather than disabled because the review
   * starts reading the catalogue the moment it renders.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly router: Router,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  /** Leaves the review for the page the operator came from, rather than only taking it off screen. */
  close(): void {
    void this.router.navigateByUrl('/products');
  }
}
