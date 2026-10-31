import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Observable, map, take } from 'rxjs';
import { AccessService } from './access.service';
import { PLAN_LOCKED_FROM_PARAM, PLAN_LOCKED_PATH } from './plan-lock';
import { PlanFeature } from './plan-feature';

/**
 * A route guard that sends a member to the "needs a plan" page while the business's entitlement
 * does not include `feature` (issue #979), in the same shape `requireProvider` does for the shared
 * provider-availability signal.
 *
 * **It is presentation, not a boundary**, for the same reason `requireProvider` is: every endpoint
 * behind these pages checks the same plan itself and refuses the request regardless (`[RequirePlan]`
 * on the controller, or the Nayax credential gate's own `PlanRequired` refusal) - see
 * docs/architecture.md § Plan-locked navigation. What the guard changes is that opening a page this
 * business has not bought shows one sentence and a link to the Plan page instead of a `403` or a
 * `409`, and that the menu entry and the URL agree with each other.
 *
 * **It is declared after the capability guard and before any `requireProvider` on the same route**
 * (issue #979's criterion that the order of checks is role, then plan, then Nayax connection): a
 * role without the page reaches its own explanation first, and a business whose plan does not
 * include the sales provider is told to upgrade rather than to connect a Nayax account upgrading
 * would make usable anyway.
 *
 * An entitlement that could not be read allows the route, for the same reason `AccessService` fails
 * open: the endpoint still refuses what the plan may not use, and refusing here would lock a
 * legitimate member out of their own page over a failed read.
 */
export function requirePlan(feature: PlanFeature): CanActivateFn {
  return (_route, state): Observable<boolean | UrlTree> => {
    const access = inject(AccessService);
    const router = inject(Router);

    return access.allowsPlanFeature(feature).pipe(
      take(1),
      map((allowed): boolean | UrlTree => {
        if (allowed) {
          return true;
        }

        // The refused URL travels as a query parameter so the page can name the destination the
        // operator asked for, exactly as the provider lock's own redirect does. It decides nothing:
        // the page resolves that destination from the declared navigation and re-reads the
        // entitlement for itself.
        return router.createUrlTree([PLAN_LOCKED_PATH], { queryParams: { [PLAN_LOCKED_FROM_PARAM]: state.url } });
      })
    );
  };
}
