import { ActivatedRouteSnapshot, Data } from '@angular/router';

/**
 * Route data that asks the application shell for the public layout (issue #954).
 *
 * A route carrying it is rendered by `AppComponent` with no authenticated chrome at all - no
 * sidebar, no header with the user control, no support-session or role-preview banners and no
 * business-context gate - so the page owns its whole layout. Only the public landing page and,
 * since issue #957, the sign-in page carry it; every authenticated route keeps the ordinary shell.
 */
export const PUBLIC_LAYOUT_DATA: Data = { layout: 'public' };

/** Whether the deepest route of a recognised or activated route tree asks for the public layout. */
export function isPublicLayoutRoute(root: ActivatedRouteSnapshot): boolean {
  let route: ActivatedRouteSnapshot | null = root;
  while (route?.firstChild) {
    route = route.firstChild;
  }
  return route?.data?.['layout'] === PUBLIC_LAYOUT_DATA['layout'];
}
