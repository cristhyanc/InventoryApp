import { Component, OnInit } from '@angular/core';
import { AuthorityId, isExternalIdConfigured } from '../auth-config';
import { SignInFailureKind, SignInService, classifySignInFailure } from './sign-in.service';

const FAILURE_MESSAGES: Record<SignInFailureKind, string> = {
  cancelled: 'Sign-in was cancelled, so you are not signed in. Choose how you would like to sign in to try again.',
  failed: 'We could not finish signing you in. Check your connection and try again.'
};

/**
 * The sign-in page at `/sign-in` (issue #711), where a signed-out visitor chooses which trusted
 * authority to sign in against **before any redirect starts** - the design's § 9.2 requirement, and
 * the reason this page exists at all: with two authorities, silently probing one of them would
 * decide who the caller is on their behalf.
 *
 * **Continue with Google uses the approved hosted flow and nothing else.** It redirects to the
 * configured Microsoft Entra External ID tenant's own sign-up/sign-in user flow, where a human has
 * enabled Google as a built-in provider (design § 13 item 5, § 14.2). This application sends no
 * provider hint, invents no query parameter, and never exchanges a Google token itself - the
 * identity the API receives is the one the External ID tenant issued, never the upstream Google
 * subject (design § 4, journey C, and § 7). The supporting line below the action says so, because
 * a person who presses "Continue with Google" and lands on a Microsoft page should know why.
 *
 * **Both actions are offered only when they exist.** The Google action is rendered only while this
 * build has a complete customer identity configuration; with the shipped empty configuration this
 * page offers the one Microsoft work account action and nothing else, and no visitor is ever routed
 * here in the first place (`selected-authority.ts`, `needsAuthorityChoice`).
 *
 * **It never redirects by itself.** A cancelled or failed hosted sign-in comes back here with an
 * explanation and the same two choices, and waits for the person to press one. Retrying
 * automatically is how a callback page becomes a redirect loop.
 */
@Component({
  selector: 'app-sign-in',
  standalone: true,
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Sign in</h1>
          <p class="page-subtitle">Choose how you would like to sign in to Inventory Manager.</p>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          @if (errorMessage) {
            <p class="mb-4 text-sm text-red-600" role="alert" data-testid="sign-in-error">{{ errorMessage }}</p>
          }

          @if (isGoogleOffered) {
            <button
              type="button"
              class="btn btn-primary inline-flex"
              data-testid="sign-in-google"
              aria-describedby="sign-in-google-hint"
              [disabled]="startingAuthority !== null"
              [attr.aria-busy]="startingAuthority === 'externalId'"
              (click)="continueWithGoogle()"
            >
              {{ startingAuthority === 'externalId' ? 'Taking you to sign in…' : 'Continue with Google' }}
            </button>
            <p id="sign-in-google-hint" class="mt-2 text-sm text-md-gray-600">
              You will finish signing in on Microsoft's secure sign-in page, where you choose your Google account.
            </p>

            <hr class="my-6" />
          }

          <button
            type="button"
            class="btn btn-secondary inline-flex"
            data-testid="sign-in-workforce"
            [disabled]="startingAuthority !== null"
            [attr.aria-busy]="startingAuthority === 'workforce'"
            (click)="continueWithMicrosoftWorkAccount()"
          >
            {{ startingAuthority === 'workforce' ? 'Taking you to sign in…' : 'Sign in with a Microsoft work account' }}
          </button>

          @if (isGoogleOffered) {
            <p class="mt-6 text-sm text-md-gray-600" data-testid="sign-in-separate-accounts">
              Each way of signing in is a separate account here, with its own access. If you already use Inventory
              Manager, sign in the same way you did before: signing in a new way never joins you to a business you
              already belong to.
            </p>
          }
        </div>
      </section>
    </div>
  `
})
export class SignInComponent implements OnInit {
  /** Whether this deployment has a customer identity tenant at all, decided once per page. */
  readonly isGoogleOffered = isExternalIdConfigured();

  /** The authority whose redirect is being started, which disables both actions while it runs. */
  startingAuthority: AuthorityId | null = null;

  errorMessage: string | null = null;

  constructor(private readonly signIn: SignInService) {}

  ngOnInit(): void {
    const failure = this.signIn.take();
    this.errorMessage = failure === null ? null : FAILURE_MESSAGES[failure];
  }

  continueWithGoogle(): void {
    this.begin('externalId');
  }

  continueWithMicrosoftWorkAccount(): void {
    this.begin('workforce');
  }

  private begin(authority: AuthorityId): void {
    if (this.startingAuthority !== null) {
      return;
    }

    this.startingAuthority = authority;
    this.errorMessage = null;

    this.signIn.signIn(authority).catch((error: unknown) => {
      // The redirect never left this page, so there is no callback coming to report it. Say so
      // here and give the actions back, rather than leaving a disabled page behind.
      this.errorMessage = FAILURE_MESSAGES[classifySignInFailure(error)];
      this.startingAuthority = null;
    });
  }
}
