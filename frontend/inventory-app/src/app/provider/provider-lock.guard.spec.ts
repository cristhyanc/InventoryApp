import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, EnvironmentProviders, Provider, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { CanActivateFn, Router, RouterOutlet, provideRouter } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { AccessService } from '../access/access.service';
import { ACCESS_NOT_ALLOWED_PATH } from '../access/capability.guard';
import { apiConfig } from '../access/identity-cache.spec-helpers';
import { routes } from '../app.routes';
import { AvcoTransitionComponent } from '../components/admin/avco-transition/avco-transition.component';
import { AvcoTransitionWorkflowComponent } from '../components/admin/avco-transition/avco-transition-workflow.component';
import { HistoricalCostRecoveryComponent } from '../components/admin/historical-cost-recovery/historical-cost-recovery.component';
import { SiteCommissionAgreementsComponent } from '../components/admin/site-commission-agreements/site-commission-agreements.component';
import { BusinessTimeZoneService } from '../formatting/business-time-zone.service';
import { ProviderAvailabilityService } from './provider-availability.service';
import { nayaxConnectionStatusDouble, SpecConnectionStatus } from './provider-availability.spec-helpers';
import { ConfigService } from '../services/config.service';
import { InventoryCostTransitionService } from '../services/inventory-cost-transition.service';
import { NayaxConnectionCheckLine, NayaxConnectionService } from '../services/nayax-connection.service';
import { ProductService } from '../services/product.service';
import { ReportingService } from '../services/reporting.service';
import { SiteService } from '../services/site.service';
import { ToastService } from '../services/toast.service';
import { SupportSessionService } from '../support-session/support-session.service';
import { PROVIDER_LOCKED_PATH } from './provider-lock';
import { requireProvider } from './provider-lock.guard';

@Component({ standalone: true, template: 'machines' })
class MachinesPageComponent {}

@Component({ standalone: true, template: 'needs a provider' })
class LockedPageComponent {}

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}

@Component({ standalone: true, template: 'a report' })
class ReportPageComponent {}

@Component({ standalone: true, template: 'an admin page' })
class AdminPageComponent {}

/**
 * A host with nothing but a router outlet, for the tests that drive a **real** page component
 * through a real navigation: what the guard does to the page - construct it, with its own requests,
 * or never construct it at all - is only visible when the router renders the outcome.
 */
@Component({ standalone: true, imports: [RouterOutlet], template: '<router-outlet />' })
class RouterHostComponent {}

@Component({ standalone: true, template: 'access not allowed' })
class AccessNotAllowedPageComponent {}

/**
 * `requireProvider` (issue #729): what happens when a live provider URL is opened directly, which
 * is the half of the locked menu entry a menu cannot cover. It is presentation, not a boundary -
 * each endpoint behind these pages answers for itself whatever the router did.
 */
async function navigateTo(
  url: string,
  status: SpecConnectionStatus,
  checks?: readonly NayaxConnectionCheckLine[]
): Promise<Router> {
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'machines', canActivate: [requireProvider('machineFleet')], component: MachinesPageComponent },
        {
          path: 'machines/:id',
          canActivate: [requireProvider('machineFleet')],
          component: MachinesPageComponent
        },
        { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
      ]),
      { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble(status) }
    ]
  }).compileComponents();

  if (checks !== undefined) {
    TestBed.inject(ProviderAvailabilityService).accept(
      { status: status === 'unreadable' ? 'PendingPermissions' : status, machineChecksPending: false },
      checks
    );
  }

  const router = TestBed.inject(Router);
  await router.navigateByUrl(url);
  return router;
}

