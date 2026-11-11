import { routes } from '../../app.routes';
import { PUBLIC_LAYOUT_DATA } from '../public-layout';
import { PRIVACY_POLICY_ROUTE, TERMS_OF_USE_ROUTE, createLegalRoutes } from './legal-routes';
import { PrivacyPolicyPageComponent } from './privacy-policy-page.component';
import { TermsOfUsePageComponent } from './terms-of-use-page.component';

describe('legal page routes (issue #1190)', () => {
  it('adds nothing at all to an optimized build', () => {
    expect(createLegalRoutes(false)).toEqual([]);
  });

  it('adds exactly the Privacy Policy and Terms of Use paths to an unoptimized build, each unguarded and public', async () => {
    const devRoutes = createLegalRoutes(true);

    expect(devRoutes).toHaveLength(2);

    const privacy = devRoutes.find((route) => route.path === PRIVACY_POLICY_ROUTE)!;
    expect(privacy.canActivate).toBeUndefined();
    expect(privacy.canMatch).toBeUndefined();
    expect(privacy.data).toEqual(PUBLIC_LAYOUT_DATA);
    expect(await privacy.loadComponent!()).toBe(PrivacyPolicyPageComponent);

    const terms = devRoutes.find((route) => route.path === TERMS_OF_USE_ROUTE)!;
    expect(terms.canActivate).toBeUndefined();
    expect(terms.canMatch).toBeUndefined();
    expect(terms.data).toEqual(PUBLIC_LAYOUT_DATA);
    expect(await terms.loadComponent!()).toBe(TermsOfUsePageComponent);
  });

  it('is registered before the wildcard route, which would otherwise swallow it', () => {
    const privacyIndex = routes.findIndex((route) => route.path === PRIVACY_POLICY_ROUTE);
    const termsIndex = routes.findIndex((route) => route.path === TERMS_OF_USE_ROUTE);
    const wildcardIndex = routes.findIndex((route) => route.path === '**');

    // Jest always runs in development mode, so both routes are present here.
    expect(privacyIndex).toBeGreaterThanOrEqual(0);
    expect(termsIndex).toBeGreaterThanOrEqual(0);
    expect(privacyIndex).toBeLessThan(wildcardIndex);
    expect(termsIndex).toBeLessThan(wildcardIndex);
  });
});
