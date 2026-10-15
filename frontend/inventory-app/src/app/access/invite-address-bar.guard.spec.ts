import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { MsalGuard } from '@azure/msal-angular';
import { NEVER, Observable, of } from 'rxjs';
import { routes } from '../app.routes';
import { AccessService } from './access.service';
import { AccountStateAnswer, AccountStateService } from './account-state.service';
import { InviteAcceptanceComponent } from './invite-acceptance.component';
import { inviteTokenAddressBarGuard } from './invite-address-bar.guard';
import { InviteRedemptionService } from './invite-redemption.service';

/** What the stand-in for MSAL's `loginRedirect` saw at the moment `MsalGuard` called it. */
interface LoginRedirectCall {
  redirectStartPage: string;
  addressBarPath: string;
}

/**
 * Stands in for `MsalGuard` with the two behaviours this round trip depends on: signed out, it
 * starts the login redirect with the router state's URL as the redirect start page - as MSAL's own
 * guard does - and never settles, because the browser leaves the page; signed in, it lets the
 * navigation through.
 */
function msalGuardStub(signedIn: boolean, onLoginRedirect: (call: LoginRedirectCall) => void) {
  return {
    canActivate: (_route: ActivatedRouteSnapshot, state: RouterStateSnapshot): Observable<boolean> => {
      if (signedIn) {
        return of(true);
      }

      onLoginRedirect({ redirectStartPage: state.url, addressBarPath: window.location.pathname });
      return NEVER;
    }
  };
}

/** Puts `path` in jsdom's address bar, as if the person had opened that link. */
function openInAddressBar(path: string): void {
  window.history.replaceState(null, '', path);
}

/**
 * The invite token leaves the address bar before `MsalGuard` can redirect (issue #681, finding 3 of
 * the #328 implementation review). These drive the application's real route table, with only
 * `MsalGuard` and the invite page's own services replaced, through a signed-out visitor's sign-in
 * redirect and back.
 */
describe('inviteTokenAddressBarGuard (issue #681)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    openInAddressBar('/');
  });

  it('strips the token from the address bar before MsalGuard starts the sign-in redirect, and the invite survives the round trip', async () => {
    // Leg 1: a signed-out visitor opens the link. MsalGuard must find the token already gone from
    // the address bar - the history entry Back returns to - while still handing the token-bearing
    // URL to MSAL as its redirect start page.
    openInAddressBar('/invite/super-secret');
    let resolveRedirect!: (call: LoginRedirectCall) => void;
    const redirected = new Promise<LoginRedirectCall>((resolve) => (resolveRedirect = resolve));
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), { provide: MsalGuard, useValue: msalGuardStub(false, resolveRedirect) }]
    });

    void TestBed.inject(Router).navigateByUrl('/invite/super-secret');
    const loginRedirect = await redirected;

    expect(loginRedirect.addressBarPath).toBe('/invite');
    expect(window.location.href).not.toContain('super-secret');
    expect(loginRedirect.redirectStartPage).toBe('/invite/super-secret');

    // Leg 2: MSAL brings the now signed-in visitor back to that redirect start page, and the invite
    // page redeems the very token the visitor opened.
    TestBed.resetTestingModule();
    openInAddressBar(loginRedirect.redirectStartPage);
    const redeem = jest.fn(() => of({ role: 'Manager' }));
    const noMembership: AccountStateAnswer = {
      status: 'known',
      accountState: { state: 'NoMembership', onboardingEnabled: false, rejectionReason: null }
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        { provide: MsalGuard, useValue: msalGuardStub(true, () => undefined) },
        { provide: AccountStateService, useValue: { state$: of(noMembership), refresh: jest.fn(() => NEVER) } },
        { provide: AccessService, useValue: { refresh: jest.fn(() => NEVER) } },
        { provide: InviteRedemptionService, useValue: { redeem } }
      ]
    });

    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl(loginRedirect.redirectStartPage, InviteAcceptanceComponent);
    page.accept();

    expect(window.location.pathname).toBe('/invite');
    expect(redeem).toHaveBeenCalledWith('super-secret');
  });

  it("leaves the previous page's history entry alone on an in-app navigation to an invite", () => {
    openInAddressBar('/welcome');
    const replaceState = jest.spyOn(window.history, 'replaceState');

    expect(TestBed.runInInjectionContext(() => inviteTokenAddressBarGuard({ path: 'invite/:token' }, []))).toBe(true);

    expect(replaceState).not.toHaveBeenCalled();
    expect(window.location.pathname).toBe('/welcome');
    replaceState.mockRestore();
  });
});
