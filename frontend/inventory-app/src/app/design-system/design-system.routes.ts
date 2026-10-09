import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';

/** The only path the design-system fixtures are ever reachable on. */
export const DESIGN_SYSTEM_FIXTURE_PATH = '__design-system/widgets';

/**
 * The design-system fixture route (issue #411).
 *
 * `WidgetGalleryComponent` exists so the #409 series' required screenshots can be taken from the
 * real shared widgets in the real application shell. It is a development fixture, not a feature:
 * in an optimized build (`ng build`, and therefore every deployed bundle) this returns an empty
 * route list, so the application's routing table, navigation and behaviour are byte-for-byte what
 * they were before. The unoptimized dev and `e2e` servers add the one fixture path, which nothing
 * links to.
 *
 * The factory is separate from the exported constant so both branches can be asserted in a unit
 * test; `isDevMode()` is always true under Jest.
 */
export function createDesignSystemRoutes(isDevelopmentBuild: boolean): Routes {
  if (!isDevelopmentBuild) {
    return [];
  }

  return [
    {
      path: DESIGN_SYSTEM_FIXTURE_PATH,
      loadComponent: () => import('./widget-gallery.component').then((m) => m.WidgetGalleryComponent)
    }
  ];
}

export const designSystemRoutes: Routes = createDesignSystemRoutes(isDevMode());
