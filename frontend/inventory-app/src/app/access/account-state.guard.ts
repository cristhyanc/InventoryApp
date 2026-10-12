import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Observable, filter, map, take } from 'rxjs';
import { AccountStateName, AccountStateService } from './account-state.service';

/**
 * The route a signed-in person whose account state is not `Member` is sent to (issue #529's
 * account-state routing contract, agreed in #328). `Member` is deliberately absent: it is handled
 * separately by {@link accountStateGuard}, because it is the one state that lets the navigation
 * through rather than redirecting it.
 */
export const ACCOUNT_STATE_ROUTE: Readonly<Record<Exclude<AccountStateName, 'Member'>, string>> = {
  NoMembership: 'welcome',
  ApplicationPending: 'application-pending',
  ApplicationRejected: 'application-rejected',
  MembershipRevoked: 'access-unavailable',
  BusinessDeactivated: 'access-unavailable',
  MembershipAmbiguous: 'access-unavailable'
};

/** `/invite/:token` (issue #505), named here rather than invented twice. */
export const INVITE_ROUTE_PREFIX = 'invite/';

/**
 * Every route a signed-in person without a usable membership may legitimately be on: the three
 * {@link ACCOUNT_STATE_ROUTE} destinations and the invite route #505 builds. None of them carries
 * `accountStateGuard`, for the reason documented on it, and `memberForbiddenNoticeInterceptor`
 * bypasses every one of them too (issue #529).
 */
export const ACCOUNT_STATE_BYPASS_PATHS: ReadonlySet<string> = new Set<string>(Object.values(ACCOUNT_STATE_ROUTE));

/**
 * Sends a signed-in person who is not `Member` to the route their account state calls for, before
 * the business route they asked for ever activates (issue #529). Every onboarding route, the
 * access-unavailable page and `/invite/:token` carry no `accountStateGuard` of their own - adding
 * one would redirect a person this guard already sent there straight back to themselves, or away
 * from the one explanation their state has.
 *
 * **It is presentation, not a boundary.** The member endpoints behind every business route stay
 * refused by `BusinessScopeMiddleware` whatever the router did, so an account state that could not
 * be read lets the navigation through rather than guessing - the same fail-open rule
 * {@link AccountStateService} documents, and the same reason `requireCapability` fails open on an
 * unreadable capability set.
 */
export const accountStateGuard: CanActivateFn = (): Observable<boolean | UrlTree> => {
  const accountState = inject(AccountStateService);
  const router = inject(Router);

  return accountState.state$.pipe(
    filter((answer) => answer.status !== 'unread'),
    take(1),
    map((answer): boolean | UrlTree => {
      if (answer.status === 'unavailable') {
        return true;
      }

      const { state } = answer.accountState;
      if (state === 'Member') {
        return true;
      }

      return router.createUrlTree([ACCOUNT_STATE_ROUTE[state]]);
    })
  );
};
