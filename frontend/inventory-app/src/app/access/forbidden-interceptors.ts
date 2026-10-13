import { HttpInterceptorFn } from '@angular/common/http';
import { accessForbiddenInterceptor } from './access-forbidden.interceptor';
import { accountStateForbiddenInterceptor } from './account-state-forbidden.interceptor';
import { memberForbiddenNoticeInterceptor } from './member-forbidden-notice.interceptor';

/**
 * The three `403` interceptors, in the order the application installs them (`app.config.ts`).
 *
 * `accessForbiddenInterceptor` and `accountStateForbiddenInterceptor` keep the capability and
 * account-state caches current when a member endpoint refuses a request (issues #528, #529);
 * neither reports anything itself. `memberForbiddenNoticeInterceptor` is the one that does
 * (issue #529's global `403` handler), so it runs after both have had their turn.
 *
 * The list lives here rather than inline in `app.config.ts` so that
 * `forbidden-interceptor-chain.spec.ts` composes the production chain itself (issue #602): what
 * these three do to each other's refresh reads is only visible when all of them are installed
 * together, and a hand-copied list in the spec could drift from the application's.
 */
export const FORBIDDEN_INTERCEPTORS: readonly HttpInterceptorFn[] = [
  accessForbiddenInterceptor,
  accountStateForbiddenInterceptor,
  memberForbiddenNoticeInterceptor
];
