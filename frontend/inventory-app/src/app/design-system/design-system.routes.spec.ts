import { routes } from '../app.routes';
import { navLinks, primaryNavigation } from '../layout/navigation';
import { DESIGN_SYSTEM_FIXTURE_PATH, createDesignSystemRoutes } from './design-system.routes';

describe('design-system fixture routes', () => {
  it('adds nothing at all to an optimized build', () => {
    expect(createDesignSystemRoutes(false)).toEqual([]);
  });

  it('adds exactly the one fixture path to an unoptimized build', () => {
    const devRoutes = createDesignSystemRoutes(true);

    expect(devRoutes).toHaveLength(1);
    expect(devRoutes[0].path).toBe(DESIGN_SYSTEM_FIXTURE_PATH);
    expect(devRoutes[0].canActivate).toBeUndefined();
  });

  it('is registered before the wildcard route, which would otherwise swallow it', () => {
    const fixtureIndex = routes.findIndex((route) => route.path === DESIGN_SYSTEM_FIXTURE_PATH);
    const wildcardIndex = routes.findIndex((route) => route.path === '**');

    // Jest always runs in development mode, so the fixture route is present here.
    expect(fixtureIndex).toBeGreaterThanOrEqual(0);
    expect(fixtureIndex).toBeLessThan(wildcardIndex);
  });

  it('is not linked from the primary navigation', () => {
    const fixtureLinks = navLinks(primaryNavigation).filter((link) => link.route.includes('__design-system'));

    expect(fixtureLinks).toEqual([]);
  });
});
