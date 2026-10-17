import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, firstValueFrom, of, throwError } from 'rxjs';
import {
  NayaxConnectionCheckLine,
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionStatusName
} from '../services/nayax-connection.service';
import { NAYAX_NOT_CONNECTED_REASON, PERMISSION_NOT_GRANTED_REASON } from './nayax-availability';
import {
  ProviderAvailability,
  ProviderAvailabilityService,
  ProviderFeatureAvailabilities,
  ProviderFeatureAvailability,
  providerConnectionAllows,
  providerConnectionOf,
  providerFeatureAvailabilityOf,
  providerFeatureOffered
} from './provider-availability.service';
import { ProviderFeature } from './provider-feature-permissions';

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

/** Every feature answered `unknown`, which is the state of a session nothing has been told. */
const NOTHING_KNOWN: ProviderFeatureAvailabilities = {
  machineFleet: 'unknown',
  machineSales: 'unknown',
  machineStock: 'unknown',
  productCatalogue: 'unknown',
  providerRecords: 'unknown'
};

function featuresWith(
  overrides: Partial<Record<ProviderFeature, ProviderFeatureAvailability>>
): ProviderFeatureAvailabilities {
  return { ...NOTHING_KNOWN, ...overrides };
}

/** One check line, as `NayaxConnectionCheckResponse` carries it. */
function passed(check: NayaxConnectionCheckLine['check']): NayaxConnectionCheckLine {
  return { check, status: 'Passed', failure: null };
}

