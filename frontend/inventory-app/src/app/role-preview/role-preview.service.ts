import { Injectable, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { BehaviorSubject, Observable, Subject, distinctUntilChanged, map, takeUntil } from 'rxjs';
import { AccessService } from '../access/access.service';
import { activeAccountKey, signedInAccountMayHaveChanged } from '../access/signed-in-account';
import { BusinessService } from '../services/business.service';
import { ToastService } from '../services/toast.service';
import { SupportSessionService } from '../support-session/support-session.service';
import { PreviewRole, ROLE_PREVIEW_ENDED_MESSAGE } from './role-preview';

/**
 * Which role this tab is previewing the application as (issue #721), over the `X-Role-Preview`
 * header issue #720 added to the API.
 *
 * **The preview lives in this instance's memory, for this tab only.** It is never written to
 * `localStorage`, `sessionStorage`, a cookie or the URL, and - unlike a support session - it is
 * never shared with another tab: a reload and a new tab both start with no preview, and there is
 * nothing to resume. {@link currentRole} is read by `rolePreviewInterceptor` alone, to set the
 * header on the requests the API would read it from.
 *
 * **Nothing here decides what the previewed role may do.** The header asks the API to answer the
 * request as that role, and `GET /api/me/access` answers with the narrowed capability set the
 * navigation, the guards and the hidden actions already render from (issue #528). There is no
 * frontend role table in this feature, and `components/admin/roles/role-definitions.ts` - the
 * Roles page's descriptive content - is deliberately not read by it.
 *
 * **Starting and exiting take the same transition a support-context change takes** (issue #670).
 * {@link applyPreviewChange} drops the cached business (`BusinessService.reset`), reports the
 * change on {@link contextChanged$} so the shell reads the business the preview now resolves,
 * re-reads the capabilities (`AccessService.refresh`) - narrowed on the way in, the
 * administrator's own on the way out - and navigates to the dashboard while
 * {@link contextChanging$} withholds the routed page, which is what *replaces* a page the tab was
 * already on rather than leaving the previous answer's figures under a banner that has changed.
 * The role is set or cleared **before** any of that, so the capability read and every request the
 * rebuilt page makes carry (or no longer carry) the header.
 *
 * **Four things end a preview, and only one of them is the Exit button.**
 *
 * | What happened | What it does |
 * | --- | --- |
 * | {@link exit} | Ends it and takes the transition above |
 * | The API refused the preview itself - `role_preview_not_permitted` or `invalid_role_preview` ({@link handleRefusedPreview}) | Ends it, takes the same transition, and says so with a toast, because nobody asked for it |
 * | A support session started or ended ({@link SupportSessionService.contextChanged$}) | Forgets the role only: that transition already drops the business, re-reads the capabilities and rebuilds the page, and doing it twice would navigate twice |
 * | The signed-in identity changed, including a sign-out | Forgets the role only: the preview belonged to the administrator who started it, and the shell and both access caches discard that identity's answers themselves (issues #603 and #604) |
 *
 * A support transition reaches this service **before** it re-reads the capabilities, because
 * `contextChanged$` is published before `AccessService.refresh()` is issued - so that read is the
 * first one made without the header rather than the last one made with it.
 */
@Injectable({ providedIn: 'root' })
export class RolePreviewService implements OnDestroy {
  private readonly roleSubject = new BehaviorSubject<PreviewRole | null>(null);

  /**
   * Whether a preview transition is still settling. It is a count rather than a flag for the same
   * reason the support session's is (issue #670): two transitions can overlap - a preview exited
   * and another started before the first navigation settled - and the page may only come back when
   * the last of them has.
   */
  private pendingContextChanges = 0;
  private readonly contextChangingSubject = new BehaviorSubject(false);
  private readonly contextChangedSubject = new Subject<void>();

  /** The signed-in identity the preview belongs to; a change of it ends the preview. */
  private accountKey: string | null;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly business: BusinessService,
    private readonly access: AccessService,
    private readonly router: Router,
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService,
    private readonly toast: ToastService,
    supportSession: SupportSessionService
  ) {
    this.accountKey = activeAccountKey(this.authService);
    signedInAccountMayHaveChanged(this.msalBroadcastService)
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.applySignedInAccount());

    // Starting or ending a support session changes which business - and which capability set - the
    // next request is answered under, and a preview of a role in the previous context is not a
    // preview of anything in the new one. `contextChanged$` is subscribed rather than `active$`
    // deliberately: the latter is published through `state$`, whose subscription is what starts the
    // service's own-session probe, and a tab must not issue that probe merely because this service
    // exists.
    supportSession.contextChanged$
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.forgetPreview());
  }

  /** The previewed role, and `null` whenever none is active. */
  readonly role$: Observable<PreviewRole | null> = this.roleSubject.asObservable().pipe(distinctUntilChanged());

  /** Whether a preview is active, for the shell and the sidebar. */
  readonly active$: Observable<boolean> = this.role$.pipe(
    map((role) => role !== null),
    distinctUntilChanged()
  );

  /**
   * Whether a preview transition is still settling, which `AppComponent` withholds the routed page
   * for - the same gate `SupportSessionService.contextChanging$` already drives (issue #670).
   */
  readonly contextChanging$: Observable<boolean> = this.contextChangingSubject
    .asObservable()
    .pipe(distinctUntilChanged());

  /**
   * That the context the page reads has changed, once per transition, published before anything in
   * the new one is issued. `AppComponent` reads the business again from here, exactly as it does
   * for a support transition (issue #671).
   */
  readonly contextChanged$: Observable<void> = this.contextChangedSubject.asObservable();

  /** The role to send as `X-Role-Preview`, or `null`. Read by `rolePreviewInterceptor` alone. */
  currentRole(): PreviewRole | null {
    return this.roleSubject.value;
  }

  /** Starts previewing the application as `role`. Choosing the role already in view does nothing. */
  start(role: PreviewRole): void {
    if (this.roleSubject.value === role) {
      return;
    }

    this.roleSubject.next(role);
    this.applyPreviewChange();
  }

  /** Ends the preview from the banner's **Exit preview** action. */
  exit(): void {
    if (this.roleSubject.value === null) {
      return;
    }

    this.roleSubject.next(null);
    this.applyPreviewChange();
  }

  /**
   * The "the API refused the preview itself" signal, read by `rolePreviewInterceptor` alone: a
   * `403` `role_preview_not_permitted` or a `400` `invalid_role_preview`. The administrator did not
   * ask for this, so they are told. Idempotent: a second refusal, or one arriving after the preview
   * has already ended, does nothing further.
   */
  handleRefusedPreview(): void {
    if (this.roleSubject.value === null) {
      return;
    }

    this.roleSubject.next(null);
    this.toast.warning(ROLE_PREVIEW_ENDED_MESSAGE, 'Role preview ended');
    this.applyPreviewChange();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /**
   * Forgets the preview without a transition of its own, for the two changes that already rebuild
   * the page themselves: a support-context transition and a change of signed-in identity.
   */
  private forgetPreview(): void {
    if (this.roleSubject.value !== null) {
      this.roleSubject.next(null);
    }
  }

  /** See the class comment: the one path a started or ended preview takes. */
  private applyPreviewChange(): void {
    this.business.reset();
    this.contextChangedSubject.next();
    this.access.refresh().subscribe();

    this.pendingContextChanges++;
    this.contextChangingSubject.next(true);
    const settled = (): void => {
      this.pendingContextChanges--;
      if (this.pendingContextChanges === 0) {
        this.contextChangingSubject.next(false);
      }
    };
    // A navigation that is refused, redirected or superseded releases the page just the same: the
    // alternative is a shell that withholds every page for the rest of the browser session.
    this.router.navigate(['/'], { onSameUrlNavigation: 'reload' }).then(settled, settled);
  }

  private applySignedInAccount(): void {
    const accountKey = activeAccountKey(this.authService);
    if (accountKey === this.accountKey) {
      return;
    }

    this.accountKey = accountKey;
    // A preview can only have been started by somebody signed in, so there is never one to forget
    // when an identity is merely *learned*; what this guards against is the previous
    // administrator's preview being attached to the next identity's requests.
    this.forgetPreview();
  }
}
