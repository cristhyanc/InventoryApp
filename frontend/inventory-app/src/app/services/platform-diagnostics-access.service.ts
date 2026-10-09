import { Injectable } from '@angular/core';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import { Observable, catchError, filter, map, of, shareReplay, switchMap, take } from 'rxjs';
import { PlatformDiagnosticsService } from './platform-diagnostics.service';

/**
 * Whether the application shell should offer the super-admin diagnostics link (issue #335).
 *
 * The answer comes from `GET /api/admin/diagnostics/access` and from nowhere else. There is
 * deliberately no frontend role, claim, flag or configuration value that could say yes: the
 * authority is the separately configured Entra `(tid, oid)` pair the API resolves, and a second
 * source could only diverge from it. A failed, refused or unanswered probe is `false`, and the
 * page and both endpoints stay independently authorized either way - this service decides what a
 * menu shows, never what a caller may read.
 *
 * It waits for MSAL to finish any sign-in interaction and only asks when an account is present.
 * That is not an optimisation: a protected request issued while a redirect is still being handled
 * would have `MsalInterceptor` start an interactive sign-in of its own, on the public `/auth`
 * landing route, for a link nobody has asked for yet.
 */
@Injectable({ providedIn: 'root' })
export class PlatformDiagnosticsAccessService {
  private granted$?: Observable<boolean>;

  constructor(
    private readonly diagnostics: PlatformDiagnosticsService,
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService
  ) {}

  /**
   * Emits once: `true` only when the diagnostics API confirmed access for the signed-in actor.
   * The answer is shared, so one probe serves the sidebar however often it is rendered.
   */
  isGranted(): Observable<boolean> {
    this.granted$ ??= this.msalBroadcastService.inProgress$.pipe(
      filter((status: InteractionStatus) => status === InteractionStatus.None),
      take(1),
      switchMap(() => (this.hasSignedInAccount() ? this.probe() : of(false))),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    return this.granted$;
  }

  private hasSignedInAccount(): boolean {
    return Boolean(this.authService.instance.getActiveAccount() ?? this.authService.instance.getAllAccounts()[0]);
  }

  private probe(): Observable<boolean> {
    return this.diagnostics.access().pipe(
      map((access) => access.authorized === true),
      // A refusal is the expected answer for everyone except one configured identity, so it is
      // not reported to the operator and not logged: the link simply does not appear.
      catchError(() => of(false))
    );
  }
}
