import { Route, Routes } from '@angular/router';
import { routes } from './app.routes';
import { LandingPageComponent } from './public/landing/landing-page.component';

/**
 * Every browser tab says which page is open (issue #1278, audit #1225 finding FE-07, WCAG 2.4.2).
 * The one named exception is the public landing page at `/` and `/home`
 * (`landing-page.component.ts`): it keeps `index.html`'s `<title>Restockroo</title>`, overriding
 * and restoring it itself, so `document-title.spec.ts` stays green and this spec excludes it by
 * resolving `loadComponent` rather than by path - `/` is also the signed-in Dashboard's path, with
 * its own title, so excluding by path alone would wrongly exempt the Dashboard too.
 */
const TITLE_SUFFIX_PATTERN = / · Restockroo$/;

function flatten(routeList: Routes): Route[] {
  return routeList.flatMap((candidate) => [candidate, ...(candidate.children ? flatten(candidate.children) : [])]);
}

/** Routes with `redirectTo`, and shell/layout routes with no component of their own, are excluded. */
function rendersOwnPageComponent(candidate: Route): boolean {
  return candidate.redirectTo === undefined && (candidate.component !== undefined || candidate.loadComponent !== undefined);
}

async function isLandingPage(candidate: Route): Promise<boolean> {
  return candidate.loadComponent !== undefined && (await candidate.loadComponent()) === LandingPageComponent;
}

describe('Route titles (issue #1278, audit #1225 finding FE-07)', () => {
  const flattened = flatten(routes);
  let titledCandidates: Route[];
  let landingRoutes: Route[];

  beforeAll(async () => {
    const pageComponentRoutes = flattened.filter(rendersOwnPageComponent);
    const landingFlags = await Promise.all(pageComponentRoutes.map(isLandingPage));
    titledCandidates = pageComponentRoutes.filter((_, index) => !landingFlags[index]);
    landingRoutes = pageComponentRoutes.filter((_, index) => landingFlags[index]);
  });

  it('finds more than thirty routes to check, so the assertions below cannot vacuously pass', () => {
    expect(titledCandidates.length).toBeGreaterThan(30);
  });

  it('gives every route that renders a page component a non-empty "<Page name> · Restockroo" title', () => {
    for (const candidate of titledCandidates) {
      expect(typeof candidate.title).toBe('string');
      const title = candidate.title as string;
      expect(title.trim()).not.toBe('');
      expect(title).toMatch(TITLE_SUFFIX_PATTERN);
    }
  });

  it('finds exactly the public landing page at "/" and "/home", and leaves both without a static title', () => {
    expect(landingRoutes).toHaveLength(2);
    for (const candidate of landingRoutes) {
      expect(candidate.title).toBeUndefined();
    }
  });

  it('leaves every redirect route without a title', () => {
    const redirects = flattened.filter((candidate) => candidate.redirectTo !== undefined);

    expect(redirects.length).toBeGreaterThan(0);
    for (const candidate of redirects) {
      expect(candidate.title).toBeUndefined();
    }
  });
});
