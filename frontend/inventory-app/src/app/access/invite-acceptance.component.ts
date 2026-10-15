import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subject, filter, switchMap, take, takeUntil } from 'rxjs';
import { AccessService } from './access.service';
import { AccountStateService } from './account-state.service';
import { REDEEMABLE_ACCOUNT_STATES, nonRedeemableAccountStateDestination } from './account-state.guard';
import { INVITE_ADDRESS_BAR_ROUTE } from './invite-address-bar.guard';
import { InviteRedemptionService } from './invite-redemption.service';

const GENERIC_ERROR_MESSAGE = "This invite link couldn't be accepted. Try again.";

/** Reads the text an API refusal carries, falling back to a generic one it never names. */
function errorMessageFrom(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { message?: unknown; title?: unknown } | null;
    const message = body?.message ?? body?.title;
    if (typeof message === 'string' && message.trim().length > 0) {
      return message;
    }
  }

  return GENERIC_ERROR_MESSAGE;
}

/**
 * The invite acceptance page at `/invite/:token` (issue #535, one slice of #505 under #328): where
 * an invited person who opens their link, signs in if needed, joins the business with one click.
 *
 * **The token never leaves memory.** It is read once, from the route the router already matched,
 * and held only in this component's own field - never in `localStorage`, `sessionStorage`, a
 * cookie, a log line or an error report. The very first thing `ngOnInit` does, before anything else
 * runs, is strip it back out of the address bar with `history.replaceState`: the browser's own
 * history entry no longer shows it, independently of whatever Angular's router still thinks the
 * current URL is. For a visitor who opened the link from the address bar, the route's `canMatch`
 * guard (`inviteTokenAddressBarGuard`, issue #681) has already done the same before `MsalGuard` ran,
 * so a signed-out visitor's sign-in redirect does not leave it in history either; this component's
 * own rewrite covers the router putting the matched URL back when it commits the navigation, and an
 * in-app navigation here. MSAL's own transient copy of the full address, kept only for the length
 * of a sign-in redirect round trip and cleared when it completes (`auth-config.ts`'s
 * `BrowserCacheLocation.LocalStorage`), is the one accepted residual - it is third-party bookkeeping
 * this application neither writes nor reads, not a second copy this code keeps.
 *
 * **Reaching this component at all already proves authentication**, because the route still
 * carries `MsalGuard` (`app.routes.ts`); a signed-out visitor is sent through the login redirect
 * first, with this same URL (token included) as `MsalGuard`'s own default redirect start page -
 * taken from the router's state, not the already-rewritten address bar - and lands back here once
 * that round trip completes. Nothing here starts or configures that redirect - it is the ordinary,
 * already-wired behaviour every other bypass route on this guard relies on.
 *
 * **Eligibility is read, never assumed.** `REDEEMABLE_ACCOUNT_STATES` mirrors the backend's
 * `MembershipEligibility.AllowsActiveMembership`: a caller in `NoMembership`, `MembershipRevoked`
 * or `ApplicationRejected` sees the Accept button, and an account state that could not be read is
 * treated the same way for the same fail-open reason `accountStateGuard` documents - the redemption
 * endpoint re-checks eligibility itself and is the one answer that counts. Every other state is not
 * eligible at all, so this page sends that caller straight to the page their own state already
 * calls for (`nonRedeemableAccountStateDestination`) rather than showing a button the API would only
 * refuse.
 *
 * **The API's refusal is shown exactly as it is written**, never reworded or guessed at by this
 * component: an unusable invite (expired, used, cancelled, invalid or granting an undeclared role),
 * an already-active membership, and a deactivated business needing release by the platform
 * administrator are each the one sentence `InviteRedemptionService`'s caller reads from
 * `error.error.message`.
 *
 * **Acceptance re-reads both caches before it lands anywhere.** `AccountStateService.refresh()` and
 * then `AccessService.refresh()` are awaited in that order, so the dashboard's own
 * `accountStateGuard` and capability guard see the caller's new `Member` state and granted
 * capabilities already published - not the stale pre-redemption answer a bare `refresh()` call
 * without awaiting it would still have cached - before the navigation to `/` they both guard ever
 * runs.
 */
@Component({
  selector: 'app-invite-acceptance',
  standalone: true,
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">You've been invited</h1>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          @if (phase === 'eligible') {
            @if (errorMessage) {
              <p class="mb-3 text-sm text-red-600">{{ errorMessage }}</p>
            } @else {
              <p class="text-md-body">Accept this invite to join the business.</p>
            }
            <button type="button" class="btn btn-primary mt-4" [disabled]="redeeming" (click)="accept()">
              {{ redeeming ? 'Joining...' : 'Accept' }}
            </button>
          }
        </div>
      </section>
    </div>
  `
})
export class InviteAcceptanceComponent implements OnInit, OnDestroy {
  phase: 'checking' | 'eligible' = 'checking';
  redeeming = false;
  errorMessage: string | null = null;

  private token = '';
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly route: ActivatedRoute,
    private readonly accountState: AccountStateService,
    private readonly access: AccessService,
    private readonly redemption: InviteRedemptionService,
    private readonly router: Router
  ) {}

  ngOnInit(): void {
    // The very first thing this page does: capture the token into memory, then remove it from the
    // address bar before any analytics, logging or navigation can see it there.
    this.token = this.route.snapshot.paramMap.get('token') ?? '';
    history.replaceState(history.state, '', `/${INVITE_ADDRESS_BAR_ROUTE}`);

    this.accountState.state$
      .pipe(
        filter((answer) => answer.status !== 'unread'),
        take(1),
        takeUntil(this.destroying$)
      )
      .subscribe((answer) => {
        if (answer.status === 'known' && !REDEEMABLE_ACCOUNT_STATES.has(answer.accountState.state)) {
          this.router.navigateByUrl(nonRedeemableAccountStateDestination(answer.accountState.state), {
            replaceUrl: true
          });
          return;
        }

        this.phase = 'eligible';
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  accept(): void {
    this.errorMessage = null;
    this.redeeming = true;

    this.redemption
      .redeem(this.token)
      .pipe(
        switchMap(() => this.accountState.refresh()),
        switchMap(() => this.access.refresh()),
        takeUntil(this.destroying$)
      )
      .subscribe({
        next: () => this.router.navigateByUrl('/'),
        error: (error: unknown) => {
          this.redeeming = false;
          this.errorMessage = errorMessageFrom(error);
        }
      });
  }
}
