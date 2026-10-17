import { HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, catchError, map, of, shareReplay } from 'rxjs';
import {
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionStatusName
} from '../services/nayax-connection.service';
import {
  NAYAX_NOT_CONNECTED_REASON,
  NayaxUnavailability,
  nayaxUnavailabilityOfError
} from './nayax-availability';

/**
 * The application-wide provider-availability signal (issue #728, child E1 of issue #329): the one
 * place the frontend asks whether this business's Nayax connection is usable, and the one place a
 * `409 nayax_not_connected` refusal is turned into an answer rather than into an error.
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
 * **An unknown answer hides nothing.** Both per-feature answers fail **open** while the status is
 * unread or unreadable, matching the status banner and the provider-optional Dashboard (issue
 * #727): withholding a connected business's machine filter because one probe failed would be a
 * worse error than showing a filter whose own request then reports its own failure.
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
 * The two kinds of thing a screen asks the provider about. They are deliberately coarse: a feature
 * is a *kind* of dependency on Nayax, not a page, so a new screen asks an existing question.
 */
export type ProviderFeature =
  /**
   * Anything whose figures come from a live Nayax read - the machine fleet, the pick list, Sync
   * Restock. It needs credentials the status gate will use.
   */
  | 'liveFleet'
  /**
   * Anything that only needs Nayax to be part of this business - the "Nayax Sync Restock" source
   * of an already-recorded stock movement, the "Nayax monthly fee" expense category. A connection
   * that needs attention still satisfies it.
   */
  | 'providerRecords';

/** The shared answer: the published status, and what it means overall. */
export interface ProviderAvailability {
  /** The status as the API published it, or `null` when it has not been read. */
  readonly status: NayaxConnectionStatusName | null;

  /** The overall answer derived from {@link status}. */
  readonly connection: ProviderConnection;
}

/** The answer before anything has been read, and after a read that failed. */
const UNKNOWN: ProviderAvailability = { status: null, connection: 'unknown' };

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

/** Whether one kind of provider-dependent feature can be offered under this overall answer. */
export function providerFeatureAvailable(connection: ProviderConnection, feature: ProviderFeature): boolean {
  if (connection === 'unknown') {
    return true;
  }
  return feature === 'liveFleet' ? connection === 'usable' : connection !== 'notConnected';
}

/** The availability answer for one status, as the shared signal publishes it. */
function availabilityOf(status: NayaxConnectionStatusName): ProviderAvailability {
  return { status, connection: providerConnectionOf(status) };
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
  /** The shared answer for this business context; `undefined` until something asks for it. */
  private availability$?: Observable<ProviderAvailability>;

  /** Bumped whenever the cached answer is replaced; an older read must not overwrite a newer one. */
  private generation = 0;

  constructor(private readonly connections: NayaxConnectionService) {}

  /**
   * The provider's availability, reading the published status once per business context. It emits
   * only a settled answer - the status, or `unknown` when the read failed - so a screen never
   * renders a decision that has not been made yet.
   *
   * A failed read is retryable: the shared attempt is dropped, so the next screen asks again.
   */
  load(): Observable<ProviderAvailability> {
    const inFlight = this.availability$;
    if (inFlight !== undefined) {
      return inFlight;
    }

    const generation = this.generation;
    const loading = this.connections.status().pipe(
      map((state) => availabilityOf(state.status)),
      catchError((error: unknown) => {
        if (generation === this.generation) {
          this.availability$ = undefined;
        }
        console.error('Could not read the Nayax connection status; provider availability is unknown.', error);
        return of(UNKNOWN);
      }),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    this.availability$ = loading;
    return loading;
  }

  /** Whether one kind of provider-dependent feature can be offered, from the one shared read. */
  feature(feature: ProviderFeature): Observable<boolean> {
    return this.load().pipe(map((availability) => providerFeatureAvailable(availability.connection, feature)));
  }

  /**
   * Records the state an Owner's save or re-test just established (issues #537, #538), so the
   * screens that share this signal reflect the new connection without re-reading it. The server
   * returned this state as the outcome of the write, so it is newer than anything cached here.
   */
  accept(state: NayaxConnectionState): void {
    this.generation++;
    this.availability$ = of(availabilityOf(state.status));
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
   * A not-connected refusal that published its `nayaxConnectionStatus` also **replaces** the shared
   * answer, because the server has just stated this business's connection state more recently than
   * any cached probe. One that published nothing records nothing: the caller is still told its
   * feature is unavailable, but the session does not guess which of the two not-connected states it
   * is in.
   */
  noteRefusal(error: unknown): NayaxUnavailability | null {
    const unavailability = nayaxUnavailabilityOfError(error);
    if (unavailability !== NAYAX_NOT_CONNECTED_REASON) {
      return unavailability;
    }

    const published = refusedConnectionStatus(error);
    if (published !== null) {
      this.generation++;
      this.availability$ = of(availabilityOf(published));
    }
    return unavailability;
  }

  /**
   * Forgets the answer for a change of the business this tab resolves - a support-session start,
   * end or expiry (issues #669 and #670). The cached answer is about one business's connection, and
   * a read still in flight for the previous one can no longer replace it.
   */
  reset(): void {
    this.generation++;
    this.availability$ = undefined;
  }
}