describe('requireProvider (issue #729)', () => {
  it('opens the page on a usable connection', async () => {
    const router = await navigateTo('/machines', 'Ready');

    expect(router.url).toBe('/machines');
  });

  it('sends a business with no connection to the needs-a-provider page, naming the URL it refused', async () => {
    const router = await navigateTo('/machines', 'NotConfigured');

    expect(router.url).toBe('/needs-provider?from=%2Fmachines');
  });

  it('sends credentials that are stored and known not to work there too', async () => {
    const router = await navigateTo('/machines', 'NeedsAttention');

    expect(router.url).toBe('/needs-provider?from=%2Fmachines');
  });

  /** A detail URL is refused the same way, and the URL it names is the one that was asked for. */
  it('refuses a detail URL and carries that exact URL to the page', async () => {
    const router = await navigateTo('/machines/7', 'NotConfigured');

    expect(router.url).toBe('/needs-provider?from=%2Fmachines%2F7');
  });

  it('refuses a feature Nayax has refused for lack of permission', async () => {
    const router = await navigateTo('/machines', 'PendingPermissions', [
      { check: 'Machines', status: 'Failed', failure: 'PermissionNotGranted' }
    ]);

    expect(router.url).toBe('/needs-provider?from=%2Fmachines');
  });

  it('opens the page when that feature\'s read is the one Nayax granted', async () => {
    const router = await navigateTo('/machines', 'PendingPermissions', [
      { check: 'Machines', status: 'Passed', failure: null },
      { check: 'MachineLastSales', status: 'Failed', failure: 'PermissionNotGranted' }
    ]);

    expect(router.url).toBe('/machines');
  });

  /**
   * The fail-open rule of issue #728: a status probe that failed says nothing about the connection,
   * so the page opens and its own request reports its own outcome. Locking it would hide a working
   * page over one failed probe.
   */
  it('opens the page when the connection status could not be read at all', async () => {
    const router = await navigateTo('/machines', 'unreadable');

    expect(router.url).toBe('/machines');
  });

  it('reads the shared status once, however many guarded URLs are opened', async () => {
    const connections = nayaxConnectionStatusDouble('Ready');
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path: 'machines', canActivate: [requireProvider('machineFleet')], component: MachinesPageComponent },
          {
            path: 'machines/:id',
            canActivate: [requireProvider('machineFleet')],
            component: MachinesPageComponent
          }
        ]),
        { provide: NayaxConnectionService, useValue: connections }
      ]
    }).compileComponents();

    const router = TestBed.inject(Router);
    await router.navigateByUrl('/machines');
    await router.navigateByUrl('/machines/7');

    expect(connections.status).toHaveBeenCalledTimes(1);
  });

  it('settles rather than hanging, so a guarded navigation always completes', async () => {
    const availability = new ProviderAvailabilityService(nayaxConnectionStatusDouble('NotConfigured'));

    await expect(firstValueFrom(availability.featureAvailability('machineFleet'))).resolves.toBe('notConnected');
  });
});

/**
 * The five reports over stored provider records (issue #868), which supersedes decision P5 for
 * exactly these: a business with no Nayax connection at all is sent to the connection-required
 * page, and every other state leaves the report open, because what it reports on is already stored.
 *
 * The guard each test drives is the one the **real** route table declares for that report, taken
 * from `app.routes.ts` rather than constructed here, so a report that lost its guard, or gained a
 * guard for the wrong feature, fails here instead of agreeing with a copy of itself.
 */
