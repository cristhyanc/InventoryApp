import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';

/**
 * The current business's IANA time zone, for operator-facing date/time display and local calendar
 * inputs (issue #499).
 *
 * It replaces the `BUSINESS_TIME_ZONE` constant this module used to export. The zone is a property
 * of the signed-in operator's business, not of the application, so it is published here once
 * `GET /api/business/current` has answered (see `BusinessService`) and read from here by the
 * `BusinessDateTimePipe` and by the components that resolve a calendar input.
 *
 * `null` means "not known yet", and it is deliberately not a usable value: a consumer must show a
 * loading state rather than fall back to a fixed zone or to the browser's own, because both would
 * silently render a different business day than the one the backend reports. This holder has no
 * dependencies of its own precisely so that every consumer can read it - the pipe included -
 * without taking on the HTTP call or the sign-in state the lookup needs.
 */
@Injectable({ providedIn: 'root' })
export class BusinessTimeZoneService {
  private readonly zone$ = new BehaviorSubject<string | null>(null);

  /** Emits the current value immediately, then each time it becomes known. */
  readonly timeZoneId$: Observable<string | null> = this.zone$.asObservable();

  /** The business's IANA zone, or `null` while it is still unknown. Never a fallback. */
  get timeZoneId(): string | null {
    return this.zone$.value;
  }

  /** Whether a zone is known yet, for a component deciding what to render. */
  get isResolved(): boolean {
    return this.zone$.value !== null;
  }

  /** Publishes the zone the API reported. A blank value is ignored, so it can never un-resolve. */
  publish(timeZoneId: string | null | undefined): void {
    if (typeof timeZoneId === 'string' && timeZoneId.trim() !== '') {
      this.zone$.next(timeZoneId.trim());
    }
  }
}
