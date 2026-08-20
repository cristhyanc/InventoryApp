import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BehaviorSubject, catchError, forkJoin, map, Observable, of, switchMap, tap } from 'rxjs';
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
          catchError(() => {
            this.error$.next('Failed to load products for this site.');
            return of([] as SiteProduct[]);
          })
        );
      })
    );
  }

  stockClass(product: SiteProduct): string {
    const percentage = product.maxStock > 0
      ? (product.quantityInStock / product.maxStock) * 100
      : 100;

    if (percentage < 30) return 'bg-red-100 text-red-700';
    if (percentage < 80) return 'bg-yellow-100 text-yellow-700';
    return 'bg-green-100 text-green-700';
  }
}