describe('requireProvider on the reports over stored provider records (issue #868)', () => {
  const REPORT_PATHS = [
    'reports/daily',
    'reports/transactions',
    'reports/reconciliation',
    'reports/machines',
    'reports/products'
  ];

  /**
   * The provider guard the route declares, which is the last of its `canActivate` - the fifth
   * since issue #979 added `requirePlan` before it.
   */
  function declaredProviderGuard(path: string): CanActivateFn {
    const declared = routes.find((candidate) => candidate.path === path)?.canActivate ?? [];

    expect(declared).toHaveLength(5);
    return declared[4] as CanActivateFn;
  }

  async function openReport(
    path: string,
    status: SpecConnectionStatus,
    checks?: readonly NayaxConnectionCheckLine[]
  ): Promise<Router> {
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path, canActivate: [declaredProviderGuard(path)], component: ReportPageComponent },
          { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
        ]),
        { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble(status) }
      ]
    }).compileComponents();

    if (checks !== undefined) {
      TestBed.inject(ProviderAvailabilityService).accept(
        { status: status === 'unreadable' ? 'PendingPermissions' : status, machineChecksPending: false },
        checks
      );
    }

    const router = TestBed.inject(Router);
    await router.navigateByUrl(`/${path}`);
    return router;
  }

  it.each(REPORT_PATHS)('sends /%s to the needs-a-provider page for a business with no connection', async (path) => {
    const router = await openReport(path, 'NotConfigured');

    expect(router.url).toBe(`/${PROVIDER_LOCKED_PATH}?from=${encodeURIComponent(`/${path}`)}`);
  });

  /**
   * Credentials that are stored and no longer work are still a Nayax connection: the sales this
   * report reads were recorded through it and are stored, so the report **opens**. This is the one
   * state in which `providerRecords` and a live read deliberately disagree.
   */
  it.each(REPORT_PATHS)('opens /%s for credentials that are stored but not usable', async (path) => {
    const router = await openReport(path, 'NeedsAttention');

    expect(router.url).toBe(`/${path}`);
  });

  it.each(REPORT_PATHS)('opens /%s on a usable connection whose fleet read Nayax refused', async (path) => {
    const router = await openReport(path, 'PendingPermissions', [
      { check: 'Machines', status: 'Failed', failure: 'PermissionNotGranted' }
    ]);

    expect(router.url).toBe(`/${path}`);
  });

  it.each(REPORT_PATHS)('opens /%s on a usable connection with nothing refused', async (path) => {
    const router = await openReport(path, 'Ready');

    expect(router.url).toBe(`/${path}`);
  });

  /** The fail-open rule of issue #728 again: an unread status is not a refusal. */
  it.each(REPORT_PATHS)('opens /%s when the connection status could not be read', async (path) => {
    const router = await openReport(path, 'unreadable');

    expect(router.url).toBe(`/${path}`);
  });

  /**
   * A refusal retained for one of the live reads must not reach these reports either: the
   * `machineSales` and `productCatalogue` refusals are the two a reader might expect to lock the
   * transaction and product reports, and neither does.
   */
  it('opens the transaction and product reports when Nayax refused the sales and catalogue reads', async () => {
    const transactions = await openReport('reports/transactions', 'Ready', [
      { check: 'MachineLastSales', status: 'Failed', failure: 'PermissionNotGranted' },
      { check: 'ProductCatalogue', status: 'Failed', failure: 'PermissionNotGranted' }
    ]);

    expect(transactions.url).toBe('/reports/transactions');
  });

  /**
   * Bookkeeping and GST carry no provider guard at all, which is what keeps a business with no
   * Nayax connection with two reports it can still open. They do carry `requirePlan` since issue
   * #979 (`access/plan-lock.guard.spec.ts` drives that guard itself), so the length here is four
   * rather than three.
   */
  it.each(['reports/bookkeeping', 'reports/gst'])('leaves /%s with no provider guard to run', (path) => {
    expect(routes.find((candidate) => candidate.path === path)?.canActivate).toHaveLength(4);
  });

  /**
   * The capability guard runs first, with all three of the route's own guards in the order it
   * declares them: a member without `Reports.View` reaches the access-not-allowed page, never an
   * invitation to upgrade or connect a provider they still could not use. The three guards are
   * taken from the real route, so this is that route's declared order rather than an order this
   * spec chose. `allowsPlanFeature` is stubbed to `true` so the plan guard itself refuses nothing -
   * what is asserted is that the capability refusal decides the outcome before either of the
   * guards after it could.
   */
  it.each(REPORT_PATHS)('refuses /%s on the capability before the plan or provider lock is consulted', async (path) => {
    const declared = routes.find((candidate) => candidate.path === path)?.canActivate ?? [];

    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path, canActivate: declared.slice(2) as CanActivateFn[], component: ReportPageComponent },
          { path: ACCESS_NOT_ALLOWED_PATH, component: AccessNotAllowedPageComponent },
          { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
        ]),
        { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble('NotConfigured') },
        { provide: AccessService, useValue: { allowsAny: () => of(false), allowsPlanFeature: () => of(true) } }
      ]
    }).compileComponents();

    const router = TestBed.inject(Router);
    await router.navigateByUrl(`/${path}`);

    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });
});

