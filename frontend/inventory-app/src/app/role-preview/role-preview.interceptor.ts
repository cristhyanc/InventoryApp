import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { classifyMemberRequest } from '../access/member-endpoint';
import { ConfigService } from '../services/config.service';
import { ToastService } from '../services/toast.service';
import {
  INVALID_ROLE_PREVIEW_CODE,
  READ_ONLY_ROLE_PREVIEW_CODE,
  ROLE_PREVIEW_HEADER,
  ROLE_PREVIEW_NOT_PERMITTED_CODE,
  readOnlyRolePreviewMessage
} from './role-preview';
import { RolePreviewService } from './role-preview.service';

function codeOf(error: HttpErrorResponse): string | undefined {
  const body = error.error as { code?: string } | null | undefined;
  return body?.code;
}

/**
 * Presents the previewed role on every request the API would read it from, and reports or applies
 * the three answers it can come back with (issue #721).
 *
 * **Which requests carry it.** The same classification the three `403` interceptors read
 * (`classifyMemberRequest`, issue #602): a `member` endpoint, and the two cache probes, because
 * `GET /api/me/access` answered *without* the header would report the administrator's own
 * capabilities and rebuild the full menu underneath a preview. A `platform-admin` request never
 * carries it - the diagnostics probe, the Super Admin businesses surface and the support-session
 * control surface are authorized by the configured Entra pair and by no business role, so
 * narrowing them to a business role could only refuse the very surface the preview is started
 * from - and a `non-api` request (a static asset, MSAL's own traffic, another origin) never did
 * either.
 *
 * **What comes back.** `read_only_role_preview` is the expected answer to pressing a write control
 * the previewed role is offered - they stay visible on purpose, so the preview is honest about
 * what that role is shown - and it is reported here, with the role and the way out of it.
 * `role_preview_not_permitted` and `invalid_role_preview` are refusals of the preview itself, so
 * they end it through {@link RolePreviewService.handleRefusedPreview}, which says so with its own
 * toast. Every refusal is re-raised unchanged either way: this interceptor reports and ends, it
 * never resolves or retries.
 *
 * **It is installed outer to `supportSessionInterceptor`** (`app.config.ts`), so an answer that one
 * discards as belonging to a context the tab has left never reaches these reactions, and inner to
 * the three `403` interceptors, two of which exclude these codes themselves
 * ({@link isRolePreviewRefusal}) so a preview refusal is neither reported twice nor turned into a
 * capability re-read.
 */
export const rolePreviewInterceptor: HttpInterceptorFn = (req, next) => {
  const preview = inject(RolePreviewService);
  const config = inject(ConfigService);
  const toast = inject(ToastService);

  const kind = classifyMemberRequest(req.url, config.apiBaseUrl);
  if (kind === 'non-api' || kind === 'platform-admin') {
    return next(req);
  }

  // The role this request is actually sent under, rather than whatever is current when the answer
  // arrives: a preview that ended meanwhile must not have its name in the sentence below.
  const role = preview.currentRole();
  if (role === null) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { [ROLE_PREVIEW_HEADER]: role } })).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        const code = codeOf(error);
        if (error.status === 403 && code === READ_ONLY_ROLE_PREVIEW_CODE) {
          toast.error(readOnlyRolePreviewMessage(role));
        } else if (
          (error.status === 403 && code === ROLE_PREVIEW_NOT_PERMITTED_CODE) ||
          (error.status === 400 && code === INVALID_ROLE_PREVIEW_CODE)
        ) {
          preview.handleRefusedPreview();
        }
      }

      return throwError(() => error);
    })
  );
};
