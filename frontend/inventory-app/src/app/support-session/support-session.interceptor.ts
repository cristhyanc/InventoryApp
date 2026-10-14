import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { EMPTY, catchError, filter, tap, throwError } from 'rxjs';
import { classifyMemberRequest } from '../access/member-endpoint';
import { ConfigService } from '../services/config.service';
import { SUPPORT_SESSION_HEADER, SupportSessionService } from './support-session.service';

/** `SupportSessionErrorCodes.SessionEnded`: the one code a presented session can be refused with. */
const SESSION_ENDED_CODE = 'support_session_ended';

function isSessionEndedRefusal(error: HttpErrorResponse): boolean {
  const body = error.error as { code?: string } | null | undefined;
  return body?.code === SESSION_ENDED_CODE;
}

/**
 * Joins the Super Admin's support session, and discards whatever it is no longer current for
 * (issue #553).
 *
 * **Attaches the header while a session is active**, and only to a request this application's own
 * API would read it from: `classifyMemberRequest`'s `platform-admin` kind is the support-session
 * control surface itself (`/api/super-admin/support-sessions/*`, reached as the administrator and
 * never inside a session - docs/architecture.md § Support sessions: the store and the control
 * endpoints) and the diagnostics probe beside it, so neither carries the header, and `non-api`
 * requests - static assets, MSAL's own traffic - never did either.
 *
 * **Discards a response sent under an id that is no longer current**, whether that is because this
 * very response answered `support_session_ended` (the session died mid-request) or because
 * something else - another tab's end, a different concurrent request's own
 * `support_session_ended` - moved the session on while this one was in flight. Both are one rule:
 * compare the id this request carried against {@link SupportSessionService.currentSessionId} once
 * the response (or refusal) has been fully processed, and if they differ, let the caller see
 * neither a value nor an error - exactly `EMPTY` would. A request this tab sent with no session
 * active is untouched by that rule, because there was never an id for it to become stale against.
 */
export const supportSessionInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SupportSessionService);
  const config = inject(ConfigService);

  const kind = classifyMemberRequest(req.url, config.apiBaseUrl);
  if (kind === 'non-api' || kind === 'platform-admin') {
    return next(req);
  }

  const sentWithId = session.currentSessionId();
  const outgoing = sentWithId === null ? req : req.clone({ setHeaders: { [SUPPORT_SESSION_HEADER]: sentWithId } });
  const isStale = (): boolean => sentWithId !== null && session.currentSessionId() !== sentWithId;

  return next(outgoing).pipe(
    tap({
      error: (error: unknown) => {
        if (sentWithId !== null && error instanceof HttpErrorResponse && error.status === 403 && isSessionEndedRefusal(error)) {
          session.handleSessionEndedSignal();
        }
      }
    }),
    filter(() => !isStale()),
    catchError((error: unknown) => (isStale() ? EMPTY : throwError(() => error)))
  );
};
