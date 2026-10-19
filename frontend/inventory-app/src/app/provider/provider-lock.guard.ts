import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Observable, map, take } from 'rxjs';
import { ProviderAvailabilityService } from './provider-availability.service';
import { ProviderFeature } from './provider-feature-permissions';
import { PROVIDER_LOCKED_FROM_PARAM, PROVIDER_LOCKED_PATH, providerLockOf } from './provider-lock';

/**
 * A route guard that sends an operator to the "needs a provider" page while the shared availability
 * signal (issue #728) refuses the live Nayax read `feature` names (issue #729).
 *
 * **It is presentation, not a boundary**, exactly like `requireCapability` beside it: every endpoint
 * behind these pages resolves the business's own Nayax credentials and answers `409` itself
 * (AGENTS.md § Nayax integration), whatever the router did. What the guard changes is that opening
 * `/machines` with no provider shows one sentence and a Connect button instead of a page of
 * refusals - and that the menu entry and the URL behave the same way, which is the whole point of
 * issue #729's locked item.
 *
 * It is declared **after** the capability guard on every route that carries it, so a member whose
 * role does not include the page is sent to their own explanation rather than told to connect a
 * provider they would still not be allowed to use.
 *
 * An unread or unreadable status answer is `open` (`providerLockOf`), so a failed status probe never
 * locks a page whose own request would have answered it.
 */
export function requireProvider(feature: ProviderFeature): CanActivateFn {
  return (_route, state): Observable<boolean | UrlTree> => {
    const availability = inject(ProviderAvailabilityService);
    const router = inject(Router);

    return availability.featureAvailability(feature).pipe(
      take(1),
      map((answer): boolean | UrlTree => {
        if (providerLockOf(answer) === 'open') {
          return true;
        }

        // The refused URL travels as a query parameter so the page can name the destination the
        // operator asked for. It decides nothing: the page resolves that destination from the
        // declared navigation and re-reads the availability answer for itself.
        return router.createUrlTree([PROVIDER_LOCKED_PATH], {
          queryParams: { [PROVIDER_LOCKED_FROM_PARAM]: state.url }
        });
      })
    );
  };
}
