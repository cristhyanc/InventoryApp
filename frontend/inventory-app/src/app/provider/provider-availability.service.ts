import { HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, catchError, map, of, shareReplay } from 'rxjs';
import {
  NayaxConnectionCheckLine,
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionStatusName
} from '../services/nayax-connection.service';
import {
  NAYAX_NOT_CONNECTED_REASON,
  NayaxUnavailability,
  PERMISSION_NOT_GRANTED_REASON,
  nayaxUnavailabilityOfError
} from './nayax-availability';
import {
  PROVIDER_FEATURES,
  ProviderFeature,
  ProviderFeaturePermission,
  ProviderPermissionEvidence,
  UNKNOWN_PROVIDER_PERMISSIONS,
  providerFeatureNeedsLiveRead,
  providerPermissionsOfChecks,
  withProviderPermission
} from './provider-feature-permissions';

/**
 * The application-wide provider-availability signal (issue #728, child E1 of issue #329): the one
 * place the frontend asks whether this business's Nayax connection is usable, the one place it asks
 * what is known about one feature's Nayax permission, and the one place a `409
 * nayax_not_connected` / `409 nayax_permission_not_granted` refusal is turned into an answer rather
 * than into an error.
 *
 * **One read per session.** `GET /api/settings/nayax-connection/status` requires only
 * `Dashboard.View`, so every member may read it, and it is read **once**: the answer is shared, so
 * however many banners, filters and forms want it the endpoint is asked a single time. Before this
 * every banner read it for itself (issue #539), which is three or four identical probes on one
 * screen whose answers could in principle disagree.
 *
 * **Nothing here decides what a status means for the credentials.** `Ready` and
 * `PendingPermissions` are the two usable states because `Inventory.Domain.Nayax.
 * NayaxConnectionStatusGate` says so, and {@link providerConnectionOf} is a translation of that one
 * rule rather than a second opinion. A status name this build has never heard of is `unknown`, not
 * usable and not broken, for the same reason the gate is an allow-list.
 *
 * **`NeedsAttention` is a connection.** The overall answer keeps it apart from `NotConfigured`,
 * because the two mean opposite things to a business: stored credentials that stopped working are
 * still a Nayax account the business pays for and records expenses against, while
 * `NotConfigured` means Nayax is not part of this business at all. That distinction is what
 * {@link ProviderFeature} exists for - a live fleet read needs working credentials, a Nayax expense
 * category only needs Nayax to exist.
 *
 * **A usable status is never read as "every feature is granted".** A connection is usable in
 * `PendingPermissions` exactly because the business's Nayax account may have granted some reads and
 * refused others, so each feature carries its own {@link ProviderFeatureAvailability} and
 * `unknown` - the ordinary state of a fresh session - is one of its answers. The only evidence
 * about a permission is evidence this application produced: the check lines of an Owner's connection
 * test or re-test (passed through {@link ProviderAvailabilityService.accept}), and a refusal
 * retained against the one feature whose request earned it
 * ({@link ProviderAvailabilityService.noteRefusal}). A refused feature never changes the
 * connection's own availability, and never changes another feature's.
 *
 * **An unknown answer hides nothing.** {@link providerFeatureOffered} fails **open** while a
 * feature's answer is `unknown`, matching the status banner and the provider-optional Dashboard
 * (issue #727): withholding a connected business's machine filter because one probe failed, or
 * because nothing has yet stated a permission, would be a worse error than showing a filter whose
 * own request then reports its own failure.
 *
 * See docs/architecture.md § Shared provider availability and error-versus-empty states.
 */

/** The overall answer about the business's one provider. */
export type ProviderConnection =
  /** Credentials are stored and the status gate allows ordinary operations. */
  | 'usable'
  /** Credentials are stored and known not to work; an operator has to reconnect Nayax. */
  | 'needsAttention'
  /** This business has no Nayax connection at all. */
  | 'notConnected'
  /** The status has not been read, could not be read, or is a name this build does not know. */
  | 'unknown';

/**
 * What is known about one feature. It is the connection's answer and the feature's own permission
 * evidence taken together, and the four states are answers rather than labels on each other.
 */
export type ProviderFeatureAvailability =
  /** The connection allows it and this feature's read is known to be granted. */
  | 'available'
  /** The connection cannot serve it at all: no credentials, or credentials known not to work. */
  | 'notConnected'
  /** The connection is usable and Nayax has refused this feature's read for lack of permission. */
  | 'permissionNotGranted'
  /**
   * Nothing has stated it: the status has not been read or could not be read, or the connection is
   * usable and no test, re-test or refusal has said anything about this feature's permission.
   */
  | 'unknown';

/** What is known about every feature. */
export type ProviderFeatureAvailabilities = Readonly<Record<ProviderFeature, ProviderFeatureAvailability>>;

/** The shared answer: the published status, what it means overall, and what each feature can do. */
export interface ProviderAvailability {
  /** The status as the API published it, or `null` when it has not been read. */
  readonly status: NayaxConnectionStatusName | null;

