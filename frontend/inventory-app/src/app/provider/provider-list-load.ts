import { Observable, catchError, map, of, switchMap, take } from 'rxjs';
import { NAYAX_NOT_CONNECTED_REASON, NayaxUnavailability } from './nayax-availability';
import {
  ProviderAvailabilityService,
  ProviderFeature,
  providerFeatureAvailable
} from './provider-availability.service';

/**
 * The one path every machine/site list load on a provider-dependent screen takes (issue #728).
 *
 * Before this, each page subscribed to `MachineService.getAll()` and assigned `[]` on failure - or,
 * on the Pick List, had no error handler at all - so a refused request, a transport failure and a
 * business with no machines were all presented as the same thing: an empty dropdown. That is the
 * one mistake decision P9 of issue #329 forbids everywhere else, and it is worse here than on a
 * figure, because an empty filter silently widens what the page is reporting on.
 *
 * It reports exactly one of three outcomes, and never confuses two of them:
 *
 * - `listed` - the request answered. The list may legitimately be empty.
 * - `unavailable` - the provider cannot list these at all, either because the shared signal already
 *   says so (in which case no request is sent) or because the request itself was refused with one
 *   of the two published `409` availability codes. A page hides the control: a filter over a list
 *   that cannot exist is not a filter, and an error about it would be an error about nothing.
 * - `failed` - anything else: a transport failure, a `502` from a real Nayax outage, a `403` about
 *   the caller's own access. A page shows an error **in place of** the control, because presenting
 *   one of those as "there are no machines" is a claim about the business that nobody made.
 *
 * See docs/architecture.md § Shared provider availability and error-versus-empty states.
 */
export type ProviderListOutcome<T> =
  | { readonly kind: 'listed'; readonly items: T[] }
  | { readonly kind: 'unavailable'; readonly unavailability: NayaxUnavailability }
  | { readonly kind: 'failed' };

/**
 * What a list not even asked for is unavailable for. The two states
 * {@link providerFeatureAvailable} refuses - `notConnected` and, for a live fleet read,
 * `needsAttention` - both carry the not-connected answer, because that is the answer the server
 * itself would have given: its one `nayax_not_connected` refusal covers stored credentials that are
 * known not to work as well as no credentials at all, and both ask an operator to connect or
 * reconnect Nayax. A permission refusal is per request and can only come back from one.
 */
const NOT_ASKED_UNAVAILABILITY: NayaxUnavailability = NAYAX_NOT_CONNECTED_REASON;

/**
 * Loads one provider-dependent list through the shared path above.
 *
 * The shared availability answer is read first - once per session, so this costs nothing after the
 * first screen - and a request the provider cannot answer is **not sent**: a business with no Nayax
 * connection should not be spending a round trip on a `409` it already knows about. A request that
 * is sent and refused is handed to `ProviderAvailabilityService.noteRefusal`, which is what records
 * the server's own answer for the rest of the session.
 *
 * It emits exactly once and completes, so a caller needs no more subscription management than the
 * bare `getAll().subscribe(...)` it replaces.
 */
export function loadProviderList<T>(
  provider: ProviderAvailabilityService,
  feature: ProviderFeature,
  request: () => Observable<T[]>
): Observable<ProviderListOutcome<T>> {
  return provider.load().pipe(
    take(1),
    switchMap((availability): Observable<ProviderListOutcome<T>> => {
      if (!providerFeatureAvailable(availability.connection, feature)) {
        return of({ kind: 'unavailable', unavailability: NOT_ASKED_UNAVAILABILITY });
      }

      return request().pipe(
        map((items): ProviderListOutcome<T> => ({ kind: 'listed', items: items ?? [] })),
        catchError((error: unknown): Observable<ProviderListOutcome<T>> => {
          const unavailability = provider.noteRefusal(error);
          return of(unavailability !== null ? { kind: 'unavailable', unavailability } : { kind: 'failed' });
        })
      );
    })
  );
}
