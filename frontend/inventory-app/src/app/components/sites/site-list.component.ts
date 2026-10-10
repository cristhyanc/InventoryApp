import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { catchError, of, tap } from 'rxjs';
import { Site } from '../../models/models';
import { SiteService } from '../../services/site.service';
import { ListLoadState } from '../shared/list-load-state';
import { trendLabel, trendClass } from '../../formatting/revenue-trend';
import { siteStockClass } from '../../formatting/site-stock-status';

@Component({
  selector: 'app-site-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './site-list.component.html'
})
export class SiteListComponent implements OnInit {
  sites: Site[] = [];
  search = '';
  readonly loadState = new ListLoadState();

  constructor(
    private readonly siteService: SiteService,
    private readonly router: Router
  ) {}

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

  openSite(site: Site): void {
    this.router.navigate(['/sites', site.siteId, 'products']);
  }
}
