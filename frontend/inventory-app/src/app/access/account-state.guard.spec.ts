import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, InteractionStatus } from '@azure/msal-browser';
import { EMPTY, Subject, of, throwError } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { AccountState, AccountStateName, AccountStateService } from './account-state.service';
import { ACCOUNT_STATE_ROUTE, accountStateGuard } from './account-state.guard';

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}

@Component({ standalone: true, template: 'welcome' })
class WelcomePageComponent {}

@Component({ standalone: true, template: 'pending' })
class PendingPageComponent {}

@Component({ standalone: true, template: 'rejected' })
class RejectedPageComponent {}

@Component({ standalone: true, template: 'unavailable' })
class UnavailablePageComponent {}

const ACCOUNT = { homeAccountId: 'member-1' } as unknown as AccountInfo;

function response(state: AccountStateName, onboardingEnabled = false): unknown {
  return { state, onboardingEnabled, rejectionReason: null };
}

/**
 * The guard is exercised against the real `AccountStateService`, with only its one `GET` doubled,
 * for the same reason `capability.guard.spec.ts` doubles only `AccessService`'s read: a stub that
 * re-implemented the account-state answer would be testing itself.
 */
function accountStateService(read: () => unknown): AccountStateService {
  return new AccountStateService(
    { get: read } as unknown as HttpClient,
    { apiBaseUrl: '/api' } as unknown as ConfigService,
    {
      instance: { getActiveAccount: () => ACCOUNT, getAllAccounts: () => [ACCOUNT] }
    } as unknown as MsalService,
    { inProgress$: of(InteractionStatus.None), msalSubject$: EMPTY } as unknown as MsalBroadcastService
  );
}

async function withGuard(read: () => unknown): Promise<{ router: Router; accountState: AccountStateService }> {
  const accountState = accountStateService(read);

  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent, canActivate: [accountStateGuard] },
        { path: 'welcome', component: WelcomePageComponent },
        { path: 'application-pending', component: PendingPageComponent },
        { path: 'application-rejected', component: RejectedPageComponent },
        { path: 'access-unavailable', component: UnavailablePageComponent }
      ]),
      { provide: AccountStateService, useValue: accountState }
    ]
  }).compileComponents();

  return { router: TestBed.inject(Router), accountState };
}

/**
 * Routing for each account state (issue #529's account-state routing contract, agreed under
 * #328). The guard is presentation only: the member endpoints behind every business route stay
 * refused by `BusinessScopeMiddleware` whatever it decides.
 */
describe('accountStateGuard (issue #529)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lets a Member through to the route they asked for', async () => {
    const { router } = await withGuard(() => of(response('Member')));

    await router.navigateByUrl('/');

    expect(router.url).toBe('/');
  });

  it.each(Object.entries(ACCOUNT_STATE_ROUTE) as [AccountStateName, string][])(
    'sends %s to /%s',
    async (state, path) => {
      const { router } = await withGuard(() => of(response(state)));

      await router.navigateByUrl('/');

      expect(router.url).toBe(`/${path}`);
    }
  );

  /**
   * The fail-open rule `AccountStateService` documents: the membership check on every business
   * endpoint remains the real boundary, so a state that could not be read must not lock a member
   * out of their own page over a failed probe.
   */
  it('lets the route through when the account state could not be read', async () => {
    const { router } = await withGuard(() => throwError(() => new HttpErrorResponse({ status: 500 })));

    await router.navigateByUrl('/');

    expect(router.url).toBe('/');
  });

  it('waits for the account state rather than deciding from an unread one', async () => {
    const pending = new Subject<unknown>();
    const { router, accountState } = await withGuard(() => pending);
    accountState.state$.subscribe();

    const navigation = router.navigateByUrl('/');
    pending.next(response('NoMembership'));
    pending.complete();

    await navigation;
    expect(router.url).toBe('/welcome');
  });

  it('reads the account state once for several guarded navigations', async () => {
    const read = jest.fn(() => of(response('Member')));
    const { router } = await withGuard(read);

    await router.navigateByUrl('/');
    await router.navigateByUrl('/');

    expect(read).toHaveBeenCalledTimes(1);
  });
});

describe('ACCOUNT_STATE_ROUTE', () => {
  it('maps every non-Member state to a route, and only those', () => {
    const mapped = Object.keys(ACCOUNT_STATE_ROUTE);
    expect(mapped).not.toContain('Member');
    expect(mapped.sort()).toEqual(
      [
        'NoMembership',
        'ApplicationPending',
        'ApplicationRejected',
        'MembershipRevoked',
        'BusinessDeactivated',
        'MembershipAmbiguous'
      ].sort()
    );
  });
});

/** Pins the shape `AccountState` exposes, so a renamed field is caught here rather than at runtime. */
describe('AccountState shape', () => {
  it('describes the state, onboarding eligibility and rejection reason, and nothing about a business', () => {
    const accountState: AccountState = { state: 'NoMembership', onboardingEnabled: true, rejectionReason: null };

    expect(Object.keys(accountState).sort()).toEqual(['onboardingEnabled', 'rejectionReason', 'state']);
  });
});
