import { Component, EventEmitter, Input, OnDestroy, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subject, takeUntil } from 'rxjs';
import { ImportService } from '../../../../services/import.service';

/** What the card says when nothing more specific came back from the upload. */
const GENERIC_FAILURE = 'The ZIP could not be uploaded. Try again.';

/** What it says once the server has accepted one. It names no count: nothing is imported yet. */
const ACCEPTED_MESSAGE =
  'Upload accepted. It is queued and imports in the background - follow it in Recent uploads below.';

/**
 * Uploading the reimbursement report ZIP (issue #1152), the write half of the Reimbursement Report
 * page.
 *
 * <p>It is its own feature component rather than markup grown into the routed page: it owns the
 * chosen file, the in-flight upload and the refusal the server answers with, and tells the page only
 * that an upload was accepted (AGENTS.md &sect; Frontend rules, docs/architecture.md &sect; Page
 * composition boundary).</p>
 *
 * <p><strong>The server is the only authority on the archive.</strong> Nothing here opens, unzips,
 * counts or validates anything: `POST api/imports/reimbursement-report` checks the upload against
 * `Inventory.Domain.Imports.ReimbursementArchivePolicy` and answers <code>400</code> with one
 * readable sentence, and that sentence is what this card shows - beside the file it refused, rather
 * than as a toast, because a refused upload stores nothing at all and the operator's next step is to
 * look at the file they chose. The picker is limited to <code>.zip</code> so the file dialog offers
 * the right attachment; that is a convenience and deliberately not a second rule.</p>
 *
 * <p>No count, figure or payment detail is rendered here, and none is available to: the reply
 * carries the queued job's id and nothing else.</p>
 */
@Component({
  selector: 'app-reimbursement-report-upload',
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="card">
      <div class="card-body">
        <h2 class="card-title">Upload the reimbursement report</h2>
        <p class="mt-1 text-sm value-muted" data-testid="reimbursement-upload-note">
          Upload the ZIP exactly as Nayax emails it. Do not unzip it, rename it or take files out of
          it - the whole attachment is what the import reads.
        </p>

        <div class="mt-4 flex flex-col gap-3 sm:flex-row sm:items-center">
          <!--
            The label is the control's accessible name, and the input stays visible rather than
            being replaced by a styled button: a file input is the one control a browser already
            makes keyboard- and screen-reader-operable, and a 390px screen has room for it.
          -->
          <label class="field-label sr-only" for="reimbursement-zip">Reimbursement report ZIP</label>
          <input
            id="reimbursement-zip"
            data-testid="reimbursement-zip-input"
            type="file"
            accept=".zip,application/zip"
            class="max-w-full text-sm"
            [disabled]="disabled || uploading"
            (change)="onSelected($event)"
          />
          <button
            type="button"
            class="btn btn-primary sm:w-auto"
            data-testid="reimbursement-upload-button"
            [disabled]="!canUpload"
            (click)="upload()"
          >
            {{ uploading ? 'Uploading...' : 'Upload' }}
          </button>
        </div>

        @if (fileName !== null) {
          <p class="mt-2 break-words text-sm value-muted" data-testid="reimbursement-selected-file">
            Chosen file: {{ fileName }}
          </p>
        }

        @if (acceptedMessage !== '') {
          <p class="mt-3 text-md-body" role="status" data-testid="reimbursement-upload-accepted">
            {{ acceptedMessage }}
          </p>
        }

        @if (errorMessage !== '') {
          <div class="alert alert-danger mt-3" role="alert" data-testid="reimbursement-upload-error">
            <p>{{ errorMessage }}</p>
          </div>
        }
      </div>
    </section>
  `
})
export class ReimbursementReportUploadComponent implements OnDestroy {
  /**
   * Withholds the upload while a support session is active (issue #661). The endpoint refuses this
   * `POST` centrally either way, so this makes the control genuinely unavailable rather than
   * offering a request that can only be refused.
   */
  @Input() disabled = false;

  /** The accepted job's id, so the page can ask the list to read the job the upload became. */
  @Output() readonly uploaded = new EventEmitter<number>();

  /** The chosen file's name, shown so the operator can see which archive is about to be sent. */
  fileName: string | null = null;

  /** True while the upload is in flight, which is what the busy label and the guard below read. */
  uploading = false;

  /** The server's own refusal sentence, or `''` while nothing has been refused. */
  errorMessage = '';

  /** The acceptance sentence, or `''` while nothing has been accepted. */
  acceptedMessage = '';

  private selected: File | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(private readonly imports: ImportService) {}

  /** Whether there is a file to send and nothing in the way of sending it. */
  get canUpload(): boolean {
    return this.selected !== null && !this.uploading && !this.disabled;
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /**
   * Takes the chosen file and clears the input, so choosing the same file again after a refusal
   * still raises a `change` event. A dismissed picker chooses nothing and leaves the card as it was.
   */
  onSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    input.value = '';
    if (file === null) {
      return;
    }

    this.selected = file;
    this.fileName = file.name;
    this.errorMessage = '';
    this.acceptedMessage = '';
  }

  upload(): void {
    // Checked here as well as disabled in the template, so the upload is actually unavailable
    // during a support session rather than merely inert - the endpoint refuses it either way.
    if (!this.canUpload || this.selected === null) {
      return;
    }

    const file = this.selected;
    this.uploading = true;
    this.errorMessage = '';
    this.acceptedMessage = '';

    this.imports
      .uploadReimbursementReport(file)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (accepted) => {
          this.uploading = false;
          // The accepted archive is cleared, so a second click cannot upload the same one again.
          this.selected = null;
          this.fileName = null;
          this.acceptedMessage = ACCEPTED_MESSAGE;
          this.uploaded.emit(accepted.jobId);
        },
        error: (err: unknown) => {
          this.uploading = false;
          this.errorMessage = refusalMessage(err);
        }
      });
  }
}

/**
 * The server's own sentence where there is one. A bad archive is a `400` whose body is the plain
 * refusal sentence, so that is read first; a `ProblemDetails` body is read next, for a failure
 * further up the pipeline that reports itself that way, and anything else falls back to one
 * sentence of this card's own rather than rendering an object.
 */
function refusalMessage(err: unknown): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string' && body.trim() !== '') {
    return body;
  }

  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? GENERIC_FAILURE;
}