  /** The overall answer derived from {@link status}. */
  readonly connection: ProviderConnection;

  /** Per-feature availability. Never inferred from {@link connection} alone. */
  readonly features: ProviderFeatureAvailabilities;

  /** The permission evidence {@link features} was derived from, for a caller that must explain it. */
  readonly permissions: ProviderPermissionEvidence;
}

export type { ProviderFeature, ProviderFeaturePermission, ProviderPermissionEvidence };

/**
 * What one published status means overall. `Ready` and `PendingPermissions` are the two states
 * `NayaxConnectionStatusGate.AllowsOrdinaryOperations` allows; everything else, including a name
 * this build does not recognise, is refused rather than assumed usable.
 */
export function providerConnectionOf(status: NayaxConnectionStatusName | null): ProviderConnection {
  switch (status) {
    case 'Ready':
    case 'PendingPermissions':
      return 'usable';
    case 'NeedsAttention':
      return 'needsAttention';
    case 'NotConfigured':
      return 'notConnected';
    default:
      return 'unknown';
  }
}

/**
 * Whether the connection on its own permits one kind of feature: a live Nayax read needs
 * credentials the status gate will use, while `providerRecords` only needs Nayax to be part of the
 * business. It answers nothing about a permission, and fails open on an `unknown` connection
 * because an unread status is not a refusal.
 */
export function providerConnectionAllows(connection: ProviderConnection, feature: ProviderFeature): boolean {
  if (connection === 'unknown') {
    return true;
  }
  return providerFeatureNeedsLiveRead(feature) ? connection === 'usable' : connection !== 'notConnected';
}

/**
 * What is known about one feature, from the connection's answer and that feature's own evidence.
 *
 * A connection that cannot serve the feature at all answers first: "connect Nayax" is what an
 * operator has to do before a permission could matter. A stated refusal then outranks an unread
 * status, because a request Nayax has already refused will be refused again whatever a status probe
 * eventually says. Everything else is `unknown`, including a usable connection nothing has stated a
 * permission for.
 */
export function providerFeatureAvailabilityOf(
  connection: ProviderConnection,
  permission: ProviderFeaturePermission,
  feature: ProviderFeature
): ProviderFeatureAvailability {
  if (!providerConnectionAllows(connection, feature)) {
    return 'notConnected';
  }
  if (permission === 'denied') {
    return 'permissionNotGranted';
  }
  if (connection === 'unknown') {
    return 'unknown';
  }
  return permission === 'granted' ? 'available' : 'unknown';
}

/**
 * Whether a screen may offer the feature. `unknown` is offered - the request then reports its own
 * outcome, which is a better error than a filter withheld because nothing has stated a permission -
 * and only a stated refusal withholds it.
 */
export function providerFeatureOffered(availability: ProviderFeatureAvailability): boolean {
  return availability === 'available' || availability === 'unknown';
}

/** Every feature's answer under one connection state and one body of permission evidence. */
export function providerFeatureAvailabilities(
  connection: ProviderConnection,
  permissions: ProviderPermissionEvidence
): ProviderFeatureAvailabilities {
  const features = {} as Record<ProviderFeature, ProviderFeatureAvailability>;
  for (const feature of PROVIDER_FEATURES) {
    features[feature] = providerFeatureAvailabilityOf(connection, permissions[feature], feature);
  }
  return features;
}

/**
 * The status a `nayax_not_connected` refusal published in its `nayaxConnectionStatus` extension
 * (issue #520), or `null` when it published none or published a name this build does not know.
 *
 * It is what makes the refusal an *answer*: the one `409` covers "nothing stored" and "stored and
 * known not to work" alike, so the code on its own cannot tell a business with no Nayax from one
 * whose token stopped working - and recording the wrong one of those would withhold the Nayax
 * expense category from a business that does pay a Nayax monthly fee.
 */
function refusedConnectionStatus(error: unknown): NayaxConnectionStatusName | null {
  if (!(error instanceof HttpErrorResponse)) {
    return null;
  }

  const body: unknown = error.error;
  const published = typeof body === 'object' && body !== null
    ? (body as { nayaxConnectionStatus?: unknown }).nayaxConnectionStatus
    : null;
  if (typeof published !== 'string') {
    return null;
  }

  const name = published as NayaxConnectionStatusName;
  return providerConnectionOf(name) === 'unknown' ? null : name;
}

@Injectable({ providedIn: 'root' })
export class ProviderAvailabilityService {
  /** The settled status for this business context; `undefined` until something asks for it. */
  private status$?: Observable<NayaxConnectionStatusName | null>;

  /** What this session has been told about each feature's permission. Never guessed. */
  private permissions: ProviderPermissionEvidence = UNKNOWN_PROVIDER_PERMISSIONS;

  /** Bumped whenever the cached answer is replaced; an older read must not overwrite a newer one. */
  private generation = 0;

  constructor(private readonly connections: NayaxConnectionService) {}

