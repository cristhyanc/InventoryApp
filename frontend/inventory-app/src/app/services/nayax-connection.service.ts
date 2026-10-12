import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * The frontend client for the business's own Nayax connection (`/api/settings/nayax-connection`,
 * issues #536 and #537), consumed by the Owner-only connection wizard of issue #538.
 *
 * **The server owns every decision.** Nothing here runs a check, interprets a Nayax answer,
 * derives a connection status, decides whether a draft may be saved, or judges whether the machine
 * checks are pending: these types mirror `InventoryApi.DTOs`' published shapes field for field so
 * the wizard can display the server's own findings unchanged
 * (see docs/architecture.md § Nayax connection checks and the Test endpoint).
 *
 * **The token is an argument and nothing else.** `test` is the one call that carries a token; it
 * takes it as a parameter, puts it in that one request body, and this service keeps no field at
 * all, so no tested token survives the call here. `save` names the server's own opaque draft id
 * and can carry no credential: the stored pair is exactly the one the server tested.
 */

/** One declared check, as `Inventory.Domain.Nayax.NayaxConnectionCheck` publishes its name. */
export type NayaxConnectionCheckName =
  | 'SignIn'
  | 'Machines'
  | 'Machine'
  | 'ProductCatalogue'
  | 'ProductGroups'
  | 'MachineProducts'
  | 'MachineLastSales'
  | 'MachineLastAlerts';

/** How one check turned out. `NotChecked` is neither a pass nor a failure. */
export type NayaxConnectionCheckStatusName = 'Passed' | 'Failed' | 'NotChecked';

/** Why one check failed, in the operator's language, as the API publishes the name. */
export type NayaxConnectionCheckFailureName =
  | 'InvalidOrExpiredToken'
  | 'PermissionNotGranted'
  | 'OperatorIdMismatch'
  | 'NayaxUnavailable'
  | 'UnexpectedResponse';

/** The connection's published status name. */
export type NayaxConnectionStatusName =
  | 'NotConfigured'
  | 'PendingPermissions'
  | 'Ready'
  | 'NeedsAttention';

/** One line of a completed run, exactly as `NayaxConnectionCheckResponse` carries it. */
export interface NayaxConnectionCheckLine {
  check: NayaxConnectionCheckName;
  status: NayaxConnectionCheckStatusName;
  failure: NayaxConnectionCheckFailureName | null;
}

/**
 * The business's connection state, as `GET …/status` returns it and as save and re-test report it.
 * Two fields and nothing else: no operator id, no credential revision, no last-tested instant.
 */
export interface NayaxConnectionState {
  status: NayaxConnectionStatusName;
  machineChecksPending: boolean;
}

/**
 * What one test found. It carries no token and no operator id - only the opaque `draftId` the save
 * step names, when that draft stops existing, and every check line.
 */
export interface NayaxConnectionTestResult {
  draftId: string;
  expiresAtUtc: string;
  machineCount: number | null;
  nothingFailed: boolean;
  checks: NayaxConnectionCheckLine[];
}

/**
 * What one re-test of the stored credentials found. `statusUpdated` is `false` when Nayax never
 * answered the sign-in, which leaves the stored status exactly as it was.
 */
export interface NayaxConnectionRetestResult {
  connection: NayaxConnectionState;
  statusUpdated: boolean;
  machineCount: number | null;
  nothingFailed: boolean;
  checks: NayaxConnectionCheckLine[];
}

@Injectable({ providedIn: 'root' })
export class NayaxConnectionService {
  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /**
   * Runs the server's read-only checks with a pasted operator id and token, and returns the draft
   * the tested pair is held under. Nothing is accepted by testing: the business's stored
   * connection, its status and its last-tested instant are untouched.
   *
   * A `POST`, because it submits a secret in a body rather than a URL and a cached response would
   * hand a caller a verdict about credentials nothing tested.
   */
  test(operatorId: string, token: string): Observable<NayaxConnectionTestResult> {
    return this.http.post<NayaxConnectionTestResult>(`${this.baseUrl}/test`, { operatorId, token });
  }

  /**
   * Promotes one completed test into the business's connection. The draft id is the whole request:
   * there is deliberately no way to express an operator id, a token, a status or a check result
   * through this client.
   */
  save(draftId: string): Observable<NayaxConnectionState> {
    return this.http.post<NayaxConnectionState>(`${this.baseUrl}/save`, { draftId });
  }

  /** Runs the same checks again with the token the business already has stored. */
  retest(): Observable<NayaxConnectionRetestResult> {
    return this.http.post<NayaxConnectionRetestResult>(`${this.baseUrl}/retest`, {});
  }

  /** Reads the connection's status and whether its machine checks are still pending. */
  status(): Observable<NayaxConnectionState> {
    return this.http.get<NayaxConnectionState>(`${this.baseUrl}/status`);
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/settings/nayax-connection`;
  }
}
