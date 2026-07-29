import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

interface AppConfig {
  apiBaseUrl: string;
}

@Injectable({ providedIn: 'root' })
export class ConfigService {
  private config: AppConfig | null = null;

  constructor(private http: HttpClient) {}

  load(): Promise<void> {
    return firstValueFrom(this.http.get<AppConfig>('/assets/config.json'))
      .then(cfg => {
        this.config = cfg;
      })
      .catch(() => {
        this.config = { apiBaseUrl: '/api' };
      });
  }

  get apiBaseUrl(): string {
    return this.config?.apiBaseUrl ?? '/api';
  }
}
