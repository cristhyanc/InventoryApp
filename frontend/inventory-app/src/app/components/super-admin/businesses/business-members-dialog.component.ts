import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { SuperAdminBusinessMember, SuperAdminBusinessesService } from '../../../services/super-admin-businesses.service';

/**
 * The Members dialog of the Super Admin Businesses page (issue #547), for one deactivated
 * business - `GET`/`POST .../release` of issue #545.
 *
 * It owns its own load, its own release confirmation and its own error, which is what makes it a
 * distinct workflow per docs/architecture.md § Page composition boundary: the routed page
 * composes it through `[businessId]`/`[businessName]` and reacts only to
 * `(closed)`/`(memberReleased)`, the latter because a release changes the business's own
 * `activeMemberCount` the page's table shows.
 */
@Component({
  selector: 'app-business-members-dialog',
  standalone: true,
  imports: [CommonModule, ConfirmationDialogComponent],
  template: `
    <div class="modal-backdrop" role="dialog" aria-modal="true" [attr.aria-label]="businessName + ' members'">
      <div class="members-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg">
        <div class="mb-3 flex items-center justify-between">
          <h2 class="text-md-card-title font-semibold text-md-gray-800">{{ businessName }} - Members</h2>
          <button type="button" class="btn btn-secondary btn-sm" (click)="close()">Close</button>
        </div>

        @if (loading) {
          <p class="text-sm value-muted">Loading members…</p>
        } @else if (loadError) {
          <p class="text-sm text-red-600" role="alert">Unable to load this business's members.</p>
        } @else if (members.length === 0) {
          <p class="text-sm value-muted">No members found.</p>
        } @else {
          <table class="table">
            <thead class="table-head">
              <tr>
                <th scope="col" class="table-cell">Name</th>
                <th scope="col" class="table-cell">Role</th>
                <th scope="col" class="table-cell">Status</th>
                <th scope="col" class="table-cell">Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (member of members; track member.membershipId) {
                <tr class="table-row">
                  <td class="table-cell">{{ member.displayName ?? '—' }}</td>
                  <td class="table-cell">{{ member.role ?? 'Unknown' }}</td>
                  <td class="table-cell">
                    <span class="badge" [class.badge-success]="member.isActive" [class.badge-neutral]="!member.isActive">
                      {{ member.isActive ? 'Active' : 'Released' }}
                    </span>
                  </td>
                  <td class="table-cell">
                    @if (member.isActive) {
                      <button type="button" class="btn btn-danger btn-sm" (click)="requestRelease(member)">Release</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }

        @if (releaseError) {
          <p class="mt-3 text-sm text-red-600" role="alert">{{ releaseError }}</p>
        }
      </div>
    </div>

    @if (pendingRelease) {
      <app-confirmation-dialog
        title="Release member"
        [message]="'Release ' + (pendingRelease.displayName ?? 'this member') + ' from ' + businessName + '?'"
        confirmLabel="Confirm"
        cancelLabel="Cancel"
        (confirmed)="confirmRelease()"
        (canceled)="pendingRelease = null"
      ></app-confirmation-dialog>
    }
  `,
  styles: [
    `
      .modal-backdrop {
        position: fixed;
        inset: 0;
        background: rgba(15, 23, 42, 0.5);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 50;
        padding: 1rem;
      }

      .members-modal {
        max-width: 560px;
        width: 100%;
        max-height: calc(100vh - 2rem);
        overflow-y: auto;
      }
    `
  ]
})
export class BusinessMembersDialogComponent implements OnInit {
  @Input({ required: true }) businessId!: number;
  @Input({ required: true }) businessName!: string;

  @Output() readonly closed = new EventEmitter<void>();
  @Output() readonly memberReleased = new EventEmitter<void>();

  members: SuperAdminBusinessMember[] = [];
  loading = false;
  loadError = false;
  pendingRelease: SuperAdminBusinessMember | null = null;
  releaseError: string | null = null;

  constructor(private readonly businesses: SuperAdminBusinessesService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = false;
    this.businesses.members(this.businessId).subscribe({
      next: (members) => {
        this.members = members;
        this.loading = false;
      },
      error: () => {
        this.loading = false;
        this.loadError = true;
      }
    });
  }

  requestRelease(member: SuperAdminBusinessMember): void {
    this.releaseError = null;
    this.pendingRelease = member;
  }

  confirmRelease(): void {
    const member = this.pendingRelease;
    if (!member) {
      return;
    }
    this.pendingRelease = null;

    this.businesses.releaseMember(this.businessId, member.membershipId).subscribe({
      next: (updated) => {
        this.members = this.members.map((candidate) =>
          candidate.membershipId === updated.membershipId ? updated : candidate
        );
        this.businesses.notifyChanged();
        this.memberReleased.emit();
      },
      error: (err: unknown) => {
        this.releaseError = extractErrorMessage(err, 'Unable to release this member.');
      }
    });
  }

  close(): void {
    this.closed.emit();
  }
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
