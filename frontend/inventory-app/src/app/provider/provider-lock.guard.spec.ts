import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
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
