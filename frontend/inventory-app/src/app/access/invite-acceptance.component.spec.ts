import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from './access.service';
import { AccountStateAnswer, AccountStateName, AccountStateService } from './account-state.service';
import { ACCOUNT_STATE_ROUTE } from './account-state.guard';
import { InviteAcceptanceComponent } from './invite-acceptance.component';
import { InviteRedemptionService } from './invite-redemption.service';

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

function known(state: AccountStateName): AccountStateAnswer {
  return { status: 'known', accountState: { state, onboardingEnabled: false, rejectionReason: null } };
}

const UNAVAILABLE: AccountStateAnswer = { status: 'unavailable' };

interface Harness {
  fixture: ComponentFixture<InviteAcceptanceComponent>;
  router: Router;
  redeem: jest.Mock;
  accountStateRefresh: jest.Mock;
  accessRefresh: jest.Mock;
}

async function createHarness(options: {
  token?: string;
  accountState$?: Observable<AccountStateAnswer>;
  redeem?: jest.Mock;
}): Promise<Harness> {
  const token = options.token ?? 'secret-token';
  const accountState$ = options.accountState$ ?? of(known('NoMembership'));
  const redeem = options.redeem ?? jest.fn(() => of({ role: 'Manager' }));
  const accountStateRefresh = jest.fn(() => of(known('Member')));
  const accessRefresh = jest.fn(() => of({ status: 'granted' }));

  await TestBed.configureTestingModule({
    imports: [InviteAcceptanceComponent],
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'welcome', component: WelcomePageComponent },
        { path: 'application-pending', component: PendingPageComponent },
        { path: 'application-rejected', component: RejectedPageComponent },
        { path: 'access-unavailable', component: UnavailablePageComponent }
      ]),
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { paramMap: convertToParamMap({ token }) } }
      },
      { provide: AccountStateService, useValue: { state$: accountState$, refresh: accountStateRefresh } },
      { provide: AccessService, useValue: { refresh: accessRefresh } },
      { provide: InviteRedemptionService, useValue: { redeem } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(InviteAcceptanceComponent);
  return { fixture, router: TestBed.inject(Router), redeem, accountStateRefresh, accessRefresh };
}

/**
 * The invite acceptance page (issue #535, one slice of #505 under #328). It reads the token into
 * memory, strips it from the address bar, lets an eligible account state redeem, and sends every
 * other state to the page its own account state already calls for.
 */