function refused(check: NayaxConnectionCheckLine['check']): NayaxConnectionCheckLine {
  return { check, status: 'Failed', failure: 'PermissionNotGranted' };
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
 * one per-feature answer each - `unknown` included, and never inferred from the status alone - a
 * refusal that is retained against the feature it was about rather than guessed at, and a failed
 * read that claims nothing.
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

describe('providerConnectionAllows (issue #728)', () => {
  it('needs usable credentials for a live Nayax read, and refuses a connection that needs attention', () => {
    expect(providerConnectionAllows('usable', 'machineFleet')).toBe(true);
    expect(providerConnectionAllows('needsAttention', 'machineFleet')).toBe(false);
    expect(providerConnectionAllows('notConnected', 'machineFleet')).toBe(false);
    expect(providerConnectionAllows('needsAttention', 'machineSales')).toBe(false);
  });

  it('keeps provider records for a connection that needs attention: the business does have Nayax', () => {
    expect(providerConnectionAllows('usable', 'providerRecords')).toBe(true);
    expect(providerConnectionAllows('needsAttention', 'providerRecords')).toBe(true);
    expect(providerConnectionAllows('notConnected', 'providerRecords')).toBe(false);
  });

  it('fails open while the answer is unknown, so an unread status hides nothing', () => {
    expect(providerConnectionAllows('unknown', 'machineFleet')).toBe(true);
    expect(providerConnectionAllows('unknown', 'providerRecords')).toBe(true);
  });
});

describe('providerFeatureAvailabilityOf (issue #728)', () => {
  it('never reads a usable connection as a granted permission', () => {
    expect(providerFeatureAvailabilityOf('usable', 'unknown', 'machineSales')).toBe('unknown');
    expect(providerFeatureOffered('unknown')).toBe(true);
  });

  it('answers available only on evidence that the read is granted', () => {
    expect(providerFeatureAvailabilityOf('usable', 'granted', 'machineSales')).toBe('available');
    expect(providerFeatureOffered('available')).toBe(true);
  });

  it('withholds a feature Nayax has refused, and nothing else', () => {
    expect(providerFeatureAvailabilityOf('usable', 'denied', 'machineSales')).toBe('permissionNotGranted');
    expect(providerFeatureAvailabilityOf('usable', 'unknown', 'machineFleet')).toBe('unknown');
    expect(providerFeatureOffered('permissionNotGranted')).toBe(false);
  });

  it('keeps a stated refusal while the status itself is unread: it will be refused again', () => {
    expect(providerFeatureAvailabilityOf('unknown', 'denied', 'machineSales')).toBe('permissionNotGranted');
  });

  it('answers from the connection when the connection cannot serve the feature at all', () => {
    expect(providerFeatureAvailabilityOf('notConnected', 'granted', 'machineFleet')).toBe('notConnected');
    expect(providerFeatureAvailabilityOf('needsAttention', 'granted', 'machineFleet')).toBe('notConnected');
    expect(providerFeatureOffered('notConnected')).toBe(false);
  });
});

describe('ProviderAvailabilityService (issue #728)', () => {
  it('reads the published status once however many screens ask for it', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));

    const first = await firstValueFrom(service.load());
    const second = await firstValueFrom(service.load());

    expect(status).toHaveBeenCalledTimes(1);
    expect(first.status).toBe('Ready');
    expect(first.connection).toBe('usable');
    expect(second).toEqual(first);
  });

  it('offers every feature of a usable connection without claiming any permission was granted', async () => {
    const { service } = createHarness(() => of(state('PendingPermissions')));

    const availability = await firstValueFrom(service.load());

    expect(availability.connection).toBe('usable');
    expect(availability.features).toEqual(NOTHING_KNOWN);
    expect(availability.permissions).toEqual({
      machineFleet: 'unknown',
      machineSales: 'unknown',
      machineStock: 'unknown',
      productCatalogue: 'unknown',
      providerRecords: 'unknown'
    });
    expect(await firstValueFrom(service.featureOffered('machineSales'))).toBe(true);
  });

  it('answers unknown when the status cannot be read, and lets the next screen ask again', async () => {
    const { service, status } = createHarness(() => throwError(() => new Error('boom')));

    const availability = await firstValueFrom(service.load());
    expect(availability.status).toBeNull();
    expect(availability.connection).toBe('unknown');
    expect(availability.features).toEqual(NOTHING_KNOWN);

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

    expect(await firstValueFrom(service.featureAvailability('machineFleet'))).toBe('notConnected');
    expect(await firstValueFrom(service.featureOffered('providerRecords'))).toBe(false);
    expect(status).toHaveBeenCalledTimes(1);
  });

  it('takes the state a save or re-test established without reading the status again', async () => {
    const { service, status } = createHarness(() => of(state('NotConfigured')));
    await firstValueFrom(service.load());

    service.accept(state('Ready'));

    const availability = await firstValueFrom(service.load());
    expect(availability.status).toBe('Ready');
    expect(availability.connection).toBe('usable');
    expect(status).toHaveBeenCalledTimes(1);
  });

  it('keeps one granted feature usable while a re-test says another was refused', async () => {
    const { service } = createHarness(() => of(state('Ready')));

    service.accept(state('PendingPermissions'), [
      passed('SignIn'),
      passed('Machines'),
      refused('MachineLastSales'),
      { check: 'MachineProducts', status: 'NotChecked', failure: null }
    ]);

    const availability = await firstValueFrom(service.load());
    expect(availability.connection).toBe('usable');
    expect(availability.features).toEqual(
      featuresWith({ machineFleet: 'available', machineSales: 'permissionNotGranted' })
    );
    expect(await firstValueFrom(service.featureOffered('machineFleet'))).toBe(true);
    expect(await firstValueFrom(service.featureOffered('machineSales'))).toBe(false);
  });

  it('refreshes a retained refusal from the next re-test, rather than keeping it for the session', async () => {
    const { service } = createHarness(() => of(state('PendingPermissions')));
    service.noteRefusal(permissionNotGranted(), 'machineSales');
    expect(await firstValueFrom(service.featureAvailability('machineSales'))).toBe('permissionNotGranted');

    service.accept(state('Ready'), [passed('SignIn'), passed('MachineLastSales')]);

    expect(await firstValueFrom(service.featureAvailability('machineSales'))).toBe('available');
  });

  it('clears the permission evidence for a connection state that reports no checks at all', async () => {
    const { service } = createHarness(() => of(state('PendingPermissions')));
    service.noteRefusal(permissionNotGranted(), 'machineSales');

    service.accept(state('PendingPermissions'));

    expect(await firstValueFrom(service.featureAvailability('machineSales'))).toBe('unknown');
  });

  it('forgets the answer and the permission evidence for a change of business context', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));
    await firstValueFrom(service.load());
    service.noteRefusal(permissionNotGranted(), 'machineSales');

    service.reset();

    const availability = await firstValueFrom(service.load());
    expect(availability.features).toEqual(NOTHING_KNOWN);
    expect(status).toHaveBeenCalledTimes(2);
  });
});

