import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { AccountStateService } from './account-state.service';
import { classifyMemberRequest } from './member-endpoint';

/**
 * Whether a refused request is one whose refusal says something about the caller's account state:
 * a `member` endpoint of the configured API, or the capability probe (issue #602).
 *
 * `GET /api/me/access` is included because it requires membership itself, so its own `403` is a
 * staleness signal for the account state too. That is the one refresh that still crosses between
 * the two caches, and it is one-way by construction: this interceptor's own probe
 * (`GET /api/me/account-state`) is excluded, so the read this refusal starts cannot start another.
 */
export function isMemberAccountStateRefusal(url: string, apiBaseUrl: string): boolean {
  const kind = classifyMemberRequest(url, apiBaseUrl);
  return kind === 'member' || kind === 'access-probe';
}

/**
 * The "any `403` from a member endpoint" trigger on the account-state cache (issue #529's cache
 * invalidation, which is #528's trigger set applied to {@link AccountStateService}).
 *
 * `GET /api/me/account-state` is excluded, for the same reason `accessForbiddenInterceptor`
 * excludes its own probe - reacting to a refusal of the read that answers this very question would
 * be a refresh loop, and that endpoint does not require membership to begin with.
 *
 * `GET /api/me/access` is deliberately included: it requires membership, so its own `403` is
 * itself a signal the cached account state may be stale - a role that was revoked while the tab
 * was open shows up there first. Since issue #602 that is the only remaining edge between the two
 * caches and it runs one way only: the account-state read it starts is a dead end, because this
 * interceptor ignores that probe's refusal and `accessForbiddenInterceptor` now ignores it too. A
 * persistent `403` therefore costs at most one read of each cache and stops.
 *
 * The platform diagnostics surface, static assets and other origins are excluded by the same
 * classification (see {@link classifyMemberRequest}).
 *
 * It reports nothing to the operator and routes nowhere, exactly like `accessForbiddenInterceptor`.
 * Showing "you don't have access to this" is `memberForbiddenNoticeInterceptor`'s separate job.
 */
export const accountStateForbiddenInterceptor: HttpInterceptorFn = (req, next) => {
  const accountState = inject(AccountStateService);
  const config = inject(ConfigService);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 403 &&
        isMemberAccountStateRefusal(req.url, config.apiBaseUrl)
      ) {
        accountState.refreshUnlessReading();
      }

      return throwError(() => error);
    })
  );
};
