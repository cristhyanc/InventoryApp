import { Injectable } from '@angular/core';
import { NavigationStart, Router } from '@angular/router';
import { BehaviorSubject, filter } from 'rxjs';

/**
 * Holds the one live breadcrumb label a routed page may contribute from data it already loaded
 * (issue #457), for example the machine name on `/machines/:id`. A page calls
 * `setCurrentPageLabel` once its own existing request resolves; this service makes no request of
 * its own.
 *
 * The label resets to `null` on every `NavigationStart`, before the destination page has had a
 * chance to set a new one. That is what keeps a reused routed component (`/machines/5` to
 * `/machines/9`, the same `MachineDetailComponent` instance) from showing the previous page's name
 * while the new one is still loading — `BreadcrumbsComponent` falls back to the route's generic
 * label (`breadcrumb-routes.ts`) for exactly that gap.
 */
@Injectable({ providedIn: 'root' })
export class BreadcrumbService {
  private readonly labelSubject = new BehaviorSubject<string | null>(null);

  readonly currentPageLabel$ = this.labelSubject.asObservable();

  constructor(router: Router) {
    router.events.pipe(filter((event): event is NavigationStart => event instanceof NavigationStart)).subscribe(() => {
      this.labelSubject.next(null);
    });
  }

  setCurrentPageLabel(label: string | null): void {
    this.labelSubject.next(label);
  }
}
