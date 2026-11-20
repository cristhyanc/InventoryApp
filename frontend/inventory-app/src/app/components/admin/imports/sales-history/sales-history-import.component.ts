import { Component, Input, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subject, Subscription, takeUntil, timer } from 'rxjs';
import { BusinessDateTimePipe } from '../../../../formatting/business-date-time.pipe';
import {
  ImportService,
  NayaxSalesHistoryImportJobSummary
} from '../../../../services/import.service';
import {
  anySalesHistoryJobRunning,
  salesHistoryJobCounts,
  salesHistoryJobProgress,
  salesHistoryJobStateBadge,
  salesHistoryJobStateLabel
} from './sales-history-import-job-display';

/**
 * How long the list waits before asking again while a job is still queued or importing.
 *
 * It is exported so the specs drive the refresh from the same number the component does, rather
 * than from a copy of it that can drift.
 */
export const SALES_HISTORY_JOBS_REFRESH_MS = 5000;

/** What the card says when nothing more specific came back from the upload. */
const GENERIC_FAILURE = 'The file could not be uploaded. Try again.';

/** What it says once the server has accepted one. It names no count: nothing is imported yet. */
const ACCEPTED_MESSAGE =
  'Upload accepted. It imports in the background, a batch at a time - follow it below. You can ' +
  'leave this page and come back to it.';

const LIST_FAILURE = 'The recent sales history uploads could not be read.';

/**
 * Importing a long Nayax sales history (issue #1304): the upload, and what this business's uploads
 * turned into.
 *
 * <p>It is its own feature component rather than markup grown into the Imports page: it owns the
 * chosen file, the in-flight upload, the server's refusal, the job list, its refresh lifecycle and
 * the failures of both (AGENTS.md &sect; Frontend rules, docs/architecture.md &sect; Page
 * composition boundary). The page composes it and tells it only whether the import is available at
 * all.</p>
 *
 * <p><strong>The server is the only authority on the file and on every number.</strong> Nothing here
 * opens, counts or validates anything: <code>POST api/imports/nayax-sales-history</code> checks the
 * upload against <code>Inventory.Domain.Imports.NayaxSalesHistoryPolicy</code> - the byte limit, the
 * format, the file's shape and the 250,000-row ceiling - and answers <code>400</code> with one
 * readable sentence, which is what this card shows beside the file it refused. The picker is limited
 * to <code>.csv</code> so the file dialog offers the right export; that is a convenience and
 * deliberately not a second rule.</p>
 *
 * <p><strong>The progress is the server's record, not a guess.</strong> Every count a row shows was
 * written in the same transaction as the sales it describes, so "1,500 of 6,000 rows" means 1,500
 * rows are actually imported - and is also where the job resumes from if it is interrupted.</p>
 *
 * <p><strong>The refresh stops by itself.</strong> A job is live while the server reports
 * <code>Queued</code> or <code>Importing</code>; while one is, the list asks again every
 * {@link SALES_HISTORY_JOBS_REFRESH_MS} milliseconds, and as soon as none is it stops asking - so a
 * settled page polls nothing, a closed page polls nothing, and leaving and coming back reads the
 * current state once on arrival. A failed read also stops the refresh and offers to read again,
 * rather than hammering an endpoint that is already answering badly.</p>
 */
