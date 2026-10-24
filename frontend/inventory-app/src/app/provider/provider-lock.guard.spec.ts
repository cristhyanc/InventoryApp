import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanActivateFn, Router, provideRouter } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { AccessService } from '../access/access.service';
import { ACCESS_NOT_ALLOWED_PATH } from '../access/capability.guard';
import { routes } from '../app.routes';
import { ProviderAvailabilityService } from './provider-availability.service';
import { nayaxConnectionStatusDouble, SpecConnectionStatus } from './provider-availability.spec-helpers';
import { NayaxConnectionCheckLine, NayaxConnectionService } from '../services/nayax-connection.service';
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

  /** The provider guard the route declares, which is the fourth and last of its `canActivate`. */
  function declaredProviderGuard(path: string): CanActivateFn {
    const declared = routes.find((candidate) => candidate.path === path)?.canActivate ?? [];

    expect(declared).toHaveLength(4);
    return declared[3] as CanActivateFn;
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
   * Nayax connection with two reports it can still open.
   */
  it.each(['reports/bookkeeping', 'reports/gst'])('leaves /%s with no provider guard to run', (path) => {
    expect(routes.find((candidate) => candidate.path === path)?.canActivate).toHaveLength(3);
  });

  /**
   * The capability guard runs first, with both of the route's own guards in the order it declares
   * them: a member without `Reports.View` reaches the access-not-allowed page, never an invitation
   * to connect a provider they still could not use. The two guards are taken from the real route,
   * so this is that route's declared order rather than an order this spec chose.
   */
  it.each(REPORT_PATHS)('refuses /%s on the capability before the provider lock is consulted', async (path) => {
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
        { provide: AccessService, useValue: { allowsAny: () => of(false) } }
      ]
    }).compileComponents();

    const router = TestBed.inject(Router);
    await router.navigateByUrl(`/${path}`);

    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });
});
