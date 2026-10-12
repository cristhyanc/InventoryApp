import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AccountStateService } from './account-state.service';

/** Whether a refused request is one whose refusal says something about the caller's account state. */
export function isMemberAccountStateRefusal(url: string): boolean {
  return !url.startsWith('/assets/') && !url.includes('/assets/') && !url.endsWith('/me/account-state');
}

/**
 * The "any `403` from a member endpoint" trigger on the account-state cache (issue #529's cache
 * invalidation, which is #528's trigger set applied to {@link AccountStateService}).
 *
 * `GET /api/me/access` is deliberately included: it requires membership, so its own `403` is
 * itself a signal the cached account state may be stale. `GET /api/me/account-state` is excluded,
 * for the same reason `accessForbiddenInterceptor` excludes `GET /api/me/access` - reacting to a
 * refusal of the read that answers this very question would be a refresh loop, and that endpoint
 * does not require membership to begin with.
 *
 * It reports nothing to the operator and routes nowhere, exactly like `accessForbiddenInterceptor`.
 * Showing "you don't have access to this" is `memberForbiddenNoticeInterceptor`'s separate job.
 */
export const accountStateForbiddenInterceptor: HttpInterceptorFn = (req, next) => {
  const accountState = inject(AccountStateService);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 403 && isMemberAccountStateRefusal(req.url)) {
        accountState.refreshUnlessReading();
      }

      return throwError(() => error);
    })
  );
};
