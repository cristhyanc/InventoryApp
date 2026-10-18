import { Inject, Injectable, InjectionToken, Provider } from '@angular/core';
import { HTTP_INTERCEPTORS } from '@angular/common/http';
import {
  ActivatedRouteSnapshot,
  CanActivate,
  GuardResult,
  MaybeAsync,
  Router,
  RouterStateSnapshot
} from '@angular/router';
import { IPublicClientApplication, InteractionType, PublicClientApplication } from '@azure/msal-browser';
import {
  MsalBroadcastService,
  MsalGuard,
  MsalGuardConfiguration,
  MsalInterceptor,
  MsalInterceptorConfiguration,
  MsalService,
  MSAL_GUARD_CONFIG,
  MSAL_INSTANCE,
  MSAL_INTERCEPTOR_CONFIG
} from '@azure/msal-angular';
import { ConfigService } from '../services/config.service';
import { buildProtectedResourceMap, createMsalConfig, loginRequestFor } from '../auth-config';
import { SIGN_IN_ROUTE, rememberIntendedDestination } from './intended-destination';
import { needsAuthorityChoice, readSelectedAuthority } from './selected-authority';

/**
 * How the browser authenticates: the real Microsoft Entra sign-in, through MSAL.
 *
 * This is the only authentication wiring the application has, and it is what every build except
 * the dedicated end-to-end one compiles. The browser-level E2E suite (issue #46) replaces this
 * module with `browser-auth-providers.e2e.ts` through an Angular `fileReplacements` entry that
 * exists in exactly one build configuration, so the test wiring is not merely disabled in a
 * production bundle - it is not in it. `browser-auth-providers.spec.ts` pins that down.
 *
 * It is a module of providers rather than inline entries in `app.config.ts` precisely so the
 * replacement can be total: a build swaps the whole authentication story, instead of a flag
 * inside the composition deciding at runtime which one applies.
 *
 * **Which authority this page load signs in against is decided once, and all three configurations
 * below are built from that one decision** (issue #711). MSAL partitions its cache by client id
 * and authority, so the instance is built for the authority `selected-authority.ts` reports rather
 * than repointed later. The three factories here run at different moments - the interceptor's not
 * until the first HTTP request - and the browser-wide choice they derive from is shared by every
 * tab, so `readSelectedAuthority()` answers from this tab's own immutable snapshot of it: another
 * tab choosing differently in between cannot hand this instance the other authority's scopes, and
 * cannot change which authority a redirect already in flight comes back into. With no customer
 * identity tenant configured that answer is always the workforce authority and nothing here
 * changes behaviour at all.
 */
export function MSALInstanceFactory(): IPublicClientApplication {
  return new PublicClientApplication(createMsalConfig(readSelectedAuthority()));
}

export function MSALGuardConfigFactory(): MsalGuardConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    authRequest: loginRequestFor(readSelectedAuthority())
  };
}

// The map key must match the API URL this build actually calls (local dev proxy or
// deployed Azure API), which ConfigService already resolves; see auth-config.ts.
//
// This factory reads ConfigService.apiBaseUrl once, when MsalInterceptor is first
// constructed. That happens on the first request through HttpClient, which cannot occur
// before the APP_INITIALIZER in app.config.ts has completed, because ConfigService
// deliberately loads the runtime configuration on the raw HttpBackend rather than the
// intercepted HttpClient.
//
// The scope is the signed-in authority's own API audience (issue #711): the two authorities expose
// `access_as_user` from two different API app registrations, and each bearer scheme on the API
// validates only its own.
export function MSALInterceptorConfigFactory(configService: ConfigService): MsalInterceptorConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    protectedResourceMap: buildProtectedResourceMap(configService.apiBaseUrl, readSelectedAuthority())
  };
}

/**
 * The real `MsalGuard`, which {@link SignInAuthorityGuard} delegates to. It is registered under its
 * own token because the guard below takes `MsalGuard`'s place in the routes: `app.routes.ts` names
 * `MsalGuard`, and overriding that one token is how the authority choice reaches every protected
 * route without touching forty route definitions - the same way the end-to-end build already
 * replaces it (`browser-auth-providers.e2e.ts`).
 */
export const MSAL_REDIRECT_GUARD = new InjectionToken<CanActivate>('MSAL redirect guard');

/**
 * Asks which authority to sign in against before letting `MsalGuard` start a redirect (issue #711,
 * design § 9.2: the person chooses once, explicitly, **before any redirect**).
 *
 * It is a gate in front of the real guard, not a replacement for it: the moment an authority is
 * chosen or somebody is signed in, every decision is `MsalGuard`'s exactly as before. On a
 * deployment with no customer identity tenant configured it never intervenes at all, so the
 * shipped behaviour - one authority, redirect straight to it - is unchanged.
 *
 * The page the visitor asked for is recorded on the way past ({@link rememberIntendedDestination}),
 * from the router state this guard refused rather than from anything a request carried, and is
 * handed to MSAL as the redirect start page when the redirect finally starts.
 */
@Injectable()
export class SignInAuthorityGuard implements CanActivate {
  constructor(
    @Inject(MSAL_REDIRECT_GUARD) private readonly msalGuard: CanActivate,
    private readonly msal: MsalService,
    private readonly router: Router
  ) {}

  canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot): MaybeAsync<GuardResult> {
    if (needsAuthorityChoice(this.msal.instance.getAllAccounts().length > 0)) {
      rememberIntendedDestination(state.url);
      return this.router.parseUrl(`/${SIGN_IN_ROUTE}`);
    }

    return this.msalGuard.canActivate(route, state);
  }
}

export const browserAuthProviders: Provider[] = [
  { provide: MSAL_INSTANCE, useFactory: MSALInstanceFactory },
  { provide: MSAL_GUARD_CONFIG, useFactory: MSALGuardConfigFactory },
  { provide: MSAL_INTERCEPTOR_CONFIG, useFactory: MSALInterceptorConfigFactory, deps: [ConfigService] },
  { provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true },
  MsalService,
  { provide: MSAL_REDIRECT_GUARD, useClass: MsalGuard },
  { provide: MsalGuard, useClass: SignInAuthorityGuard },
  MsalBroadcastService
];
