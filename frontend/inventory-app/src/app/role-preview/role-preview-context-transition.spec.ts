import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { NEVER, Subject, of } from 'rxjs';
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
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { SuperAdminBusinessesService } from '../services/super-admin-businesses.service';
import { supportSessionMessageInterceptor } from '../support-session/support-session-message.interceptor';
import { supportSessionInterceptor } from '../support-session/support-session.interceptor';
import { SupportSessionService, SupportSessionState } from '../support-session/support-session.service';
import { ROLE_PREVIEW_HEADER } from './role-preview';
import { rolePreviewInterceptor } from './role-preview.interceptor';
import { RolePreviewService } from './role-preview.service';

const SYNC = '/api/nayax-sales-sync';
const MACHINES = '/api/machines';
const SITES = '/api/sites';
const LOW_STOCK = '/api/products/alerts/low-stock';
const SUMMARY = '/api/dashboard/summary';

/** One answer's Dashboard, as the figures a test can recognise on screen. */
interface Figures {
  readonly machineName: string;
  readonly siteName: string;
  readonly salesThisWeek: number;
}

/** What the administrator's own capabilities are answered with. */
const OWN: Figures = { machineName: 'Own Machine', siteName: 'Own Site', salesThisWeek: 100 };

/** What the API answers the same page with once it is asked as the previewed role. */
const PREVIEWED: Figures = { machineName: 'Operator Machine', siteName: 'Operator Site', salesThisWeek: 0 };

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
    asOfUtc: '2026-10-16T09:30:00Z',
    businessDate: '2026-10-16T00:00:00Z',
    salesThisWeek: {
      sales: figures.salesThisWeek,
      transactionCount: 1,
      period: {
        startUtc: '2026-10-13T14:00:00Z',
        endUtc: '2026-10-16T09:30:00Z',
        firstBusinessDate: '2026-10-13T00:00:00Z',
        lastBusinessDate: '2026-10-16T00:00:00Z'
      },
      comparisonPeriod: {
        startUtc: '2026-10-06T14:00:00Z',
        endUtc: '2026-10-09T09:30:00Z',
        firstBusinessDate: '2026-10-06T00:00:00Z',
        lastBusinessDate: '2026-10-09T00:00:00Z'
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

interface Rendered {
  readonly fixture: ComponentFixture<AppComponent>;
  readonly host: HTMLElement;
  readonly preview: RolePreviewService;
  readonly httpMock: HttpTestingController;
  readonly accessRefresh: jest.Mock;
  readonly businessReset: jest.Mock;
  readonly router: Router;
}

/**
 * The real application shell over the **real** `RolePreviewService`, the real router, the real
 * production-order interceptor chain and the real `DashboardComponent`, with the API answered by
 * `HttpTestingController` (issue #721).
 *
 * It follows `support-session/support-session-context-transition.spec.ts` (issue #670), which is
 * the pattern a preview transition deliberately reuses: what has to be proved is what is **on
 * screen** after the transition and what the page asked for, and a router spy cannot see a
 * component the router decided to reuse. What is doubled is everything the preview is not about -
 * MSAL, the two member caches, the cached business, the platform-admin probe, the Super Admin
 * badge and the support session, whose own transitions have their own spec.
 */
async function render(options: { at?: string; answer?: boolean } = {}): Promise<Rendered> {
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
  const accessRefresh = jest.fn(() => of(access));
  const businessReset = jest.fn();
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
        withInterceptors([
          ...FORBIDDEN_INTERCEPTORS,
          supportSessionMessageInterceptor,
          rolePreviewInterceptor,
          supportSessionInterceptor
        ])
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
          refresh: accessRefresh,
          refreshUnlessReading: jest.fn()
        }
      },
      {
        provide: BusinessService,
        useValue: {
          load: () => of({ name: 'Vending Co', timeZoneId: 'Australia/Sydney' }),
          reset: businessReset
        }
      },
      // The administrator this spec is about: the `Super Admin` group is offered, so the preview
      // taking it away is visible.
      { provide: PlatformDiagnosticsAccessService, useValue: { granted$: of(true), isGranted: () => of(true) } },
      { provide: SuperAdminBusinessesService, useValue: { pendingCount: () => of(0), changed$: NEVER } },
      {
        // No support session anywhere in this spec: a preview is its own transition, and what a
        // support session does to one is `role-preview.service.spec.ts`'s. The request-context
        // methods are the ones `supportSessionInterceptor` asks of every request it lets through
        // (issue #669), answered here as "ordinary context, still current".
        provide: SupportSessionService,
        useValue: {
          state$: of<SupportSessionState>({ kind: 'inactive' }),
          active$: of(false),
          contextChanging$: of(false),
          contextChanged$: new Subject<void>(),
          captureRequestContext: () => ({ sessionId: null, generation: 0 }),
          isCurrentContext: () => true,
          handleSessionEndedSignal: jest.fn(),
          end: () => of(undefined),
          endBeforeSignOut: () => of(undefined)
        }
      }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AppComponent);
  fixture.detectChanges();
  const router = TestBed.inject(Router);
  await router.navigateByUrl(options.at ?? '/');
  fixture.detectChanges();

  const rendered: Rendered = {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    preview: TestBed.inject(RolePreviewService),
    httpMock: TestBed.inject(HttpTestingController),
    accessRefresh,
    businessReset,
    router
  };

  if (options.answer ?? true) {
    answerDashboard(rendered, OWN, null);
  }
  return rendered;
}

