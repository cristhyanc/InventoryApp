import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, firstValueFrom, of, throwError } from 'rxjs';
import {
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionStatusName
} from '../services/nayax-connection.service';
import { NAYAX_NOT_CONNECTED_REASON, PERMISSION_NOT_GRANTED_REASON } from './nayax-availability';
import {
  ProviderAvailability,
  ProviderAvailabilityService,
  providerConnectionOf,
  providerFeatureAvailable
} from './provider-availability.service';

function state(status: NayaxConnectionStatusName): NayaxConnectionState {
  return { status, machineChecksPending: false };
}

/** A `409` exactly as `NayaxConnectionExceptionHandler` writes it, with its published status. */
function notConnected(publishedStatus?: string): HttpErrorResponse {
  const body: Record<string, unknown> = { code: 'nayax_not_connected' };
  if (publishedStatus !== undefined) {
    body['nayaxConnectionStatus'] = publishedStatus;
  }
  return new HttpErrorResponse({ status: 409, error: body });
}

function permissionNotGranted(): HttpErrorResponse {
  return new HttpErrorResponse({ status: 409, error: { code: 'nayax_permission_not_granted' } });
}

interface Harness {
  readonly service: ProviderAvailabilityService;
  readonly status: jest.Mock<Observable<NayaxConnectionState>, []>;
}

function createHarness(source: () => Observable<NayaxConnectionState>): Harness {
  const status = jest.fn(source);

  TestBed.configureTestingModule({
    providers: [{ provide: NayaxConnectionService, useValue: { status } }]
  });

  return { service: TestBed.inject(ProviderAvailabilityService), status };
}

afterEach(() => TestBed.resetTestingModule());

/**
 * The application-wide provider-availability signal (issue #728, child E1 of issue #329). Its whole
 * specification is: one read of the published connection status per session, one overall answer and
 * two per-feature answers derived from it, a refusal that replaces that answer rather than being
 * guessed at, and a failed read that claims nothing.
 */
describe('providerConnectionOf (issue #728)', () => {
  it.each<[NayaxConnectionStatusName, string]>([
    ['Ready', 'usable'],
    ['PendingPermissions', 'usable']
  ])('treats %s as %s, matching the status gate', (status, expected) => {
    expect(providerConnectionOf(status)).toBe(expected);
  });

  it('keeps NeedsAttention apart from NotConfigured: a broken connection is still a connection', () => {
    expect(providerConnectionOf('NeedsAttention')).toBe('needsAttention');
    expect(providerConnectionOf('NotConfigured')).toBe('notConnected');
  });

  it('answers unknown for an unread status and for a status name this build does not recognise', () => {
    expect(providerConnectionOf(null)).toBe('unknown');
    expect(providerConnectionOf('Enabled' as NayaxConnectionStatusName)).toBe('unknown');
  });
});

describe('providerFeatureAvailable (issue #728)', () => {
  it('needs usable credentials for a live fleet read, and refuses a connection that needs attention', () => {
    expect(providerFeatureAvailable('usable', 'liveFleet')).toBe(true);
    expect(providerFeatureAvailable('needsAttention', 'liveFleet')).toBe(false);
    expect(providerFeatureAvailable('notConnected', 'liveFleet')).toBe(false);
  });

  it('keeps provider records for a connection that needs attention: the business does have Nayax', () => {
    expect(providerFeatureAvailable('usable', 'providerRecords')).toBe(true);
    expect(providerFeatureAvailable('needsAttention', 'providerRecords')).toBe(true);
    expect(providerFeatureAvailable('notConnected', 'providerRecords')).toBe(false);
  });

  it('fails open while the answer is unknown, so an unread status hides nothing', () => {
    expect(providerFeatureAvailable('unknown', 'liveFleet')).toBe(true);
    expect(providerFeatureAvailable('unknown', 'providerRecords')).toBe(true);
  });
});

