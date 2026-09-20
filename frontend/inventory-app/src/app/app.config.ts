import { ApplicationConfig, APP_INITIALIZER } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { routes } from './app.routes';
import { ConfigService } from './services/config.service';
import { loadingInterceptor } from './interceptors/loading.interceptor';

export function initializeApp(configService: ConfigService) {
  return () => configService.load();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(withInterceptors([loadingInterceptor]))
    , { provide: APP_INITIALIZER, useFactory: initializeApp, deps: [ConfigService], multi: true }
  ]
};
