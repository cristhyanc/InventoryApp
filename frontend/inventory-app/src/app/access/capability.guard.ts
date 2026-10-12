import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Observable, map, take } from 'rxjs';
import { AccessService } from './access.service';
import { Capability } from './capabilities';

/** The page a route the member's capability set does not allow sends them to. */
export const ACCESS_NOT_ALLOWED_PATH = 'access-not-allowed';

/**
 * A route guard that sends the member to {@link ACCESS_NOT_ALLOWED_PATH} unless their role grants
 * at least one of `capabilities` (issue #528).
 *
 * **It is presentation, not a boundary.** The endpoints the page then calls each check the same
 * capability themselves and answer a bare `403` whatever the router did (see docs/architecture.md
 * § Capability authorization), so this guard replaces a page full of refusals with one sentence
 * that explains them - it protects nothing, and nothing may be moved behind it instead of behind
 * the API. A capability set that could not be read allows the route, for the reason stated on
 * {@link AccessService}: the API still refuses what the role may not do, and refusing here would
 * lock a legitimate member out of their own page over a failed capability read.
 *
 * Several capabilities mean "any of these", which is what a page reachable through more than one
 * of them needs: the Admin hub links to pages an Imports, site-commission, integration or repair
 * capability each opens, so holding any one of them is enough to be shown the hub.
 */
export function requireCapability(...capabilities: readonly Capability[]): CanActivateFn {
  return (): Observable<boolean | UrlTree> => {
    const access = inject(AccessService);
    const router = inject(Router);

    return access.allowsAny(capabilities).pipe(
      take(1),
      map((allowed): boolean | UrlTree => allowed || router.createUrlTree([ACCESS_NOT_ALLOWED_PATH]))
    );
  };
}