@Component({
  selector: 'app-sales-history-import',
  standalone: true,
  imports: [CommonModule, BusinessDateTimePipe],
  template: `
    <section class="card">
      <div class="card-body">
        <h2 class="card-title">Import a sales history</h2>
        <p class="mt-1 text-sm value-muted" data-testid="sales-history-note">
          Bring in one to three years of Nayax sales as a CSV of up to 250,000 rows. The upload is
          accepted straight away and imports in the background, so other saves keep working while it
          runs. Export the history from Nayax Core as CSV - a spreadsheet is not accepted.
        </p>

        <div class="mt-4 flex flex-col gap-3 sm:flex-row sm:items-center">
          <!--
            The label is the control's accessible name, and the input stays visible rather than
            being replaced by a styled button: a file input is the one control a browser already
            makes keyboard- and screen-reader-operable, and a 390px screen has room for it.
          -->
          <label class="field-label sr-only" for="sales-history-csv">Sales history CSV</label>
          <input
            id="sales-history-csv"
            data-testid="sales-history-file-input"
            type="file"
            accept=".csv,text/csv"
            class="max-w-full text-sm"
            [disabled]="disabled || uploading"
            (change)="onSelected($event)"
          />
          <button
            type="button"
            class="btn btn-primary sm:w-auto"
            data-testid="sales-history-upload-button"
            [disabled]="!canUpload"
            (click)="upload()"
          >
            {{ uploading ? 'Uploading...' : 'Upload' }}
          </button>
        </div>

        @if (fileName !== null) {
          <p class="mt-2 break-words text-sm value-muted" data-testid="sales-history-selected-file">
            Chosen file: {{ fileName }}
          </p>
        }

        @if (acceptedMessage !== '') {
          <p class="mt-3 text-md-body" role="status" data-testid="sales-history-upload-accepted">
            {{ acceptedMessage }}
          </p>
        }

        @if (uploadError !== '') {
          <div class="alert alert-danger mt-3" role="alert" data-testid="sales-history-upload-error">
            <p>{{ uploadError }}</p>
          </div>
        }

        <h3 class="mt-6 text-md-title">Recent sales history uploads</h3>

        @if (status === 'loading') {
          <output class="mt-3 block text-sm value-muted" data-testid="sales-history-jobs-loading">
            Loading...
          </output>
        }

        @if (listError !== '') {
          <div class="alert alert-warning mt-3" role="alert" data-testid="sales-history-jobs-error">
            <p>{{ listError }}</p>
            <button
              type="button"
              class="btn btn-secondary btn-sm mt-2"
              data-testid="sales-history-jobs-retry"
              (click)="retry()"
            >
              Try again
            </button>
          </div>
        }

        @if (status === 'ready' && jobs.length === 0 && listError === '') {
          <p class="mt-3 text-sm value-muted" data-testid="sales-history-jobs-empty">
            No sales history has been uploaded yet. The uploads you make appear here.
          </p>
        }

        @if (jobs.length > 0) {
          <!--
            A stacked list rather than a table: at 390px a table of five columns either scrolls
            sideways or truncates the one thing an operator came to read, so each upload is its own
            block that lays its summary out in a row only once there is room for one.
          -->
          <ul class="mt-4 flex flex-col">
            @for (job of jobs; track job.id) {
              <li
                class="flex flex-col gap-2 border-t border-md-gray-200 py-3 first:border-t-0"
                data-testid="sales-history-job-row"
              >
                <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                  <div class="flex min-w-0 flex-col gap-1">
                    <span class="break-words font-medium text-md-gray-800" data-testid="job-file">{{
                      job.fileName
                    }}</span>
                    <span class="text-sm value-muted" data-testid="job-uploaded-at">{{
                      job.uploadedAtUtc | businessDateTime
                    }}</span>
                  </div>
                  <div class="flex flex-wrap items-center gap-2">
                    <span [class]="stateBadge(job)" data-testid="job-state">{{ stateLabel(job) }}</span>
                    <span class="text-sm value-muted" data-testid="job-progress">{{ progress(job) }}</span>
                  </div>
                </div>

                <span class="text-sm value-muted" data-testid="job-counts">{{ counts(job) }}</span>

                @if (job.lastError) {
                  <p class="break-words text-sm value-muted" data-testid="job-last-error">
                    {{ job.lastError }}
                  </p>
                }
              </li>
            }
          </ul>
        }
      </div>
    </section>
  `
})
export class SalesHistoryImportComponent implements OnInit, OnDestroy {
  /**
   * Withholds the upload while a support session is active (issue #661). The endpoint refuses this
   * `POST` centrally either way, so this makes the control genuinely unavailable rather than
   * offering a request that can only be refused.
   */
  @Input() disabled = false;

  /** The chosen file's name, shown so the operator can see which file is about to be sent. */
  fileName: string | null = null;

