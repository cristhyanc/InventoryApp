import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { BUSINESS_ROLES } from '../../../services/member.service';
import { MemberInvite, MemberInviteService } from '../../../services/member-invite.service';
import { ToastService } from '../../../services/toast.service';

/** The note's longest accepted length, mirroring `MemberInvitePolicy.MaxNoteLength`. */
const MAX_NOTE_LENGTH = 200;

/** One freshly created invite's link, shown exactly once (issue #534). */
interface CreatedInviteLink {
  readonly invite: MemberInvite;
  readonly link: string;
}

/**
 * The Invites section of the Admin → Users page (issue #534), composed by `AdminUsersComponent`.
 *
 * Creates, lists and cancels invite links over `/api/members/invites` (issue #531). The created
 * link is composed here from the token the creation response carries exactly once - the API has no
 * authoritative knowledge of the web application's address - and is shown once, with a Copy button;
 * this component keeps no other copy of a token anywhere, and the link is gone from view the moment
 * the Owner dismisses it or creates another invite.
 */
@Component({
  selector: 'app-member-invites',
  standalone: true,
  imports: [CommonModule, FormsModule, ConfirmationDialogComponent],
  template: `
    <section class="card">
      <div class="card-header">
        <h2 class="card-title">Invites</h2>
      </div>
      <div class="card-body">
        @if (justCreated) {
          <div class="mb-5 rounded-md border border-md-gray-200 p-4">
            <p class="text-sm font-semibold text-md-gray-800">
              Invite created. Copy this link now - it will not be shown again.
            </p>
            <div class="mt-2 flex flex-wrap items-center gap-2">
              <input
                #linkField
                type="text"
                readonly
                class="min-w-0 flex-1 rounded-lg border border-md-gray-200 p-2 font-mono text-xs"
                [value]="justCreated.link"
                data-testid="invite-link"
              />
              <button type="button" class="btn btn-secondary btn-sm" data-testid="copy-invite-link" (click)="copyLink()">
                Copy
              </button>
            </div>
            <p class="mt-2 text-sm value-muted">Expires {{ justCreated.invite.expiresAtUtc | date: 'medium' }}.</p>
            <button type="button" class="btn btn-secondary btn-sm mt-3" (click)="dismissCreatedLink()">Done</button>
          </div>
        }

        <div class="grid gap-4 sm:grid-cols-3">
          <div class="field">
            <label class="field-label" for="invite-role">Role</label>
            <select id="invite-role" [(ngModel)]="createRole">
              <option value="">Select a role</option>
              @for (role of roles; track role) {
                <option [value]="role">{{ role }}</option>
              }
            </select>
          </div>
          <div class="field sm:col-span-2">
            <label class="field-label" for="invite-note">Note (optional)</label>
            <input id="invite-note" type="text" [maxlength]="maxNoteLength" [(ngModel)]="createNote" />
          </div>
        </div>
        <button type="button" class="btn btn-primary mt-3" [disabled]="creating" (click)="create()">
          {{ creating ? 'Creating…' : 'Create invite' }}
        </button>

        <div class="mt-5 overflow-x-auto border-t border-md-gray-200 pt-4">
          <h3 class="mb-2 text-sm font-semibold text-md-gray-800">Existing invites</h3>
          @if (loading) {
            <p class="text-sm value-muted">Loading invites…</p>
          } @else if (loadError) {
            <p class="text-sm text-red-600">Unable to load invites.</p>
          } @else if (invites.length === 0) {
            <p class="text-sm value-muted">No invites have been created.</p>
          } @else {
            <table class="table">
              <thead class="table-head">
                <tr>
                  <th scope="col" class="table-cell">Role</th>
                  <th scope="col" class="table-cell">Note</th>
                  <th scope="col" class="table-cell">Status</th>
                  <th scope="col" class="table-cell">Created</th>
                  <th scope="col" class="table-cell">Expires</th>
                  <th scope="col" class="table-cell">Actions</th>
                </tr>
              </thead>
              <tbody>
                @for (invite of invites; track invite.inviteId) {
                  <tr class="table-row">
                    <td class="table-cell">{{ invite.role ?? 'Unknown' }}</td>
                    <td class="table-cell">{{ invite.note ?? '—' }}</td>
                    <td class="table-cell">
                      <span class="badge" [class.badge-success]="invite.status === 'Pending'" [class.badge-neutral]="invite.status !== 'Pending'">
                        {{ invite.status ?? 'Unknown' }}
                      </span>
                    </td>
                    <td class="table-cell">{{ invite.createdAtUtc | date: 'short' }}</td>
                    <td class="table-cell">{{ invite.expiresAtUtc | date: 'short' }}</td>
                    <td class="table-cell">
                      @if (invite.status === 'Pending') {
                        <button type="button" class="btn btn-danger btn-sm" (click)="requestCancel(invite)">Cancel</button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </div>
      </div>
    </section>

    @if (pendingCancel) {
      <app-confirmation-dialog
        title="Cancel invite"
        [message]="'Cancel this invite for ' + (pendingCancel.role ?? 'an unknown role') + '?'"
        confirmLabel="Cancel invite"
        cancelLabel="Keep invite"
        (confirmed)="confirmCancel()"
        (canceled)="cancelCancel()"
      ></app-confirmation-dialog>
    }
  `
})
export class MemberInvitesComponent implements OnInit {
  readonly roles = BUSINESS_ROLES;
  readonly maxNoteLength = MAX_NOTE_LENGTH;

