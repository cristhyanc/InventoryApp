import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { NEVER, filter, firstValueFrom, of } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { AccountStateAnswer, AccountStateService } from '../access/account-state.service';
import { CAPABILITIES, Capability, capabilitySet } from '../access/capabilities';
import { FORBIDDEN_INTERCEPTORS } from '../access/forbidden-interceptors';
import { signedInIdentity } from '../access/identity-cache.spec-helpers';
import { AppComponent } from '../app.component';
import { DashboardComponent } from '../components/dashboard/dashboard.component';
import { DashboardSummary, Machine, Site } from '../models/models';
import { BusinessService } from '../services/business.service';
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

const SYNC = '/api/nayax-sales-sync';
const MACHINES = '/api/machines';
const SITES = '/api/sites';
const LOW_STOCK = '/api/products/alerts/low-stock';
const SUMMARY = '/api/dashboard/summary';

/** One business's Dashboard, as the figures a test can recognise on screen. */
interface Figures {
  readonly machineName: string;
  readonly siteName: string;
  readonly salesThisWeek: number;
}

const OWN: Figures = { machineName: 'Own Machine', siteName: 'Own Site', salesThisWeek: 100 };
const VIEWED: Figures = { machineName: 'Snack Co Machine', siteName: 'Snack Co Site', salesThisWeek: 900 };

function machine(figures: Figures): Machine {
  return {
    machineID: 1,
    machineName: figures.machineName,
    machineNumber: 'A1',
    todayGrossRevenue: 0,
    currentWeekGrossRevenue: figures.salesThisWeek,
    lastWeekGrossRevenue: 0,
    twoWeeksAgoGrossRevenue: 0,
    todayDirectProfit: 0,
    twoWeeksAgoDirectProfit: 0,
    lastWeekDirectProfit: 0,
    currentWeekDirectProfit: 0,
    previousComparableWeekGrossRevenue: 0,
    monthToDateGrossRevenue: 0,
    monthToDateDirectProfit: 0,
    previousComparableWeekDirectProfit: 0,
    profitabilityStatus: null
  };
}

function site(figures: Figures): Site {
  return {
    siteId: 1,
    siteName: figures.siteName,
    machineCount: 1,
    totalStockPercentage: 50,
    lowProductCount: 0,
    emptyProductCount: 0,
    todayRevenue: 0,
    currentWeekRevenue: figures.salesThisWeek,
    previousComparableWeekRevenue: 0
  };
}

function summary(figures: Figures): DashboardSummary {
  return {
    asOfUtc: '2026-10-15T09:30:00Z',
    businessDate: '2026-10-15T00:00:00Z',
    salesThisWeek: {
      sales: figures.salesThisWeek,
      transactionCount: 1,
      period: {
        startUtc: '2026-10-12T14:00:00Z',
        endUtc: '2026-10-15T09:30:00Z',
        firstBusinessDate: '2026-10-12T00:00:00Z',
        lastBusinessDate: '2026-10-15T00:00:00Z'
      },
      comparisonPeriod: {
        startUtc: '2026-10-05T14:00:00Z',
        endUtc: '2026-10-08T09:30:00Z',
        firstBusinessDate: '2026-10-05T00:00:00Z',
        lastBusinessDate: '2026-10-08T00:00:00Z'
      },
      isComparisonAvailable: false,
      comparisonSales: 0,
      comparisonTransactionCount: 0,
      changeAmount: 0,
      changePercent: null,
      comparisonNote: null
    },
    needsRefill: {
      machinesNeedingRefill: 0,
      machinesWithEmptySelections: 0,
      machinesWithLowSelections: 0,
      emptySelectionCount: 0,
      lowSelectionCount: 0,
      machinesEvaluated: 1,
      selectionsEvaluated: 1
    },
    needsOrdering: { productsNeedingOrdering: 0, productsEvaluated: 1, mode: 'MachineAware' },
    inventory: {
      inventoryValueAtCost: figures.salesThisWeek,
      isInventoryValueComplete: true,
      productsWithUnknownCost: 0,
      productCount: 1,
      unitsInStorage: 1
    }
  };
}

@Component({ standalone: true, template: 'another page' })
class OtherPageComponent {}

