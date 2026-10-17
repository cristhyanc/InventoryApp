import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, firstValueFrom, of, throwError } from 'rxjs';
import { NayaxConnectionService, NayaxConnectionStatusName } from '../services/nayax-connection.service';
import { NAYAX_NOT_CONNECTED_REASON, PERMISSION_NOT_GRANTED_REASON } from './nayax-availability';
import { ProviderAvailabilityService } from './provider-availability.service';
import { ProviderListOutcome, loadProviderList } from './provider-list-load';
import { provideNayaxConnectionStatus } from './provider-availability.spec-helpers';

interface Row {
  readonly id: number;
}

const ROWS: Row[] = [{ id: 1 }];

function refusal(code: string, publishedStatus?: string): HttpErrorResponse {
  const body: Record<string, unknown> = { code };
  if (publishedStatus !== undefined) {
    body['nayaxConnectionStatus'] = publishedStatus;
  }
  return new HttpErrorResponse({ status: 409, error: body });
}

async function load(
  status: NayaxConnectionStatusName | 'unreadable',
  request: jest.Mock<Observable<Row[]>, []>
): Promise<{ outcome: ProviderListOutcome<Row>; provider: ProviderAvailabilityService }> {
  TestBed.configureTestingModule({ providers: [provideNayaxConnectionStatus(status)] });
  const provider = TestBed.inject(ProviderAvailabilityService);

  return { outcome: await firstValueFrom(loadProviderList(provider, 'machineFleet', request)), provider };
}

afterEach(() => TestBed.resetTestingModule());

/**
 * The shared machine/site list load path (issue #728). Its whole specification is: a list, a
 * provider that cannot list it, or a request that failed - three outcomes a page may never confuse,
 * and never an empty list standing in for one of the other two.
 */
describe('loadProviderList (issue #728)', () => {
  it('reports the list a usable connection returned', async () => {
    const { outcome } = await load('Ready', jest.fn(() => of(ROWS)));

    expect(outcome).toEqual({ kind: 'listed', items: ROWS });
  });

  it('reports a legitimately empty list as a list, not as anything being unavailable', async () => {
    const { outcome } = await load('Ready', jest.fn(() => of([])));

    expect(outcome).toEqual({ kind: 'listed', items: [] });
  });

  it('does not even send the request when the shared signal says there is no provider', async () => {
    const request = jest.fn(() => of(ROWS));
    const { outcome } = await load('NotConfigured', request);

    expect(request).not.toHaveBeenCalled();
    expect(outcome).toEqual({ kind: 'unavailable', unavailability: NAYAX_NOT_CONNECTED_REASON });
  });

  it('does not send a live fleet request for a connection that needs attention either', async () => {
    const request = jest.fn(() => of(ROWS));
    const { outcome } = await load('NeedsAttention', request);

    expect(request).not.toHaveBeenCalled();
    expect(outcome).toEqual({ kind: 'unavailable', unavailability: NAYAX_NOT_CONNECTED_REASON });
  });

  it('still asks when the status could not be read, so one failed probe hides nothing', async () => {
    const request = jest.fn(() => of(ROWS));
    const { outcome } = await load('unreadable', request);

    expect(request).toHaveBeenCalledTimes(1);
    expect(outcome).toEqual({ kind: 'listed', items: ROWS });
  });

  it('turns a 409 nayax_not_connected into the unavailable answer, not an error', async () => {
    const request = jest.fn(() => throwError(() => refusal('nayax_not_connected', 'NotConfigured')));
    const { outcome, provider } = await load('Ready', request);

    expect(outcome).toEqual({ kind: 'unavailable', unavailability: NAYAX_NOT_CONNECTED_REASON });
    // The refusal is the session's answer from here on, so the next screen does not ask again.
    const availability = await firstValueFrom(provider.load());
    expect(availability.status).toBe('NotConfigured');
    expect(availability.connection).toBe('notConnected');
  });

  it('turns a 409 nayax_permission_not_granted into the unavailable answer', async () => {
    const request = jest.fn(() => throwError(() => refusal('nayax_permission_not_granted')));
    const { outcome, provider } = await load('PendingPermissions', request);

    expect(outcome).toEqual({ kind: 'unavailable', unavailability: PERMISSION_NOT_GRANTED_REASON });
    // Retained against the feature the refused request was for, and against nothing else.
    const availability = await firstValueFrom(provider.load());
    expect(availability.features.machineFleet).toBe('permissionNotGranted');
    expect(availability.features.machineSales).toBe('unknown');
    expect(availability.connection).toBe('usable');
  });

  it('does not send a second request for a feature this session has seen Nayax refuse', async () => {
    const refused = jest.fn(() => throwError(() => refusal('nayax_permission_not_granted')));
    const { provider } = await load('PendingPermissions', refused);
    const again = jest.fn(() => of(ROWS));

    const outcome = await firstValueFrom(loadProviderList(provider, 'machineFleet', again));

    expect(again).not.toHaveBeenCalled();
    expect(outcome).toEqual({ kind: 'unavailable', unavailability: PERMISSION_NOT_GRANTED_REASON });
  });

  it('keeps an unrelated feature loadable after one feature was refused for permission', async () => {
    const refused = jest.fn(() => throwError(() => refusal('nayax_permission_not_granted')));
    const { provider } = await load('PendingPermissions', refused);
    const records = jest.fn(() => of(ROWS));

    expect(await firstValueFrom(loadProviderList(provider, 'providerRecords', records))).toEqual({
      kind: 'listed',
      items: ROWS
    });
  });

  it.each<[string, unknown]>([
    ['a transport failure', new HttpErrorResponse({ status: 0 })],
    ['a Nayax outage', new HttpErrorResponse({ status: 502, error: { code: 'nayax_unavailable' } })],
    ['an access refusal', new HttpErrorResponse({ status: 403, error: { code: 'read_only_support_session' } })],
    ['a server error', new HttpErrorResponse({ status: 500 })]
  ])('reports %s as a failed load, never as an empty list', async (_, error) => {
    const request = jest.fn(() => throwError(() => error));
    const { outcome } = await load('Ready', request);

    expect(outcome).toEqual({ kind: 'failed' });
  });

  it('asks the provider only about the feature it was given', async () => {
    TestBed.configureTestingModule({ providers: [provideNayaxConnectionStatus('NeedsAttention')] });
    const provider = TestBed.inject(ProviderAvailabilityService);
    const request = jest.fn(() => of(ROWS));

    // `providerRecords` survives a connection that needs attention; `machineFleet` does not.
    expect(await firstValueFrom(loadProviderList(provider, 'providerRecords', request))).toEqual({
      kind: 'listed',
      items: ROWS
    });
    expect(await firstValueFrom(loadProviderList(provider, 'machineFleet', request))).toEqual({
      kind: 'unavailable',
      unavailability: NAYAX_NOT_CONNECTED_REASON
    });
  });

  it('reads the shared status once however many lists are loaded', async () => {
    TestBed.configureTestingModule({ providers: [provideNayaxConnectionStatus('Ready')] });
    const provider = TestBed.inject(ProviderAvailabilityService);
    const connections = TestBed.inject(NayaxConnectionService) as unknown as { status: jest.Mock };

    await firstValueFrom(loadProviderList(provider, 'machineFleet', jest.fn(() => of(ROWS))));
    await firstValueFrom(loadProviderList(provider, 'machineFleet', jest.fn(() => of(ROWS))));

    expect(connections.status).toHaveBeenCalledTimes(1);
  });
});
