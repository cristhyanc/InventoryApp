import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { AccessService } from './access.service';
import { classifyMemberRequest } from './member-endpoint';

/**
 * Whether a refused request is one whose refusal says something about the member's role.
 *
 * Only a `member` endpoint of the configured API does (issue #602): the two cache probes, the
 * platform diagnostics surface, static assets and every other origin do not. See
 * {@link classifyMemberRequest}, which is the one classification all three `403` interceptors
 * read, so none of them can treat a request as a member refusal that another one excludes.
 */
export function isMemberAccessRefusal(url: string, apiBaseUrl: string): boolean {
  return classifyMemberRequest(url, apiBaseUrl) === 'member';
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
 * **Both cache probes are excluded, not just this one's** (issue #602). A refusal of
 * `GET /api/me/access` is the answer to this very question, and reacting to it would read it again
 * and again; a refusal of `GET /api/me/account-state` is the other cache's own read, and reacting
 * to it used to make the two caches refresh each other without end, because that probe's refusal
 * refreshed the capabilities whose refused probe refreshed the account state. The remaining
 * refresh between the two is deliberately one-way: see `accountStateForbiddenInterceptor`.
 *
 * The platform diagnostics probe (`/api/admin/diagnostics/*`) is excluded too, because a `403`
 * there is the expected answer for every member and says nothing about a business role, and so are
 * static assets and any other origin, which are not member endpoints at all.
 *
 * It reports nothing to the operator and routes nowhere. Showing "you don't have access to this"
 * for a refused member request is issue #529's global `403` handler, which is a separate decision
 * from keeping this cache current.
 */
export const accessForbiddenInterceptor: HttpInterceptorFn = (req, next) => {
  const access = inject(AccessService);
  const config = inject(ConfigService);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 403 &&
        isMemberAccessRefusal(req.url, config.apiBaseUrl)
      ) {
        access.refreshUnlessReading();
      }

      return throwError(() => error);
    })
  );
};
