import { Component, EventEmitter, OnDestroy, OnInit, Output } from '@angular/core';
import { Subject, catchError, combineLatest, of, startWith, takeUntil } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { PREVIEWABLE_ROLES, PreviewRole } from '../role-preview/role-preview';
import { RolePreviewService } from '../role-preview/role-preview.service';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';

/**
 * Which roles the **View as** entry offers, given the three answers it is decided from
 * (issue #721). Exported because it is the whole rule, and a pure function is where a rule like
 * this belongs.
 *
 * - **Only the configured platform administrator is offered one at all.** The answer comes from
 *   `GET /api/admin/diagnostics/access` and from nowhere else, exactly as the `Super Admin`
 *   navigation group does (issue #335); no claim, role or stored value is a second source. A
 *   hidden entry hides a control rather than protecting one: the API refuses the header from
 *   anybody else with `role_preview_not_permitted`.
 * - **Only while `GET /api/me/access` has answered.** A preview narrows the capabilities a request
 *   already holds, so there has to be something to narrow: an `unavailable` answer (nobody signed
 *   in, no usable membership, a failed read) offers nothing, and an `unread` one offers nothing
 *   yet.
 * - **Never the caller's own role**, which is what they are already looking at. Inside a support
 *   session `GET /api/me/access` reports no role at all - the administrator holds no membership in
 *   the business being viewed - so all three are offered there.
 * - **Never while a preview is already active.** The way to another role is out of this one first,
 *   so the entry is replaced by the banner's **Exit preview**.
 */
export function offeredPreviewRoles(
  hasPlatformAdminAccess: boolean,
  access: MemberAccessState,
  previewing: PreviewRole | null
): readonly PreviewRole[] {
  if (!hasPlatformAdminAccess || previewing !== null || access.status !== 'granted') {
    return [];
  }

  return PREVIEWABLE_ROLES.filter((role) => role !== access.access.role);
}

/**
 * The **View as** section of the signed-in user menu (issue #721), projected into
 * `UserMenuComponent` so that component stays the presentation-only control it has been since
 * issue #391 and this one owns the access answers the entry is decided from.
 *
 * Choosing a role starts the preview and reports it, which is how the menu above closes: the
 * application is about to rebuild every page under the previewed role's capabilities, and leaving
 * the panel open over it would be the one part of the screen the transition did not touch.
 */
@Component({
  selector: 'app-role-preview-menu',
  standalone: true,
  template: `
    @if (roles.length > 0) {
      <div class="my-1 border-t border-md-gray-200"></div>
      <p [id]="headingId" class="px-3 py-2 text-md-gray-500">View as</p>
      <div role="group" [attr.aria-labelledby]="headingId">
        @for (role of roles; track role) {
          <button
            type="button"
            class="w-full rounded-md-control px-3 py-2 text-left text-md-gray-800 transition hover:bg-md-gray-100"
            (click)="previewAs(role)"
          >
            {{ role }}
          </button>
        }
      </div>
    }
  `
})
export class RolePreviewMenuComponent implements OnInit, OnDestroy {
  /** A preview was started, so the menu that offered it can close. */
  @Output() readonly previewStarted = new EventEmitter<PreviewRole>();

  readonly headingId = 'user-menu-view-as';

  roles: readonly PreviewRole[] = [];

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly access: AccessService,
    private readonly platformAdminAccess: PlatformDiagnosticsAccessService,
    private readonly rolePreview: RolePreviewService
  ) {}

  ngOnInit(): void {
    // `granted$` rather than the one-shot `isGranted()`, for the reason issue #605 gave the
    // sidebar: it keeps following the signed-in identity, so a granted entry goes the moment the
    // Microsoft account changes instead of being left on screen for somebody else. A probe that
    // cannot be answered offers nothing, and reports nothing - a menu is not the place to say an
    // access check failed.
    const hasPlatformAdminAccess = this.platformAdminAccess.granted$.pipe(
      catchError(() => of(false)),
      startWith(false)
    );

    combineLatest([hasPlatformAdminAccess, this.access.access$, this.rolePreview.role$])
      .pipe(takeUntil(this.destroying$))
      .subscribe(([granted, access, previewing]) => {
        this.roles = offeredPreviewRoles(granted, access, previewing);
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  previewAs(role: PreviewRole): void {
    this.rolePreview.start(role);
    this.previewStarted.emit(role);
  }
}
