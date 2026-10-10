import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, of, shareReplay, tap } from 'rxjs';
import { ConfigService } from './config.service';
import { BusinessTimeZoneService } from '../formatting/business-time-zone.service';

/** The signed-in operator's own business, as `GET /api/business/current` returns it (issue #499). */
export interface CurrentBusiness {
  name: string;
  timeZoneId: string;
}

/**
 * Reads the signed-in operator's business and publishes its IANA time zone to
 * `BusinessTimeZoneService`, which is where every date/time display and calendar input gets it
 * from (issue #499).
 *
 * The request is authenticated like any other business call, so it cannot be made at application
 * bootstrap - there is no token yet. The shell triggers it once sign-in has settled and waits for
 * it before rendering a routed page (`AppComponent`), so a page that is on screen is one whose
 * business context is known. The answer is shared, so however many consumers want the zone the
 * endpoint is asked once.
 *
 * A failed lookup publishes nothing and emits `null`: the zone stays unknown, so dates render as
 * unavailable rather than in a zone nobody confirmed, and the shell still renders the application
 * instead of waiting forever. Calling `load()` again retries.
 *
 * The cached answer belongs to one signed-in identity. When the account changes, the shell calls
 * `reset()`, which drops the cached answer and the published zone and makes any lookup still in
 * flight for the previous account inert, so its late response can never publish the previous
 * business's zone for the new account.
 */
@Injectable({ providedIn: 'root' })
export class BusinessService {
  private current$?: Observable<CurrentBusiness | null>;

  /** Bumped by `reset()`; a lookup started under an older generation must not publish. */
  private generation = 0;

  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService,
    private readonly timeZone: BusinessTimeZoneService
  ) {}

  /** One request for the current business; `load()` is what shares and publishes it. */
  current(): Observable<CurrentBusiness> {
    return this.http.get<CurrentBusiness>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/business/current`);
  }

  /**
   * Loads the business and publishes its time zone, emitting once the lookup has settled - the
   * business, or `null` if it could not be read. Safe to call repeatedly: a successful load is
   * shared, and only a failed one is asked again.
   */
  load(): Observable<CurrentBusiness | null> {
    const inFlight = this.current$;
    if (inFlight !== undefined) {
      return inFlight;
    }

    const generation = this.generation;
    const loading = this.current().pipe(
      tap((business) => {
        if (generation === this.generation) {
          this.timeZone.publish(business?.timeZoneId);
        }
      }),
      catchError((error: unknown) => {
        // Retryable: drop the shared attempt so a later call asks again. Nothing is published, so
        // no date is rendered in a zone the API never confirmed.
        if (generation === this.generation) {
          this.current$ = undefined;
        }
        console.error('Could not read the current business; business dates are unavailable.', error);
        return of(null);
      }),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    this.current$ = loading;
    return loading;
  }

  /**
   * Forgets the current business, for a change of signed-in account: the cached answer and the
   * published zone are dropped, and a lookup still in flight can no longer publish. The next
   * `load()` asks the endpoint again, as the new account.
   */
  reset(): void {
    this.generation++;
    this.current$ = undefined;
    this.timeZone.clear();
  }
}