  /** True while the upload is in flight, which is what the busy label and the guard below read. */
  uploading = false;

  /** The server's own refusal sentence, or `''` while nothing has been refused. */
  uploadError = '';

  /** The acceptance sentence, or `''` while nothing has been accepted. */
  acceptedMessage = '';

  /** `loading` until the first answer - success or failure - has arrived. */
  status: 'loading' | 'ready' = 'loading';

  jobs: readonly NayaxSalesHistoryImportJobSummary[] = [];

  /** Why the last read could not be made, or `''` when the last one succeeded. */
  listError = '';

  private selected: File | null = null;

  private refresh?: Subscription;

  /** The one list read that may still change the screen. See {@link load}. */
  private listRequest?: Subscription;

  private readonly destroying$ = new Subject<void>();

  constructor(private readonly imports: ImportService) {}

  /** Whether there is a file to send and nothing in the way of sending it. */
  get canUpload(): boolean {
    return this.selected !== null && !this.uploading && !this.disabled;
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  stateLabel(job: NayaxSalesHistoryImportJobSummary): string {
    return salesHistoryJobStateLabel(job.state);
  }

  stateBadge(job: NayaxSalesHistoryImportJobSummary): string {
    return salesHistoryJobStateBadge(job.state);
  }

  /** How far through the file the job is, as the server recorded it. */
  progress(job: NayaxSalesHistoryImportJobSummary): string {
    return salesHistoryJobProgress(job);
  }

  /** What the rows turned into, as the server counted them. */
  counts(job: NayaxSalesHistoryImportJobSummary): string {
    return salesHistoryJobCounts(job);
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
    this.uploadError = '';
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
    this.uploadError = '';
    this.acceptedMessage = '';

    this.imports
      .uploadNayaxSalesHistory(file)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: () => {
          this.uploading = false;
          // The accepted file is cleared, so a second click cannot upload the same one again.
          this.selected = null;
          this.fileName = null;
          this.acceptedMessage = ACCEPTED_MESSAGE;

          // The job the upload became appears without a page reload.
          this.load();
        },
        error: (err: unknown) => {
          this.uploading = false;
          this.uploadError = refusalMessage(err);
        }
      });
  }

  retry(): void {
    this.listError = '';
    this.load();
  }

  /**
   * Reads the list, and makes that read the only one that may still change the screen.
   *
   * <p>Reads overlap: an accepted upload reloads immediately while a refresh is still in flight.
   * Whichever was asked for first can answer last, and an answer older than the upload does not know
   * about the job it accepted - so left alone it would put the pre-upload list back, take the queued
   * job off screen and, finding nothing running, stop the refresh that was following it. Cancelling
   * the superseded read ends that: it is unsubscribed before the new one is made, so its answer and
   * its failure both reach nobody.</p>
   */
  private load(): void {
    this.refresh?.unsubscribe();
    this.refresh = undefined;
    this.listRequest?.unsubscribe();

    this.listRequest = this.imports
      .nayaxSalesHistoryJobs()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (jobs) => {
          this.jobs = jobs;
          this.status = 'ready';
          this.listError = '';
          this.schedule();
        },
        error: () => {
          // What was already read stays on screen: it is still the last thing the server said.
          this.status = 'ready';
          this.listError = LIST_FAILURE;
        }
      });
  }

  /** Arms the next read, and only while the server still has something to move on. */
  private schedule(): void {
    this.refresh?.unsubscribe();
    this.refresh = undefined;

    if (!anySalesHistoryJobRunning(this.jobs)) {
      return;
    }

    this.refresh = timer(SALES_HISTORY_JOBS_REFRESH_MS)
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.load());
  }
}

/**
 * The server's own sentence where there is one. A refused file is a `400` whose body is the plain
 * refusal sentence, so that is read first; a `ProblemDetails` body is read next, for a failure
 * further up the pipeline that reports itself that way, and anything else falls back to one sentence
 * of this card's own rather than rendering an object.
 */
function refusalMessage(err: unknown): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string' && body.trim() !== '') {
    return body;
  }

  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? GENERIC_FAILURE;
}