describe('ProviderAvailabilityService.noteRefusal (issue #728)', () => {
  it('reports the not-connected answer and records the status the refusal published', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));

    expect(service.noteRefusal(notConnected('NotConfigured'), 'machineFleet')).toBe(NAYAX_NOT_CONNECTED_REASON);

    const availability = await firstValueFrom(service.load());
    expect(availability.status).toBe('NotConfigured');
    expect(availability.connection).toBe('notConnected');
    expect(status).not.toHaveBeenCalled();
  });

  it('records NeedsAttention as a connection that needs attention, never as no connection at all', async () => {
    const { service } = createHarness(() => of(state('Ready')));

    service.noteRefusal(notConnected('NeedsAttention'), 'machineFleet');

    const availability = await firstValueFrom(service.load());
    expect(availability.status).toBe('NeedsAttention');
    expect(availability.connection).toBe('needsAttention');
    // A live read is refused by the connection itself; what is only recorded locally survives.
    expect(availability.features).toEqual(
      featuresWith({
        machineFleet: 'notConnected',
        machineSales: 'notConnected',
        machineStock: 'notConnected',
        productCatalogue: 'notConnected'
      })
    );
  });

  it('reports the refusal but records nothing when no status was published', async () => {
    const { service, status } = createHarness(() => of(state('Ready')));
    await firstValueFrom(service.load());

    expect(service.noteRefusal(notConnected(), 'machineFleet')).toBe(NAYAX_NOT_CONNECTED_REASON);

    expect((await firstValueFrom(service.load())).status).toBe('Ready');
    expect(status).toHaveBeenCalledTimes(1);
  });

  it('retains a permission refusal against that one feature and leaves the connection alone', async () => {
    const { service } = createHarness(() => of(state('PendingPermissions')));

    expect(service.noteRefusal(permissionNotGranted(), 'machineSales')).toBe(PERMISSION_NOT_GRANTED_REASON);

    const availability = await firstValueFrom(service.load());
    expect(availability.status).toBe('PendingPermissions');
    expect(availability.connection).toBe('usable');
    expect(availability.features).toEqual(featuresWith({ machineSales: 'permissionNotGranted' }));
    expect(availability.permissions.machineSales).toBe('denied');
    expect(availability.permissions.machineFleet).toBe('unknown');
  });

  it.each<[string, unknown]>([
    ['a transport failure', new HttpErrorResponse({ status: 0 })],
    ['a Nayax outage', new HttpErrorResponse({ status: 502, error: { code: 'nayax_unavailable' } })],
    ['an access refusal', new HttpErrorResponse({ status: 403, error: { code: 'read_only_support_session' } })],
    ['a 409 this build does not recognise', new HttpErrorResponse({ status: 409, error: { code: 'something_else' } })],
    ['a plain error', new Error('boom')]
  ])('reports %s as no availability answer at all, so the page keeps its error state', (_, error) => {
    const { service } = createHarness(() => of(state('Ready')));

    expect(service.noteRefusal(error, 'machineFleet')).toBeNull();
  });

  it('keeps an ordinary transport failure out of the permission evidence', async () => {
    const { service } = createHarness(() => of(state('PendingPermissions')));

    expect(service.noteRefusal(new HttpErrorResponse({ status: 0 }), 'machineSales')).toBeNull();

    const availability = await firstValueFrom(service.load());
    expect(availability.features).toEqual(NOTHING_KNOWN);
    expect(await firstValueFrom(service.featureOffered('machineSales'))).toBe(true);
  });
});
