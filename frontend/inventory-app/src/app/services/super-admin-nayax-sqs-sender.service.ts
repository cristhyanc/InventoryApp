import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * One business's Nayax SQS sender identity as
 * `GET /api/super-admin/businesses/{businessId}/nayax-sqs-sender` reports it (issue #1101).
 *
 * `awsSenderId` is the AWS IAM user id SQS reports as a message's `SenderId` - an identifier, never
 * a credential - and is `null` for a business whose sender has never been recorded or has been
 * removed. `lastMessageReceivedAtUtc` is written by the queue reader of issue #1103 and is only
 * reported here. `revision` is the number the next write must present; this client never increments,
 * guesses or omits it.
 */
export interface SuperAdminNayaxSqsSender {
  readonly businessId: number;
  readonly awsSenderId: string | null;
  readonly isEnabled: boolean;
  readonly lastMessageReceivedAtUtc: string | null;
  readonly revision: number;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

/**
 * The frontend client for one business's Nayax SQS sender identity (issue #1101), consumed by the
 * Super Admin Businesses page's **Nayax SQS sender** card.
 *
 * It is a thin wrapper over the two endpoints: no validation, revision or uniqueness rule is decided
 * here. In particular the `expectedRevision` a write carries is the one the server last reported -
 * or `null` to create the first row - because that value is the whole of what stops two Super Admin
 * tabs from overwriting each other.
 *
 * There is deliberately no `delete`: removing a sender is the `PUT` below with a null id, which the
 * server records as an audited update rather than deleting anything.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminNayaxSqsSenderService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /**
   * The business's recorded sender, with the revision the next write must present. A `404` means
   * nothing has ever been recorded for it, which is how the card knows to create the first row with
   * `expectedRevision: null`.
   */
  get(businessId: number): Observable<SuperAdminNayaxSqsSender> {
    return this.http.get<SuperAdminNayaxSqsSender>(this.url(businessId));
  }

  /**
   * Records, changes, switches or removes the sender.
   *
   * @param awsSenderId The IAM user id, or `null` to remove it - which frees it for another business.
   * @param expectedRevision The revision the server last reported, or `null` to create the first row.
   */
  set(
    businessId: number,
    awsSenderId: string | null,
    isEnabled: boolean,
    expectedRevision: number | null
  ): Observable<SuperAdminNayaxSqsSender> {
    return this.http.put<SuperAdminNayaxSqsSender>(this.url(businessId), {
      awsSenderId,
      isEnabled,
      expectedRevision
    });
  }

  private url(businessId: number): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/businesses/${businessId}/nayax-sqs-sender`;
  }
}
