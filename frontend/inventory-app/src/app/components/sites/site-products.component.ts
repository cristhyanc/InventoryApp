import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BehaviorSubject, catchError, map, Observable, of, switchMap, tap } from 'rxjs';
import { Site, SiteProduct } from '../../models/models';
import { SiteService } from '../../services/site.service';

@Component({
  selector: 'app-site-products',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './site-products.component.html'
})
export class SiteProductsComponent implements OnInit {
  site$!: Observable<Site | null>;
  products$!: Observable<SiteProduct[]>;
  products: SiteProduct[] = [];
  loading$ = new BehaviorSubject(true);
  error$ = new BehaviorSubject('');

  constructor(
    private route: ActivatedRoute,
    private siteService: SiteService
  ) {}

  ngOnInit(): void {
    const siteId$ = this.route.paramMap.pipe(map(params => Number(params.get('id'))));

    this.site$ = siteId$.pipe(
      tap(() => {
        this.loading$.next(true);
        this.error$.next('');
      }),
      switchMap(siteId => {
        if (!siteId || Number.isNaN(siteId)) {
          this.error$.next('Invalid site id.');
          this.loading$.next(false);
          return of(null);
        }

        return this.siteService.getAll().pipe(
          map(sites => sites.find(site => site.siteId === siteId) ?? null),
          tap(() => this.loading$.next(false)),
          catchError(() => {
            this.error$.next('Failed to load site details.');
            this.loading$.next(false);
            return of(null);
          })
        );
      })
    );

    this.products$ = siteId$.pipe(
      switchMap(siteId => {
        if (!siteId || Number.isNaN(siteId)) return of([] as SiteProduct[]);

        return this.siteService.getProducts(siteId).pipe(
          tap(products => (this.products = products)),
          catchError(() => {
            this.products = [];
            this.error$.next('Failed to load products for this site.');
            return of([] as SiteProduct[]);
          })
        );
      })
    );
  }

  sortColumn: SiteProductSortColumn = 'mdbCode';
  sortDirection: SiteProductSortDirection = 'asc';

  get sortedProducts(): SiteProduct[] {
    const direction = this.sortDirection === 'asc' ? 1 : -1;

    return [...this.products].sort((left, right) => {
      const leftValue = this.columnValue(left, this.sortColumn);
      const rightValue = this.columnValue(right, this.sortColumn);

      // Missing values always sort last, in both directions, so toggling a column's direction never
      // scatters its "Unavailable"/missing rows to the top.
      if (leftValue == null && rightValue == null) return this.tieBreak(left, right);
      if (leftValue == null) return 1;
      if (rightValue == null) return -1;

      const comparison = typeof leftValue === 'number' && typeof rightValue === 'number'
        ? leftValue - rightValue
        : String(leftValue).localeCompare(String(rightValue));
      return comparison !== 0 ? comparison * direction : this.tieBreak(left, right);
    });
  }

  sortBy(column: SiteProductSortColumn): void {
    if (this.sortColumn === column) {
      this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
      return;
    }

    this.sortColumn = column;
    this.sortDirection = 'asc';
  }

  ariaSort(column: SiteProductSortColumn): 'ascending' | 'descending' | 'none' {
    if (this.sortColumn !== column) return 'none';
    return this.sortDirection === 'asc' ? 'ascending' : 'descending';
  }

  sortLabel(label: string, column: SiteProductSortColumn): string {
    if (this.sortColumn !== column) return label;
    return `${label} (${this.sortDirection === 'asc' ? 'ascending' : 'descending'})`;
  }

  private columnValue(product: SiteProduct, column: SiteProductSortColumn): string | number | null {
    switch (column) {
      case 'name': return product.name;
      case 'mdbCode': return product.mdbCode;
      case 'averageUnitCost': return product.averageUnitCost;
      case 'sitePrice': return product.sitePrice;
      case 'estimatedCardProfit': return product.estimatedCardProfit;
      case 'quantityInStock': return product.quantityInStock;
    }
  }

  /** The deterministic tie-break for equal or missing values on the active column: product name, then id. */
  private tieBreak(left: SiteProduct, right: SiteProduct): number {
    const byName = left.name.localeCompare(right.name);
    return byName !== 0 ? byName : left.productId - right.productId;
  }

  stockClass(product: SiteProduct): string {
    const percentage = product.maxStock > 0
      ? (product.quantityInStock / product.maxStock) * 100
      : 100;

    if (percentage < 30) return 'badge-danger';
    if (percentage < 80) return 'badge-warning';
    return 'badge-success';
  }

  exportProducts(site: Site): void {
    if (!this.products.length) return;

    const headers = ['Product', 'Average Unit Cost', 'Site Price', 'Estimated Card Profit', 'Stock', 'Max Stock'];
    const rows = this.products.map(product => [
      product.name,
      product.averageUnitCost?.toFixed(2) ?? 'Unavailable',
      product.sitePrice.toFixed(2),
      product.estimatedCardProfit?.toFixed(2) ?? 'Unavailable',
      product.quantityInStock.toString(),
      product.maxStock.toString()
    ]);
    const csv = [headers, ...rows]
      .map(row => row.map(value => `"${value.replace(/"/g, '""')}"`).join(','))
      .join('\r\n');
    const blob = new Blob([`\ufeff${csv}`], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    const filename = (site.siteName || 'site').replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '');

    link.href = url;
    link.download = `${filename || 'site'}-products.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }
}

type SiteProductSortColumn = 'name' | 'mdbCode' | 'averageUnitCost' | 'sitePrice' | 'estimatedCardProfit' | 'quantityInStock';
type SiteProductSortDirection = 'asc' | 'desc';
