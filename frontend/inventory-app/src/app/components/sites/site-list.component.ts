import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, of, Subscription, tap } from 'rxjs';
import { Site } from '../../models/models';
import { SiteService } from '../../services/site.service';
import { ListLoadState } from '../shared/list-load-state';
import { trendLabel, trendClass } from '../../formatting/revenue-trend';
import {
  compareRefillPriority,
  compareSiteNames,
  knownPercentage,
  siteNeedsRefill,
  siteRefillStatus,
  SiteRefillStatus
} from '../../formatting/site-refill-status';
import { plural } from '../../formatting/plural';
import { NayaxStatusBannerComponent } from '../shared/nayax-status-banner/nayax-status-banner.component';
import { IconComponent } from '../shared/icon.component';
import { AccessService } from '../../access/access.service';

/** The Sites overview's two views of the list (issue #1128). */
export type SiteListFilter = 'all' | 'needsRefill';

/** The Sites overview's orders; refill priority is the default (issue #1128). */
export type SiteListSort = 'refillPriority' | 'name';

@Component({
  selector: 'app-site-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NayaxStatusBannerComponent, IconComponent],
  templateUrl: './site-list.component.html'
})
export class SiteListComponent implements OnInit, OnDestroy {
  sites: Site[] = [];
  search = '';
  filter: SiteListFilter = 'all';
  sort: SiteListSort = 'refillPriority';
  readonly loadState = new ListLoadState();

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials` (issue #699): the
   * Sales today/Sales this week/vs last week figures are hidden without it, on the frontend-level
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

  /**
   * The rows on screen (issue #1128): the name search and the All sites / Needs refill filter
   * applied together, in the selected order. Both run over the one `GET /api/sites` answer already
   * loaded, so neither changes any figure a row shows.
   */
  get filteredSites(): Site[] {
    const term = this.search.trim().toLowerCase();
    const compare = this.sort === 'name' ? compareSiteNames : compareRefillPriority;
    return this.sites
      .filter((site) => !term || site.siteName.toLowerCase().includes(term))
      .filter((site) => this.filter === 'all' || siteNeedsRefill(site))
      .sort(compare);
  }

  /** The count the Needs refill control shows: sites with a known positive low or empty count. */
  get needsRefillCount(): number {
    return this.sites.filter((site) => siteNeedsRefill(site)).length;
  }

  /** "2 sites · 3 machines", counted from the loaded overview rows themselves. */
  get summaryLine(): string {
    const machines = this.sites.reduce(
      (total, site) => total + (Number.isFinite(site.machineCount) ? site.machineCount : 0),
      0
    );
    return `${plural(this.sites.length, 'site')} · ${plural(machines, 'machine')}`;
  }

  get hasSearch(): boolean {
    return this.search.trim().length > 0;
  }

  setFilter(filter: SiteListFilter): void {
    this.filter = filter;
  }

  clearSearch(): void {
    this.search = '';
  }

  /** The no-results action: back to every site, with no search text. */
  resetSearchAndFilter(): void {
    this.search = '';
    this.filter = 'all';
  }

  refillStatus(site: Site): SiteRefillStatus {
    return siteRefillStatus(site);
  }

  /** The site's stock percentage, or `null` when it is not a known figure (no bar fill is drawn). */
  stockPercentage(site: Site): number | null {
    return knownPercentage(site.totalStockPercentage);
  }

  /** The bar's fill width, held inside 0-100% so an out-of-range figure cannot break the layout. */
  stockBarWidth(percentage: number): string {
    return `${Math.min(100, Math.max(0, percentage))}%`;
  }

  /**
   * The site name split before its last word, so the link's arrow stays on the same line as that
   * word when a long name wraps (the arrow is decoration; the link's accessible name is its label).
   */
  nameParts(site: Site): { head: string; tail: string } {
    const name = site.siteName;
    const lastSpace = name.lastIndexOf(' ');
    return lastSpace < 0 ? { head: '', tail: name } : { head: name.slice(0, lastSpace + 1), tail: name.slice(lastSpace + 1) };
  }

  machineLabel(site: Site): string {
    return plural(site.machineCount, 'machine');
  }

  /** A revenue figure for display; `null` (withheld or redacted) stays "Unavailable", never $0.00. */
  money(value: number | null | undefined): string {
    return value == null ? 'Unavailable' : `$${value.toFixed(2)}`;
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