/**
 * The two Admin destinations of issue #869: Site Commission Agreements, whose site picker is the
 * live fleet read (`GET /api/sites`), and the Inventory AVCO Transition Baseline, whose preview
 * reads the machines and then their products. Both declare `machineFleet`, so they behave exactly
 * as `/sites` and the site commission report do rather than as the five stored-record reports of
 * issue #868: credentials that stopped working lock them too, because what is behind them is read
 * from Nayax as the page opens and there is nothing stored to fall back on.
 *
 * Every guard driven here is the one the **real** route table declares for that page, taken from
 * `app.routes.ts` rather than constructed in this spec, so a page that lost its guard, or gained one
 * for the wrong feature, fails here instead of agreeing with a copy of itself.
 */
describe('requireProvider on the two locked Admin pages (issue #869)', () => {
  const ADMIN_PATHS = ['admin/site-commission-agreements', 'admin/avco-transition'];

  function declaredGuards(path: string): CanActivateFn[] {
    return ((routes.find((candidate) => candidate.path === path)?.canActivate ?? []) as CanActivateFn[]).slice();
  }

  /** The provider guard the route declares, which is the fourth and last of its `canActivate`. */
  function declaredProviderGuard(path: string): CanActivateFn {
    const guards = declaredGuards(path);

    expect(guards).toHaveLength(4);
    return guards[3];
  }

  async function openAdminPage(
    path: string,
    status: SpecConnectionStatus,
    checks?: readonly NayaxConnectionCheckLine[]
  ): Promise<Router> {
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path, canActivate: [declaredProviderGuard(path)], component: AdminPageComponent },
          { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
        ]),
        { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble(status) }
      ]
    }).compileComponents();

    if (checks !== undefined) {
      TestBed.inject(ProviderAvailabilityService).accept(
        { status: status === 'unreadable' ? 'PendingPermissions' : status, machineChecksPending: false },
        checks
      );
    }

    const router = TestBed.inject(Router);
    await router.navigateByUrl(`/${path}`);
    return router;
  }

  function lockedUrl(path: string): string {
    return `/${PROVIDER_LOCKED_PATH}?from=${encodeURIComponent(`/${path}`)}`;
  }

  it.each(ADMIN_PATHS)('sends /%s to the needs-a-provider page for a business with no connection', async (path) => {
    const router = await openAdminPage(path, 'NotConfigured');

    expect(router.url).toBe(lockedUrl(path));
  });

  it.each(ADMIN_PATHS)('sends /%s there too for credentials that are stored and stopped working', async (path) => {
    const router = await openAdminPage(path, 'NeedsAttention');

    expect(router.url).toBe(lockedUrl(path));
  });

  it.each(ADMIN_PATHS)('refuses /%s when Nayax refused the fleet read for lack of permission', async (path) => {
    const router = await openAdminPage(path, 'PendingPermissions', [
      { check: 'Machines', status: 'Failed', failure: 'PermissionNotGranted' }
    ]);

    expect(router.url).toBe(lockedUrl(path));
  });

  it.each(ADMIN_PATHS)('opens /%s on a usable connection', async (path) => {
    const router = await openAdminPage(path, 'Ready');

    expect(router.url).toBe(`/${path}`);
  });

  it.each(ADMIN_PATHS)('opens /%s when the fleet read is the one Nayax granted', async (path) => {
    const router = await openAdminPage(path, 'PendingPermissions', [
      { check: 'Machines', status: 'Passed', failure: null },
      { check: 'MachineLastSales', status: 'Failed', failure: 'PermissionNotGranted' }
    ]);

    expect(router.url).toBe(`/${path}`);
  });

  /** The fail-open rule of issue #728: an unread status is not a refusal. */
  it.each(ADMIN_PATHS)('opens /%s when the connection status could not be read', async (path) => {
    const router = await openAdminPage(path, 'unreadable');

    expect(router.url).toBe(`/${path}`);
  });

  /**
   * The capability guard runs first, in the order the real route declares it: a member whose role
   * does not include the page reaches the access-not-allowed page, never an invitation to connect a
   * provider they still could not use.
   */
  it.each(ADMIN_PATHS)('refuses /%s on the capability before the provider lock is consulted', async (path) => {
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path, canActivate: declaredGuards(path).slice(2), component: AdminPageComponent },
          { path: ACCESS_NOT_ALLOWED_PATH, component: AccessNotAllowedPageComponent },
          { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
        ]),
        { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble('NotConfigured') },
        { provide: AccessService, useValue: { allowsAny: () => of(false) } }
      ]
    }).compileComponents();

    const router = TestBed.inject(Router);
    await router.navigateByUrl(`/${path}`);

    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });

  /**
   * Nayax Historical Cost Recovery keeps no provider guard at all, by the user's decision of 24
   * October 2026: it makes no Nayax call - it reads the Nayax cost already stored on imported sales
   * - so a disconnected business with imported history can still recover those costs. Its own
   * declared guards past the authentication one are driven here, with no connection at all.
   */
  it('opens Nayax Historical Cost Recovery for a business with no Nayax connection', async () => {
    const path = 'admin/historical-cost-recovery';
    const guards = declaredGuards(path);
    expect(guards).toHaveLength(3);
    const backfillNayaxSaleCosts = jest.fn();

    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path, canActivate: guards.slice(2), component: HistoricalCostRecoveryComponent },
          { path: ACCESS_NOT_ALLOWED_PATH, component: AccessNotAllowedPageComponent },
          { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
        ]),
        { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble('NotConfigured') },
        { provide: AccessService, useValue: { allowsAny: () => of(true) } },
        { provide: ReportingService, useValue: { backfillNayaxSaleCosts } },
        { provide: SupportSessionService, useValue: { active$: of(false) } }
      ]
    }).compileComponents();

    const router = TestBed.inject(Router);
    await router.navigateByUrl(`/${path}`);

    expect(router.url).toBe(`/${path}`);
    // And its workflow asks for nothing until a person runs it, so opening it makes no call at all.
    expect(backfillNayaxSaleCosts).not.toHaveBeenCalled();
  });
});

