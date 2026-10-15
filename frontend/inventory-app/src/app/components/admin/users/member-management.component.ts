import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { BUSINESS_ROLES, BusinessMember, MemberService } from '../../../services/member.service';
import { ToastService } from '../../../services/toast.service';

type PendingMemberAction =
  | { readonly kind: 'role'; readonly member: BusinessMember; readonly role: string }
  | { readonly kind: 'revoke'; readonly member: BusinessMember }
  | { readonly kind: 'reinstate'; readonly member: BusinessMember };

/**
 * The Members section of the Admin → Users page (issue #534), composed by `AdminUsersComponent`.
 *
 * Lists every member of the caller's business over `GET /api/members` and carries out the three
 * changes issue #533 exposes - role change, revoke, reinstate - each behind a confirmation dialog.
 * `MemberService` is the sole authority for every rule a change might refuse: a last-Owner change
 * and a reinstatement of someone who has since joined another business are both `409` refusals
 * whose sentence this component shows exactly as the API phrases it, rather than reinterpreting or
 * duplicating the rule. That authority is also why the role control is displayed from this
 * component's own `selectedRoles` state rather than straight from `member.role`: see
 * `displayedRole`.
 */
@Component({
  selector: 'app-member-management',
  standalone: true,
  imports: [CommonModule, ConfirmationDialogComponent],
  template: `
    <section class="card">
      <div class="card-header">
        <h2 class="card-title">Members</h2>
      </div>
      <div class="card-body">
        @if (loading) {
          <p class="text-sm value-muted">Loading members…</p>
        } @else if (loadError) {
          <p class="text-sm text-red-600">Unable to load members.</p>
        } @else if (members.length === 0) {
          <p class="text-sm value-muted">No members found.</p>
        } @else {
          <table class="table">
            <thead class="table-head">
              <tr>
                <th scope="col" class="table-cell">Name</th>
                <th scope="col" class="table-cell">Email</th>
                <th scope="col" class="table-cell">Role</th>
                <th scope="col" class="table-cell">Status</th>
                <th scope="col" class="table-cell">Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (member of members; track member.membershipId) {
                <tr class="table-row">
                  <td class="table-cell">{{ member.displayName ?? '—' }}</td>
                  <td class="table-cell">{{ member.email ?? '—' }}</td>
                  <td class="table-cell">
                    <select
                      #roleSelect
                      [disabled]="!member.isActive"
                      [attr.aria-label]="'Role for ' + memberLabel(member)"
                      (change)="requestRoleChange(member, roleSelect.value)"
                    >
                      @if (!member.role) {
                        <option value="" disabled [selected]="!displayedRole(member)">Unknown</option>
                      }
                      @for (role of roles; track role) {
                        <option [value]="role" [selected]="role === displayedRole(member)">{{ role }}</option>
                      }
                    </select>
                  </td>
                  <td class="table-cell">
                    <span class="badge" [class.badge-success]="member.isActive" [class.badge-neutral]="!member.isActive">
                      {{ member.isActive ? 'Active' : 'Revoked' }}
                    </span>
                  </td>
                  <td class="table-cell">
                    @if (member.isActive) {
                      <button type="button" class="btn btn-danger btn-sm" (click)="requestRevoke(member)">Revoke</button>
                    } @else {
                      <button type="button" class="btn btn-secondary btn-sm" (click)="requestReinstate(member)">
                        Reinstate
                      </button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    </section>

    @if (pendingAction) {
      <app-confirmation-dialog
        [title]="dialogTitle"
        [message]="dialogMessage"
        confirmLabel="Confirm"
        cancelLabel="Cancel"
        (confirmed)="confirmPendingAction()"
        (canceled)="cancelPendingAction()"
      ></app-confirmation-dialog>
    }
  `
})
export class MemberManagementComponent implements OnInit {
  readonly roles = BUSINESS_ROLES;

  members: BusinessMember[] = [];
  loading = false;
  loadError = false;
  pendingAction: PendingMemberAction | null = null;

