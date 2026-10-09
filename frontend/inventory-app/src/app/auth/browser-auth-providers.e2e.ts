import { Injectable, Provider } from '@angular/core';
import { CanActivate } from '@angular/router';
import { MsalBroadcastService, MsalGuard, MsalService } from '@azure/msal-angular';
import { AccountInfo, InteractionStatus } from '@azure/msal-browser';
import { Observable, of } from 'rxjs';

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
 * Only the members the application shell actually uses (`app.component.ts`): redirect handling,
 * the active account, and the login/logout commands, which an E2E run never issues.
 */
export function createE2ETestMsalService(): MsalService {
  return {
    instance: {
      getActiveAccount: () => e2eTestAccount,
      getAllAccounts: () => [e2eTestAccount],
      setActiveAccount: () => undefined
    },
    handleRedirectObservable: () => of(null),
    loginRedirect: () => undefined,
    logoutRedirect: () => undefined
  } as unknown as MsalService;
}

/** MSAL is never interacting here, so the broadcast service reports a settled pipeline once. */
export function createE2ETestMsalBroadcastService(): MsalBroadcastService {
  return {
    inProgress$: of(InteractionStatus.None) as Observable<InteractionStatus>
  } as unknown as MsalBroadcastService;
}

export const browserAuthProviders: Provider[] = [
  E2ETestAuthGuard,
  { provide: MsalGuard, useExisting: E2ETestAuthGuard },
  { provide: MsalService, useFactory: createE2ETestMsalService },
  { provide: MsalBroadcastService, useFactory: createE2ETestMsalBroadcastService }
];
