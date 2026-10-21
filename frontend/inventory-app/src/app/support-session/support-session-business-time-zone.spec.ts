import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { NEVER, of } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { AccountStateAnswer, AccountStateName, AccountStateService } from '../access/account-state.service';
import { CAPABILITIES, Capability, capabilitySet } from '../access/capabilities';
import { FORBIDDEN_INTERCEPTORS } from '../access/forbidden-interceptors';
import { signedInIdentity } from '../access/identity-cache.spec-helpers';
import { AppComponent } from '../app.component';
import { BusinessDateTimePipe } from '../formatting/business-date-time.pipe';
import { BusinessTimeZoneService } from '../formatting/business-time-zone.service';
import { ConfigService } from '../services/config.service';
import { provideNayaxConnectionStatus } from '../provider/provider-availability.spec-helpers';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { SuperAdminBusinessesService } from '../services/super-admin-businesses.service';
import {
  FakeBroadcastChannel,
  installBroadcastChannel,
  removeBroadcastChannel
} from './broadcast-channel.spec-helpers';
import { supportSessionMessageInterceptor } from './support-session-message.interceptor';
import { supportSessionInterceptor } from './support-session.interceptor';
import {
  SUPPORT_SESSION_BROADCAST_CHANNEL,
  SUPPORT_SESSION_HEADER,
  SupportSessionService
} from './support-session.service';

const CURRENT_BUSINESS = '/api/business/current';

/** Two businesses whose calendar days genuinely differ, so a wrong zone is visible on screen. */
const OWN_BUSINESS = { name: 'Vending Co', timeZoneId: 'Australia/Sydney' };
const VIEWED_BUSINESS = { name: 'Snack Co', timeZoneId: 'America/New_York' };

/** A session's server-fixed lifetime (`SupportSessionPolicy`), as the expiry of a started one. */
const HOUR_MS = 60 * 60 * 1000;

function expiringIn(milliseconds: number): string {
  return new Date(Date.now() + milliseconds).toISOString();
}

/**
 * A routed page that renders one true instant, which is the only thing a time zone is observable
 * through: `BusinessDateTimePipe` renders nothing at all while no zone is published, and the same
 * instant as a different wall clock in each of the two business zones above.
 */
@Component({
  standalone: true,
  imports: [BusinessDateTimePipe],
  template: '<p data-testid="instant">{{ instant | businessDateTime }}</p>'
})
class InstantPageComponent {
  readonly instant = '2026-10-15T09:30:00Z';
}

interface Rendered {
  readonly fixture: ComponentFixture<AppComponent>;
  readonly host: HTMLElement;
  readonly session: SupportSessionService;
  readonly timeZone: BusinessTimeZoneService;
  readonly httpMock: HttpTestingController;
}

/**
 * The real application shell over the real `SupportSessionService`, the real `BusinessService` and
 * `BusinessTimeZoneService`, the real router and the production interceptor chain, with the API
 * answered by `HttpTestingController` (issue #671).
 *
 * Nothing about the business lookup is doubled, because which business's zone is published - and
 * which request it was read with - is exactly what this spec has to prove. What is doubled is
 * everything the question is not about: MSAL, the two member caches, the platform-admin eligibility
 * probe and the sidebar's pending-applications badge.
 */
