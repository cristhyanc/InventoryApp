import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/** The three declared business roles, exactly as `Inventory.Domain.Tenancy.BusinessRoles` names them. */
export const BUSINESS_ROLES = ['Owner', 'Manager', 'Operator'] as const;

/**
 * One member of the signed-in Owner's business, as `GET /api/members` and the three change
 * endpoints return them (issue #533).
 */
export interface BusinessMember {
  readonly membershipId: number;
  readonly displayName: string | null;
  readonly email: string | null;
  readonly role: string | null;
  readonly isActive: boolean;
  readonly joinedAtUtc: string;
}

/**
 * The frontend client for `/api/members` (issue #533), consumed by the Admin → Users page (issue
 * #534). It is a thin wrapper over the four endpoints: no role, state or eligibility rule is
 * decided here, only what the server already returned.
 */
@Injectable({ providedIn: 'root' })
export class MemberService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  list(): Observable<BusinessMember[]> {
    return this.http.get<BusinessMember[]>(`${this.baseUrl}`);
  }

  changeRole(membershipId: number, role: string): Observable<BusinessMember> {
    return this.http.put<BusinessMember>(`${this.baseUrl}/${membershipId}/role`, { role });
  }

  revoke(membershipId: number): Observable<BusinessMember> {
    return this.http.post<BusinessMember>(`${this.baseUrl}/${membershipId}/revoke`, {});
  }

  reinstate(membershipId: number): Observable<BusinessMember> {
    return this.http.post<BusinessMember>(`${this.baseUrl}/${membershipId}/reinstate`, {});
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/members`;
  }
}
