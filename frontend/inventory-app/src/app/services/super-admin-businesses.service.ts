import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, Subject, map } from 'rxjs';
import { ConfigService } from './config.service';
import type { NayaxConnectionStatusName } from './nayax-connection.service';

/**
 * One business as `GET /api/super-admin/businesses` reports it (issue #544), consumed by the
 * Super Admin Businesses page (issue #547). These twelve fields are the whole contract: no sale,
 * stock, cost, purchase, product, machine, site or report value, and no member's display name,
 * email address or Entra identifier. `state` is the Australian state or territory the business
 * recorded, and `null` for one that has not recorded any (issue #933).
 */
export interface SuperAdminBusiness {
  readonly businessId: number;
  readonly name: string;
  readonly status: string;
  readonly createdAtUtc: string;
  readonly appliedAtUtc: string | null;
  readonly decidedAtUtc: string | null;
  readonly contactEmail: string | null;
  readonly abn: string | null;
  readonly state: string | null;
  readonly activeMemberCount: number;
  readonly nayaxConfigured: boolean;

  /**
   * How usable the business's stored Nayax credentials were at its own last permission test (issue
   * #983), as the published status name - `NotConfigured` for a business with nothing stored. It is
   * the *stored* result: loading this list makes no Nayax call. The type is the one
   * `NayaxConnectionStatusName` the business-facing connection service already publishes, so the
   * four names cannot drift apart between the two surfaces that read them.
   */
  readonly nayaxConnectionStatus: NayaxConnectionStatusName;
}

/** A recorded approval or rejection, as those two endpoints answer (issue #544). */
export interface SuperAdminBusinessDecision {
  readonly businessId: number;
  readonly name: string;
  readonly status: string;
  readonly decidedAtUtc: string;
  readonly rejectionReason: string | null;
}

/** A recorded deactivation, as `POST .../deactivate` answers (issue #545). */
export interface SuperAdminBusinessDeactivation {
  readonly businessId: number;
  readonly name: string;
  readonly status: string;
  readonly invitesCancelled: number;
}

/** A recorded reactivation, as `POST .../reactivate` answers (issue #545). */
export interface SuperAdminBusinessReactivation {
  readonly businessId: number;
  readonly name: string;
  readonly status: string;
}

/**
 * One membership of a deactivated business, as `GET .../members` and the release endpoint beside
 * it report it (issue #545). Four fields, and no email address or Entra identifier.
 */
export interface SuperAdminBusinessMember {
  readonly membershipId: number;
  readonly displayName: string | null;
  readonly role: string | null;
  readonly isActive: boolean;
}

/**
 * The frontend client for `/api/super-admin/businesses` (issues #544, #545), consumed by the
 * Super Admin Businesses page (issue #547). It is a thin wrapper over the eight endpoints: no
 * status, eligibility or last-Owner rule is decided here, only what the server already returned.
 *
 * `changed$` is a plain notification, not cached state: a consumer that completes a mutating call
 * calls `notifyChanged()` so every other interested party - today, only the sidebar's pending-count
 * badge - re-reads the count rather than being told a number this service did not itself confirm.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminBusinessesService {
  private readonly changedSubject = new Subject<void>();

  /** Emits after a caller has completed an approve, reject, deactivate, reactivate or release. */
  readonly changed$: Observable<void> = this.changedSubject.asObservable();

  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** Every business, pending applications first. No filter, sort or search parameter exists. */
  list(): Observable<SuperAdminBusiness[]> {
    return this.http.get<SuperAdminBusiness[]>(this.baseUrl);
  }

  /** How many applications are waiting for a decision. */
  pendingCount(): Observable<number> {
    return this.http
      .get<{ pendingCount: number }>(`${this.baseUrl}/pending-count`)
      .pipe(map((response) => response.pendingCount));
  }

  approve(businessId: number): Observable<SuperAdminBusinessDecision> {
    return this.http.post<SuperAdminBusinessDecision>(`${this.baseUrl}/${businessId}/approve`, {});
  }

  /** `reason` is optional: a rejection with no reason is still a rejection. */
  reject(businessId: number, reason: string | null): Observable<SuperAdminBusinessDecision> {
    const body = reason === null || reason.trim() === '' ? {} : { reason };
    return this.http.post<SuperAdminBusinessDecision>(`${this.baseUrl}/${businessId}/reject`, body);
  }

  deactivate(businessId: number): Observable<SuperAdminBusinessDeactivation> {
    return this.http.post<SuperAdminBusinessDeactivation>(`${this.baseUrl}/${businessId}/deactivate`, {});
  }

  reactivate(businessId: number): Observable<SuperAdminBusinessReactivation> {
    return this.http.post<SuperAdminBusinessReactivation>(`${this.baseUrl}/${businessId}/reactivate`, {});
  }

  /** The memberships of one deactivated business; the server answers 409 for any other status. */
  members(businessId: number): Observable<SuperAdminBusinessMember[]> {
    return this.http.get<SuperAdminBusinessMember[]>(`${this.baseUrl}/${businessId}/members`);
  }

  releaseMember(businessId: number, membershipId: number): Observable<SuperAdminBusinessMember> {
    return this.http.post<SuperAdminBusinessMember>(`${this.baseUrl}/${businessId}/members/${membershipId}/release`, {});
  }

  /** Call once a mutating call above has completed, so the pending-count badge re-reads it. */
  notifyChanged(): void {
    this.changedSubject.next();
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/businesses`;
  }
}