  /**
   * The provider's availability, reading the published status once per business context. It emits
   * only a settled answer - the status, or `unknown` when the read failed - so a screen never
   * renders a decision that has not been made yet.
   *
   * The per-feature part is derived when a subscriber asks, not when the status was read, so a
   * screen that subscribes after a refusal was retained sees that refusal.
   *
   * A failed read is retryable: the shared attempt is dropped, so the next screen asks again.
   */
  load(): Observable<ProviderAvailability> {
    return this.publishedStatus().pipe(map((status) => this.availabilityOf(status)));
  }

  /** What is known about one feature, from the one shared read and this session's evidence. */
  featureAvailability(feature: ProviderFeature): Observable<ProviderFeatureAvailability> {
    return this.load().pipe(map((availability) => availability.features[feature]));
  }

  /** Whether a screen may offer one feature - {@link providerFeatureOffered} over that answer. */
  featureOffered(feature: ProviderFeature): Observable<boolean> {
    return this.featureAvailability(feature).pipe(map(providerFeatureOffered));
  }

  /**
   * Records the state an Owner's save or re-test just established (issues #537, #538), so the
   * screens that share this signal reflect the new connection without re-reading it. The server
   * returned this state as the outcome of the write, so it is newer than anything cached here.
   *
   * `checks` is the completed run's own check lines, when the caller has them: a re-test's, or the
   * test whose draft a save has just promoted - the stored pair is exactly the pair that was tested.
   * They **replace** this session's permission evidence, because they are the only per-read
   * statement about the credentials now stored. Called without them - a plain status read, which
   * says nothing about any permission - the evidence is **cleared** rather than carried over onto a
   * connection that may have changed.
   */
  accept(state: NayaxConnectionState, checks?: readonly NayaxConnectionCheckLine[]): void {
    this.generation++;
    this.status$ = of(state.status);
    this.permissions = checks === undefined
      ? UNKNOWN_PROVIDER_PERMISSIONS
      : providerPermissionsOfChecks(checks);
  }

  /**
   * The shared handling path for a request that a provider availability answer refused
   * (`409 nayax_not_connected` / `409 nayax_permission_not_granted`).
   *
   * It returns the availability answer the refusal stated, so the caller can present a "needs a
   * provider" state - a hidden filter or field - instead of an error, and `null` for every other
   * failure, which stays an error: reporting a transport failure, a real Nayax outage or a `403`
   * about the caller's own access as "Nayax is not connected" would describe a connection nobody
   * tested.
   *
   * `feature` is the feature the refused request was for, and it is required because the refusal
   * itself does not name one: the `409` carries the stable code and no operation name. A permission
   * refusal is therefore retained against **that** feature and nothing else - the connection's own
   * availability is untouched, because the credentials are working, and every other feature keeps
   * whatever was known about it.
   *
   * A not-connected refusal that published its `nayaxConnectionStatus` instead **replaces** the
   * shared status answer, because the server has just stated this business's connection state more
   * recently than any cached probe. One that published nothing records nothing: the caller is still
   * told its feature is unavailable, but the session does not guess which of the two not-connected
   * states it is in.
   */
  noteRefusal(error: unknown, feature: ProviderFeature): NayaxUnavailability | null {
    const unavailability = nayaxUnavailabilityOfError(error);
    if (unavailability === PERMISSION_NOT_GRANTED_REASON) {
      this.permissions = withProviderPermission(this.permissions, feature, 'denied');
      return unavailability;
    }
    if (unavailability !== NAYAX_NOT_CONNECTED_REASON) {
      return unavailability;
    }

    const published = refusedConnectionStatus(error);
    if (published !== null) {
      this.generation++;
      this.status$ = of(published);
    }
    return unavailability;
  }

  /**
   * Forgets the answer for a change of the business this tab resolves - a support-session start,
   * end or expiry (issues #669 and #670). The cached status, and every permission this session was
   * told about, are about one business's connection, and a read still in flight for the previous
   * one can no longer replace them.
   */
  reset(): void {
    this.generation++;
    this.status$ = undefined;
    this.permissions = UNKNOWN_PROVIDER_PERMISSIONS;
  }

  /** The published status, read once per business context and shared. */
  private publishedStatus(): Observable<NayaxConnectionStatusName | null> {
    const inFlight = this.status$;
    if (inFlight !== undefined) {
      return inFlight;
    }

    const generation = this.generation;
    const loading = this.connections.status().pipe(
      map((state) => state.status),
      catchError((error: unknown) => {
        if (generation === this.generation) {
          this.status$ = undefined;
        }
        console.error('Could not read the Nayax connection status; provider availability is unknown.', error);
        return of(null);
      }),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    this.status$ = loading;
    return loading;
  }

  /** The whole answer for one published status and this session's current evidence. */
  private availabilityOf(status: NayaxConnectionStatusName | null): ProviderAvailability {
    const connection = providerConnectionOf(status);
    return {
      status,
      connection,
      features: providerFeatureAvailabilities(connection, this.permissions),
      permissions: this.permissions
    };
  }
}
