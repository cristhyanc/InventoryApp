import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * One invite link of the signed-in Owner's business, as `GET /api/members/invites` and the cancel
 * endpoint return them (issue #531). It carries no token, in plaintext or hashed.
 */
export interface MemberInvite {
  readonly inviteId: number;
  readonly role: string | null;
  readonly note: string | null;
  readonly status: string | null;
  readonly createdAtUtc: string;
  readonly expiresAtUtc: string;
  readonly usedAtUtc: string | null;
  readonly cancelledAtUtc: string | null;
  readonly redeemedByMembershipId: number | null;
}

/**
 * The body of `POST /api/members/invites`'s response: the invite, and its token - once. This is
 * the only place in the application the plaintext token exists outside the request that generated
 * it.
 */
export interface CreatedMemberInvite {
  readonly token: string;
  readonly invite: MemberInvite;
}

/**
 * The frontend client for `/api/members/invites` (issue #531), consumed by the Admin → Users page
 * (issue #534). It is a thin wrapper over the three endpoints: the token it returns from `create`
 * is handed straight back to the caller and kept in no field of this service.
 */
@Injectable({ providedIn: 'root' })
export class MemberInviteService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  list(): Observable<MemberInvite[]> {
    return this.http.get<MemberInvite[]>(`${this.baseUrl}`);
  }

  create(role: string, note: string | null): Observable<CreatedMemberInvite> {
    return this.http.post<CreatedMemberInvite>(`${this.baseUrl}`, { role, note });
  }

  cancel(inviteId: number): Observable<MemberInvite> {
    return this.http.post<MemberInvite>(`${this.baseUrl}/${inviteId}/cancel`, {});
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/members/invites`;
  }
}
