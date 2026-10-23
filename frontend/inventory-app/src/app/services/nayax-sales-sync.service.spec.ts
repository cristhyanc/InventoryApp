import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import { NayaxSalesSyncResult, NayaxSalesSyncService } from './nayax-sales-sync.service';

interface HttpStub {
  post: jest.Mock;
}

function createService(response: unknown = null): { service: NayaxSalesSyncService; http: HttpStub } {
  const http: HttpStub = { post: jest.fn(() => of(response)) };
  const service = new NayaxSalesSyncService(http as unknown as HttpClient, {
    apiBaseUrl: '/api'
  } as ConfigService);
  return { service, http };
}

function result(syncLatest: Observable<NayaxSalesSyncResult>): NayaxSalesSyncResult {
  let answer: NayaxSalesSyncResult | undefined;
  syncLatest.subscribe((value) => (answer = value));
  if (answer === undefined) throw new Error('syncLatest emitted nothing.');
  return answer;
}

/**
 * The three answers of `POST api/nayax-sales-sync` the Dashboard has to tell apart (issue #843).
 * The `429` run limit is an error response and stays the component's business; what this service
 * owns is the two success answers and the `force` flag.
 */
describe('NayaxSalesSyncService', () => {
  it('reads a 204 as a run that happened, with no instant of its own', () => {
    const { service, http } = createService(null);

    expect(result(service.syncLatest())).toEqual({ wasRecentlySynced: false, lastSyncedAtUtc: null });
    expect(http.post).toHaveBeenCalledWith('/api/nayax-sales-sync', {});
  });

  it('reads a 200 body as a skipped run and carries the instant the sales were synced', () => {
    const { service } = createService({ wasRecentlySynced: true, lastSyncedAtUtc: '2026-10-23T09:00:00Z' });

    expect(result(service.syncLatest())).toEqual({
      wasRecentlySynced: true,
      lastSyncedAtUtc: '2026-10-23T09:00:00Z'
    });
  });

  // The ordinary on-load call must stay the request it has always been, so `force` is absent rather
  // than sent as false.
  it('sends no force parameter unless the caller forces the run', () => {
    const { service, http } = createService(null);

    service.syncLatest(false).subscribe();
    expect(http.post).toHaveBeenCalledWith('/api/nayax-sales-sync', {});

    service.syncLatest(true).subscribe();
    expect(http.post).toHaveBeenLastCalledWith('/api/nayax-sales-sync?force=true', {});
  });
});
