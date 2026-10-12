import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ConfigService } from '../services/config.service';

/** The `POST /api/invites/redeem` response body (issue #532): the role the caller now holds. */
interface RedeemedMemberInviteResponse {
  role: string;
}

/** What a completed redemption granted the caller (issue #535). */
export interface RedeemedMemberInvite {
  readonly role: string;
}

/**
 * Redeems an invite token against `POST /api/invites/redeem` (issue #532), for the invite
 * acceptance page (issue #535).
 *
 * The token travels only in this one request's body, never in a URL: the endpoint takes no route
 * or query parameter, so nothing before this call - the address bar, a router event, an HTTP
 * logger - ever sees it in a form that would put it in a log line. The API is authoritative over
 * every refusal (expired, used, cancelled, invalid, already a member, a business that must first
 * release the caller); this service does not interpret the response body beyond the one field a
 * successful redemption carries, so none of those sentences are duplicated or reworded here.
 */
@Injectable({ providedIn: 'root' })
export class InviteRedemptionService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  redeem(token: string): Observable<RedeemedMemberInvite> {
    return this.http
      .post<RedeemedMemberInviteResponse>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/invites/redeem`, { token })
      .pipe(map((response) => ({ role: response.role })));
  }
}
