import { Injectable, Provider } from '@angular/core';
import { CanActivate } from '@angular/router';
import { MsalBroadcastService, MsalGuard, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, InteractionStatus } from '@azure/msal-browser';
import { EMPTY, Observable, of } from 'rxjs';

/**
 * The browser-side half of the dedicated end-to-end testing setup (issue #46).
 *
 * This file is compiled into exactly one build: `ng build/serve --configuration e2e`, which
 * replaces `browser-auth-providers.ts` with it. No production, development or default build
 * references it, nothing switches to it at runtime, and there is no flag, URL or storage value
 * that can reach it - which is why the production bundle cannot be made to run without the real
 * Microsoft Entra sign-in. `browser-auth-providers.spec.ts` asserts that the replacement is
 * declared in that one configuration only.
 *
 * It replaces interactive sign-in and nothing else:
 *
 * - The route guard allows navigation instead of redirecting to Entra, because the E2E suite is
 *   explicitly not testing the interactive sign-in flow (that is out of scope for issue #46).
 * - `MsalService`/`MsalBroadcastService` become inert stubs, so the application shell renders as
 *   signed in without MSAL contacting an identity provider. A browser E2E run therefore makes no
 *   network call to `login.microsoftonline.com`.
 * - No `MsalInterceptor` is registered, so no bearer token is attached. The E2E host authenticates
 *   the request from the synthetic actor header the test's browser context sends, and then applies
 *   the ordinary authorization, membership and tenant-isolation rules to it (see
 *   `backend/InventoryApi/Auth/E2ETesting`).
 */
@Injectable()
export class E2ETestAuthGuard implements CanActivate {
  /**
   * The E2E host is the only thing that decides whether a caller is authenticated, and it does
   * so per request. There is nothing for this guard to check in the browser, so it lets the
   * router through and lets the API answer 401/403 if the run is misconfigured.
   */
  canActivate(): boolean {
    return true;
  }
}

/** A fixed synthetic account, so the shell's user menu renders deterministically. */
export const e2eTestAccount = {
  homeAccountId: 'e2e-test-actor',
  environment: 'e2e-test',
  tenantId: 'e2e-test',
  localAccountId: 'e2e-test-actor',
  username: 'e2e-test-actor@example.invalid',
  name: 'E2E Test Actor'
} as unknown as AccountInfo;

/**
 * The one way a browser run can ask this stub for the *signed-out* application: a `sessionStorage`
 * flag the test sets before the bundle loads (issue #1191).
 *
 * The public landing page only exists for a visitor nobody has signed in as - `signedOutVisitorGuard`
 * matches it at `/` on exactly that condition, and the page chooses Sign in/Start free over Open app
 * the same way - so a browser check of that page needs this stub to report no account. It is a flag
 * rather than a second build configuration because the signed-in default has to stay the default for
 * the whole workflow suite.
 *
 * It grants nothing: this file is compiled into the `e2e` build alone, and reporting no account is
 * strictly *less* than reporting one. The E2E host still decides every request for itself from the
 * synthetic actor header, so nothing here can make an unauthenticated request succeed.
 */
export const E2E_SIGNED_OUT_KEY = 'e2e-signed-out';

/**
 * Read per call rather than once at module load: a test sets the flag with an init script before
 * bootstrap, and the guard and the landing page both ask afterwards.
 */
function e2eTestAccounts(): AccountInfo[] {
  const signedOut = globalThis.sessionStorage?.getItem(E2E_SIGNED_OUT_KEY) === 'true';
  return signedOut ? [] : [e2eTestAccount];
}

/**
 * Only the members the composition actually calls: `initialize`, which `app.config.ts` runs as an
 * APP_INITIALIZER on every build, and the ones the application shell uses (`app.component.ts`) -
 * redirect handling, the active account, and the login/logout commands, which an E2E run never
 * issues. The initializer is not optional: an APP_INITIALIZER that throws aborts bootstrap, so a
 * member missing here renders a blank page for the whole suite rather than failing one test.
 * `browser-auth-providers.spec.ts` asserts the real initializer against this stub.
 */
export function createE2ETestMsalService(): MsalService {
  return {
    instance: {
      getActiveAccount: () => e2eTestAccounts()[0] ?? null,
      getAllAccounts: () => e2eTestAccounts(),
      setActiveAccount: () => undefined
    },
    initialize: () => of(undefined),
    handleRedirectObservable: () => of(null),
    loginRedirect: () => undefined,
    logoutRedirect: () => undefined
  } as unknown as MsalService;
}

/**
 * MSAL is never interacting here, so the broadcast service reports a settled pipeline once and
 * never raises an account event: the synthetic account is fixed for the whole run, so there is no
 * sign-in, sign-out or active-account change for `AccessService` to react to (issue #528).
 */
export function createE2ETestMsalBroadcastService(): MsalBroadcastService {
  return {
    inProgress$: of(InteractionStatus.None) as Observable<InteractionStatus>,
    msalSubject$: EMPTY as Observable<EventMessage>
  } as unknown as MsalBroadcastService;
}

export const browserAuthProviders: Provider[] = [
  E2ETestAuthGuard,
  { provide: MsalGuard, useExisting: E2ETestAuthGuard },
  { provide: MsalService, useFactory: createE2ETestMsalService },
  { provide: MsalBroadcastService, useFactory: createE2ETestMsalBroadcastService }
];
