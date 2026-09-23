import { Injectable } from '@angular/core';
import { HttpBackend, HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { LOCAL_API_BASE_URL, isLocalDevelopmentHost, resolveApiBaseUrl } from '../api-base-url';

interface AppConfig {
  apiBaseUrl: string;
}

@Injectable({ providedIn: 'root' })
export class ConfigService {
  // A dedicated HttpClient on the raw HttpBackend: the runtime configuration must not go
  // through the interceptor chain. MSAL's interceptor configuration is built from this
  // service (see app.config.ts), so an intercepted request here would construct
  // MsalInterceptor before load() had finished and freeze its protected-resource map on the
  // fallback API base URL.
  private readonly http: HttpClient;
  private resolvedApiBaseUrl = LOCAL_API_BASE_URL;

  constructor(backend: HttpBackend) {
    this.http = new HttpClient(backend);
  }

  async load(): Promise<void> {
    const hostname = window.location.hostname;

    // Local development always uses the dev-server proxy, so the tracked config.json never
    // has to be edited to switch between local and deployed API URLs.
    if (isLocalDevelopmentHost(hostname)) {
      this.resolvedApiBaseUrl = LOCAL_API_BASE_URL;
      return;
    }

    let config: AppConfig | null = null;
    try {
      config = await firstValueFrom(this.http.get<AppConfig>('/assets/config.json'));
    } catch {
      config = null;
    }

    this.resolvedApiBaseUrl = resolveApiBaseUrl(hostname, config);
  }

  get apiBaseUrl(): string {
    return this.resolvedApiBaseUrl;
  }
}
