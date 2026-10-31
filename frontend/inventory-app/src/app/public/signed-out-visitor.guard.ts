import { inject } from '@angular/core';
import { CanMatchFn } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { Observable, catchError, map, of } from 'rxjs';

/**
 * Matches the public landing page at `/` only for a visitor nobody has signed in as (issue #954).
 *
 * `/` stays the application's entry route: sign-in returns there, `signInPageGuard` and the
 * wildcard send people there, and a signed-in member must keep reaching the dashboard there. So
 * the landing page does not replace the dashboard route; it is declared before it with this
 * `canMatch` guard, and the router falls through to the unchanged, guarded dashboard route
 * whenever this answers `false`. A signed-in person reaches the landing page at `/home` instead.
 *
 * **It decides only after any redirect in progress has been handled**, like `MsalGuard` and
 * `signInPageGuard`: the account a sign-in redirect returning to `/` creates does not exist until
 * then, and deciding earlier would show that person the landing page instead of their dashboard.
 *
 * A failed redirect answers `true`. `AppComponent` reports that failure and sends the person to
 * `/sign-in`; until it does, the public page is the only thing safe to show, because it makes no
 * request at all.
 *
 * This is not an authorization boundary - it protects nothing, and the dashboard route behind it
 * keeps every guard it had. It only chooses which of two pages `/` shows.
 */
export const signedOutVisitorGuard: CanMatchFn = (): Observable<boolean> => {
  const msal = inject(MsalService);

  return msal.handleRedirectObservable().pipe(
    map(() => msal.instance.getAllAccounts().length === 0),
    catchError(() => of(true))
  );
};
