import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalGuard } from '@azure/msal-angular';
import { of } from 'rxjs';
import { routes } from '../../app.routes';
import { AccessService } from '../../access/access.service';
import { AccountState, AccountStateService } from '../../access/account-state.service';
import { ACCESS_NOT_ALLOWED_PATH } from '../../access/capability.guard';

const MEMBER: AccountState = { state: 'Member', onboardingEnabled: false, rejectionReason: null };

/**
 * Drives the application's real route table (`routes`), with only `MsalGuard`, `AccountStateService`
 * and `AccessService` replaced - the same approach `invite-address-bar.guard.spec.ts` uses - so the
 * redirect is exercised through the production route order and the guards `/reports/bookkeeping`
 * already declares, rather than a hand-built router config that could drift from it.
 */
function configureRouter(reportsViewGranted: boolean): void {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      { provide: MsalGuard, useValue: { canActivate: () => true } },
      { provide: AccountStateService, useValue: { state$: of({ status: 'known', accountState: MEMBER }) } },
      { provide: AccessService, useValue: { allowsAny: () => of(reportsViewGranted), allowsPlanFeature: () => of(true) } }
    ]
  });
}

/**
 * Issue #806 (parent #803): the Reporting Dashboard page is retired once Bookkeeping carries its
 * distinct capabilities, and its old address becomes a compatibility redirect instead of a second
 * page. `pathMatch: 'full'` with no `canActivate` of its own (asserted statically in
 * `app.routes.spec.ts`) means the redirect itself decides nothing about access; these tests assert
 * what that wiring actually does once the router runs it.
 */
describe('the retired Reporting Dashboard redirect (issue #806)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('sends the old address straight to Bookkeeping with no filters', async () => {
    configureRouter(true);
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/reports');

    expect(router.url).toBe('/reports/bookkeeping');
  });

  it('preserves the date, period and machine query filters across the redirect', async () => {
    configureRouter(true);
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/reports?from=2026-01-01&to=2026-01-31&period=month&machineId=5');

    expect(router.url).toBe('/reports/bookkeeping?from=2026-01-01&to=2026-01-31&period=month&machineId=5');
  });

  /**
   * The redirect is subject to the same access controls the retired page enforced (acceptance
   * criterion of issue #806): a role without `Reports.View` lands on the access-not-allowed page,
   * exactly as it would have landing on `/reports/bookkeeping` directly.
   */
  it("applies Bookkeeping's own Reports.View guard to a role that does not hold it", async () => {
    configureRouter(false);
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/reports?period=month');

    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });
});