  invites: MemberInvite[] = [];
  loading = false;
  loadError = false;
  creating = false;
  createRole = '';
  createNote = '';
  justCreated: CreatedInviteLink | null = null;
  pendingCancel: MemberInvite | null = null;

  constructor(
    private readonly inviteService: MemberInviteService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = false;
    this.inviteService.list().subscribe({
      next: (invites) => {
        this.invites = invites;
        this.loading = false;
      },
      error: () => {
        this.loading = false;
        this.loadError = true;
      }
    });
  }

  create(): void {
    if (!this.createRole) {
      this.toast.error('Select a role for the invite.');
      return;
    }

    this.creating = true;
    const note = this.createNote.trim() === '' ? null : this.createNote.trim();
    this.inviteService.create(this.createRole, note).subscribe({
      next: (created) => {
        this.creating = false;
        this.createRole = '';
        this.createNote = '';
        this.justCreated = { invite: created.invite, link: this.linkFor(created.token) };
        this.invites = [created.invite, ...this.invites];
      },
      error: (err) => {
        this.creating = false;
        this.toast.error(this.extractErrorMessage(err, 'Unable to create the invite.'));
      }
    });
  }

  copyLink(): void {
    const link = this.justCreated?.link;
    if (!link) {
      return;
    }

    const clipboard = navigator.clipboard;
    if (!clipboard?.writeText) {
      this.toast.info('Select the link text and copy it with your keyboard.');
      return;
    }

    clipboard.writeText(link).then(
      () => this.toast.success('Invite link copied.'),
      () => this.toast.info('Select the link text and copy it with your keyboard.')
    );
  }

  dismissCreatedLink(): void {
    this.justCreated = null;
  }

  requestCancel(invite: MemberInvite): void {
    this.pendingCancel = invite;
  }

  cancelCancel(): void {
    this.pendingCancel = null;
  }

  confirmCancel(): void {
    const invite = this.pendingCancel;
    if (!invite) {
      return;
    }
    this.pendingCancel = null;

    this.inviteService.cancel(invite.inviteId).subscribe({
      next: (cancelled) => {
        this.invites = this.invites.map((candidate) => (candidate.inviteId === cancelled.inviteId ? cancelled : candidate));
      },
      error: (err) => this.toast.error(this.extractErrorMessage(err, 'Unable to cancel this invite.'))
    });
  }

  private linkFor(token: string): string {
    return `${window.location.origin}/invite/${token}`;
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
