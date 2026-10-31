import { Component, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthorityId, isExternalIdConfigured } from '../auth-config';
import { IconComponent } from '../components/shared/icon.component';
import { PUBLIC_HOME_ROUTE } from '../public/landing/landing-routes';
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
 * configuration this page offers the one Microsoft account action and nothing else, says
 * nothing about passwords or recovery, and no visitor is ever routed here in the first place
 * (`selected-authority.ts`, `needsAuthorityChoice`).
 *
 * **It is a public Restockroo page (issue #957).** The route carries the public layout, so no
 * authenticated chrome surrounds it, and the page draws the homepage's own header brand, `rr`
 * tokens and buttons around one centred card. The presentation changed; the authorities offered,
 * the redirect each action starts, the return destination and the failure handling did not. The
 * longer verification and password-reset help sits in a native disclosure, while the warning that
 * each sign-in method is a separate account stays visible.
 *
 * **"Microsoft account", not "work account" (follow-up to issue #957).** The workforce action uses
 * the `common` authority, and both of its app registrations accept personal Microsoft accounts
 * (outlook.com, hotmail.com, live.com) as well as work and school accounts, so the label names
 * neither kind.
 *
 * **It never redirects by itself.** A cancelled or failed hosted sign-in comes back here with an
 * explanation and the same two choices, and waits for the person to press one. Retrying
 * automatically is how a callback page becomes a redirect loop.
 */
@Component({
  selector: 'app-sign-in',
  standalone: true,
  imports: [IconComponent, RouterLink],
  template: `
    <div class="flex min-h-screen flex-col bg-rr-canvas font-rr">
      <header class="border-b border-rr-line bg-white">
        <div class="mx-auto flex h-16 max-w-7xl items-center gap-4 px-4 sm:px-8">
          <a
            [routerLink]="homeLink"
            class="flex min-h-11 items-center gap-2 rounded-lg text-lg font-bold text-rr-navy no-underline"
            data-testid="sign-in-brand"
          >
            <span class="flex h-8 w-8 items-center justify-center rounded-lg bg-rr-teal text-white" aria-hidden="true">
              <app-icon name="inventory_2" [size]="18" />
            </span>
            Restockroo
          </a>
        </div>
      </header>

      <main class="flex flex-1 flex-col items-center px-4 py-8 sm:px-6 sm:py-16">
        <div class="w-full max-w-[480px]">
          <a
            [routerLink]="homeLink"
            class="mb-4 inline-flex min-h-11 items-center gap-1 rounded-lg text-sm font-semibold text-rr-teal no-underline hover:text-rr-teal-hover"
            data-testid="sign-in-back-home"
          >
            <span class="inline-flex rotate-180" aria-hidden="true"><app-icon name="arrow_forward" [size]="18" /></span>
            Back to home
          </a>

          <section
            class="rounded-2xl border border-rr-line bg-white p-6 shadow-md-lg sm:p-8"
            aria-labelledby="sign-in-title"
            data-testid="sign-in-card"
          >
            <h1 id="sign-in-title" class="m-0 text-2xl font-bold tracking-tight text-rr-navy sm:text-3xl">
              Sign in to Restockroo
            </h1>
            <p class="m-0 mt-2 text-base text-rr-muted">Choose how you’d like to sign in.</p>

            @if (errorMessage) {
              <p
                class="m-0 mt-6 rounded-lg bg-rr-danger-bg px-4 py-3 text-sm text-rr-danger-text"
                role="alert"
                data-testid="sign-in-error"
              >
                {{ errorMessage }}
              </p>
            }

            <div class="mt-6 flex flex-col gap-3">
              @if (isCustomerIdentityOffered) {
                <button
                  type="button"
                  class="rr-button-primary w-full disabled:cursor-not-allowed disabled:opacity-60"
                  data-testid="sign-in-customer-identity"
                  aria-describedby="sign-in-customer-identity-hint"
                  [disabled]="startingAuthority !== null"
                  [attr.aria-busy]="startingAuthority === 'externalId'"
                  (click)="continueWithCustomerIdentity()"
                >
                  {{ startingAuthority === 'externalId' ? 'Taking you to sign in…' : 'Continue with Google or email' }}
                </button>
                <p id="sign-in-customer-identity-hint" class="m-0 text-sm text-rr-muted">
                  You’ll continue on Microsoft’s secure sign-in page, where you can use Google or an email address,
                  create an account or reset your password.
                </p>
              }

              <button
                type="button"
                [class]="
                  (isCustomerIdentityOffered ? 'rr-button-secondary' : 'rr-button-primary') +
                  ' w-full disabled:cursor-not-allowed disabled:opacity-60'
                "
                [class.mt-3]="isCustomerIdentityOffered"
                data-testid="sign-in-workforce"
                [disabled]="startingAuthority !== null"
                [attr.aria-busy]="startingAuthority === 'workforce'"
                (click)="continueWithMicrosoftWorkAccount()"
              >
                {{ startingAuthority === 'workforce' ? 'Taking you to sign in…' : 'Sign in with a Microsoft account' }}
              </button>
            </div>

            @if (isCustomerIdentityOffered) {
              <div
                class="mt-6 flex gap-3 rounded-lg bg-rr-warn-bg p-4 text-sm text-rr-navy"
                data-testid="sign-in-separate-accounts"
              >
                <span class="shrink-0 text-rr-warn-text" aria-hidden="true"><app-icon name="warning" [size]="20" /></span>
                <p class="m-0">
                  <strong class="font-semibold">Already use Restockroo?</strong> Sign in the same way you did before.
                  Each sign-in method is a separate account, and a new one doesn’t share your business access.
                </p>
              </div>

              <details class="mt-4 rounded-lg border border-rr-line" data-testid="sign-in-help">
                <summary class="min-h-11 cursor-pointer rounded-lg px-4 py-3 text-sm font-semibold text-rr-navy hover:bg-rr-canvas">
                  Help with verification codes and passwords
                </summary>
                <p class="m-0 border-t border-rr-line px-4 py-3 text-sm text-rr-muted" data-testid="sign-in-password-help">
                  On Microsoft’s page you can create an account with any email address, verify it with the code
                  Microsoft emails you, or reset your password. Restockroo never sees or stores your password and
                  cannot reset it for you. If a verification or reset code has expired, start again here and you’ll be
                  sent a new one.
                </p>
              </details>
            }
          </section>
        </div>
      </main>

      <footer class="border-t border-rr-line bg-white">
        <div class="mx-auto max-w-7xl px-4 py-6 sm:px-8">
          <p class="m-0 text-sm text-rr-muted">© {{ year }} Restockroo. All rights reserved.</p>
        </div>
      </footer>
    </div>
  `
})
export class SignInComponent implements OnInit {
  /** Whether this deployment has a customer identity tenant at all, decided once per page. */
  readonly isCustomerIdentityOffered = isExternalIdConfigured();

  /** The public homepage, which the brand and the Back to home link both return to (issue #957). */
  readonly homeLink = '/' + PUBLIC_HOME_ROUTE;

  readonly year = new Date().getFullYear();

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
