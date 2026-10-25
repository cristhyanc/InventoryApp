import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { Observable, catchError, map, of } from 'rxjs';

/**
 * Sends a person who is already signed in away from the sign-in page, to the application's home
 * page.
 *
 * **Why it exists.** A redirect returns to the page it started from, and a sign-in started from
 * this page with no destination held came back to this page: the person was signed in, but saw the
 * chooser again and had every reason to think the sign-in had failed. `SignInService` now starts
 * such a redirect back to the home page; this guard covers every other way a signed-in person
 * reaches `/sign-in` - an older redirect already in flight, the header's own **Sign in**, a
 * bookmark or the browser history.
 *
 * **It decides only after any redirect in progress has been handled**, the way `MsalGuard` does,
 * because the account a redirect returning here signs in does not exist until then. MSAL answers
 * every call for the same response with the same promise, so this never handles a response twice.
 *
 * **A failed redirect keeps the page.** `AppComponent` reports the failure here, and the page has
 * to stay reachable for that explanation even while an earlier session's account is still cached.
 *
 * It is not an authentication guard: a signed-out visitor is always let through, which is what
 * keeps this page public.
 */
export const signInPageGuard: CanActivateFn = (): Observable<boolean | UrlTree> => {
  const msal = inject(MsalService);
  const router = inject(Router);

  return msal.handleRedirectObservable().pipe(
    map((): boolean | UrlTree => (msal.instance.getAllAccounts().length > 0 ? router.parseUrl('/') : true)),
    catchError(() => of(true))
  );
};
