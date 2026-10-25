import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { AccountInfo, AuthenticationResult } from '@azure/msal-browser';
import { Observable, of, throwError } from 'rxjs';
import { signInPageGuard } from './sign-in-page.guard';

@Component({ standalone: true, template: 'sign in' })
class SignInPageComponent {}

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}

const account = { homeAccountId: 'someone', username: 'someone@example.invalid' } as AccountInfo;

async function withGuard(
  accounts: AccountInfo[],
  handleRedirectObservable: () => Observable<AuthenticationResult | null> = () => of(null)
): Promise<Router> {
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'sign-in', component: SignInPageComponent, canActivate: [signInPageGuard] }
      ]),
      {
        provide: MsalService,
        useValue: { instance: { getAllAccounts: () => accounts }, handleRedirectObservable }
      }
    ]
  }).compileComponents();

  return TestBed.inject(Router);
}

/**
 * The sign-in page is for somebody who is not signed in. A person who already is - most often one
 * whose redirect started on this page and so came back to it - is sent on to the application
 * rather than shown the chooser again, which is what made a successful sign-in look like a loop.
 */
describe('signInPageGuard', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the sign-in page to a signed-out visitor', async () => {
    const router = await withGuard([]);

    await router.navigateByUrl('/sign-in');

    expect(router.url).toBe('/sign-in');
  });

  it('sends a signed-in person on to the home page', async () => {
    const router = await withGuard([account]);

    await router.navigateByUrl('/sign-in');

    expect(router.url).toBe('/');
  });

  /**
   * A redirect that returns to `/sign-in` is still being handled when the page is first routed,
   * so the account it signs in does not exist until that handling settles.
   */
  it('decides only after the redirect in progress has been handled', async () => {
    const accounts: AccountInfo[] = [];
    const router = await withGuard(accounts, () => {
      accounts.push(account);
      return of({ account } as AuthenticationResult);
    });

    await router.navigateByUrl('/sign-in');

    expect(router.url).toBe('/');
  });

  /**
   * A failed redirect is reported on this page (`AppComponent`), so it has to stay reachable for
   * that explanation even when an earlier session's account is still cached.
   */
  it('keeps the sign-in page reachable after a failed redirect', async () => {
    const router = await withGuard([account], () => throwError(() => new Error('redirect failed')));

    await router.navigateByUrl('/sign-in');

    expect(router.url).toBe('/sign-in');
  });
});
