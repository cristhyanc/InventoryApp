import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, of, Subscription, tap } from 'rxjs';
import { Site } from '../../models/models';
import { SiteService } from '../../services/site.service';
import { ListLoadState } from '../shared/list-load-state';
import { trendLabel, trendClass } from '../../formatting/revenue-trend';
import { siteStockClass } from '../../formatting/site-stock-status';
import { NayaxStatusBannerComponent } from '../shared/nayax-status-banner/nayax-status-banner.component';
import { AccessService } from '../../access/access.service';

@Component({
  selector: 'app-site-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NayaxStatusBannerComponent],
  templateUrl: './site-list.component.html'
})
export class SiteListComponent implements OnInit, OnDestroy {
  sites: Site[] = [];
  search = '';
  readonly loadState = new ListLoadState();

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials` (issue #699): the
   * Today/This Week/vs Last Week revenue columns are hidden without it, on the frontend-level
   * check this page applies ahead of `GET /api/sites` redacting the figures itself (#697/#698).
   * Starts `true`, the fail-open default every other capability-gated control in this codebase
   * uses while the capability has not been read yet.
   */
  canViewFinancials = true;

  private readonly financialsAccessSubscription: Subscription;

  constructor(
    private readonly siteService: SiteService,
    access: AccessService
  ) {
    this.financialsAccessSubscription = access
      .allows('Dashboard.ViewFinancials')
      .subscribe((allowed) => (this.canViewFinancials = allowed));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.financialsAccessSubscription.unsubscribe();
  }

  load(): void {
    const token = this.loadState.start();
    this.siteService
      .getAll()
      .pipe(
        tap(sites => {
          if (this.loadState.isCurrent(token)) {
            this.sites = sites;
          }
          this.loadState.succeed(token);
        }),
        catchError(() => {
          this.loadState.fail(token);
          return of([] as Site[]);
        })
      )
      .subscribe();
  }

  get filteredSites(): Site[] {
    const term = this.search.trim().toLowerCase();
    if (!term) return this.sites;
    return this.sites.filter(site => site.siteName.toLowerCase().includes(term));
  }

  siteStockClass(site: Site): string {
    return siteStockClass(site);
  }

  trendLabel(current: number | null | undefined, previous: number | null | undefined): string {
    return trendLabel(current, previous);
  }

  trendClass(current: number | null | undefined, previous: number | null | undefined): string {
    return trendClass(current, previous);
  }

  /**
   * The whole row is the navigation target, but the link itself is the real `<a>` in the site
   * cell: the browser gives that anchor its focus, Enter activation, ctrl/cmd/middle-click and
   * "copy link address" behaviour, which an ARIA `role="link"` on the row cannot provide. This
   * handler only extends the anchor's plain-click target to the rest of the row by activating
   * it, so there is one destination and one navigation path. Two kinds of click are left alone:
   * one that came from a control inside the row - the anchor included, which would otherwise
   * navigate twice - and a modified or non-primary click, which the browser handles itself.
   * Enter is bound to the same handler so the row's pointer path has a keyboard equivalent: the
   * row is not focusable, so Enter only reaches it from a focused control inside it (normally
   * the anchor, which has already navigated) and is ignored by the same rule.
   */
  followRowLink(rowEvent: Event): void {
    // Bound to (click) and (keydown.enter) only; Angular types both template $events as Event.
    const event = rowEvent as MouseEvent | KeyboardEvent;
    const ownControl = (event.target as Element | null)?.closest(
      'a, button, input, select, textarea, label, [role="button"]'
    );
    const browserHandlesIt =
      event.defaultPrevented ||
      (event instanceof MouseEvent && event.button !== 0) ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey ||
      event.altKey;
    if (ownControl || browserHandlesIt) return;

    (event.currentTarget as Element).querySelector<HTMLAnchorElement>('a.table-row-anchor')?.click();
  }
}
