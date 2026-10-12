import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Observable, map } from 'rxjs';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';

/**
 * Guards every `/super-admin/*` route (issue #546) behind the platform-admin check
 * `GET /api/admin/diagnostics/access` answers (issue #335), reusing
 * `PlatformDiagnosticsAccessService.isGranted()` so the sidebar's `Super Admin` group and the
 * routes it points at ask the same cached probe and never disagree.
 *
 * That cache belongs to one signed-in identity (issue #605): `isGranted()` emits the answer for
 * whoever is signed in when this guard asks, waiting for that identity's own probe rather than
 * replaying a previous one's grant or refusing on an answer that has not arrived yet.
 *
 * **It is presentation, not a boundary.** Platform-admin authority is a separately configured
 * Entra `(tid, oid)` pair, re-checked by every diagnostics endpoint on every request regardless of
 * what this guard decided, so entering a `/super-admin/*` URL directly is safe either way.
 *
 * Unlike `accountStateGuard`/`requireCapability`, a refusal here has nothing to do with business
 * membership or role capability - the configured platform administrator may hold no business
 * membership at all - so it sends the caller to the dashboard rather than to any
 * membership-handling destination, and this route therefore carries no `accountStateGuard`.
 */
export const platformAdminGuard: CanActivateFn = (): Observable<boolean | UrlTree> => {
  const diagnosticsAccess = inject(PlatformDiagnosticsAccessService);
  const router = inject(Router);

  return diagnosticsAccess.isGranted().pipe(map((granted): boolean | UrlTree => granted || router.createUrlTree(['/'])));
};
