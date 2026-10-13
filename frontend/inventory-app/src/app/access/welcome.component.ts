import { AsyncPipe } from '@angular/common';
import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Observable, map } from 'rxjs';
import { AccountStateService } from './account-state.service';
import { extractInviteToken } from './invite-link-token';

/**
 * The welcome page at `/welcome`, where `accountStateGuard` sends a `NoMembership` caller (issue
 * #542, one slice of #508 under #328): the first thing a person with no membership sees, offering
 * **I have an invite link** and, when the deployment's onboarding flag is on, **Start a new
 * business**. It replaced `OnboardingPlaceholderComponent` here.
 *
 * **I have an invite link reuses the invite acceptance route (#535) rather than redeeming
 * anything itself.** Pasting the full link or the bare token and choosing Continue extracts the
 * token with {@link extractInviteToken} and navigates to `/invite/<token>`, where
 * `InviteAcceptanceComponent` is the one caller of the redemption endpoint and the one place that
 * decides whether it is actually usable. Opening the link directly - from an email or anywhere
 * else - reaches that same route without ever landing here, so nothing further is needed for that
 * case.
 *
 * **Start a new business has no destination yet.** The application form is #543's task; this
 * page only shows that the choice exists, gated on the same `onboardingEnabled` flag
 * `GET /api/me/account-state` reports, so what is shown here matches the deployment's own
 * eligibility rule rather than a frontend guess - the same rule `AccessUnavailableComponent`
 * already follows for a `MembershipRevoked` caller.
 */
@Component({
  selector: 'app-welcome',
  standalone: true,
  imports: [AsyncPipe, FormsModule],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Welcome</h1>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          <p class="text-md-body">Choose how you'd like to get started.</p>

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

          @if (onboardingEnabled$ | async) {
            <div class="mt-6">
              <h2 class="text-md-subtitle font-medium">Start a new business</h2>
              <p class="text-md-body">Apply to create a new business. This will be available soon.</p>
            </div>
          }
        </div>
      </section>
    </div>
  `
})
export class WelcomeComponent {
  readonly onboardingEnabled$: Observable<boolean>;

  inviteInput = '';
  inviteError: string | null = null;

  constructor(
    accountState: AccountStateService,
    private readonly router: Router
  ) {
    this.onboardingEnabled$ = accountState.state$.pipe(
      map((answer) => answer.status === 'known' && answer.accountState.onboardingEnabled)
    );
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
