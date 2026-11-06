import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import {
  SuperAdminNayaxSqsSender,
  SuperAdminNayaxSqsSenderService
} from '../../../services/super-admin-nayax-sqs-sender.service';

/**
 * The **Nayax SQS sender** card of the Super Admin Businesses page, over the two endpoints of issue
 * #1101: which AWS IAM user sends this business's Nayax sales to the shared Restockroo queue.
 *
 * It owns its own load, form state, submission state and error, which is what makes it a distinct
 * workflow per docs/architecture.md § Page composition boundary - the routed page composes it
 * through `[businessId]`/`[businessName]` and reacts only to `(saved)`/`(closed)`.
 *
 * **It decides nothing.** What a sender id may look like, whether the mapping may be switched on,
 * whether the caller's view is stale, whether another business already holds the id and whether the
 * business may be written at all are all the server's answers; this card shows them and shows the
 * server's own refusal sentence when one comes back. In particular the revision it submits is the one
 * the last response carried - or `null` when the server answered `404`, which is how the first row is
 * created - and a `409` reloads the card and says it changed elsewhere rather than being retried with
 * a guessed number.
 *
 * **Remove is not a delete.** The server keeps the row and its audit history and clears the id; the
 * confirmation says so, and says that the id becomes free for another business, because that is the
 * consequence an administrator has to weigh. Every path that would remove the id reaches that one
 * confirmation - the Remove button, and submitting a cleared field over a recorded sender.
 *
 * **No AWS credential is ever asked for.** The one field here is an IAM user *id*, which AWS
 * publishes; there is no field for an access key or a secret anywhere in this application.
 */
