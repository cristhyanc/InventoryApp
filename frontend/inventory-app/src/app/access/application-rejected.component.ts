import { AsyncPipe } from '@angular/common';
import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Observable, map } from 'rxjs';
import { AccountStateAnswer, AccountStateService } from './account-state.service';
import { extractInviteToken } from './invite-link-token';

/** What this page tells a rejected applicant, read from the cached account state. */
interface RejectedView {
  readonly rejectionReason: string | null;
  readonly onboardingEnabled: boolean;
}

function toView(answer: AccountStateAnswer): RejectedView | null {
  if (answer.status !== 'known') {
    return null;
  }

  return {
    rejectionReason: answer.accountState.rejectionReason,
    onboardingEnabled: answer.accountState.onboardingEnabled
  };
}

/**
 * The rejected-application page at `/application-rejected`, where `accountStateGuard` sends an
 * `ApplicationRejected` caller (issue #542, one slice of #508 under #328). It replaced
 * `OnboardingPlaceholderComponent` here.
 *
 * **Shows the Super Admin's own recorded reason, verbatim, when there is one.** `rejectionReason`
 * is the one field `GET /api/me/account-state` carries for this state (issue #523); a submission
 * with none shown falls back to one generic sentence this page owns instead of inventing a reason
 * the API never gave.
 *
 * **I have an invite link** behaves exactly as it does on the welcome page: pasting the full link
 * or the bare token navigates to `/invite/<token>`, where `InviteAcceptanceComponent` is the one
 * caller of the redemption endpoint - and `ApplicationRejected` is one of
 * `REDEEMABLE_ACCOUNT_STATES`, so that page offers the Accept button once it arrives there.
 *
 * **Apply again has no destination yet**, for the same reason the welcome page's start-a-new-
 * business choice does not: the application form is #543's task. This page shows the choice,
 * gated on the same `onboardingEnabled` flag, so what a rejected applicant sees matches the
 * deployment's own eligibility rule.
 */
@Component({
  selector: 'app-application-rejected',
  standalone: true,
  imports: [AsyncPipe, FormsModule],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Your application was not approved</h1>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          @if (view$ | async; as view) {
            <p class="text-md-body">{{ view.rejectionReason ?? 'Your application was not approved.' }}</p>

            <div class="mt-4">
              <h2 class="text-md-subtitle font-medium">I have an invite link</h2>
              <p class="text-md-body">Open the invite link you were sent, or paste it below.</p>
              <div class="mt-2 flex flex-wrap items-center gap-2">
                <input
                  type="text"
                  class="min-w-0 flex-1"
                  [(ngModel)]="inviteInput"
                  placeholder="Paste your invite link or code"
                  data-testid="invite-input"
                />
                <button type="button" class="btn btn-secondary" (click)="goToInvite()">Continue</button>
              </div>
              @if (inviteError) {
                <p class="mt-1 text-sm text-red-600">{{ inviteError }}</p>
              }
            </div>

            @if (view.onboardingEnabled) {
              <div class="mt-6">
                <h2 class="text-md-subtitle font-medium">Apply again</h2>
                <p class="text-md-body">You can submit a new application. This will be available soon.</p>
              </div>
            }
          }
        </div>
      </section>
    </div>
  `
})
export class ApplicationRejectedComponent {
  readonly view$: Observable<RejectedView | null>;

  inviteInput = '';
  inviteError: string | null = null;

  constructor(
    accountState: AccountStateService,
    private readonly router: Router
  ) {
    this.view$ = accountState.state$.pipe(map(toView));
  }

  goToInvite(): void {
    const token = extractInviteToken(this.inviteInput);
    if (token === null) {
      this.inviteError = 'Paste the invite link or the code from it.';
      return;
    }

    this.inviteError = null;
    this.router.navigateByUrl(`/invite/${token}`);
  }
}