/** A session's server-fixed lifetime (`SupportSessionPolicy`), as the expiry of a started one. */
const HOUR_MS = 60 * 60 * 1000;

/**
 * An expiry relative to the clock this test is actually running on, because that is the clock the
 * service schedules the session's end from. A fixed future instant would be worse than it looks:
 * `setTimeout` overflows past about 24 days and fires at once, so a far-future expiry would expire
 * immediately.
 */
function expiringIn(milliseconds: number): string {
  return new Date(Date.now() + milliseconds).toISOString();
}

interface Rendered {
  readonly fixture: ComponentFixture<AppComponent>;
  readonly host: HTMLElement;
  readonly session: SupportSessionService;
  readonly httpMock: HttpTestingController;
}

/**
 * The real application shell over the real `SupportSessionService`, the real router and the **real**
 * `DashboardComponent`, with the API answered by `HttpTestingController` (issue #670).
 *
 * What is doubled is everything a support-context transition is not about: MSAL, the two member
 * caches, the cached business the shell reads, the platform-admin eligibility probe and the sidebar's
 * pending-applications badge. The page, the outlet, the navigation and the session's own transition
 * are all real, because what this spec has to prove is what is **on screen** after a transition -
 * router spies cannot see a component the router decided to reuse.
 */
async function render(options: { answer?: boolean; at?: string } = {}): Promise<Rendered> {
  const identity = signedInIdentity();
  const access: MemberAccessState = {
    status: 'granted',
    access: {
      role: 'Owner',
      // Everything except `Business.Manage`, which is the one capability the Dashboard's legacy
      // business-settings prompt reads its own settings for: this spec is about the figures.
      capabilities: capabilitySet(CAPABILITIES.filter((capability) => capability !== 'Business.Manage')),
      businessName: 'Vending Co',
      timeZoneId: 'Australia/Sydney'
    }
  };
  const accountState: AccountStateAnswer = {
    status: 'known',
    accountState: { state: 'Member', onboardingEnabled: false, rejectionReason: null }
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
      provideRouter([
        { path: '', component: DashboardComponent },
        { path: 'other', component: OtherPageComponent }
      ]),
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
      {
        provide: BusinessService,
        useValue: {
          load: () => of({ name: 'Vending Co', timeZoneId: 'Australia/Sydney' }),
          reset: jest.fn()
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
  await TestBed.inject(Router).navigateByUrl(options.at ?? '/');
  fixture.detectChanges();

  const rendered: Rendered = {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    session: TestBed.inject(SupportSessionService),
    httpMock: TestBed.inject(HttpTestingController)
  };

  if (options.answer ?? true) {
    answerDashboard(rendered, OWN);
  }
  return rendered;
}

/** Everything the page has asked for and not been answered yet. */
function pending(rendered: Rendered): TestRequest[] {
  return rendered.httpMock.match(() => true);
}

/** Everything the routed Dashboard asked for, answered as one business's figures. */
function answerDashboard(rendered: Rendered, figures: Figures, expectedSessionId?: string | null): TestRequest[] {
  const answered: TestRequest[] = [];
  const answer = (url: string, body: Machine[] | Site[] | DashboardSummary | null): void => {
    for (const request of rendered.httpMock.match((candidate) => candidate.url === url)) {
      answered.push(request);
      request.flush(body);
    }
  };

  // The sales synchronization is a `POST` and a Nayax call, so it is deliberately not attempted in
  // a support view at all (issue #661); `match` therefore tolerates its absence.
  // The summary first: since issue #727 the Dashboard asks it whether the business has a usable
  // provider and only then synchronizes sales and loads Sites and Machines, so those three
  // requests do not exist until this one is answered.
  answer(LOW_STOCK, []);
  answer(SUMMARY, summary(figures));
  answer(SYNC, null);
  answer(MACHINES, [machine(figures)]);
  answer(SITES, [site(figures)]);
  rendered.fixture.detectChanges();

  if (expectedSessionId !== undefined) {
    for (const request of answered) {
      expect(request.request.headers.get(SUPPORT_SESSION_HEADER)).toBe(expectedSessionId);
    }
  }
  return answered;
}

/** Answers a set of requests captured earlier, which is how a stale context answers late. */
function flushStale(requests: readonly TestRequest[], figures: Figures): void {
  for (const request of requests) {
    expect(request.request.headers.get(SUPPORT_SESSION_HEADER)).toBeNull();
    request.flush(
      request.request.url === MACHINES
        ? [machine(figures)]
        : request.request.url === SITES
          ? [site(figures)]
          : request.request.url === SUMMARY
            ? summary(figures)
            : []
    );
  }
}

/** The business figures actually on screen: the machine and site names the Dashboard rendered. */
function onScreen(rendered: Rendered): string {
  return (rendered.host.querySelector('main') as HTMLElement).textContent ?? '';
}

/** The "Viewing as" banner, which the shell renders above the page while a session is effective. */
function banner(rendered: Rendered): string {
  return rendered.host.querySelector('.support-session-banner')?.textContent ?? '';
}

/** Lets the transition's navigation settle and renders whatever it left behind. */
async function settle(rendered: Rendered): Promise<void> {
  await rendered.fixture.whenStable();
  rendered.fixture.detectChanges();
}

/** Starts a session the way the **View as this business** dialog does, and answers the start call. */
function startSession(rendered: Rendered, sessionId = 'session-abc', expiresInMs = HOUR_MS): void {
  rendered.session.start(9, 'Investigating a reported bug').subscribe();
  rendered.httpMock.expectOne('/api/super-admin/support-sessions/9').flush({
    sessionId,
    businessId: 9,
    businessName: 'Snack Co',
    startedAtUtc: new Date().toISOString(),
    expiresAtUtc: expiringIn(expiresInMs)
  });
}

/** Another open tab of this browser, reaching this one the only way one can. */
function otherTab(): { post(message: unknown): void } {
  const channel = new FakeBroadcastChannel(SUPPORT_SESSION_BROADCAST_CHANNEL);
  return { post: (message) => channel.postMessage(message) };
}

/** A `started` message as the tab the session was started in sends it. */
function sharedSession(sessionId: string, accountKey = 'member-1'): unknown {
  return {
    type: 'started',
    accountKey,
    sessionId,
    businessName: 'Snack Co',
    expiresAtUtc: expiringIn(HOUR_MS)
  };
}

/** Ends it the way the banner's own action does. */
function endSession(rendered: Rendered): void {
  rendered.session.end().subscribe();
  rendered.httpMock.expectOne('/api/super-admin/support-sessions/active/end').flush(null);
}

describe('refreshing visible business data on a support-context transition (issue #670)', () => {
  beforeEach(() => installBroadcastChannel());

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
    removeBroadcastChannel();
  });

  it('replaces the administrator’s own Dashboard figures when a session starts on the dashboard', async () => {
    const rendered = await render();
    expect(onScreen(rendered)).toContain(OWN.machineName);

    startSession(rendered);
    await settle(rendered);

    // Nothing of the administrator's own business may still be on screen, and the page the session
    // landed on must have asked for the viewed business's figures - presenting the session id.
    expect(onScreen(rendered)).not.toContain(OWN.machineName);
    answerDashboard(rendered, VIEWED, 'session-abc');
    expect(onScreen(rendered)).toContain(VIEWED.machineName);
    expect(onScreen(rendered)).not.toContain(OWN.machineName);
  });

  it('replaces the viewed business’s Dashboard figures when the session ends on the dashboard', async () => {
    const rendered = await render();
    startSession(rendered);
    await settle(rendered);
    answerDashboard(rendered, VIEWED, 'session-abc');
    expect(onScreen(rendered)).toContain(VIEWED.machineName);

    endSession(rendered);
    await settle(rendered);

    expect(onScreen(rendered)).not.toContain(VIEWED.machineName);
    answerDashboard(rendered, OWN, null);
    expect(onScreen(rendered)).toContain(OWN.machineName);
    expect(onScreen(rendered)).not.toContain(VIEWED.machineName);
  });

  it('still takes a tab that was on another page to the dashboard', async () => {
    const rendered = await render({ at: '/other', answer: false });
    expect(onScreen(rendered)).toContain('another page');

    startSession(rendered);
    await settle(rendered);

    expect(onScreen(rendered)).not.toContain('another page');
    answerDashboard(rendered, VIEWED, 'session-abc');
    expect(onScreen(rendered)).toContain(VIEWED.machineName);
  });

  it('replaces the page when the session expires, with no call having to be refused first', async () => {
    const rendered = await render();
    // A session with its whole lifetime in front of it, only much shorter: what the service
    // schedules is the expiry the server reported, so a session about to expire is one whose
    // reported expiry is about to arrive.
    startSession(rendered, 'session-abc', 200);
    answerDashboard(rendered, VIEWED, 'session-abc');
    expect(banner(rendered)).toContain('Viewing as Snack Co');

    await firstValueFrom(rendered.session.state$.pipe(filter((state) => state.kind === 'inactive')));
    await settle(rendered);

    expect(banner(rendered)).toBe('');
    expect(onScreen(rendered)).not.toContain(VIEWED.machineName);
    answerDashboard(rendered, OWN, null);
    expect(onScreen(rendered)).toContain(OWN.machineName);
  });

  it('replaces the page when a call in the current session is refused as ended', async () => {
    const rendered = await render();
    startSession(rendered);
    await settle(rendered);
    const inSession = pending(rendered);
    expect(inSession.length).toBeGreaterThan(0);

    // The server ended the session first - it expired there, or another tab of another browser
    // ended it - so the support view's own reads are refused with the one code that says so.
    for (const request of inSession) {
      request.flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });
    }
    await settle(rendered);

    expect(banner(rendered)).toBe('');
    expect(rendered.session.currentSessionId()).toBeNull();
    answerDashboard(rendered, OWN, null);
    expect(onScreen(rendered)).toContain(OWN.machineName);
  });

  it('applies another tab’s start to this one, and presents the id it shared', async () => {
    const rendered = await render();

    otherTab().post(sharedSession('session-from-the-other-tab'));
    await settle(rendered);

    expect(banner(rendered)).toContain('Viewing as Snack Co');
    expect(onScreen(rendered)).not.toContain(OWN.machineName);
    answerDashboard(rendered, VIEWED, 'session-from-the-other-tab');
    expect(onScreen(rendered)).toContain(VIEWED.machineName);
  });

  it('applies another tab’s end to this one', async () => {
    const rendered = await render();
    otherTab().post(sharedSession('session-from-the-other-tab'));
    await settle(rendered);
    answerDashboard(rendered, VIEWED, 'session-from-the-other-tab');

    otherTab().post({ type: 'ended', accountKey: 'member-1' });
    await settle(rendered);

    expect(banner(rendered)).toBe('');
    expect(onScreen(rendered)).not.toContain(VIEWED.machineName);
    answerDashboard(rendered, OWN, null);
    expect(onScreen(rendered)).toContain(OWN.machineName);
  });

  it.each([
    ['from another signed-in identity', sharedSession('session-from-elsewhere', 'another-administrator')],
    ['with no identity', { type: 'started', sessionId: 'session-from-elsewhere', businessName: 'Snack Co' }],
    ['of an unknown kind', { type: 'resumed', accountKey: 'member-1', sessionId: 'session-from-elsewhere' }]
  ])('ignores a message %s entirely', async (_case, message) => {
    const rendered = await render();

    otherTab().post(message);
    await settle(rendered);

    // No banner, no reload, and nothing presenting an id this tab was never granted.
    expect(banner(rendered)).toBe('');
    expect(onScreen(rendered)).toContain(OWN.machineName);
    expect(rendered.session.currentSessionId()).toBeNull();
    expect(pending(rendered)).toHaveLength(0);
  });

  it('never renders an answer the page’s previous context asked for', async () => {
    const rendered = await render({ answer: false });
    const stale = pending(rendered);
    expect(stale.length).toBeGreaterThan(0);

    startSession(rendered);
    await settle(rendered);
    // The administrator's own business answering after the session started: it was asked for in a
    // context that no longer exists, and the page it would have rendered into is gone (issue #669).
    flushStale(stale, OWN);
    answerDashboard(rendered, VIEWED, 'session-abc');

    expect(onScreen(rendered)).toContain(VIEWED.machineName);
    expect(onScreen(rendered)).not.toContain(OWN.machineName);
  });
});