describe('ProviderAvailabilityService (issue #728)', () => {
  it('reads the published status once however many screens ask for it', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));

    const first = await firstValueFrom(service.load());
    const second = await firstValueFrom(service.load());

    expect(status).toHaveBeenCalledTimes(1);
    expect(first).toEqual({ status: 'Ready', connection: 'usable' });
    expect(second).toEqual(first);
  });

  it('answers unknown when the status cannot be read, and lets the next screen ask again', async () => {
    const { service, status } = createHarness(() => throwError(() => new Error('boom')));

    expect(await firstValueFrom(service.load())).toEqual({ status: null, connection: 'unknown' });

    await firstValueFrom(service.load());
    expect(status).toHaveBeenCalledTimes(2);
  });

  it('emits nothing at all while the status read is still outstanding', () => {
    const pending = new Subject<NayaxConnectionState>();
    const { service } = createHarness(() => pending.asObservable());
    const seen: ProviderAvailability[] = [];

    service.load().subscribe((availability) => seen.push(availability));

    expect(seen).toEqual([]);
  });

  it('answers a per-feature question from the one shared read', async () => {
    const { service, status } = createHarness(() => of(state('NotConfigured')));

    expect(await firstValueFrom(service.feature('liveFleet'))).toBe(false);
    expect(await firstValueFrom(service.feature('providerRecords'))).toBe(false);
    expect(status).toHaveBeenCalledTimes(1);
  });

  it('takes the state a save or re-test established without reading the status again', async () => {
    const { service, status } = createHarness(() => of(state('NotConfigured')));
    await firstValueFrom(service.load());

    service.accept(state('Ready'));

    expect(await firstValueFrom(service.load())).toEqual({ status: 'Ready', connection: 'usable' });
    expect(status).toHaveBeenCalledTimes(1);
  });

  it('forgets the answer for a change of business context, and reads it again', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));
    await firstValueFrom(service.load());

    service.reset();

    await firstValueFrom(service.load());
    expect(status).toHaveBeenCalledTimes(2);
  });
});

describe('ProviderAvailabilityService.noteRefusal (issue #728)', () => {
  it('reports the not-connected answer and records the status the refusal published', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));

    expect(service.noteRefusal(notConnected('NotConfigured'))).toBe(NAYAX_NOT_CONNECTED_REASON);

    expect(await firstValueFrom(service.load())).toEqual({ status: 'NotConfigured', connection: 'notConnected' });
    expect(status).not.toHaveBeenCalled();
  });

  it('records NeedsAttention as a connection that needs attention, never as no connection at all', async () => {
    const { service } = createHarness(() => of(state('Ready')));

    service.noteRefusal(notConnected('NeedsAttention'));

    expect(await firstValueFrom(service.load())).toEqual({
      status: 'NeedsAttention',
      connection: 'needsAttention'
    });
  });

  it('reports the refusal but records nothing when no status was published', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));
    await firstValueFrom(service.load());

    expect(service.noteRefusal(notConnected())).toBe(NAYAX_NOT_CONNECTED_REASON);

    expect(await firstValueFrom(service.load())).toEqual({ status: 'Ready', connection: 'usable' });
    expect(status).toHaveBeenCalledTimes(1);
  });

  it('reports a permission refusal and leaves the connection answer alone', async () => {
    const { service } = createHarness(() => of(state('PendingPermissions')));

    expect(service.noteRefusal(permissionNotGranted())).toBe(PERMISSION_NOT_GRANTED_REASON);

    expect(await firstValueFrom(service.load())).toEqual({
      status: 'PendingPermissions',
      connection: 'usable'
    });
  });

  it.each<[string, unknown]>([
    ['a transport failure', new HttpErrorResponse({ status: 0 })],
    ['a Nayax outage', new HttpErrorResponse({ status: 502, error: { code: 'nayax_unavailable' } })],
    ['an access refusal', new HttpErrorResponse({ status: 403, error: { code: 'read_only_support_session' } })],
    ['a 409 this build does not recognise', new HttpErrorResponse({ status: 409, error: { code: 'something_else' } })],
    ['a plain error', new Error('boom')]
  ])('reports %s as no availability answer at all, so the page keeps its error state', (_, error) => {
    const { service } = createHarness(() => of(state('Ready')));

    expect(service.noteRefusal(error)).toBeNull();
  });
});
