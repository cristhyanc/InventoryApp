import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, Subject, tap } from 'rxjs';
import { ConfigService } from './config.service';
import { BusinessApplicationRequest } from '../access/business-application-rules';

/**
 * `GET`/`PUT /api/business/settings`'s response body (issue #548): the business's own name,
 * contact address, ABN, GST registration, time zone and the state or territory it trades in
 * (issue #933). `contactEmail`, `abn`, `gstRegistered` and `state` are nullable because "not
 * recorded" is a real state for the business that predates the application form - the migrated
 * business - which is exactly what the legacy prompt (issue #549) watches for.
 */
export interface BusinessSettings {
  readonly name: string;
  readonly contactEmail: string | null;
  readonly abn: string | null;
  readonly gstRegistered: boolean | null;
  readonly timeZoneId: string;
  readonly state: string | null;
}

/**
 * Whether `settings` is missing the contact email, the GST registration or the state the
 * application form would have collected (issue #549, and issue #933 for the state) - the migrated
 * business that predates it. An empty or whitespace-only contact address or state counts as
 * missing, the same as `null`.
 */
export function isLegacyBusinessProfile(settings: BusinessSettings): boolean {
  return (
    settings.contactEmail === null ||
    settings.contactEmail.trim().length === 0 ||
    settings.gstRegistered === null ||
    settings.state === null ||
    settings.state.trim().length === 0
  );
}

/**
 * Reads and saves the signed-in Owner's own business settings, behind `Business.Manage` on both
 * `GET` and `PUT /api/business/settings` (issue #548). The save body is
 * `BusinessApplicationRequest` because it is the exact same six settings `BusinessSettingsPolicy`
 * validates for an application - this service sends the whole set together, exactly as the API
 * requires, and interprets no refusal itself.
 *
 * **{@link saved$} announces every successful save** (issue #681) to anything else on screen that
 * shows a conclusion drawn from the settings - the legacy-profile prompt composed beside the form
 * reads the settings only once, so without it the prompt would keep asking for fields the Owner
 * has just recorded. It emits the `PUT`'s own response body as soon as the `PUT` succeeds, and
 * never for a refused save.
 */
@Injectable({ providedIn: 'root' })
export class BusinessSettingsService {
  private readonly savedSubject = new Subject<BusinessSettings>();

  /** The settings each successful {@link save} returned, in the order the saves answered. */
  readonly saved$: Observable<BusinessSettings> = this.savedSubject.asObservable();

  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  get(): Observable<BusinessSettings> {
    return this.http.get<BusinessSettings>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/business/settings`);
  }

  save(request: BusinessApplicationRequest): Observable<BusinessSettings> {
    return this.http
      .put<BusinessSettings>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/business/settings`, request)
      .pipe(tap((settings) => this.savedSubject.next(settings)));
  }
}