async function render(options: { accountState?: AccountStateName } = {}): Promise<Rendered> {
  const identity = signedInIdentity();
  const access: MemberAccessState = {
    status: 'granted',
    access: {
      role: 'Owner',
      capabilities: capabilitySet([...CAPABILITIES]),
      businessName: OWN_BUSINESS.name,
      timeZoneId: OWN_BUSINESS.timeZoneId
    }
  };
  const accountState: AccountStateAnswer = {
    status: 'known',
    accountState: { state: options.accountState ?? 'Member', onboardingEnabled: false, rejectionReason: null }
  };
  const msal = {
    instance: {
      getActiveAccount: () => identity.msal.instance.getActiveAccount(),
      getAllAccounts: () => identity.msal.instance.getAllAccounts(),
      setActiveAccount: jest.fn()
    },
    handleRedirectObservable: () => of(null),
    loginRedirect: jest.fn(),
    logoutRedirect: jest.fn()
  };

  await TestBed.configureTestingModule({
    imports: [AppComponent],
    providers: [
      provideRouter([{ path: '**', component: InstantPageComponent }]),
      provideHttpClient(
        withInterceptors([...FORBIDDEN_INTERCEPTORS, supportSessionMessageInterceptor, supportSessionInterceptor])
      ),
      provideHttpClientTesting(),
      { provide: ConfigService, useValue: { apiBaseUrl: '/api' } },
      { provide: MsalService, useValue: msal },
      { provide: MsalBroadcastService, useValue: identity.broadcast },
      { provide: AccountStateService, useValue: { state$: of(accountState), refreshUnlessReading: jest.fn() } },
      {
        provide: AccessService,
        useValue: {
          access$: of(access),
          allows: (capability: Capability) => of(access.access.capabilities.has(capability)),
          allowsAny: () => of(true),
          refresh: () => of(access),
          refreshUnlessReading: jest.fn()
        }
      },
      { provide: PlatformDiagnosticsAccessService, useValue: { granted$: of(false), isGranted: () => of(false) } },
      { provide: SuperAdminBusinessesService, useValue: { pendingCount: () => of(0), changed$: NEVER } },
      // The sidebar reads the shared provider-availability status, which decides what issue #729's
      // menu locks; it is stubbed as a connected business so the requests this spec asserts on stay
      // the ones it is about.
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AppComponent);
  fixture.detectChanges();
  await TestBed.inject(Router).navigateByUrl('/');
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    session: TestBed.inject(SupportSessionService),
    timeZone: TestBed.inject(BusinessTimeZoneService),
    httpMock: TestBed.inject(HttpTestingController)
  };
}

/** The one business read that is outstanding, and the session id it presented. */
function businessRead(rendered: Rendered): TestRequest {
  return rendered.httpMock.expectOne(CURRENT_BUSINESS);
}

/** Answers the outstanding business read, asserting which session id it was made with. */
function answerBusiness(
  rendered: Rendered,
  business: { name: string; timeZoneId: string },
  expectedSessionId: string | null
): void {
  const request = businessRead(rendered);
  expect(request.request.headers.get(SUPPORT_SESSION_HEADER)).toBe(expectedSessionId);
  request.flush(business);
  rendered.fixture.detectChanges();
}

/** What the routed page rendered the instant as, or `''` while no page is on screen. */
function instantOnScreen(rendered: Rendered): string {
  return rendered.host.querySelector('[data-testid="instant"]')?.textContent?.trim() ?? '';
}

function mainText(rendered: Rendered): string {
  return rendered.host.querySelector('main')?.textContent ?? '';
}

/** Lets the transition's navigation settle and renders whatever it left behind. */
async function settle(rendered: Rendered): Promise<void> {
  await rendered.fixture.whenStable();
  rendered.fixture.detectChanges();
}

/** Starts a session the way the **View as this business** dialog does, and answers the start call. */
function startSession(rendered: Rendered, sessionId = 'session-abc'): void {
  rendered.session.start(9, 'Investigating a reported bug').subscribe();
  rendered.httpMock.expectOne('/api/super-admin/support-sessions/9').flush({
    sessionId,
    businessId: 9,
    businessName: VIEWED_BUSINESS.name,
    startedAtUtc: new Date().toISOString(),
    expiresAtUtc: expiringIn(HOUR_MS)
  });
}

/** Ends it the way the banner's own action does. */
function endSession(rendered: Rendered): void {
  rendered.session.end().subscribe();
  rendered.httpMock.expectOne('/api/super-admin/support-sessions/active/end').flush(null);
}

/** Another open tab of this browser, reaching this one the only way one can. */
function otherTab(): { post(message: unknown): void } {
  const channel = new FakeBroadcastChannel(SUPPORT_SESSION_BROADCAST_CHANNEL);
  return { post: (message) => channel.postMessage(message) };
}

describe('the effective business time zone across support transitions (issue #671)', () => {
  beforeEach(() => installBroadcastChannel());

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
    removeBroadcastChannel();
  });

  it('publishes the viewed business’s own zone when a session starts, holding the page until it answers', async () => {
    const rendered = await render();
    answerBusiness(rendered, OWN_BUSINESS, null);
    const own = instantOnScreen(rendered);
    expect(rendered.timeZone.timeZoneId).toBe(OWN_BUSINESS.timeZoneId);
    expect(own).not.toBe('');

    startSession(rendered);
    await settle(rendered);

    // No zone at all meanwhile, and no page: an instant rendered here would be rendered in the
    // administrator's own business's zone for a page labelled as somebody else's.
    expect(rendered.timeZone.timeZoneId).toBeNull();
    expect(mainText(rendered)).toContain('Loading your business');
    expect(instantOnScreen(rendered)).toBe('');

    // The read is made as the session, so the business it describes is the one being viewed.
    answerBusiness(rendered, VIEWED_BUSINESS, 'session-abc');

    expect(rendered.timeZone.timeZoneId).toBe(VIEWED_BUSINESS.timeZoneId);
    expect(instantOnScreen(rendered)).not.toBe('');
    expect(instantOnScreen(rendered)).not.toBe(own);
  });

  it('publishes the administrator’s own business zone again when the session ends', async () => {
    const rendered = await render();
    answerBusiness(rendered, OWN_BUSINESS, null);
    const own = instantOnScreen(rendered);
    startSession(rendered);
    await settle(rendered);
    answerBusiness(rendered, VIEWED_BUSINESS, 'session-abc');
    expect(instantOnScreen(rendered)).not.toBe(own);

    endSession(rendered);
    await settle(rendered);

    expect(rendered.timeZone.timeZoneId).toBeNull();
    answerBusiness(rendered, OWN_BUSINESS, null);

    expect(rendered.timeZone.timeZoneId).toBe(OWN_BUSINESS.timeZoneId);
    expect(instantOnScreen(rendered)).toBe(own);
  });

  it('reads the viewed business for an administrator who holds no membership of their own', async () => {
    const rendered = await render({ accountState: 'NoMembership' });
    // Nothing to read outside a session: the lookup would only be refused, so none is made and no
    // zone is published.
    rendered.httpMock.expectNone(CURRENT_BUSINESS);
    expect(rendered.timeZone.timeZoneId).toBeNull();
    expect(instantOnScreen(rendered)).toBe('');

    startSession(rendered);
    await settle(rendered);
    answerBusiness(rendered, VIEWED_BUSINESS, 'session-abc');

    expect(rendered.timeZone.timeZoneId).toBe(VIEWED_BUSINESS.timeZoneId);
    expect(instantOnScreen(rendered)).not.toBe('');
  });

  it('never lets the previous context’s business answer publish its zone', async () => {
    const rendered = await render();
    const stale = businessRead(rendered);

    startSession(rendered);
    await settle(rendered);
    const inSession = businessRead(rendered);

    // The administrator's own business answering after the session started: it was asked for in a
    // context that no longer exists (issue #669), and the zone it names is not the one in view.
    stale.flush(OWN_BUSINESS);
    rendered.fixture.detectChanges();
    expect(rendered.timeZone.timeZoneId).toBeNull();
    expect(mainText(rendered)).toContain('Loading your business');

    inSession.flush(VIEWED_BUSINESS);
    rendered.fixture.detectChanges();

    expect(rendered.timeZone.timeZoneId).toBe(VIEWED_BUSINESS.timeZoneId);
  });

  /** The case a settled/unsettled flag cannot report: one `true` covers both transitions. */
  it('reads the business for each of two overlapping transitions, not only the first', async () => {
    const rendered = await render();
    answerBusiness(rendered, OWN_BUSINESS, null);

    startSession(rendered);
    const inSession = businessRead(rendered);
    // Ended before the start's own navigation has settled, which is what overlaps them.
    endSession(rendered);
    const afterSession = businessRead(rendered);
    await settle(rendered);

    // The first read belongs to a context this tab has already left, so it publishes nothing.
    inSession.flush(VIEWED_BUSINESS);
    afterSession.flush(OWN_BUSINESS);
    rendered.fixture.detectChanges();

    expect(rendered.timeZone.timeZoneId).toBe(OWN_BUSINESS.timeZoneId);
    expect(instantOnScreen(rendered)).not.toBe('');
  });

  it('reads the business when another tab shares its session, and when that tab ends it', async () => {
    const rendered = await render();
    answerBusiness(rendered, OWN_BUSINESS, null);

    otherTab().post({
      type: 'started',
      accountKey: 'member-1',
      sessionId: 'session-from-the-other-tab',
      businessName: VIEWED_BUSINESS.name,
      expiresAtUtc: expiringIn(HOUR_MS)
    });
    await settle(rendered);
    answerBusiness(rendered, VIEWED_BUSINESS, 'session-from-the-other-tab');
    expect(rendered.timeZone.timeZoneId).toBe(VIEWED_BUSINESS.timeZoneId);

    otherTab().post({ type: 'ended', accountKey: 'member-1' });
    await settle(rendered);
    answerBusiness(rendered, OWN_BUSINESS, null);

    expect(rendered.timeZone.timeZoneId).toBe(OWN_BUSINESS.timeZoneId);
  });

  it('reads the business again when the session’s own lifetime runs out', async () => {
    const rendered = await render();
    answerBusiness(rendered, OWN_BUSINESS, null);
    // A session with its whole lifetime in front of it, only much shorter: what the service
    // schedules is the expiry the server reported.
    rendered.session.start(9, 'Investigating a reported bug').subscribe();
    rendered.httpMock.expectOne('/api/super-admin/support-sessions/9').flush({
      sessionId: 'session-abc',
      businessId: 9,
      businessName: VIEWED_BUSINESS.name,
      startedAtUtc: new Date().toISOString(),
      expiresAtUtc: expiringIn(50)
    });
    await settle(rendered);
    answerBusiness(rendered, VIEWED_BUSINESS, 'session-abc');
    expect(rendered.timeZone.timeZoneId).toBe(VIEWED_BUSINESS.timeZoneId);

    await new Promise((resolve) => setTimeout(resolve, 80));
    await settle(rendered);

    expect(rendered.timeZone.timeZoneId).toBeNull();
    answerBusiness(rendered, OWN_BUSINESS, null);

    expect(rendered.timeZone.timeZoneId).toBe(OWN_BUSINESS.timeZoneId);
  });
});
