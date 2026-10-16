import { Component, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { Subject, map, of, switchMap, takeUntil } from 'rxjs';
import { AccessService } from './access.service';
import { AccountStateAnswer, AccountStateService } from './account-state.service';
import { nonRedeemableAccountStateDestination } from './account-state.guard';

const GENERIC_ERROR_MESSAGE = "Couldn't check your application status. Try again.";

/**
 * The pending-approval page at `/application-pending`, where `accountStateGuard` sends an
 * `ApplicationPending` caller (issue #542, one slice of #508 under #328). It replaced
 * `OnboardingPlaceholderComponent` here.
 *
 * **Check again re-reads the account state, never assumes an outcome.** The button calls
 * `AccountStateService.refresh()`, which is the one caller for whether the Super Admin has
 * decided: a still-`ApplicationPending` answer stays on this page with a reminder that nothing has
 * changed yet, an `unavailable` answer (the fail-open case every account-state read shares) shows
 * a retry message rather than guessing, and any other known state navigates to the page
 * {@link nonRedeemableAccountStateDestination} already names for it.
 *
 * **Once approved, the access cache is refreshed before the shell ever sees it.** A `Member`
 * answer runs `AccessService.refresh()` and waits for it before navigating to `/`, exactly as
 * `InviteAcceptanceComponent` does after a redemption - so the dashboard's own capability guard
 * reads the caller's freshly granted Owner capabilities rather than the stale pre-approval answer
 * either cache would still be holding if the navigation ran first.
 */
@Component({
  selector: 'app-application-pending',
  standalone: true,
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Your application is pending</h1>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          <p class="text-md-body">
            Your application has been submitted. The Admin will review it, and you can check back here for a
            decision.
          </p>

          @if (errorMessage) {
            <p class="mt-2 text-sm text-red-600">{{ errorMessage }}</p>
          } @else if (stillPending) {
            <p class="mt-2 text-md-body">Still pending. Check again later.</p>
          }

          <button type="button" class="btn btn-primary mt-4" [disabled]="checking" (click)="checkAgain()">
            {{ checking ? 'Checking...' : 'Check again' }}
          </button>
        </div>
      </section>
    </div>
  `
})
export class ApplicationPendingComponent implements OnDestroy {
  checking = false;
  stillPending = false;
  errorMessage: string | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly accountState: AccountStateService,
    private readonly access: AccessService,
    private readonly router: Router
  ) {}

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  checkAgain(): void {
    this.checking = true;
    this.errorMessage = null;
    this.stillPending = false;

    this.accountState
      .refresh()
      .pipe(
        switchMap((answer: AccountStateAnswer) =>
          answer.status === 'known' && answer.accountState.state === 'Member'
            ? this.access.refresh().pipe(map(() => answer))
            : of(answer)
        ),
        takeUntil(this.destroying$)
      )
      .subscribe((answer: AccountStateAnswer) => {
        this.checking = false;

        if (answer.status !== 'known') {
          this.errorMessage = GENERIC_ERROR_MESSAGE;
          return;
        }

        if (answer.accountState.state === 'ApplicationPending') {
          this.stillPending = true;
          return;
        }

        this.router.navigateByUrl(nonRedeemableAccountStateDestination(answer.accountState.state), {
          replaceUrl: true
        });
      });
  }
}
