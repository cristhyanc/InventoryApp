import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ToastService } from '../services/toast.service';
import {
  NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_CODE,
  NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE,
  READ_ONLY_SUPPORT_SESSION_CODE,
  READ_ONLY_SUPPORT_SESSION_MESSAGE
} from './support-session-messages';

function codeOf(error: HttpErrorResponse): string | undefined {
  const body = error.error as { code?: string } | null | undefined;
  return body?.code;
}

/**
 * The support session's own two `403` messages (issue #554): a write refused with
 * `read_only_support_session`, from a write control this build failed to hide or disable ahead of
 * the request, and a Nayax call refused with `unavailable_in_support_view`, from any Nayax-backed
 * request made while a session is active.
 *
 * It shows a message for exactly these two codes and leaves every other refusal, including an
 * ordinary member `403`, to `memberForbiddenNoticeInterceptor` beside it - which excludes both of
 * these codes for the same reason, so a support-session refusal is never reported twice. The
 * refusal itself is re-raised unchanged either way: this interceptor only reports, it never
 * resolves or retries.
 */
export const supportSessionMessageInterceptor: HttpInterceptorFn = (req, next) => {
  const toast = inject(ToastService);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 403) {
        const code = codeOf(error);
        if (code === READ_ONLY_SUPPORT_SESSION_CODE) {
          toast.error(READ_ONLY_SUPPORT_SESSION_MESSAGE);
        } else if (code === NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_CODE) {
          toast.error(NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE);
        }
      }

      return throwError(() => error);
    })
  );
};
