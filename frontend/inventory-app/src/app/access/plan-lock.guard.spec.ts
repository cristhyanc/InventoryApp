import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AccessService } from './access.service';
import { Entitlement } from './entitlement';
import { apiConfig, readingHttp, signedInIdentity } from './identity-cache.spec-helpers';
import { PLAN_LOCKED_PATH } from './plan-lock';
import { requirePlan } from './plan-lock.guard';

@Component({ standalone: true, template: 'sites' })
class SitesPageComponent {}

@Component({ standalone: true, template: 'needs a plan' })
class LockedPageComponent {}

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}

function response(entitlement: Entitlement | undefined): unknown {
  return {
    role: 'Owner',
    capabilities: [],
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney',
    entitlement
  };
}

function entitlement(plan: string | null, enforced = true): Entitlement {
  return { enforced, plan, source: 'BillingSubscription', endsAtUtc: null };
}

/**
 * `requirePlan` (issue #979), exercised against the real `AccessService` with only its one `GET`
 * and MSAL's two signals doubled, exactly as `requireCapability`'s own spec does: a stub that
 * re-implemented the entitlement answer would be testing itself.
 */
async function navigateTo(url: string, read: () => unknown): Promise<Router> {
  const identity = signedInIdentity();
  const access = new AccessService(readingHttp(read), apiConfig(), identity.msal, identity.broadcast);

  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        {
          path: 'sites',
          canActivate: [requirePlan('SalesProviderIntegration')],
          component: SitesPageComponent
        },
        { path: PLAN_LOCKED_PATH, component: LockedPageComponent }
      ]),
      { provide: AccessService, useValue: access }
    ]
  }).compileComponents();

  const router = TestBed.inject(Router);
  await router.navigateByUrl(url);
  return router;
}

describe('requirePlan (issue #979)', () => {
  it('opens the page for a business whose plan includes the feature', async () => {
    const router = await navigateTo('/sites', () => of(response(entitlement('Pro'))));

    expect(router.url).toBe('/sites');
  });

  it('sends a business whose plan does not include it to the needs-a-plan page, naming the URL it refused', async () => {
    const router = await navigateTo('/sites', () => of(response(entitlement('Starter'))));

    expect(router.url).toBe('/needs-plan?from=%2Fsites');
  });

  it('opens the page while enforcement is switched off (issue #966)', async () => {
    const router = await navigateTo('/sites', () => of(response(entitlement('Free', false))));

    expect(router.url).toBe('/sites');
  });

  /** The fail-open rule of `AccessService`: an entitlement nobody read locks nothing. */
  it('opens the page when the entitlement could not be read at all', async () => {
    const router = await navigateTo('/sites', () => of(response(undefined)));

    expect(router.url).toBe('/sites');
  });
});
