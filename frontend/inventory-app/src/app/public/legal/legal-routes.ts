import { Routes } from '@angular/router';
import { PUBLIC_LAYOUT_DATA } from '../public-layout';

/**
 * The public Privacy Policy and Terms of Use pages (issue #1190 built the layouts, issue #1214
 * published them with the owner-approved copy).
 *
 * Both routes exist in every build, development and production alike. Each page carries
 * `PUBLIC_LAYOUT_DATA` and no guard, so a signed-out visitor can open it directly or refresh it,
 * exactly as `/home` and `/sign-in` already can be; neither page makes a request. The public
 * homepage footer links to both.
 */
export const PRIVACY_POLICY_ROUTE = 'privacy-policy';
export const TERMS_OF_USE_ROUTE = 'terms-of-use';

export const legalRoutes: Routes = [
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
