import { ApplicationConfig, APP_INITIALIZER } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors, withInterceptorsFromDi } from '@angular/common/http';
import { routes } from './app.routes';
import { ConfigService } from './services/config.service';
import { accessForbiddenInterceptor } from './access/access-forbidden.interceptor';
import { loadingInterceptor } from './interceptors/loading.interceptor';
import { browserAuthProviders } from './auth/browser-auth-providers';

export function initializeApp(configService: ConfigService) {
  return () => configService.load();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    // `accessForbiddenInterceptor` keeps the capability cache current when a member endpoint
    // refuses a request (issue #528); it reports nothing and routes nowhere.
    provideHttpClient(withInterceptors([loadingInterceptor, accessForbiddenInterceptor]), withInterceptorsFromDi()),
    { provide: APP_INITIALIZER, useFactory: initializeApp, deps: [ConfigService], multi: true },
    // How the browser authenticates. The real Microsoft Entra/MSAL wiring lives in
    // auth/browser-auth-providers.ts; the dedicated end-to-end build configuration replaces that
    // one module (issue #46), so this composition never has to know which of the two it got.
    ...browserAuthProviders
  ]
};