/**
 * The same two Admin pages, driven over their **real** page components so what the guard does to the
 * page itself is visible: the component is either constructed, with its own requests and its own
 * read-only support-session view, or never constructed at all.
 *
 * **A support session is not an input to this decision** (issue #869). The guard reads one thing,
 * `GET /api/settings/nayax-connection/status`, which in a session is the *supported* business's
 * stored status - that endpoint resolves no credential (`GetNayaxConnectionStatus`), and a session
 * resolves none at all (AGENTS.md § Nayax integration). So an active session neither adds a lock nor
 * bypasses one: a connected business opens the page and the page shows the read-only view it already
 * had (issues #659 and #661), and a business that is not connected is refused exactly as it is for a
 * member.
 */
describe('the two locked Admin pages over their real components (issue #869)', () => {
  /** One page under test: its route, its component, the doubles it needs and what it must not call. */
  interface AdminPage {
    readonly path: string;
    readonly component: Type<unknown>;
    readonly providers: readonly (Provider | EnvironmentProviders)[];
    /** Every request the page makes when it opens, so a refused navigation can be shown to make none. */
    readonly requestsOnOpen: readonly jest.Mock[];
    /** Every write or preview the page would make, which a support session must leave unused. */
    readonly writes: readonly jest.Mock[];
    /** The page's own read-only view: the control it disables while a session is active. */
    readonly disabledControl: (fixture: ComponentFixture<unknown>) => HTMLButtonElement | undefined;
  }

  function toast(): Provider {
    return {
      provide: ToastService,
      useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() }
    };
  }

  function button(fixture: ComponentFixture<unknown>, text: string): HTMLButtonElement | undefined {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((candidate) =>
      candidate.textContent?.includes(text)
    );
  }

  /** Site Commission Agreements: the agreement list, the site picker's list, and the one write. */
  function agreementsPage(sessionActive: boolean): AdminPage {
    const siteCommissionAgreements = jest.fn(() => of([]));
    const saveSiteCommissionAgreement = jest.fn(() => of({}));
    const getList = jest.fn(() => of([]));

    return {
      path: 'admin/site-commission-agreements',
      component: SiteCommissionAgreementsComponent,
      requestsOnOpen: [siteCommissionAgreements, getList],
      writes: [saveSiteCommissionAgreement],
      providers: [
        { provide: ReportingService, useValue: { siteCommissionAgreements, saveSiteCommissionAgreement } },
        { provide: SiteService, useValue: { getList } },
        toast(),
        { provide: SupportSessionService, useValue: { active$: of(sessionActive) } }
      ],
      disabledControl: (fixture) => button(fixture, 'Save agreement')
    };
  }

  /** AVCO Transition: the product list the selector needs, and the four preview/apply calls. */
  function avcoPage(sessionActive: boolean): AdminPage {
    const getAll = jest.fn(() => of([{ id: 1, name: 'Coke', averageUnitCost: 1.5 }]));
    const transition = {
      preview: jest.fn(),
      apply: jest.fn(),
      previewAll: jest.fn(),
      applyAll: jest.fn()
    };

    return {
      path: 'admin/avco-transition',
      component: AvcoTransitionComponent,
      requestsOnOpen: [getAll],
      writes: [transition.preview, transition.apply, transition.previewAll, transition.applyAll],
      providers: [
        { provide: ProductService, useValue: { getAll } },
        { provide: InventoryCostTransitionService, useValue: transition },
        toast(),
        { provide: SupportSessionService, useValue: { active$: of(sessionActive) } }
      ],
      disabledControl: (fixture) => {
        // The preview button is also disabled until a product is chosen, so the all-products mode
        // is selected first: what is asserted is the session's own effect and nothing else.
        const workflow = fixture.debugElement.query(By.directive(AvcoTransitionWorkflowComponent))
          ?.componentInstance as AvcoTransitionWorkflowComponent;
        workflow.transitionAllProducts = true;
        fixture.detectChanges();
        return button(fixture, 'Preview all product baselines');
      }
    };
  }

  const PAGES = [
    ['Site Commission Agreements', agreementsPage],
    ['AVCO Transition', avcoPage]
  ] as const;

  /** The provider guard the real route declares, which is the fourth and last of its `canActivate`. */
  function declaredProviderGuard(path: string): CanActivateFn {
    const guards = (routes.find((candidate) => candidate.path === path)?.canActivate ?? []) as CanActivateFn[];

    expect(guards).toHaveLength(4);
    return guards[3];
  }

  async function navigate(page: AdminPage, status: SpecConnectionStatus): Promise<ComponentFixture<unknown>> {
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path: page.path, canActivate: [declaredProviderGuard(page.path)], component: page.component },
          { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
        ]),
        { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble(status) },
        ...page.providers
      ]
    }).compileComponents();

    // The business time zone the shell publishes at sign-in (issue #499); the AVCO page renders
    // cutoff instants in it.
    TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

    const fixture = TestBed.createComponent(RouterHostComponent);
    fixture.detectChanges();
    await TestBed.inject(Router).navigateByUrl(`/${page.path}`);
    fixture.detectChanges();
    return fixture;
  }

  /**
   * A support session on a **connected** business opens the page, and the page is the read-only one
   * it already was: the guard must not redirect a session to `/needs-provider`, because the business
   * being supported does have a Nayax connection and the session changes nothing about that.
   */
  it.each(PAGES)('opens %s in a support session on a connected business, read-only', async (_label, build) => {
    const page = build(true);
    const fixture = await navigate(page, 'Ready');

    expect(TestBed.inject(Router).url).toBe(`/${page.path}`);
    expect(page.disabledControl(fixture)?.disabled).toBe(true);
    for (const write of page.writes) {
      expect(write).not.toHaveBeenCalled();
    }
  });

  /**
   * A support session on a business that is **not** connected is refused exactly as a member is.
   * A session grants `SiteCommissions.Manage` and `Data.Repair`, so without this the administrator
   * would reach a page whose every live read fails.
   */
  it.each(PAGES)('refuses %s in a support session on a business with no connection', async (_label, build) => {
    const page = build(true);
    await navigate(page, 'NotConfigured');

    expect(TestBed.inject(Router).url).toBe(`/${PROVIDER_LOCKED_PATH}?from=${encodeURIComponent(`/${page.path}`)}`);
  });

  it.each(PAGES)('refuses %s in a support session on credentials that stopped working', async (_label, build) => {
    const page = build(true);
    await navigate(page, 'NeedsAttention');

    expect(TestBed.inject(Router).url).toBe(`/${PROVIDER_LOCKED_PATH}?from=${encodeURIComponent(`/${page.path}`)}`);
  });

  /**
   * The connection-required view never loads the page component, so none of the page's own requests
   * is sent: no site list, no agreement list, no transition preview, apply or baseline.
   */
  it.each(PAGES)('loads no part of %s, and none of its requests, when refused', async (_label, build) => {
    const page = build(false);
    await navigate(page, 'NotConfigured');

    for (const request of [...page.requestsOnOpen, ...page.writes]) {
      expect(request).not.toHaveBeenCalled();
    }
  });

  /** With a connection, the page component is constructed and its own reads do happen as before. */
  it.each(PAGES)('loads %s and its own reads on a usable connection', async (_label, build) => {
    const page = build(false);
    await navigate(page, 'Ready');

    for (const request of page.requestsOnOpen) {
      expect(request).toHaveBeenCalled();
    }
  });

  /**
   * A `403 read_only_support_session` from a page's own request is not a provider refusal
   * (`noteRefusal` returns `null` for it), so it can never turn the page into the connection-required
   * view: the page still opens on the next navigation.
   */
  it.each(PAGES)('never turns a read-only support refusal on %s into a provider lock', async (_label, build) => {
    const page = build(true);
    await navigate(page, 'Ready');
    const availability = TestBed.inject(ProviderAvailabilityService);

    const noted = availability.noteRefusal(
      new HttpErrorResponse({ status: 403, error: { code: 'read_only_support_session' } }),
      'machineFleet'
    );

    expect(noted).toBeNull();
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/');
    await router.navigateByUrl(`/${page.path}`);
    expect(router.url).toBe(`/${page.path}`);
  });

  /**
   * And the guard path resolves no Nayax credentials in a session: the only request it makes is the
   * status read, which reports the stored connection record alone.
   */
  it('reads only the connection status endpoint on the guard path', async () => {
    const page = agreementsPage(true);

    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path: page.path, canActivate: [declaredProviderGuard(page.path)], component: AdminPageComponent },
          { path: PROVIDER_LOCKED_PATH, component: LockedPageComponent }
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfigService, useValue: apiConfig() }
      ]
    }).compileComponents();

    const router = TestBed.inject(Router);
    const navigation = router.navigateByUrl(`/${page.path}`);
    const httpMock = TestBed.inject(HttpTestingController);
    // The router runs its guards asynchronously, so the status read is in flight only after a tick.
    await new Promise((resolve) => setTimeout(resolve, 0));

    const status = httpMock.expectOne('/api/settings/nayax-connection/status');
    expect(status.request.method).toBe('GET');
    status.flush({ status: 'Ready', machineChecksPending: false });
    await navigation;

    httpMock.verify();
    expect(router.url).toBe(`/${page.path}`);
  });
});
