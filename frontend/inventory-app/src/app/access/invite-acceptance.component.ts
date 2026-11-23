import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { Subject, filter, switchMap, take, takeUntil } from 'rxjs';
import { AccessService } from './access.service';
import { AccountStateService } from './account-state.service';
import { REDEEMABLE_ACCOUNT_STATES, nonRedeemableAccountStateDestination } from './account-state.guard';
import { INVITE_ADDRESS_BAR_ROUTE } from './invite-address-bar.guard';
import { InviteRedemptionService } from './invite-redemption.service';
import { LegalAgreementComponent } from './legal-agreement.component';
import { takePendingInviteToken } from './pending-invite-token';

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
 * The invite acceptance page at `/invite` (issue #535, one slice of #505 under #328): where an
 * invited person who opens their link, signs in if needed, joins the business with one click.
 *
 * **The token never reaches the address bar, history or MSAL.** An invite link's `/invite/<token>`
 * never activates: its `canMatch` guard (`inviteTokenAddressBarGuard`, issue #681) hands the token to
 * this tab's pending invite (`pending-invite-token.ts`), strips it from the address bar and redirects
 * to `/invite`, before `MsalGuard` runs. This page is matched at `/invite` only while that pending
 * invite exists, and the very first thing `ngOnInit` does is take it - reading and removing it in one
 * step - into this component's own field. From then on the token is held only in memory: never
 * written to a cookie, a log line or an error report, and a reload of `/invite` finds no pending
 * invite and shows the "open your invite link again" page instead.
 *
 * **Reaching this component at all already proves authentication**, because the route still
 * carries `MsalGuard` (`app.routes.ts`); a signed-out visitor is sent through the login redirect
 * first, with `/invite` as `MsalGuard`'s redirect start page, and lands back here once that round
 * trip completes, the pending invite having waited in the tab's `sessionStorage`. Nothing here starts
 * or configures that redirect - it is the ordinary, already-wired behaviour every other bypass route
 * on this guard relies on.
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
  imports: [LegalAgreementComponent],
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
              <p class="mb-3 text-sm text-red-600" data-testid="invite-error">{{ errorMessage }}</p>
            } @else {
              <p class="text-md-body">Accept this invite to join the business.</p>
            }

            <div class="mt-4">
              <app-legal-agreement
                [accepted]="acceptedLegalTerms"
                (acceptedChange)="acceptedLegalTerms = $event"
              />
            </div>

            <button
              type="button"
              class="btn btn-primary mt-4"
              [disabled]="redeeming || !acceptedLegalTerms"
              (click)="accept()"
              data-testid="accept-invite"
            >
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

  /** Whether the person has ticked the agreement (issue #1259). Unticked until they tick it. */
  acceptedLegalTerms = false;

  private token = '';

  /**
   * The version of the published documents this page is offering, taken from the account state it
   * already reads and held from then on - so what is sent is the version the page showed when the
   * box was ticked, not whatever is current by the time the request lands. `null` until the account
   * state answers, and if it never does; the API refuses that with the sentence asking for a
   * refresh, which this page shows exactly as it is written like every other refusal.
   */
  private legalVersion: string | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly accountState: AccountStateService,
    private readonly access: AccessService,
    private readonly redemption: InviteRedemptionService,
    private readonly router: Router
  ) {}

  ngOnInit(): void {
    // The very first thing this page does: take the pending invite into memory, removing it from
    // the tab's storage in the same step, so it can be read exactly once.
    const token = takePendingInviteToken();
    if (token === null) {
      // Only a pending invite matches this page, so this is a race with another reader: say what
      // the tokenless `/invite` page says rather than offering a button that cannot work.
      this.router.navigateByUrl(`/${INVITE_ADDRESS_BAR_ROUTE}`, { replaceUrl: true });
      return;
    }

    this.token = token;

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

        if (answer.status === 'known') {
          this.legalVersion = answer.accountState.legalVersion;
        }

        this.phase = 'eligible';
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  accept(): void {
    // The button is disabled until the box is ticked, so this is the belt to that brace: a click
    // reaching here another way must not join a business nobody agreed to the Terms for.
    if (!this.acceptedLegalTerms) {
      return;
    }

    this.errorMessage = null;
    this.redeeming = true;

    this.redemption
      .redeem(this.token, this.legalVersion)
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
