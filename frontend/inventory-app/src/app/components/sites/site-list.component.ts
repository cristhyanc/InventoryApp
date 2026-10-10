import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, of, tap } from 'rxjs';
import { Site } from '../../models/models';
import { SiteService } from '../../services/site.service';
import { ListLoadState } from '../shared/list-load-state';
import { trendLabel, trendClass } from '../../formatting/revenue-trend';
import { siteStockClass } from '../../formatting/site-stock-status';

@Component({
  selector: 'app-site-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './site-list.component.html'
})
export class SiteListComponent implements OnInit {
  sites: Site[] = [];
  search = '';
  readonly loadState = new ListLoadState();

  constructor(private readonly siteService: SiteService) {}

  ngOnInit(): void {
    this.load();
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

  trendLabel(current: number, previous: number): string {
    return trendLabel(current, previous);
  }

  trendClass(current: number, previous: number): string {
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
   */
  followRowLink(event: MouseEvent): void {
    const ownControl = (event.target as Element | null)?.closest(
      'a, button, input, select, textarea, label, [role="button"]'
    );
    const browserHandlesIt =
      event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey;
    if (ownControl || browserHandlesIt) return;

    (event.currentTarget as Element).querySelector<HTMLAnchorElement>('a.table-row-anchor')?.click();
  }
}
