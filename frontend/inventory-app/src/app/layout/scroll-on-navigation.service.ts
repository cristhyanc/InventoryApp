import { Injectable, OnDestroy, inject } from '@angular/core';
import { ViewportScroller } from '@angular/common';
import { NavigationEnd, Router, Scroll } from '@angular/router';
import { Subscription } from 'rxjs';

/**
 * The root-level `Scroll` event handler (issue #1048). `app.config.ts` enables
 * `withInMemoryScrolling({ scrollPositionRestoration: 'disabled', anchorScrolling: 'disabled' })`
 * so the router still emits a deferred `Scroll` event after every navigation, but its own built-in
 * restoration - which resets to the top on *every* forward navigation, including a same-path one -
 * never runs. This service is what decides what that event does instead, in the one central place
 * the issue asks for; no page component needs any scroll code of its own.
 *
 * The rule is the "primary route path" - the URL without its query string or fragment:
 *
 * - Back/Forward (`event.position` is set) restores the position that history entry had.
 * - A fragment (`event.anchor` is set, and it is not a Back/Forward navigation) scrolls to that
 *   element instead of the top.
 * - Otherwise, a primary path that differs from the one before it scrolls to the top; a primary
 *   path that stays the same leaves the window exactly where it was. That one comparison is also
 *   what keeps a query-param-only update, a `replaceUrl`, re-clicking the current page's menu item
 *   and the role-preview / support-session `onSameUrlNavigation: 'reload'` back to `/` from moving
 *   the page: none of them changes the primary path.
 *
 * A cancelled, guard-redirected-away or failed navigation never reaches a `NavigationEnd` (or the
 * same-URL `NavigationSkipped`) the router turns into a `Scroll` event, so there is nothing here to
 * hold back for it; the current page simply stays where it was.
 */
@Injectable({ providedIn: 'root' })
export class ScrollOnNavigationService implements OnDestroy {
  private readonly router = inject(Router);
  private readonly viewportScroller = inject(ViewportScroller);
  private readonly subscription: Subscription;

  private previousPrimaryPath: string | null = null;

  constructor() {
    this.subscription = this.router.events.subscribe((event) => {
      if (event instanceof Scroll) {
        this.handleScroll(event);
      }
    });
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
  }

  private handleScroll(event: Scroll): void {
    const url = event.routerEvent instanceof NavigationEnd ? event.routerEvent.urlAfterRedirects : event.routerEvent.url;
    const primaryPath = ScrollOnNavigationService.primaryPathOf(url);
    const previousPrimaryPath = this.previousPrimaryPath;
    this.previousPrimaryPath = primaryPath;

    if (event.position) {
      this.viewportScroller.scrollToPosition(event.position);
      return;
    }

    if (event.anchor) {
      this.viewportScroller.scrollToAnchor(event.anchor);
      return;
    }

    if (previousPrimaryPath === primaryPath) {
      return;
    }

    this.viewportScroller.scrollToPosition([0, 0]);
  }

  /** The URL up to its query string or fragment, whichever comes first. */
  private static primaryPathOf(url: string): string {
    const queryIndex = url.indexOf('?');
    const fragmentIndex = url.indexOf('#');
    const end = Math.min(
      queryIndex === -1 ? url.length : queryIndex,
      fragmentIndex === -1 ? url.length : fragmentIndex
    );
    return url.slice(0, end);
  }
}
