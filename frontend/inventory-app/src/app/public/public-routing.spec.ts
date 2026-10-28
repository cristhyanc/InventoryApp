import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { CanActivateFn, Route, Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { MsalGuard, MsalService } from '@azure/msal-angular';
import { AccountInfo, AuthenticationResult } from '@azure/msal-browser';
import { Observable, of } from 'rxjs';
import { accountStateGuard } from '../access/account-state.guard';
import { routes } from '../app.routes';
import { LandingPageComponent } from './landing/landing-page.component';
import { PUBLIC_LAYOUT_DATA } from './public-layout';
import { signedOutVisitorGuard } from './signed-out-visitor.guard';

@Component({ standalone: true, template: 'dashboard page' })
class DashboardStubComponent {}

@Component({ standalone: true, template: 'products page' })
class ProductsStubComponent {}

const account = { homeAccountId: 'dana', username: 'dana@example.invalid', name: 'Dana' } as AccountInfo;

/** The route `routes` declares for the landing page at `/` - the first `''` route. */
function landingRoute(): Route {
  return routes.find((candidate) => candidate.path === '')!;
}

/** The authenticated dashboard route: the `''` route that is not the landing page. */
function dashboardRoute(): Route {
  return routes.find((candidate) => candidate.path === '' && candidate !== landingRoute())!;
}

function homeRoute(): Route {
  return routes.find((candidate) => candidate.path === 'home')!;
}

/**
 * The public root route (issue #954), on the real route declarations.
 *
 * The landing page is matched at `/` only for a signed-out visitor; everyone else falls through to
 * the dashboard route, whose guards are unchanged. These tests use the real landing and `/home`
 * route objects and stand a recording guard in for the dashboard's (and a product page's) real
 * authentication guards, so what they prove is which page each person reaches and whether the
 * authentication guard runs - not the guards' own behaviour, which their own specs cover.
 */
describe('public root route (issue #954)', () => {
  describe('declarations', () => {
    it('declares the landing page before the dashboard at the same empty path', () => {
      const landing = routes.indexOf(landingRoute());
      const dashboard = routes.indexOf(dashboardRoute());

      expect(landing).toBeGreaterThanOrEqual(0);
      expect(landing).toBeLessThan(dashboard);
    });

    it('matches the landing page only for a signed-out visitor, and guards nothing else with it', () => {
      expect(landingRoute().pathMatch).toBe('full');
      expect(landingRoute().canMatch).toEqual([signedOutVisitorGuard]);
      expect(landingRoute().canActivate).toBeUndefined();
      expect(landingRoute().data).toEqual(PUBLIC_LAYOUT_DATA);
    });

    it('keeps the dashboard route exactly as guarded as it was', () => {
      const guards = dashboardRoute().canActivate ?? [];

      expect(guards[0]).toBe(MsalGuard);
      expect(guards[1]).toBe(accountStateGuard);
      expect(guards).toHaveLength(3);
      expect(dashboardRoute().canMatch).toBeUndefined();
    });

    it('serves /home publicly in the public layout', () => {
      expect(homeRoute().canActivate).toBeUndefined();
      expect(homeRoute().canMatch).toBeUndefined();
      expect(homeRoute().data).toEqual(PUBLIC_LAYOUT_DATA);
    });

    it('loads the landing page for both public routes', async () => {
      expect(await landingRoute().loadComponent!()).toBe(LandingPageComponent);
      expect(await homeRoute().loadComponent!()).toBe(LandingPageComponent);
    });

    it('adds no other route without an authentication guard', () => {
      const unguardedPaths = routes
        .filter((candidate) => candidate.redirectTo === undefined && candidate.canMatch === undefined)
        .filter((candidate) => !(candidate.canActivate ?? []).length)
        .map((candidate) => candidate.path);

      // `auth`, `invite` and the design-system showcase were public before issue #954 (`sign-in`
      // carries only `signInPageGuard`, which is not an authentication guard); `home` is this
      // issue's.
      expect(unguardedPaths.sort()).toEqual(['__design-system/widgets', 'auth', 'home', 'invite'].sort());
    });
  });

  describe('which page each person reaches', () => {
    let accounts: AccountInfo[];
    let authenticationChecks: string[];

    /** Stands in for `MsalGuard`: records that authentication was required, then lets them in. */
    const requireAuthentication: CanActivateFn = (_route, state) => {
      authenticationChecks.push(state.url);
      return true;
    };

    async function harness(
      handleRedirectObservable: () => Observable<AuthenticationResult | null> = () => of(null)
    ): Promise<RouterTestingHarness> {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([
            landingRoute(),
            homeRoute(),
            { path: '', canActivate: [requireAuthentication], component: DashboardStubComponent },
            { path: 'products', canActivate: [requireAuthentication], component: ProductsStubComponent },
            { path: '**', redirectTo: '' }
          ]),
          provideHttpClient(),
          provideHttpClientTesting(),
          {
            provide: MsalService,
            useValue: {
              instance: { getAllAccounts: () => accounts },
              handleRedirectObservable,
              loginRedirect: jest.fn()
            }
          }
        ]
      });
      return RouterTestingHarness.create();
    }

    beforeEach(() => {
      accounts = [];
      authenticationChecks = [];
    });

    afterEach(() => {
      // The landing page makes no request at all: no account, business, access or Nayax read.
      TestBed.inject(HttpTestingController).verify();
      TestBed.resetTestingModule();
    });

    it('shows a signed-out visitor the landing page at /, without requiring sign-in', async () => {
      const tester = await harness();

      await tester.navigateByUrl('/');

      expect(tester.routeNativeElement?.querySelector('h1')?.textContent).toContain('Know your stock.');
      expect(authenticationChecks).toEqual([]);
    });

    it('shows the landing page again on a refresh', async () => {
      const tester = await harness();
      await tester.navigateByUrl('/');

      // A refresh is a fresh initial navigation to the same URL.
      await tester.navigateByUrl('/', LandingPageComponent);

      expect(authenticationChecks).toEqual([]);
    });

    it('still sends a signed-out visitor through authentication for a protected page', async () => {
      const tester = await harness();

      await tester.navigateByUrl('/products');

      expect(authenticationChecks).toEqual(['/products']);
    });

    it('lands a signed-out visitor on an unknown address on the landing page, not the dashboard', async () => {
      const tester = await harness();

      await tester.navigateByUrl('/no-such-page');

      expect(TestBed.inject(Router).url).toBe('/');
      expect(tester.routeNativeElement?.querySelector('h1')?.textContent).toContain('Know your stock.');
      expect(authenticationChecks).toEqual([]);
    });

    it('keeps / the dashboard for a signed-in person, behind its authentication guard', async () => {
      accounts = [account];
      const tester = await harness();

      await tester.navigateByUrl('/');

      expect(tester.routeNativeElement?.textContent).toContain('dashboard page');
      expect(authenticationChecks).toEqual(['/']);
    });

    it('decides only after a sign-in redirect returning to / has been handled', async () => {
      // The account the redirect signs in does not exist until handling settles; deciding before
      // that would show a person who has just signed in the landing page instead of the app.
      const tester = await harness(() => {
        accounts.push(account);
        return of({ account } as AuthenticationResult);
      });

      await tester.navigateByUrl('/');

      expect(tester.routeNativeElement?.textContent).toContain('dashboard page');
      expect(authenticationChecks).toEqual(['/']);
    });

    it('lets a signed-in person reach the landing page at /home', async () => {
      accounts = [account];
      const tester = await harness();

      await tester.navigateByUrl('/home');

      expect(tester.routeNativeElement?.querySelector('h1')?.textContent).toContain('Know your stock.');
      expect(authenticationChecks).toEqual([]);
    });

    it('sends a signed-in person from Open app to the dashboard with no redirect loop', async () => {
      accounts = [account];
      const tester = await harness();
      await tester.navigateByUrl('/home');

      const openApp = tester.routeNativeElement!.querySelector<HTMLAnchorElement>('[data-testid="landing-open-app"]')!;
      expect(openApp.getAttribute('href')).toBe('/');
      openApp.click();
      await tester.fixture.whenStable();
      tester.detectChanges();

      expect(TestBed.inject(Router).url).toBe('/');
      expect(tester.routeNativeElement?.textContent).toContain('dashboard page');
    });
  });
});
