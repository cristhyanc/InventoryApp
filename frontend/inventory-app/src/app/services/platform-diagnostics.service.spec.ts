import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import { PlatformDiagnosticsService } from './platform-diagnostics.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
}

function createService(apiBaseUrl: string): { service: PlatformDiagnosticsService; http: HttpStub } {
  const http: HttpStub = { get: jest.fn(() => of({})), post: jest.fn(() => of({})) };
  const service = new PlatformDiagnosticsService(
    http as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, http };
}

/**
 * The two diagnostics endpoints are an authorization boundary, so the URLs and the request body
 * are pinned down here: a request sent to the wrong path, or carrying anything besides the
 * statement, would be a contract change rather than a refactor (issue #336).
 */
describe('PlatformDiagnosticsService (issues #335, #336)', () => {
  it('reads the capability signal from the diagnostics access endpoint', () => {
    const { service, http } = createService('/api');
    service.access().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/admin/diagnostics/access');
    expect(http.post).not.toHaveBeenCalled();
  });

  it('submits a query to the diagnostics query endpoint', () => {
    const { service, http } = createService('/api');
    service.query('SELECT Id FROM Businesses').subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/admin/diagnostics/query', { sql: 'SELECT Id FROM Businesses' });
  });

  /**
   * The endpoint takes one statement and nothing else - no limit, no page, no business - so there
   * is no request input that could widen what a query may read. This asserts the body stays that.
   */
  it('sends the statement and nothing else', () => {
    const { service, http } = createService('/api');
    service.query('SELECT Id FROM Products').subscribe();

    expect(Object.keys(http.post.mock.calls[0][1])).toEqual(['sql']);
  });

  it('resolves both endpoints from the configured API base URL, trailing slash or not', () => {
    const deployed = createService('https://api.example.test/api/');
    deployed.service.access().subscribe();
    deployed.service.query('SELECT Id FROM Products').subscribe();

    expect(deployed.http.get).toHaveBeenCalledWith('https://api.example.test/api/admin/diagnostics/access');
    expect(deployed.http.post.mock.calls[0][0]).toBe('https://api.example.test/api/admin/diagnostics/query');
  });
});
