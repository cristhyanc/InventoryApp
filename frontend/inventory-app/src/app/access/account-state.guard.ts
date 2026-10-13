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
 * `/apply`, the business application form (issue #543), named here rather than invented twice: it
 * is a page a person without a usable membership is sent to, like the destinations above.
 */
export const BUSINESS_APPLICATION_ROUTE = 'apply';

/**
 * Every route a signed-in person without a usable membership may legitimately be on: the
 * {@link ACCOUNT_STATE_ROUTE} destinations, the business application form (issue #543) and the
 * invite route #505 builds. None of them carries `accountStateGuard`, for the reason documented on
 * it, and `memberForbiddenNoticeInterceptor` bypasses every one of them too (issue #529).
 */
export const ACCOUNT_STATE_BYPASS_PATHS: ReadonlySet<string> = new Set<string>([
  ...Object.values(ACCOUNT_STATE_ROUTE),
  BUSINESS_APPLICATION_ROUTE
]);

/**
 * The account states #532's redemption endpoint admits - `NoMembership`, `MembershipRevoked` and
 * `ApplicationRejected`, the three that hold no active membership of their own - mirroring
 * `MembershipEligibility.AllowsActiveMembership` on the backend (issue #535). `InviteAcceptanceComponent`
 * uses this to decide whether to offer the Accept button at all; it decides nothing by itself, since
 * the redemption endpoint re-checks eligibility authoritatively and is the one that may refuse it.
 */
export const REDEEMABLE_ACCOUNT_STATES: ReadonlySet<AccountStateName> = new Set<AccountStateName>([
  'NoMembership',
  'MembershipRevoked',
  'ApplicationRejected'
]);

/**
 * Where a caller lands once their account state is known and they are not staying on the page
 * that asked for it: `Member` to the dashboard, every other state to the page
 * {@link ACCOUNT_STATE_ROUTE} already names for it. Written separately from that table rather than
 * folded into it, because `Member`'s destination there is "the route asked for" - meaningless on a
 * route nobody but these callers could be asking for.
 *
 * Two callers use it: `/invite/:token` (issue #535) for a caller whose state is not one of
 * {@link REDEEMABLE_ACCOUNT_STATES}, and `ApplicationPendingComponent`'s "check again" button
 * (issue #542) for whatever state a re-read reports once it is no longer `ApplicationPending`.
 */
export function nonRedeemableAccountStateDestination(state: AccountStateName): string {
  return state === 'Member' ? '/' : `/${ACCOUNT_STATE_ROUTE[state]}`;
}

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
