import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';
import { BusinessApplicationRequest } from '../access/business-application-rules';

/**
 * `GET`/`PUT /api/business/settings`'s response body (issue #548): the business's own name,
 * contact address, ABN, GST registration and time zone. `contactEmail`, `abn` and `gstRegistered`
 * are nullable because "not recorded" is a real state for the business that predates the
 * application form - the migrated business - which is exactly what the legacy prompt (issue #549)
 * watches for.
 */
export interface BusinessSettings {
  readonly name: string;
  readonly contactEmail: string | null;
  readonly abn: string | null;
  readonly gstRegistered: boolean | null;
  readonly timeZoneId: string;
}

/**
 * Whether `settings` is missing the contact email or the GST registration the application form
 * would have collected (issue #549) - the migrated business that predates it. An empty or
 * whitespace-only contact address counts as missing, the same as `null`.
 */
export function isLegacyBusinessProfile(settings: BusinessSettings): boolean {
  return settings.contactEmail === null || settings.contactEmail.trim().length === 0 || settings.gstRegistered === null;
}

/**
 * Reads and saves the signed-in Owner's own business settings, behind `Business.Manage` on both
 * `GET` and `PUT /api/business/settings` (issue #548). The save body is
 * `BusinessApplicationRequest` because it is the exact same five settings `BusinessSettingsPolicy`
 * validates for an application - this service sends the whole set together, exactly as the API
 * requires, and interprets no refusal itself.
 */
@Injectable({ providedIn: 'root' })
export class BusinessSettingsService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  get(): Observable<BusinessSettings> {
    return this.http.get<BusinessSettings>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/business/settings`);
  }

  save(request: BusinessApplicationRequest): Observable<BusinessSettings> {
    return this.http.put<BusinessSettings>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/business/settings`, request);
  }
}