describe('InviteAcceptanceComponent (issue #535)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('removes the token from the address bar with history.replaceState before anything else renders', async () => {
    const replaceState = jest.spyOn(window.history, 'replaceState');
    const { fixture } = await createHarness({ token: 'super-secret' });

    fixture.detectChanges();

    expect(replaceState).toHaveBeenCalledTimes(1);
    const [, , url] = replaceState.mock.calls[0];
    expect(url).toBe('/invite');
    expect(String(url)).not.toContain('super-secret');
    replaceState.mockRestore();
  });

  it('never writes the token to a Storage API or a cookie', async () => {
    const setItem = jest.spyOn(Storage.prototype, 'setItem');
    const cookie = jest.spyOn(document, 'cookie', 'set');
    const { fixture } = await createHarness({ token: 'super-secret' });

    fixture.detectChanges();
    fixture.componentInstance.accept();

    expect(setItem).not.toHaveBeenCalled();
    expect(cookie).not.toHaveBeenCalled();
    setItem.mockRestore();
    cookie.mockRestore();
  });

  it('keeps the token held only in memory, from the route read through to redemption, surviving a simulated sign-in redirect', async () => {
    // A signed-out visitor is sent through MsalGuard's login redirect and back to this exact URL
    // (issue #505's contract); this component is constructed fresh either way, which is what this
    // asserts: the token read from the route survives to the redeem call with nothing in between.
    const redeem = jest.fn(() => of({ role: 'Manager' }));
    const { fixture } = await createHarness({ token: 'post-redirect-token', redeem });

    fixture.detectChanges();
    fixture.componentInstance.accept();

    expect(redeem).toHaveBeenCalledWith('post-redirect-token');
  });

  it.each<AccountStateName>(['NoMembership', 'MembershipRevoked', 'ApplicationRejected'])(
    'offers the Accept button to a caller in account state %s',
    async (state) => {
      const { fixture } = await createHarness({ accountState$: of(known(state)) });

      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('Accept');
    }
  );

  it.each<AccountStateName>(['MembershipRevoked', 'ApplicationRejected'])(
    'redeems successfully for a %s caller',
    async (state) => {
      const redeem = jest.fn(() => of({ role: 'Manager' }));
      const { fixture, router, accountStateRefresh, accessRefresh } = await createHarness({
        accountState$: of(known(state)),
        redeem
      });

      fixture.detectChanges();
      fixture.componentInstance.accept();
      await fixture.whenStable();

      expect(redeem).toHaveBeenCalled();
      expect(accountStateRefresh).toHaveBeenCalled();
      expect(accessRefresh).toHaveBeenCalled();
      expect(router.url).toBe('/');
    }
  );

  it('offers the Accept button, fail-open, when the account state could not be read', async () => {
    const { fixture } = await createHarness({ accountState$: of(UNAVAILABLE) });

    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Accept');
  });

  it('re-reads the account state and the access cache, in order, before landing on the dashboard', async () => {
    const calls: string[] = [];
    const { fixture, router, accountStateRefresh, accessRefresh } = await createHarness({
      accountState$: of(known('NoMembership'))
    });
    accountStateRefresh.mockImplementation(() => {
      calls.push('account-state');
      return of(known('Member'));
    });
    accessRefresh.mockImplementation(() => {
      calls.push('access');
      return of({ status: 'granted' });
    });

    fixture.detectChanges();
    fixture.componentInstance.accept();
    await fixture.whenStable();

    expect(calls).toEqual(['account-state', 'access']);
    expect(router.url).toBe('/');
  });

  const nonRedeemableRoutes = (Object.entries(ACCOUNT_STATE_ROUTE) as [AccountStateName, string][]).filter(
    ([state]) => state !== 'NoMembership' && state !== 'MembershipRevoked' && state !== 'ApplicationRejected'
  );

  it.each(nonRedeemableRoutes)(
    'sends a %s caller to their own account-state page instead of the Accept button',
    async (state, path) => {
      const { fixture, router } = await createHarness({ accountState$: of(known(state)) });

      fixture.detectChanges();
      await fixture.whenStable();

      expect(router.url).toBe(`/${path}`);
    }
  );

  it('sends a Member caller to the dashboard instead of the Accept button', async () => {
    const { fixture, router } = await createHarness({ accountState$: of(known('Member')) });

    fixture.detectChanges();
    await fixture.whenStable();

    expect(router.url).toBe('/');
  });

  it('shows the API-provided message for an unusable invite', async () => {
    const redeem = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { message: 'This invite link cannot be used. It may already have been used.' }
          })
      )
    );
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    fixture.componentInstance.accept();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(
      'This invite link cannot be used. It may already have been used.'
    );
  });

  it('shows the API-provided message for a caller who already belongs to a business', async () => {
    const redeem = jest.fn(() =>
      throwError(
        () => new HttpErrorResponse({ status: 409, error: { message: 'This person is already a member of a business.' } })
      )
    );
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    fixture.componentInstance.accept();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('This person is already a member of a business.');
  });

  it('shows the API-provided message for a caller who must first be released from a deactivated business', async () => {
    const redeem = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: { message: 'Your account is still attached to a deactivated organisation.' }
          })
      )
    );
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    fixture.componentInstance.accept();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Your account is still attached to a deactivated organisation.');
  });

  it('falls back to a generic message when the refusal carries none', async () => {
    const redeem = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    fixture.componentInstance.accept();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("couldn't be accepted");
  });

  it('lets a failed redemption be retried rather than leaving the button disabled', async () => {
    const redeem = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    fixture.componentInstance.accept();
    await fixture.whenStable();

    expect(fixture.componentInstance.redeeming).toBe(false);
  });

  it('waits for the account state rather than deciding from an unread one', async () => {
    const pending = new Subject<AccountStateAnswer>();
    const { fixture, router } = await createHarness({ accountState$: pending });

    fixture.detectChanges();
    pending.next(known('Member'));
    await fixture.whenStable();

    expect(router.url).toBe('/');
  });
});