/** Everything the routed Dashboard asked for, answered as one set of figures. */
function answerDashboard(rendered: Rendered, figures: Figures, expectedRole: string | null): TestRequest[] {
  const answered: TestRequest[] = [];
  const answer = (url: string, body: Machine[] | Site[] | DashboardSummary | null): void => {
    for (const request of rendered.httpMock.match((candidate) => candidate.url === url)) {
      answered.push(request);
      request.flush(body);
    }
  };

  // The summary first: since issue #727 the Dashboard asks it whether the business has a usable
  // provider and only then synchronizes sales and loads Sites and Machines, so those three
  // requests do not exist until this one is answered.
  answer(LOW_STOCK, []);
  answer(SUMMARY, summary(figures));
  answer(SYNC, null);
  answer(MACHINES, [machine(figures)]);
  answer(SITES, [site(figures)]);
  rendered.fixture.detectChanges();

  expect(answered.length).toBeGreaterThan(0);
  for (const request of answered) {
    expect(request.request.headers.get(ROLE_PREVIEW_HEADER)).toBe(expectedRole);
  }
  return answered;
}

/** The business figures actually on screen: the machine and site names the Dashboard rendered. */
function onScreen(rendered: Rendered): string {
  return (rendered.host.querySelector('main') as HTMLElement).textContent ?? '';
}

/** The role-preview banner, which the shell renders above the page while a preview is active. */
function banner(rendered: Rendered): string {
  return rendered.host.querySelector('.role-preview-banner')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

/** The sidebar's own text, which is where the `Super Admin` group is. */
function sidebar(rendered: Rendered): string {
  return rendered.host.querySelector('nav[aria-label="Primary"]')?.textContent ?? '';
}

/** Lets the transition's navigation settle and renders whatever it left behind. */
async function settle(rendered: Rendered): Promise<void> {
  await rendered.fixture.whenStable();
  rendered.fixture.detectChanges();
}

/** Starts a preview the way the user menu's **View as** entry does. */
function startPreview(rendered: Rendered): void {
  const entry = Array.from(rendered.host.querySelectorAll<HTMLButtonElement>('button')).find(
    (button) => button.textContent?.trim() === 'Operator'
  );
  expect(entry).toBeDefined();
  entry!.click();
  rendered.fixture.detectChanges();
}

/** Opens the signed-in user menu, which is where that entry is. */
function openUserMenu(rendered: Rendered): void {
  const trigger = rendered.host.querySelector<HTMLButtonElement>('button[aria-haspopup="true"]');
  expect(trigger).not.toBeNull();
  trigger!.click();
  rendered.fixture.detectChanges();
}

describe('starting and exiting a role preview (issue #721)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('replaces the page’s figures with the previewed role’s, on the dashboard it was already on', async () => {
    const rendered = await render();
    expect(onScreen(rendered)).toContain(OWN.machineName);

    openUserMenu(rendered);
    startPreview(rendered);
    await settle(rendered);

    // Nothing the administrator's own capabilities answered may still be on screen, and the page
    // the preview landed on must have asked again, presenting the role.
    expect(onScreen(rendered)).not.toContain(OWN.machineName);
    expect(rendered.businessReset).toHaveBeenCalled();
    expect(rendered.accessRefresh).toHaveBeenCalledTimes(1);
    answerDashboard(rendered, PREVIEWED, 'Operator');
    expect(onScreen(rendered)).toContain(PREVIEWED.machineName);
    expect(onScreen(rendered)).not.toContain(OWN.machineName);
  });

  it('shows the banner, hides the Super Admin group, and closes the menu it was started from', async () => {
    const rendered = await render();
    expect(sidebar(rendered)).toContain('Super Admin');

    openUserMenu(rendered);
    expect((rendered.host.querySelector('#user-menu-panel') as HTMLElement).textContent).toContain('View as');
    startPreview(rendered);
    await settle(rendered);
    answerDashboard(rendered, PREVIEWED, 'Operator');

    expect(banner(rendered)).toContain('Viewing as Operator (preview, read-only)');
    expect(sidebar(rendered)).not.toContain('Super Admin');
    expect(rendered.host.querySelector('#user-menu-panel')).toBeNull();
  });

  it('puts everything back when the banner’s Exit preview is pressed', async () => {
    const rendered = await render();
    openUserMenu(rendered);
    startPreview(rendered);
    await settle(rendered);
    answerDashboard(rendered, PREVIEWED, 'Operator');

    const exit = Array.from(rendered.host.querySelectorAll<HTMLButtonElement>('.role-preview-banner button')).find(
      (button) => button.textContent?.trim() === 'Exit preview'
    );
    expect(exit).toBeDefined();
    exit!.click();
    rendered.fixture.detectChanges();
    await settle(rendered);

    expect(banner(rendered)).toBe('');
    expect(onScreen(rendered)).not.toContain(PREVIEWED.machineName);
    expect(rendered.accessRefresh).toHaveBeenCalledTimes(2);
    answerDashboard(rendered, OWN, null);
    expect(onScreen(rendered)).toContain(OWN.machineName);
    expect(sidebar(rendered)).toContain('Super Admin');
  });

  it('takes a tab that was on another page to the dashboard', async () => {
    const rendered = await render({ at: '/other', answer: false });
    expect(onScreen(rendered)).toContain('another page');

    rendered.preview.start('Manager');
    await settle(rendered);

    expect(onScreen(rendered)).not.toContain('another page');
    expect(rendered.router.url).toBe('/');
    answerDashboard(rendered, PREVIEWED, 'Manager');
    expect(onScreen(rendered)).toContain(PREVIEWED.machineName);
  });

  it('presents no header at all once the preview has ended', async () => {
    const rendered = await render();
    rendered.preview.start('Operator');
    await settle(rendered);
    answerDashboard(rendered, PREVIEWED, 'Operator');

    rendered.preview.exit();
    await settle(rendered);

    answerDashboard(rendered, OWN, null);
    expect(onScreen(rendered)).toContain(OWN.machineName);
  });
});
