import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/** The plans a Super Admin may grant (issue #982). `Free` is deliberately not one of them. */
export const GRANTABLE_PLANS = ['Starter', 'Pro'] as const;
export type GrantablePlan = (typeof GRANTABLE_PLANS)[number];

/**
 * One business's manual plan grant as `GET /api/super-admin/businesses/{businessId}/plan-grant`
 * reports it (issue #982).
 *
 * `hasGrant` is stated by the server rather than inferred from a null plan, so "this business has
 * never been granted anything" is never read as "it was granted Free", and `active` is the server's
 * own answer about the half-open period rather than a date comparison made here. `endsOnUtc` is the
 * last day the grant applies to and `endsAtUtc` the exclusive instant underneath it - both produced
 * by the one backend rule, so this client never converts between them.
 */
export interface SuperAdminPlanGrant {
  readonly businessId: number;
  readonly name: string;
  readonly hasGrant: boolean;
  readonly plan: string | null;
  readonly startsAtUtc: string | null;
  readonly endsAtUtc: string | null;
  readonly endsOnUtc: string | null;
  readonly reason: string | null;
  readonly revision: number;
  readonly grantedAtUtc: string | null;
  readonly active: boolean;
}

/**
 * The frontend client for one business's manual plan grant (issue #982), consumed by the Super Admin
 * Businesses page's Plan dialog.
 *
 * It is a thin wrapper over the three endpoints: no plan, revision, end-date or eligibility rule is
 * decided here. In particular the `revision` a write carries is the one the server last reported -
 * this client never increments, guesses or omits it, because that number is the whole of what stops
 * two Super Admin tabs from overwriting each other.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminPlanGrantsService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** The business's grant as it stands, with the revision the next write must present. */
  get(businessId: number): Observable<SuperAdminPlanGrant> {
    return this.http.get<SuperAdminPlanGrant>(this.url(businessId));
  }

  /**
   * Creates or changes the grant.
   *
   * @param endsOnUtc The last UTC day it applies to, as `yyyy-MM-dd`, or `null` for no end.
   * @param revision The revision last reported by the server; a stale one is refused with 409.
   */
  set(
    businessId: number,
    plan: GrantablePlan,
    endsOnUtc: string | null,
    reason: string,
    revision: number
  ): Observable<SuperAdminPlanGrant> {
    return this.http.put<SuperAdminPlanGrant>(this.url(businessId), {
      plan,
      endsOnUtc,
      reason,
      revision
    });
  }

  /** Ends the grant now. The server decides the instant; nothing is deleted. */
  end(businessId: number, reason: string, revision: number): Observable<SuperAdminPlanGrant> {
    return this.http.post<SuperAdminPlanGrant>(`${this.url(businessId)}/end`, { reason, revision });
  }

  private url(businessId: number): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/businesses/${businessId}/plan-grant`;
  }
}
