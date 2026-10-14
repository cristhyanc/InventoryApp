import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ToastService } from '../services/toast.service';
import { supportSessionMessageInterceptor } from './support-session-message.interceptor';
import { NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE, READ_ONLY_SUPPORT_SESSION_MESSAGE } from './support-session-messages';

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  error: jest.Mock;
}

function configure(): Harness {
  const error = jest.fn();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([supportSessionMessageInterceptor])),
      provideHttpClientTesting(),
      { provide: ToastService, useValue: { error } }
    ]
  });

  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    error
  };
}

function refuseWithCode(harness: Harness, url: string, code: string): HttpErrorResponse | null {
  let failure: HttpErrorResponse | null = null;
  harness.http.get(url).subscribe({ error: (e: HttpErrorResponse) => (failure = e) });
  harness.httpMock.expectOne(url).flush({ code, message: 'refused' }, { status: 403, statusText: 'Forbidden' });
  return failure;
}

/**
 * The support session's own two `403` messages (issue #554).
 */
describe('supportSessionMessageInterceptor (issue #554)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('shows the read-only message for a write refused with read_only_support_session', () => {
    const harness = configure();

    refuseWithCode(harness, '/api/products/1', 'read_only_support_session');

    expect(harness.error).toHaveBeenCalledWith(READ_ONLY_SUPPORT_SESSION_MESSAGE);
  });

  it('shows the Nayax message for a request refused with unavailable_in_support_view', () => {
    const harness = configure();

    refuseWithCode(harness, '/api/nayax/sales/sync', 'unavailable_in_support_view');

    expect(harness.error).toHaveBeenCalledWith(NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE);
  });

  it('shows nothing for an ordinary member 403 carrying no support-session code', () => {
    const harness = configure();

    refuseWithCode(harness, '/api/reports/bookkeeping', 'some_other_code');

    expect(harness.error).not.toHaveBeenCalled();
  });

  it('ignores a non-403 failure', () => {
    const harness = configure();
    harness.http.get('/api/products/1').subscribe({ error: () => undefined });
    harness.httpMock.expectOne('/api/products/1').flush({ code: 'read_only_support_session' }, { status: 500, statusText: 'Error' });

    expect(harness.error).not.toHaveBeenCalled();
  });

  it('re-raises the refusal unchanged', () => {
    const harness = configure();

    const failure = refuseWithCode(harness, '/api/products/1', 'read_only_support_session');

    expect(failure?.status).toBe(403);
  });
});
