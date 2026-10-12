import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AccessService } from './access.service';

/** Whether a refused request is one whose refusal says something about the member's role. */
export function isMemberAccessRefusal(url: string): boolean {
  return !url.startsWith('/assets/') && !url.includes('/assets/') && !url.endsWith('/me/access');
}

/**
 * The "any `403` from a member endpoint" trigger on the capability cache (issue #528).
 *
 * A member endpoint refusing a request the cached capabilities said was allowed means the cache is
 * behind the server - most plausibly because somebody changed the member's role - so the
 * capabilities are read again and the navigation, guards and hidden actions re-render from the new
 * answer. `AccessService.refreshUnlessReading()` reuses a read already in flight, so a page whose
 * requests all refuse issues one read rather than one per refusal.
 *
 * `GET /api/me/access` is excluded, because a refusal there is the answer to this very question and
 * reacting to it would be a refresh loop. Static `/assets/` requests are excluded for the same
 * reason the loading indicator excludes them: they are not member endpoints.
 *
 * It reports nothing to the operator and routes nowhere. Showing "you don't have access to this"
 * for a refused member request is issue #529's global `403` handler, which is a separate decision
 * from keeping this cache current.
 */
export const accessForbiddenInterceptor: HttpInterceptorFn = (req, next) => {
  const access = inject(AccessService);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 403 && isMemberAccessRefusal(req.url)) {
        access.refreshUnlessReading();
      }

      return throwError(() => error);
    })
  );
};
