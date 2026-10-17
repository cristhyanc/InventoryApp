import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, Subject } from 'rxjs';
import { FORBIDDEN_INTERCEPTORS } from '../access/forbidden-interceptors';
import { ConfigService } from '../services/config.service';
import { ToastService } from '../services/toast.service';
import { supportSessionMessageInterceptor } from '../support-session/support-session-message.interceptor';
import {
  INVALID_ROLE_PREVIEW_CODE,
  PreviewRole,
  READ_ONLY_ROLE_PREVIEW_CODE,
  ROLE_PREVIEW_HEADER,
  ROLE_PREVIEW_NOT_PERMITTED_CODE,
  readOnlyRolePreviewMessage
} from './role-preview';
import { rolePreviewInterceptor } from './role-preview.interceptor';
import { RolePreviewService } from './role-preview.service';

const MEMBER = '/api/products/5';
const ACCESS_PROBE = '/api/me/access';
const ACCOUNT_STATE_PROBE = '/api/me/account-state';
const MEMBER_MESSAGE = "You don't have access to this.";

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  toastError: jest.Mock;
  handleRefusedPreview: jest.Mock;
}

/**
 * The production chain from `app.config.ts` - the three `403` interceptors, the support-session
 * message interceptor and `rolePreviewInterceptor` - over the **real** `AccessService` and
 * `AccountStateService`, so a refusal drives the reads those caches actually issue. Only MSAL, the
 * API base URL, the router's current URL, the toast and the preview itself are doubled.
 *
 * It follows `access/forbidden-interceptor-chain.spec.ts` (issue #602) for the same reason that
 * file exists: what these interceptors do to each other's refresh reads is only visible when they
 * are installed together, and the preview's refusals are exactly the kind of `403` the two cache
 * interceptors used to react to on their own.
 */
function configure(role: PreviewRole | null, currentUrl = '/products/5'): Harness {
  const toastError = jest.fn();
  const handleRefusedPreview = jest.fn();
  const signedIn = { homeAccountId: 'admin-1' } as unknown as AccountInfo;

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(
        withInterceptors([...FORBIDDEN_INTERCEPTORS, supportSessionMessageInterceptor, rolePreviewInterceptor])
      ),
      provideHttpClientTesting(),
      { provide: ConfigService, useValue: { apiBaseUrl: '/api' } },
      { provide: ToastService, useValue: { error: toastError, warning: jest.fn() } },
      { provide: Router, useValue: { url: currentUrl } },
      { provide: RolePreviewService, useValue: { currentRole: () => role, handleRefusedPreview } },
      {
        provide: MsalService,
        useValue: { instance: { getActiveAccount: () => signedIn, getAllAccounts: () => [signedIn] } }
      },
      {
        provide: MsalBroadcastService,
        useValue: {
          inProgress$: new BehaviorSubject(InteractionStatus.None),
          msalSubject$: new Subject<EventMessage>()
        }
      }
    ]
  });

  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    toastError,
    handleRefusedPreview
  };
}

/**
 * Refuses the request the test made with `status` and `body`, then every request the chain issues
 * because of it with a bare `403`, until it stops issuing them, and returns the URLs in the order
 * they were refused.
 *
 * The follow-ups are refused *ordinarily* on purpose: a cache probe is a `GET`, so the API would
 * never answer one with `read_only_role_preview`, and refusing them with the same body would only
 * measure the test's own helper. A chain that never settles is one of the defects this file exists
 * for, so the rounds are capped and exhausting the cap fails loudly rather than hanging the suite.
 */
function refuseEverything(harness: Harness, status: number, body: string | object, rounds = 12): string[] {
  const refused: string[] = [];

  for (let round = 0; round < rounds; round++) {
    const outstanding = harness.httpMock.match(() => true);
    if (outstanding.length === 0) {
      return refused;
    }

    for (const request of outstanding) {
      refused.push(request.request.url);
      request.flush(round === 0 ? body : 'refused', {
        status: round === 0 ? status : 403,
        statusText: round === 0 && status === 400 ? 'Bad Request' : 'Forbidden'
      });
    }
  }

  throw new Error(`the chain was still issuing requests after ${rounds} rounds: ${refused.join(', ')}`);
}

