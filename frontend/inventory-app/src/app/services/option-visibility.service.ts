import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { OptionVisibility } from '../access/option-visibility';
import { ConfigService } from './config.service';

/**
 * The frontend client for `PUT /api/super-admin/option-visibility` (issue #873): the one call that
 * switches `Historical GST Classification` on or off for everybody but a Super Admin.
 *
 * It is a thin wrapper over that one endpoint, and deliberately has no read of its own: the current
 * state arrives with the capability set on `GET /api/me/access`, which the shell already reads, so a
 * second read here would be a second answer to one question. Nothing is decided in this service -
 * who may make this call is the API's answer, and a caller who is not the configured platform
 * administrator is refused there whatever the UI offered.
 */
@Injectable({ providedIn: 'root' })
export class OptionVisibilityService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /**
   * Stores whether `Historical GST Classification` is offered to anybody but a Super Admin, and
   * answers the setting as the server stored it.
   */
  setHistoricalGstClassificationVisible(visible: boolean): Observable<OptionVisibility> {
    return this.http.put<OptionVisibility>(this.url, {
      historicalGstClassificationVisible: visible
    });
  }

  private get url(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/option-visibility`;
  }
}
