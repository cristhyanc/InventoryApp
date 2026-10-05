import { Provider } from '@angular/core';
import { HTTP_INTERCEPTORS } from '@angular/common/http';
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
import { buildProtectedResourceMap, createMsalConfig, loginRequest } from '../auth-config';

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
 */
export function MSALInstanceFactory(): IPublicClientApplication {
  return new PublicClientApplication(createMsalConfig());
}

export function MSALGuardConfigFactory(): MsalGuardConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    authRequest: loginRequest
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
export function MSALInterceptorConfigFactory(configService: ConfigService): MsalInterceptorConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    protectedResourceMap: buildProtectedResourceMap(configService.apiBaseUrl)
  };
}

export const browserAuthProviders: Provider[] = [
  { provide: MSAL_INSTANCE, useFactory: MSALInstanceFactory },
  { provide: MSAL_GUARD_CONFIG, useFactory: MSALGuardConfigFactory },
  { provide: MSAL_INTERCEPTOR_CONFIG, useFactory: MSALInterceptorConfigFactory, deps: [ConfigService] },
  { provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true },
  MsalService,
  MsalGuard,
  MsalBroadcastService
];
