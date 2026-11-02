import { ApplicationConfig, APP_INITIALIZER, inject, provideEnvironmentInitializer } from '@angular/core';
import { provideRouter, withInMemoryScrolling } from '@angular/router';
import { provideHttpClient, withInterceptors, withInterceptorsFromDi } from '@angular/common/http';
import { routes } from './app.routes';
import { ConfigService } from './services/config.service';
import { FORBIDDEN_INTERCEPTORS } from './access/forbidden-interceptors';
import { loadingInterceptor } from './interceptors/loading.interceptor';
import { rolePreviewInterceptor } from './role-preview/role-preview.interceptor';
import { supportSessionInterceptor } from './support-session/support-session.interceptor';
import { supportSessionMessageInterceptor } from './support-session/support-session-message.interceptor';
import { browserAuthProviders } from './auth/browser-auth-providers';
import { ScrollOnNavigationService } from './layout/scroll-on-navigation.service';
import { MsalService } from '@azure/msal-angular';
import { firstValueFrom } from 'rxjs';

export function initializeApp(configService: ConfigService) {
  return () => configService.load();
}

export function initializeMsal(msal: MsalService) {
  return () => firstValueFrom(msal.initialize());
}

export const appConfig: ApplicationConfig = {
  providers: [
    // `withInMemoryScrolling` is configured with both options disabled so the router still emits
    // the deferred `Scroll` event this feature requires, but never performs its own built-in
    // restoration - which resets to the top on every forward navigation, including a same-path one
    // (issue #1048). `ScrollOnNavigationService`, started below, is what decides what that event
    // does instead.
    provideRouter(routes, withInMemoryScrolling({ scrollPositionRestoration: 'disabled', anchorScrolling: 'disabled' })),
    // Starts the router's one central scroll-on-navigation handler (issue #1048); see
    // `ScrollOnNavigationService` for the rule it applies.
    provideEnvironmentInitializer(() => inject(ScrollOnNavigationService)),
    // The three `403` interceptors and the order they run in are `FORBIDDEN_INTERCEPTORS`, which
    // documents each one's job and is the same list the composed-chain regression test installs
    // (issues #528, #529, #602). `supportSessionMessageInterceptor` runs right after them - still
    // outer to `supportSessionInterceptor`, so it never sees a response that one has already
    // discarded as stale - and reports the support session's own two `403` messages
    // (`read_only_support_session`, `unavailable_in_support_view`), which the last of the three
    // excludes for the same reason it excludes a bypassed route (issue #554).
    // `rolePreviewInterceptor` runs next (issue #721): it presents `X-Role-Preview` on the member
    // and cache-probe requests and reports or ends the preview on its own three codes, which the
    // two `403` interceptors that would otherwise react to them exclude. It is outer to
    // `supportSessionInterceptor` for the same reason the message interceptor is - it must never
    // react to a response that one has already discarded as stale.
    // `supportSessionInterceptor` runs last - innermost, closest to the backend - so it is the
    // first to see a raw response on the way back: it can discard a stale one before any of the
    // interceptors above ever reacts to it, and `loadingInterceptor`'s `finalize` still runs
    // regardless, because completion (which a discarded response still is) is enough to end it
    // (issue #553).
    provideHttpClient(
      withInterceptors([
        loadingInterceptor,
        ...FORBIDDEN_INTERCEPTORS,
        supportSessionMessageInterceptor,
        rolePreviewInterceptor,
        supportSessionInterceptor
      ]),
      withInterceptorsFromDi()
    ),
    {
  provide: APP_INITIALIZER,
  useFactory: initializeMsal,
  deps: [MsalService],
  multi: true
},
    { provide: APP_INITIALIZER, useFactory: initializeApp, deps: [ConfigService], multi: true },
    // How the browser authenticates. The real Microsoft Entra/MSAL wiring lives in
    // auth/browser-auth-providers.ts; the dedicated end-to-end build configuration replaces that
    // one module (issue #46), so this composition never has to know which of the two it got.
    ...browserAuthProviders
  ]
};