@Component({
  selector: 'app-business-nayax-sqs-sender-card',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="modal-backdrop" role="dialog" aria-modal="true" [attr.aria-label]="'Nayax SQS sender for ' + businessName">
      <div class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg">
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800">
          Nayax SQS sender for {{ businessName }}
        </h2>

        @if (loading) {
          <p class="text-sm value-muted">Loading the recorded sender…</p>
        } @else if (loadError) {
          <p class="mb-3 text-sm text-red-600" role="alert">{{ loadError }}</p>
        } @else if (confirmingRemove) {
          <p class="mb-3 text-md-body text-md-gray-600" data-testid="remove-confirmation">
            Remove this business's AWS sender ID? The recorded ID and its history are kept in the audit
            trail, the mapping is turned off, and the sender ID becomes free for another business.
          </p>

          @if (errorMessage) {
            <p class="mb-3 text-sm text-red-600" role="alert">{{ errorMessage }}</p>
          }

          <div class="confirm-actions">
            <button
              type="button"
              class="btn btn-secondary btn-sm"
              data-testid="cancel-remove"
              [disabled]="submitting"
              (click)="cancelRemove()"
            >
              Cancel
            </button>
            <button
              type="button"
              class="btn btn-danger btn-sm"
              data-testid="confirm-remove"
              [disabled]="submitting"
              (click)="remove()"
            >
              {{ submitting ? 'Working…' : 'Remove sender' }}
            </button>
          </div>
        } @else {
          <dl class="mb-3 text-md-body text-md-gray-600">
            <div class="flex gap-2">
              <dt class="font-medium">Sender ID</dt>
              <dd data-testid="current-sender">{{ sender?.awsSenderId ?? 'None' }}</dd>
            </div>
            <div class="flex gap-2">
              <dt class="font-medium">Status</dt>
              <dd data-testid="current-status">{{ sender?.isEnabled ? 'On' : 'Off' }}</dd>
            </div>
            <div class="flex gap-2">
              <dt class="font-medium">Last message received</dt>
              <dd data-testid="last-message">{{ lastMessage }}</dd>
            </div>
          </dl>

          <label class="mb-1 block text-sm font-medium text-md-gray-700" for="aws-sender-id">
            AWS sender ID
          </label>
          <input
            id="aws-sender-id"
            type="text"
            class="mb-1 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
            maxlength="128"
            autocomplete="off"
            [disabled]="submitting"
            [(ngModel)]="awsSenderId"
          />
          <p class="mb-3 text-xs value-muted" data-testid="sender-id-help">
            Find it in AWS CloudShell:
            <code>aws iam get-user --user-name &lt;user&gt; --query User.UserId</code>. It is the IAM
            user's unique ID (for example AIDAEXAMPLEUSERID123), never an access key.
          </p>

          @if (errorMessage) {
            <p class="mb-3 text-sm text-red-600" role="alert">{{ errorMessage }}</p>
          }

          <div class="confirm-actions">
            <button type="button" class="btn btn-secondary btn-sm" [disabled]="submitting" (click)="close()">
              Close
            </button>
            @if (sender?.isEnabled) {
              <button
                type="button"
                class="btn btn-secondary btn-sm"
                data-testid="turn-off-sender"
                [disabled]="submitting"
                (click)="turnOff()"
              >
                {{ submitting ? 'Working…' : 'Turn off' }}
              </button>
            }
            @if (sender?.awsSenderId) {
              <button
                type="button"
                class="btn btn-danger btn-sm"
                data-testid="remove-sender"
                [disabled]="submitting"
                (click)="requestRemove()"
              >
                Remove
              </button>
            }
            <button
              type="button"
              class="btn btn-primary btn-sm"
              data-testid="save-sender"
              [disabled]="submitting"
              (click)="save()"
            >
              {{ submitting ? 'Working…' : saveLabel }}
            </button>
          </div>
        }
      </div>
    </div>
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

      .confirm-modal {
        max-width: 480px;
        width: 100%;
      }

      .confirm-actions {
        display: flex;
        justify-content: flex-end;
        gap: 0.75rem;
        flex-wrap: wrap;
      }
    `
  ]
})
export class BusinessNayaxSqsSenderCardComponent implements OnInit {
  @Input({ required: true }) businessId!: number;
  @Input({ required: true }) businessName!: string;

  /** Emitted after a committed recording, change, switch or removal. */
  @Output() readonly saved = new EventEmitter<SuperAdminNayaxSqsSender>();
  @Output() readonly closed = new EventEmitter<void>();

  awsSenderId = '';

  loading = false;
  loadError: string | null = null;
  submitting = false;
  errorMessage: string | null = null;
  confirmingRemove = false;

  sender: SuperAdminNayaxSqsSender | null = null;

  /**
   * The revision the server last reported, and `null` for a business nothing has been recorded for -
   * which is the value its first write has to present. Never a number this card chose.
   */
  private revision: number | null = null;

  constructor(private readonly senders: SuperAdminNayaxSqsSenderService) {}

  ngOnInit(): void {
    this.load();
  }

  get saveLabel(): string {
    return this.sender?.awsSenderId ? 'Change sender' : 'Set sender';
  }

  /**
   * The last message in Sydney time, as the issue asks for it, or "Never" for a sender that has never
   * sent anything. The zone is named explicitly rather than taken from the browser, because this page
   * is the platform's own and resolves no business whose zone it could use.
   */
  get lastMessage(): string {
    const received = this.sender?.lastMessageReceivedAtUtc;
    if (!received) {
      return 'Never';
    }

    // dd/MM/yyyy with a four-digit year, matching how every other instant on the Super Admin pages
    // is written. The fields are named rather than taken from a `dateStyle` preset, because a preset
    // abbreviates the year.
    return new Intl.DateTimeFormat('en-AU', {
      timeZone: 'Australia/Sydney',
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
      hour: 'numeric',
      minute: '2-digit'
    }).format(new Date(received));
  }

  close(): void {
    if (this.submitting) {
      return;
    }
    this.closed.emit();
  }

  requestRemove(): void {
    this.errorMessage = null;
    this.confirmingRemove = true;
  }

  cancelRemove(): void {
    if (this.submitting) {
      return;
    }
    this.errorMessage = null;
    this.confirmingRemove = false;
    // Nothing was written, so the card goes back to showing the recorded id - including when the
    // confirmation was reached by clearing the field, which must not leave the form contradicting
    // the mapping it reports above it.
    this.awsSenderId = this.sender?.awsSenderId ?? '';
  }

  /**
   * Records or changes the sender, and turns the mapping on - the only action that does.
   *
   * **A cleared field is not a change.** With an id recorded, submitting nothing is a removal: it
   * would clear the mapping and release its globally unique id for another business. That is the
   * decision the issue requires a confirmation for, so this path shows the same confirmation the
   * Remove button shows rather than committing it on one click. With nothing recorded there is
   * nothing to remove, so the card asks for an id instead of writing an empty row.
   */
  save(): void {
    const submitted = this.awsSenderId.trim();
    if (submitted === '') {
      if (this.sender?.awsSenderId) {
        this.requestRemove();
        return;
      }

      this.errorMessage = 'Enter an AWS sender ID, or close this card to leave this business without one.';
      return;
    }

    this.submit(submitted, true);
  }

  /** Turns the mapping off and keeps the recorded id, which is a different decision from removing it. */
  turnOff(): void {
    this.submit(this.sender?.awsSenderId ?? null, false);
  }

  /** Removes the id: the server clears it, keeps the row, and frees the id for another business. */
  remove(): void {
    this.submit(null, false);
  }

  private submit(awsSenderId: string | null, isEnabled: boolean): void {
    this.submitting = true;
    this.errorMessage = null;

    this.senders.set(this.businessId, awsSenderId, isEnabled, this.revision).subscribe({
      next: (sender) => {
        this.submitting = false;
        this.confirmingRemove = false;
        this.apply(sender);
        this.saved.emit(sender);
      },
      error: (err: unknown) => {
        this.submitting = false;

        // A 409 means somebody else decided this since the card was loaded. Reloading is the only
        // honest next step: the revision this card holds is no longer one the server will accept,
        // and guessing the next one would overwrite their decision.
        if (statusOf(err) === 409) {
          this.confirmingRemove = false;
          this.errorMessage = extractErrorMessage(
            err,
            'This sender changed elsewhere. The card has been reloaded.'
          );
          this.load({ keepError: true });
          return;
        }

        this.errorMessage = extractErrorMessage(err, 'Unable to save this sender.');
      }
    });
  }

  private load(options: { keepError?: boolean } = {}): void {
    this.loading = true;
    this.loadError = null;
    if (!options.keepError) {
      this.errorMessage = null;
    }

    this.senders.get(this.businessId).subscribe({
      next: (sender) => {
        this.loading = false;
        this.apply(sender);
      },
      error: (err: unknown) => {
        this.loading = false;

        // 404 is not a failure here: it is "nothing has ever been recorded for this business", which
        // is the state the first write presents a null revision for.
        if (statusOf(err) === 404) {
          this.sender = null;
          this.revision = null;
          this.awsSenderId = '';
          return;
        }

        this.loadError = extractErrorMessage(err, 'Unable to load this business’s Nayax SQS sender.');
      }
    });
  }

  /**
   * Takes the whole state from the server's answer - the id to show, the flag, the last message and
   * the revision the next write must present - so a committed decision leaves the card showing what
   * was stored rather than what was typed.
   */
  private apply(sender: SuperAdminNayaxSqsSender): void {
    this.sender = sender;
    this.revision = sender.revision;
    this.awsSenderId = sender.awsSenderId ?? '';
  }
}

function statusOf(err: unknown): number | null {
  return err instanceof HttpErrorResponse ? err.status : ((err as { status?: number })?.status ?? null);
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
