import { ApplicationConfig, APP_INITIALIZER } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors, withInterceptorsFromDi } from '@angular/common/http';
import { routes } from './app.routes';
import { ConfigService } from './services/config.service';
import { accessForbiddenInterceptor } from './access/access-forbidden.interceptor';
import { accountStateForbiddenInterceptor } from './access/account-state-forbidden.interceptor';
import { memberForbiddenNoticeInterceptor } from './access/member-forbidden-notice.interceptor';
import { loadingInterceptor } from './interceptors/loading.interceptor';
import { browserAuthProviders } from './auth/browser-auth-providers';

export function initializeApp(configService: ConfigService) {
  return () => configService.load();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    // `accessForbiddenInterceptor` and `accountStateForbiddenInterceptor` keep the capability and
    // account-state caches current when a member endpoint refuses a request (issues #528, #529);
    // neither reports anything itself. `memberForbiddenNoticeInterceptor` is the one that does
    // (issue #529's global 403 handler), so it runs after both have had their turn.
    provideHttpClient(
      withInterceptors([
        loadingInterceptor,
        accessForbiddenInterceptor,
        accountStateForbiddenInterceptor,
        memberForbiddenNoticeInterceptor
      ]),
      withInterceptorsFromDi()
    ),
    { provide: APP_INITIALIZER, useFactory: initializeApp, deps: [ConfigService], multi: true },
    // How the browser authenticates. The real Microsoft Entra/MSAL wiring lives in
    // auth/browser-auth-providers.ts; the dedicated end-to-end build configuration replaces that
    // one module (issue #46), so this composition never has to know which of the two it got.
    ...browserAuthProviders
  ]
};
