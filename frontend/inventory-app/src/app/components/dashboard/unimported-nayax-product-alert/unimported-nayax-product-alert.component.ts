import { CommonModule } from '@angular/common';
import { Component, OnDestroy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription, catchError, combineLatest, of, switchMap } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { ProviderAvailabilityService } from '../../../provider/provider-availability.service';
import {
  CatalogueReviewAttentionService,
  UnimportedProviderProductAttention,
  UnimportedProviderProductExample
} from '../../../services/catalogue-review-attention.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { IconComponent } from '../../shared/icon.component';

/**
 * The Dashboard's "Nayax products are selling but aren't in your products" alert (issue #1352): one
 * alert in the Needs attention panel, shown while this business has sold a Nayax product in the last
 * thirty business dates that no product of its own carries the provider's id for. It links to Import
 * products, where those products - and the categories they belong to - are brought in.
 *
 * <p>**Why it matters.** A sale of a product the business never imported is stored with no local
 * product, so it takes no stock and carries no cost: the machine really did sell a unit, and nothing
 * in the inventory or the costing knows about it. Until this alert the only way to find out was to
 * open Import products and read the Nayax catalogue.</p>
 *
 * <p>**It makes no Nayax call.** `GET api/catalogue-review/attention` reads stored sales and stored
 * products, so the alert costs nothing and says nothing about whether Nayax is reachable right now.
 * What clears it is a product that carries the provider's id - the link or create Import products
 * performs. Attributing the old sales to that product afterwards stays the Owner's separate step on
 * the review itself, so the alert goes quiet as soon as the products are in, which is what it was
 * asking for.</p>
 *
 * <p>**Who sees it, and when it asks nothing at all.** `Imports.Run`, the capability Import products
 * itself requires: a role that may not run the import has nothing to do with being told the import
 * has work waiting, and the endpoint refuses it anyway. Like
 * `MachineRestockSyncReminderComponent`, and unlike `NayaxStatusBannerComponent`'s fail-open
 * default, it fetches nothing while that capability is not affirmatively granted. It also asks
 * nothing with no Nayax connection at all - there is then no provider catalogue to import from - and
 * nothing during a support session, which grants `Imports.Run` but cannot run the import, exactly as
 * the Import products page withholds the review there.</p>
 *
 * <p>**A failed read shows nothing**, the same rule as the other attention alerts: the alternative
 * would be claiming every selling product is imported, which a failed read cannot say either.</p>
 */
@Component({
  selector: 'app-unimported-nayax-product-alert',
  standalone: true,
  imports: [CommonModule, RouterLink, IconComponent],
  // No box of its own: the Dashboard places it as one alert in its Needs attention grid, so the
  // rendered alert - or nothing at all - is the grid item.
  host: { class: 'contents' },
  template: `
    @if (isShown) {
      <output class="flex items-start gap-3 rounded-lg border border-rr-line bg-white p-3 font-rr" data-testid="unimported-nayax-product-alert">
        <span class="mt-0.5 shrink-0 text-rr-warn-text" aria-hidden="true"><app-icon name="download" [size]="20" /></span>
        <span class="block min-w-0">
          <span class="block text-sm font-semibold text-rr-navy" data-testid="unimported-nayax-product-alert-title">{{ title }}</span>
          <span class="block text-sm text-rr-muted">
            <span data-testid="unimported-nayax-product-alert-detail">{{ detail }}</span>
            <a routerLink="/products/import" class="font-semibold text-rr-navy underline" data-testid="unimported-nayax-product-alert-link">Import products</a>
          </span>
        </span>
      </output>
    }
  `
})
export class UnimportedNayaxProductAlertComponent implements OnDestroy {
  /** The read's answer, or `null` while nothing has been asked and after a failed read. */
  attention: UnimportedProviderProductAttention | null = null;

  private readonly subscription: Subscription;

  constructor(
    attention: CatalogueReviewAttentionService,
    access: AccessService,
    provider: ProviderAvailabilityService,
    supportSession: SupportSessionService
  ) {
    // All three answers are gates rather than inputs: while any of them refuses, no request is made
    // at all and the alert stays hidden. `switchMap` is what makes a later refusal - a session
    // starting - drop a read already in flight instead of letting its answer arrive anyway.
    this.subscription = combineLatest([
      access.allows('Imports.Run'),
      provider.featureOffered('providerRecords'),
      supportSession.active$
    ])
      .pipe(
        switchMap(([allowed, providerOffered, sessionActive]) =>
          allowed && providerOffered && !sessionActive
            ? attention.read().pipe(catchError(() => of(null)))
            : of(null)
        )
      )
      .subscribe((answer) => (this.attention = answer));
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
  }

  /** Whether any Nayax product is selling without a product of this business behind it. */
  get isShown(): boolean {
    return this.count > 0;
  }

  /**
   * For example "3 Nayax products are selling but aren't in your products". It is a count of
   * products rather than of sales: two sales of one product are one product to import.
   */
  get title(): string {
    return this.count === 1
      ? "1 Nayax product is selling but isn't in your products"
      : `${this.count} Nayax products are selling but aren't in your products`;
  }

  /**
   * The named products, with "and N more" when the read named fewer than it counted, followed by
   * what those sales are doing to the figures. The link that follows is what fixes it.
   */
  get detail(): string {
    const named = (this.attention?.examples ?? []).map(displayName);
    const remaining = this.count - named.length;
    const products = remaining > 0 ? `${named.join(', ')} and ${remaining} more` : named.join(', ');
    const consequence =
      this.count === 1
        ? 'This sale takes no stock and carries no cost until the product is in Restockroo.'
        : 'These sales take no stock and carry no cost until the products are in Restockroo.';

    return named.length === 0 ? consequence : `${products}. ${consequence}`;
  }

  private get count(): number {
    return this.attention?.unimportedProductCount ?? 0;
  }
}

/**
 * What one product is called in the alert. A sale that stored no name leaves the provider's own id
 * as the only thing known about the product, and saying so is better than inventing a name: the
 * operator finds that id on the Import products page.
 */
function displayName(example: UnimportedProviderProductExample): string {
  return example.productName?.trim() ? example.productName : `Nayax product ${example.nayaxProductId}`;
}
