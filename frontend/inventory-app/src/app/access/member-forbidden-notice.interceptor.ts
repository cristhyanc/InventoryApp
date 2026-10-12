import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { ToastService } from '../services/toast.service';
import { ACCOUNT_STATE_BYPASS_PATHS, INVITE_ROUTE_PREFIX } from './account-state.guard';
import { isMemberAccessRefusal } from './access-forbidden.interceptor';

const SLASH = '/'.charCodeAt(0);

function normalizedCurrentPath(url: string): string {
  const path = url.split('?')[0].split('#')[0];
  let start = 0;
  while (path.charCodeAt(start) === SLASH) start++;
  let end = path.length;
  while (end > start && path.charCodeAt(end - 1) === SLASH) end--;
  return path.slice(start, end);
}

/**
 * Whether `currentUrl` is a route a signed-in person without a usable membership may legitimately
 * be on - the account-state routing destinations and the invite route (issue #529's bypass list,
 * agreed under #328). A `403` on one of them never says "you don't have access to this": it is
 * either expected (the shell's own capability or business-context reads, refused for exactly the
 * reason the person is here) or belongs to a page with its own, more specific error handling.
 */
export function isAccountStateBypassRoute(currentUrl: string): boolean {
  const path = normalizedCurrentPath(currentUrl);
  return ACCOUNT_STATE_BYPASS_PATHS.has(path) || path.startsWith(INVITE_ROUTE_PREFIX);
}

/**
 * The global `403` handler (issue #529): a member endpoint refusing a request shows "you don't
 * have access to this" through the toast the application already renders every other failure
 * with, rather than each page describing the refusal itself.
 *
 * It reuses `isMemberAccessRefusal`'s member-endpoint test - the same one `accessForbiddenInterceptor`
 * uses to decide whether a refusal says anything about the caller's role - so the two stay in
 * agreement about what counts as a member endpoint. It does **not** fire while the current route
 * is one of {@link isAccountStateBypassRoute}'s: a person there has already been told, by the
 * account-state routing itself or by the page they are on, exactly what their situation is, and a
 * generic toast on top would only contradict or duplicate that.
 */
export const memberForbiddenNoticeInterceptor: HttpInterceptorFn = (req, next) => {
  const toast = inject(ToastService);
  const router = inject(Router);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 403 &&
        isMemberAccessRefusal(req.url) &&
        !isAccountStateBypassRoute(router.url)
      ) {
        toast.error("You don't have access to this.");
      }

      return throwError(() => error);
    })
  );
};
