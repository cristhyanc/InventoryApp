import { routes } from '../../app.routes';
import { PUBLIC_LAYOUT_DATA } from '../public-layout';
import { PRIVACY_POLICY_ROUTE, TERMS_OF_USE_ROUTE, legalRoutes } from './legal-routes';
import { PrivacyPolicyPageComponent } from './privacy-policy-page.component';
import { TermsOfUsePageComponent } from './terms-of-use-page.component';

describe('legal page routes (issue #1214)', () => {
  it('declares exactly the Privacy Policy and Terms of Use paths, each unguarded and public', async () => {
    expect(legalRoutes).toHaveLength(2);

    const privacy = legalRoutes.find((route) => route.path === PRIVACY_POLICY_ROUTE)!;
    expect(privacy.canActivate).toBeUndefined();
    expect(privacy.canMatch).toBeUndefined();
    expect(privacy.data).toEqual(PUBLIC_LAYOUT_DATA);
    expect(await privacy.loadComponent!()).toBe(PrivacyPolicyPageComponent);

    const terms = legalRoutes.find((route) => route.path === TERMS_OF_USE_ROUTE)!;
    expect(terms.canActivate).toBeUndefined();
    expect(terms.canMatch).toBeUndefined();
    expect(terms.data).toEqual(PUBLIC_LAYOUT_DATA);
    expect(await terms.loadComponent!()).toBe(TermsOfUsePageComponent);
  });

  it('does not depend on the build mode, so a production build serves both pages too', async () => {
    // The #1190 layouts were gated on `isDevMode()`. Re-importing the module with Angular's
    // production mode switched on proves the published routes no longer read it.
    jest.resetModules();
    jest.doMock('@angular/core', () => ({ ...jest.requireActual('@angular/core'), isDevMode: () => false }));
    const production = await import('./legal-routes');
    jest.dontMock('@angular/core');

    expect(production.legalRoutes.map((route) => route.path)).toEqual([PRIVACY_POLICY_ROUTE, TERMS_OF_USE_ROUTE]);
  });

  it('is registered before the wildcard route, which would otherwise swallow it', () => {
    const privacyIndex = routes.findIndex((route) => route.path === PRIVACY_POLICY_ROUTE);
    const termsIndex = routes.findIndex((route) => route.path === TERMS_OF_USE_ROUTE);
    const wildcardIndex = routes.findIndex((route) => route.path === '**');

    expect(privacyIndex).toBeGreaterThanOrEqual(0);
    expect(termsIndex).toBeGreaterThanOrEqual(0);
    expect(privacyIndex).toBeLessThan(wildcardIndex);
    expect(termsIndex).toBeLessThan(wildcardIndex);
  });
});
