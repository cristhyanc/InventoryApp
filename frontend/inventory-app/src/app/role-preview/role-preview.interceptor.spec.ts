import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigService } from '../services/config.service';
import { ToastService } from '../services/toast.service';
import {
  INVALID_ROLE_PREVIEW_CODE,
  PreviewRole,
  ROLE_PREVIEW_HEADER,
  ROLE_PREVIEW_NOT_PERMITTED_CODE,
  READ_ONLY_ROLE_PREVIEW_CODE,
  readOnlyRolePreviewMessage
} from './role-preview';
import { rolePreviewInterceptor } from './role-preview.interceptor';
import { RolePreviewService } from './role-preview.service';

const MEMBER = '/api/dashboard/summary';
const ACCESS_PROBE = '/api/me/access';
const ACCOUNT_STATE_PROBE = '/api/me/account-state';
const DIAGNOSTICS_PROBE = '/api/admin/diagnostics/access';
const SUPPORT_SESSION_CONTROL = '/api/super-admin/support-sessions/active';
const BUSINESSES = '/api/super-admin/businesses/pending-count';

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  /** What the preview is, as the interceptor asks for it before each request. */
  setRole(role: PreviewRole | null): void;
  toastError: jest.Mock;
  handleRefusedPreview: jest.Mock;
}

/**
 * The interceptor alone, over a `RolePreviewService` double: what this file is about is which
 * request carries the header and what each refusal code does, not how a preview starts or ends.
 */
function configure(apiBaseUrl = '/api'): Harness {
  let role: PreviewRole | null = null;
  const toastError = jest.fn();
  const handleRefusedPreview = jest.fn();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([rolePreviewInterceptor])),
      provideHttpClientTesting(),
      { provide: ConfigService, useValue: { apiBaseUrl } },
      { provide: ToastService, useValue: { error: toastError } },
      {
        provide: RolePreviewService,
        useValue: { currentRole: () => role, handleRefusedPreview }
      }
    ]
  });

  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    setRole: (next) => (role = next),
    toastError,
    handleRefusedPreview
  };
}

/** The value of `X-Role-Preview` on the one outstanding request to `url`, or `null`. */
function headerOn(harness: Harness, url: string): string | null {
  harness.http.get(url).subscribe({ next: () => undefined, error: () => undefined });
  const request = harness.httpMock.expectOne(url);
  const header = request.request.headers.get(ROLE_PREVIEW_HEADER);
  request.flush({});
  return header;
}

/** Refuses the one outstanding request to `url` with `status` and `code`. */
function refuse(harness: Harness, url: string, status: number, code: string): () => HttpErrorResponse | null {
  let failure: HttpErrorResponse | null = null;
  harness.http.post(url, {}).subscribe({ error: (error: HttpErrorResponse) => (failure = error) });
  harness.httpMock.expectOne(url).flush({ code }, { status, statusText: status === 400 ? 'Bad Request' : 'Forbidden' });
  return () => failure;
}

describe('rolePreviewInterceptor (issue #721)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it.each([MEMBER, ACCESS_PROBE, ACCOUNT_STATE_PROBE])('presents the previewed role on %s', (url) => {
    const harness = configure();
    harness.setRole('Operator');

    expect(headerOn(harness, url)).toBe('Operator');
  });

  /**
   * The platform-admin surfaces are reached as the administrator themselves and are authorized by
   * no business role at all, so narrowing them to a business role would only refuse the control
   * surface the preview is started from.
   */
  it.each([DIAGNOSTICS_PROBE, SUPPORT_SESSION_CONTROL, BUSINESSES])('never presents it on %s', (url) => {
    const harness = configure();
    harness.setRole('Operator');

    expect(headerOn(harness, url)).toBeNull();
  });

  it.each(['/assets/config.json', 'https://login.microsoftonline.test/common/discovery'])(
    'never presents it on the non-API request %s',
    (url) => {
      const harness = configure();
      harness.setRole('Manager');

      expect(headerOn(harness, url)).toBeNull();
    }
  );

  it('presents nothing while no preview is active', () => {
    const harness = configure();

    expect(headerOn(harness, MEMBER)).toBeNull();
  });

  it('presents nothing once the preview has been exited', () => {
    const harness = configure();
    harness.setRole('Manager');
    expect(headerOn(harness, MEMBER)).toBe('Manager');

    harness.setRole(null);

    expect(headerOn(harness, MEMBER)).toBeNull();
  });

  it('presents the role on a deployed API origin too', () => {
    const harness = configure('https://api.example.test/api/');
    harness.setRole('Owner');

    expect(headerOn(harness, 'https://api.example.test/api/machines')).toBe('Owner');
  });

  it('shows one read-only toast for a refused write, and keeps the preview', () => {
    const harness = configure();
    harness.setRole('Manager');

    const failure = refuse(harness, MEMBER, 403, READ_ONLY_ROLE_PREVIEW_CODE);

    expect(harness.toastError).toHaveBeenCalledTimes(1);
    expect(harness.toastError).toHaveBeenCalledWith(readOnlyRolePreviewMessage('Manager'));
    expect(harness.handleRefusedPreview).not.toHaveBeenCalled();
    expect(failure()?.status).toBe(403);
  });

  it.each([
    [403, ROLE_PREVIEW_NOT_PERMITTED_CODE],
    [400, INVALID_ROLE_PREVIEW_CODE]
  ])('ends the preview on a %s %s, with no read-only toast', (status, code) => {
    const harness = configure();
    harness.setRole('Operator');

    const failure = refuse(harness, MEMBER, status, code);

    expect(harness.handleRefusedPreview).toHaveBeenCalledTimes(1);
    expect(harness.toastError).not.toHaveBeenCalled();
    expect(failure()?.status).toBe(status);
  });

  it.each([
    [403, 'read_only_support_session'],
    [403, undefined],
    [500, READ_ONLY_ROLE_PREVIEW_CODE]
  ])('reacts to neither a %s %s', (status, code) => {
    const harness = configure();
    harness.setRole('Operator');

    refuse(harness, MEMBER, status, code as string);

    expect(harness.toastError).not.toHaveBeenCalled();
    expect(harness.handleRefusedPreview).not.toHaveBeenCalled();
  });

  it('reacts to nothing at all while no preview is active', () => {
    const harness = configure();

    refuse(harness, MEMBER, 403, READ_ONLY_ROLE_PREVIEW_CODE);

    expect(harness.toastError).not.toHaveBeenCalled();
    expect(harness.handleRefusedPreview).not.toHaveBeenCalled();
  });
});
