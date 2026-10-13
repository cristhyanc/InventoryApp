import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import {
  NayaxConnectionRetestResult,
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionTestResult
} from './nayax-connection.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
}

const testResult: NayaxConnectionTestResult = {
  draftId: '6f3a7d5e-0000-4000-8000-000000000001',
  expiresAtUtc: '2026-10-12T09:15:00Z',
  machineCount: 3,
  nothingFailed: true,
  checks: [{ check: 'SignIn', status: 'Passed', failure: null }]
};

const connected: NayaxConnectionState = { status: 'Ready', machineChecksPending: false };

const retestResult: NayaxConnectionRetestResult = {
  connection: connected,
  statusUpdated: true,
  machineCount: 3,
  nothingFailed: true,
  checks: [{ check: 'SignIn', status: 'Passed', failure: null }]
};

function createService(
  responses: { get?: () => Observable<unknown>; post?: () => Observable<unknown> } = {},
  apiBaseUrl = '/api'
): { service: NayaxConnectionService; http: HttpStub } {
  const http: HttpStub = {
    get: jest.fn(responses.get ?? (() => of(connected))),
    post: jest.fn(responses.post ?? (() => of(testResult)))
  };
  const service = new NayaxConnectionService(
    http as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, http };
}

/**
 * The frontend client for the business's own Nayax connection endpoints (issues #536 and #537),
 * consumed by the Owner-only wizard of issue #538.
 */
describe('NayaxConnectionService (issue #538)', () => {
  it('tests a pasted pair against the test endpoint, sending the token exactly once', () => {
    const { service, http } = createService();

    service.test('7788', 'lynx-token-value').subscribe();

    expect(http.post).toHaveBeenCalledTimes(1);
    expect(http.post).toHaveBeenCalledWith('/api/settings/nayax-connection/test', {
      operatorId: '7788',
      token: 'lynx-token-value'
    });
  });

  it('keeps no field of its own, so a tested token survives the call nowhere in the service', () => {
    const { service } = createService();

    service.test('7788', 'lynx-token-value').subscribe();

    expect(JSON.stringify(service)).not.toContain('lynx-token-value');
    expect(Object.values(service)).toHaveLength(2);
  });

  it('saves a tested connection by draft id alone, never by token', () => {
    const { service, http } = createService({ post: () => of(connected) });

    service.save('6f3a7d5e-0000-4000-8000-000000000001').subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/settings/nayax-connection/save', {
      draftId: '6f3a7d5e-0000-4000-8000-000000000001'
    });
  });

  it('re-tests the stored credentials with an empty body', () => {
    const { service, http } = createService({ post: () => of(retestResult) });

    service.retest().subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/settings/nayax-connection/retest', {});
  });

  it('reads the connection state from the status endpoint', () => {
    const { service, http } = createService();

    service.status().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/settings/nayax-connection/status');
  });

  it('builds its URLs from the configured API base URL without doubling the separator', () => {
    const { service, http } = createService({}, 'https://api.example.com/api/');

    service.status().subscribe();

    expect(http.get).toHaveBeenCalledWith('https://api.example.com/api/settings/nayax-connection/status');
  });

  it('returns the API payloads unchanged', () => {
    const { service } = createService({ get: () => of(connected) });
    let state: NayaxConnectionState | null = null;

    service.status().subscribe((value) => (state = value));

    expect(state).toEqual(connected);
  });
});