  /**
   * The role each row's `<select>` is currently displaying, keyed by membership id.
   *
   * The native control moves to the chosen option before anything is confirmed, so the displayed
   * role is this component's state and not `member.role`. A one-way `[value]="member.role"`
   * binding could not put the control back after a cancelled confirmation or a refused change,
   * because the bound expression never changed, and the control would keep showing a role the
   * server never accepted - next to the toast refusing it - and would not even raise a `change`
   * event if the Owner tried the same selection again. Holding the displayed role here means a
   * rollback is a real value change that Angular writes back to the DOM.
   */
  private readonly selectedRoles = new Map<number, string>();

  constructor(
    private readonly memberService: MemberService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = false;
    this.memberService.list().subscribe({
      next: (members) => {
        this.members = members;
        this.selectedRoles.clear();
        this.loading = false;
      },
      error: () => {
        this.loading = false;
        this.loadError = true;
      }
    });
  }

  /** The role the member's row shows: the one proposed in this session, or the server-confirmed one. */
  displayedRole(member: BusinessMember): string {
    return this.selectedRoles.get(member.membershipId) ?? member.role ?? '';
  }

  requestRoleChange(member: BusinessMember, role: string): void {
    this.selectedRoles.set(member.membershipId, role);
    if (!role || role === member.role) {
      return;
    }
    this.pendingAction = { kind: 'role', member, role };
  }

  requestRevoke(member: BusinessMember): void {
    this.pendingAction = { kind: 'revoke', member };
  }

  requestReinstate(member: BusinessMember): void {
    this.pendingAction = { kind: 'reinstate', member };
  }

  cancelPendingAction(): void {
    const action = this.pendingAction;
    this.pendingAction = null;
    if (action?.kind === 'role') {
      this.restoreDisplayedRole(action.member);
    }
  }

  confirmPendingAction(): void {
    const action = this.pendingAction;
    if (!action) {
      return;
    }
    this.pendingAction = null;

    switch (action.kind) {
      case 'role':
        this.memberService.changeRole(action.member.membershipId, action.role).subscribe({
          next: (updated) => this.replaceMember(updated),
          error: (err) => {
            this.restoreDisplayedRole(action.member);
            this.toast.error(this.extractErrorMessage(err, "Unable to change this member's role."));
          }
        });
        break;
      case 'revoke':
        this.memberService.revoke(action.member.membershipId).subscribe({
          next: (updated) => this.replaceMember(updated),
          error: (err) => this.toast.error(this.extractErrorMessage(err, 'Unable to revoke this member.'))
        });
        break;
      case 'reinstate':
        this.memberService.reinstate(action.member.membershipId).subscribe({
          next: (updated) => this.replaceMember(updated),
          error: (err) => this.toast.error(this.extractErrorMessage(err, 'Unable to reinstate this member.'))
        });
        break;
    }
  }

  get dialogTitle(): string {
    switch (this.pendingAction?.kind) {
      case 'role':
        return 'Change role';
      case 'revoke':
        return 'Revoke member';
      case 'reinstate':
        return 'Reinstate member';
      default:
        return '';
    }
  }

  get dialogMessage(): string {
    const action = this.pendingAction;
    if (!action) {
      return '';
    }
    const name = this.memberLabel(action.member);
    switch (action.kind) {
      case 'role':
        return `Change ${name}'s role to ${action.role}?`;
      case 'revoke':
        return `Revoke ${name}'s access?`;
      case 'reinstate':
        return `Reinstate ${name}'s access?`;
      default:
        return '';
    }
  }

  memberLabel(member: BusinessMember): string {
    return member.displayName ?? member.email ?? `member ${member.membershipId}`;
  }

  /** Puts the row's control back on the role the server last confirmed, discarding an unsaved choice. */
  private restoreDisplayedRole(member: BusinessMember): void {
    this.selectedRoles.set(member.membershipId, member.role ?? '');
  }

  private replaceMember(updated: BusinessMember): void {
    this.members = this.members.map((candidate) => (candidate.membershipId === updated.membershipId ? updated : candidate));
    this.restoreDisplayedRole(updated);
  }

  private extractErrorMessage(err: unknown, fallback: string): string {
    const body = (err as { error?: unknown } | undefined)?.error;
    if (typeof body === 'string') {
      return body;
    }
    const problem = body as { message?: string; title?: string } | undefined;
    return problem?.message ?? problem?.title ?? fallback;
  }
}
