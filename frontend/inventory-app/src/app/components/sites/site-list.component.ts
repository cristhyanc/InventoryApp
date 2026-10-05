import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, of, tap } from 'rxjs';
import { Site } from '../../models/models';
import { SiteService } from '../../services/site.service';
import { ListLoadState } from '../shared/list-load-state';

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

  constructor(private siteService: SiteService) {}

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
    if (site.totalStockPercentage < 30) return 'bg-red-100 text-red-700';
    if (site.totalStockPercentage < 80) return 'bg-yellow-100 text-yellow-700';
    return 'bg-green-100 text-green-700';
  }

  private trend(current: number, previous: number): number | null {
    return previous === 0 ? null : ((current - previous) / previous) * 100;
  }

  trendLabel(current: number, previous: number): string {
    const value = this.trend(current, previous);
    if (value === null) return 'No prior sales';
    return `${value >= 0 ? '↑' : '↓'} ${Math.abs(value).toFixed(1)}%`;
  }

  trendClass(current: number, previous: number): string {
    const value = this.trend(current, previous);
    if (value === null) return 'text-slate-500';
    return value >= 0 ? 'text-emerald-600' : 'text-rose-600';
  }
}
