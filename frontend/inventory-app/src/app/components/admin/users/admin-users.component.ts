import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MemberManagementComponent } from './member-management.component';
import { MemberInvitesComponent } from './member-invites.component';

/**
 * The Admin → Users page (issue #534, one slice of #505 under #328): where an Owner manages their
 * business's people and invite links from one page.
 *
 * This routed page is composition only (docs/architecture.md § Page composition boundary, issue
 * #191): the Members section (`MemberManagementComponent`, issue #533's four endpoints) and the
 * Invites section (`MemberInvitesComponent`, issue #531's three endpoints) are each a distinct
 * workflow with its own substantial UI, state, actions and error lifecycle, so each owns its own
 * service calls and confirmation dialogs rather than growing into this page.
 *
 * The route already requires `Members.Manage` (`app.routes.ts`), which only an Owner holds; the
 * endpoints behind both sections enforce the same capability themselves and refuse every other
 * caller regardless of what this page renders.
 */
@Component({
  selector: 'app-admin-users',
  standalone: true,
  imports: [RouterLink, MemberManagementComponent, MemberInvitesComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Users</h1>
          <p class="page-subtitle">Manage your business's members and invite links.</p>
        </div>
      </header>

      <div class="grid gap-5">
        <app-member-management></app-member-management>
        <app-member-invites></app-member-invites>
      </div>
    </div>
  `
})
export class AdminUsersComponent {}
