import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';
import { PUBLIC_LAYOUT_DATA } from '../public-layout';

/**
 * The Privacy Policy and Terms of Use page layouts (issue #1190).
 *
 * The owner's Stitch designs for both are approved, but the legal copy is not, so these routes
 * exist only in a development build - never in `ng build`, and therefore never in anything
 * deployed - following the design-system fixture's own pattern (issue #411,
 * `design-system/design-system.routes.ts`). Nothing links to either path: not the homepage footer,
 * the header, the sign-in page or the application shell. Each page carries `PUBLIC_LAYOUT_DATA` and
 * no guard, so a signed-out visitor can open it directly, exactly as `/home` and `/sign-in` already
 * can be.
 *
 * The factory is separate from the exported constant so both branches can be asserted in a unit
 * test; `isDevMode()` is always true under Jest.
 */
export const PRIVACY_POLICY_ROUTE = 'privacy-policy';
export const TERMS_OF_USE_ROUTE = 'terms-of-use';

export function createLegalRoutes(isDevelopmentBuild: boolean): Routes {
  if (!isDevelopmentBuild) {
    return [];
  }

  return [
    {
      path: PRIVACY_POLICY_ROUTE,
      data: PUBLIC_LAYOUT_DATA,
      loadComponent: () => import('./privacy-policy-page.component').then((m) => m.PrivacyPolicyPageComponent)
    },
    {
      path: TERMS_OF_USE_ROUTE,
      data: PUBLIC_LAYOUT_DATA,
      loadComponent: () => import('./terms-of-use-page.component').then((m) => m.TermsOfUsePageComponent)
    }
  ];
}

export const legalRoutes: Routes = createLegalRoutes(isDevMode());
