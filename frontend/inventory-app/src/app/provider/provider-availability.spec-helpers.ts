import { Provider } from '@angular/core';
import { Observable, of, throwError } from 'rxjs';
import { NayaxConnectionService, NayaxConnectionState, NayaxConnectionStatusName } from '../services/nayax-connection.service';
import { ProviderAvailabilityService } from './provider-availability.service';

/**
 * The connection status a spec puts the business in. `'unreadable'` is the third case every screen
 * has to cope with - a status probe that failed, which must hide nothing (issue #728).
 */
export type SpecConnectionStatus = NayaxConnectionStatusName | 'unreadable';

/** A `NayaxConnectionService` whose `status()` answers with one published status. */
export function nayaxConnectionStatusDouble(status: SpecConnectionStatus): NayaxConnectionService {
  const answer = (): Observable<NayaxConnectionState> =>
    status === 'unreadable'
      ? throwError(() => new Error('The Nayax connection status could not be read.'))
      : of({ status, machineChecksPending: false });

  return { status: jest.fn(answer) } as unknown as NayaxConnectionService;
}

/**
 * The one provider a spec of a screen that reads the shared provider-availability signal needs: the
 * **real** {@link ProviderAvailabilityService}, over a stubbed status read, so the availability the
 * screen reacts to is derived by the code under test rather than by the spec.
 */
export function provideNayaxConnectionStatus(status: SpecConnectionStatus = 'Ready'): Provider {
  return { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble(status) };
}

/** The same thing for a spec that constructs its component by hand instead of through `TestBed`. */
export function providerAvailabilityDouble(status: SpecConnectionStatus = 'Ready'): ProviderAvailabilityService {
  return new ProviderAvailabilityService(nayaxConnectionStatusDouble(status));
}
