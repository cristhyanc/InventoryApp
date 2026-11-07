import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
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

function createFailingService(status: number): { service: NayaxSalesSyncService; http: HttpStub } {
  const http: HttpStub = {
    post: jest.fn(() => throwError(() => new HttpErrorResponse({ status })))
  };
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
 * The answers of `POST api/nayax-sales-sync` the Dashboard has to tell apart (issue #843, extended
 * by issue #1104). The `429` run limit is an error response and stays the component's business; what
 * this service owns is the two success answers, the `409` skip, and the `force` flag.
 */
describe('NayaxSalesSyncService', () => {
  it('reads a 204 as a run that happened, with no instant of its own', () => {
    const { service, http } = createService(null);

    expect(result(service.syncLatest())).toEqual({
      wasRecentlySynced: false,
      wasBusy: false,
      lastSyncedAtUtc: null
    });
    expect(http.post).toHaveBeenCalledWith('/api/nayax-sales-sync', {});
  });

  it('reads a 200 body as a skipped run and carries the instant the sales were synced', () => {
    const { service } = createService({ wasRecentlySynced: true, lastSyncedAtUtc: '2026-10-23T09:00:00Z' });

    expect(result(service.syncLatest())).toEqual({
      wasRecentlySynced: true,
      wasBusy: false,
      lastSyncedAtUtc: '2026-10-23T09:00:00Z'
    });
  });

  // Issue #1104: a sync for this business is already running - the hourly reconciliation, or another
  // member's refresh. It is a skip and not a failure, so it reaches `next` and never `error`: the
  // caller shows no error, and there is nothing to retry because the run in progress is already
  // producing exactly the data this call wanted.
  it('reads a 409 as a skip rather than an error, with no instant and no retry', () => {
    const { service, http } = createFailingService(409);

    let errored = false;
    let emitted: NayaxSalesSyncResult | undefined;
    service.syncLatest().subscribe({
      next: (value) => (emitted = value),
      error: () => (errored = true)
    });

    expect(errored).toBe(false);
    expect(emitted).toEqual({ wasRecentlySynced: true, wasBusy: true, lastSyncedAtUtc: null });
    // One request, and no second attempt: the service does not retry a busy sync.
    expect(http.post).toHaveBeenCalledTimes(1);
  });

  // The run limit keeps its own notice on the Dashboard, so it must still arrive as an error.
  it.each([429, 403, 500])('leaves a %i to the caller as an error', (status) => {
    const { service } = createFailingService(status);

    let caught: unknown;
    service.syncLatest().subscribe({
      next: () => undefined,
      error: (error: unknown) => (caught = error)
    });

    expect(caught).toBeInstanceOf(HttpErrorResponse);
    expect((caught as HttpErrorResponse).status).toBe(status);
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