describe('the composed interceptor chain with the role preview (issue #721)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  /**
   * The acceptance criterion in one test: one toast, the preview's own, and no capability read -
   * which is what would otherwise rebuild the whole menu from the narrowed answer on every refused
   * write.
   */
  it('answers a refused write with one preview toast, no member message and no capability read', () => {
    const harness = configure('Manager');

    harness.http.put(MEMBER, {}).subscribe({ error: () => undefined });
    const refused = refuseEverything(harness, 403, { code: READ_ONLY_ROLE_PREVIEW_CODE });

    expect(harness.toastError).toHaveBeenCalledTimes(1);
    expect(harness.toastError).toHaveBeenCalledWith(readOnlyRolePreviewMessage('Manager'));
    expect(harness.toastError).not.toHaveBeenCalledWith(MEMBER_MESSAGE);
    expect(refused).not.toContain(ACCESS_PROBE);
    // The account-state cache still re-reads, exactly as it does for a support session's own
    // read-only refusal: that read is a dead end (its own probe's refusal starts nothing), and the
    // criterion is about the capability cache, whose answer builds the menu.
    expect(refused).toEqual([MEMBER, ACCOUNT_STATE_PROBE]);
  });

  it('ends the preview on a refused preview, with no member message and no capability read', () => {
    const harness = configure('Operator');

    harness.http.get(MEMBER).subscribe({ error: () => undefined });
    const refused = refuseEverything(harness, 403, { code: ROLE_PREVIEW_NOT_PERMITTED_CODE });

    expect(harness.handleRefusedPreview).toHaveBeenCalledTimes(1);
    expect(harness.toastError).not.toHaveBeenCalled();
    expect(refused).not.toContain(ACCESS_PROBE);
  });

  it('ends the preview on an invalid value, which is a 400 nothing else reacts to', () => {
    const harness = configure('Operator');

    harness.http.get(MEMBER).subscribe({ error: () => undefined });
    const refused = refuseEverything(harness, 400, { code: INVALID_ROLE_PREVIEW_CODE });

    expect(harness.handleRefusedPreview).toHaveBeenCalledTimes(1);
    expect(harness.toastError).not.toHaveBeenCalled();
    expect(refused).toEqual([MEMBER]);
  });

  /**
   * A refusal the preview is not about is still an ordinary member refusal: the previewed role
   * genuinely may not open the page, which is the whole point of previewing it.
   */
  it('still shows the member message for an ordinary 403 during a preview', () => {
    const harness = configure('Operator');

    harness.http.get(MEMBER).subscribe({ error: () => undefined });
    const refused = refuseEverything(harness, 403, 'refused');

    expect(harness.toastError).toHaveBeenCalledWith(MEMBER_MESSAGE);
    expect(harness.handleRefusedPreview).not.toHaveBeenCalled();
    expect(refused).toEqual([MEMBER, ACCOUNT_STATE_PROBE, ACCESS_PROBE, ACCOUNT_STATE_PROBE]);
  });

  it('presents the role on the capability read the chain itself issues', () => {
    const harness = configure('Operator');

    harness.http.get(MEMBER).subscribe({ error: () => undefined });
    harness.httpMock.expectOne(MEMBER).flush('refused', { status: 403, statusText: 'Forbidden' });

    for (const probe of harness.httpMock.match(() => true)) {
      expect(probe.request.headers.get(ROLE_PREVIEW_HEADER)).toBe('Operator');
      probe.flush({ role: 'Owner', capabilities: [], businessName: 'Vending Co', timeZoneId: 'Australia/Sydney' });
    }

    harness.httpMock.verify();
  });

  it('re-raises every refusal to the caller unchanged', () => {
    const harness = configure('Manager');
    let failure: HttpErrorResponse | null = null;

    harness.http.post(MEMBER, {}).subscribe({ error: (error: HttpErrorResponse) => (failure = error) });
    refuseEverything(harness, 403, { code: READ_ONLY_ROLE_PREVIEW_CODE });

    expect(failure).not.toBeNull();
    expect(failure!.status).toBe(403);
  });

  it('changes nothing about an ordinary member refusal when no preview is active', () => {
    const harness = configure(null);

    harness.http.get(MEMBER).subscribe({ error: () => undefined });
    const refused = refuseEverything(harness, 403, 'refused');

    expect(refused).toEqual([MEMBER, ACCOUNT_STATE_PROBE, ACCESS_PROBE, ACCOUNT_STATE_PROBE]);
    expect(harness.toastError).toHaveBeenCalledWith(MEMBER_MESSAGE);
  });
});
