import { Component, OnInit } from '@angular/core';
import { AuthorityId, isExternalIdConfigured } from '../auth-config';
import { SignInFailureKind, SignInService, classifySignInFailure } from './sign-in.service';

const FAILURE_MESSAGES: Record<SignInFailureKind, string> = {
  cancelled: 'Sign-in was cancelled, so you are not signed in. Choose how you would like to sign in to try again.',
  failed: 'We could not finish signing you in. Check your connection and try again.'
};

/**
 * The sign-in page at `/sign-in` (issue #711, extended by #712), where a signed-out visitor chooses
 * which trusted authority to sign in against **before any redirect starts** - the design's § 9.2
 * requirement, and the reason this page exists at all: with two authorities, silently probing one of
 * them would decide who the caller is on their behalf.
 *
 * **One action for the customer identity authority, naming both of its journeys.** It redirects to
 * the configured Microsoft Entra External ID tenant's own sign-up/sign-in user flow, where a human
 * has enabled Google as a built-in provider and the local email-with-password option (design § 13
 * items 5 and 7, § 14.2). One hosted flow offers both, and nothing in the verified record confirms
 * a way for a relying application to preselect one of them, so this application sends no provider
 * hint and invents no query parameter: a second button would send the person to the identical page
 * and promise a choice this application does not make. Issue #711 labelled the action for Google
 * alone because journey D had not shipped; now that it has, the label and the supporting line name
 * both, which is also what design § 9.2 asked for and § 16 left to this task.
 *
 * **No password, verification code or reset link is ever handled here** (design § 16's #712 row).
 * This page has no credential field, no registration form and no recovery link of its own:
 * creating an account, verifying the mailbox with the provider's one-time code, and resetting a
 * password all happen on the provider's hosted pages, and the help line says so rather than
 * implying this application could do any of them. An expired verification or reset code is the
 * same answer - start again from here, and the hosted page issues a new one.
 *
 * **A failure says one of two fixed sentences and nothing the provider sent.** A refused credential
 * journey is exactly where "no such account" leaks: the provider's own error text names the
 * mailbox, the directory and often the reason. {@link classifySignInFailure} reduces a failure to
 * "cancelled" or "failed", and only these two sentences are rendered, so the page never tells the
 * person opposite whether a mailbox has an account here.
 *
 * **Both actions are offered only when they exist.** The customer identity action is rendered only
 * while this build has a complete customer identity configuration; with the shipped empty
 * configuration this page offers the one Microsoft work account action and nothing else, says
 * nothing about passwords or recovery, and no visitor is ever routed here in the first place
 * (`selected-authority.ts`, `needsAuthorityChoice`).
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

          @if (isCustomerIdentityOffered) {
            <button
              type="button"
              class="btn btn-primary inline-flex"
              data-testid="sign-in-customer-identity"
              aria-describedby="sign-in-customer-identity-hint"
              [disabled]="startingAuthority !== null"
              [attr.aria-busy]="startingAuthority === 'externalId'"
              (click)="continueWithCustomerIdentity()"
            >
              {{
                startingAuthority === 'externalId'
                  ? 'Taking you to sign in…'
                  : 'Continue with Google or an email address'
              }}
            </button>
            <p id="sign-in-customer-identity-hint" class="mt-2 text-sm text-md-gray-600">
              You will finish signing in on Microsoft's secure sign-in page, where you choose your Google account or
              use an email address and password.
            </p>

            <p class="mt-2 text-sm text-md-gray-600" data-testid="sign-in-password-help">
              On that page you can also create an account with any email address, verify it with the code Microsoft
              emails you, or reset your password. Inventory Manager never sees or stores your password, and cannot
              reset it for you. If a verification or reset code has expired, start again here and the page will send
              you a new one.
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

          @if (isCustomerIdentityOffered) {
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
  readonly isCustomerIdentityOffered = isExternalIdConfigured();

  /** The authority whose redirect is being started, which disables both actions while it runs. */
  startingAuthority: AuthorityId | null = null;

  errorMessage: string | null = null;

  constructor(private readonly signIn: SignInService) {}

  ngOnInit(): void {
    const failure = this.signIn.take();
    this.errorMessage = failure === null ? null : FAILURE_MESSAGES[failure];
  }

  continueWithCustomerIdentity(): void {
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
