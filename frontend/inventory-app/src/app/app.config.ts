import { ApplicationConfig, APP_INITIALIZER } from '@angular/core';
import { provideRouter } from '@angular/router';
import {
  HTTP_INTERCEPTORS,
  provideHttpClient,
  withInterceptors,
  withInterceptorsFromDi
} from '@angular/common/http';
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
import { routes } from './app.routes';
import { ConfigService } from './services/config.service';
import { loadingInterceptor } from './interceptors/loading.interceptor';
import { buildProtectedResourceMap, createMsalConfig, loginRequest } from './auth-config';

export function initializeApp(configService: ConfigService) {
  return () => configService.load();
}

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
export function MSALInterceptorConfigFactory(configService: ConfigService): MsalInterceptorConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    protectedResourceMap: buildProtectedResourceMap(configService.apiBaseUrl)
  };
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(withInterceptors([loadingInterceptor]), withInterceptorsFromDi()),
    { provide: APP_INITIALIZER, useFactory: initializeApp, deps: [ConfigService], multi: true },
    { provide: MSAL_INSTANCE, useFactory: MSALInstanceFactory },
    { provide: MSAL_GUARD_CONFIG, useFactory: MSALGuardConfigFactory },
    { provide: MSAL_INTERCEPTOR_CONFIG, useFactory: MSALInterceptorConfigFactory, deps: [ConfigService] },
    { provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true },
    MsalService,
    MsalGuard,
    MsalBroadcastService
  ]
};
