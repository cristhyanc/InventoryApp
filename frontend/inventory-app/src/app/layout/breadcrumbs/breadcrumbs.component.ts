import { ChangeDetectionStrategy, ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { Subject, combineLatest, filter, map, startWith, takeUntil } from 'rxjs';
import { BreadcrumbItem, buildBreadcrumbTrail } from './breadcrumb-routes';
import { BreadcrumbService } from './breadcrumb.service';

/**
 * The shared route breadcrumb (issue #457), rendered once in `app.component.html` above every
 * routed page's own title. It owns no navigation data of its own: `breadcrumb-routes.ts` decides
 * the labels and parent destinations, and `BreadcrumbService` carries the one live label a page may
 * contribute from data it already loaded. Rendering nothing for a URL with no mapping (the
 * Dashboard, every standalone list page, and any page outside the known inventory) is deliberate —
 * see `breadcrumb-routes.ts` for why each omission is safe.
 */
@Component({
  selector: 'app-breadcrumbs',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './breadcrumbs.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class BreadcrumbsComponent implements OnInit, OnDestroy {
  trail: readonly BreadcrumbItem[] = [];

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly router: Router,
    private readonly breadcrumbService: BreadcrumbService,
    private readonly changeDetectorRef: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    const url$ = this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
      startWith(this.router.url)
    );

    combineLatest([url$, this.breadcrumbService.currentPageLabel$])
      .pipe(takeUntil(this.destroying$))
      .subscribe(([url, dynamicLabel]) => {
        this.trail = buildBreadcrumbTrail(url, dynamicLabel);
        this.changeDetectorRef.markForCheck();
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }
}
