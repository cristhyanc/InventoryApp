import { Component, Input, OnChanges, OnDestroy, OnInit, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subject, Subscription, takeUntil, timer } from 'rxjs';
import { BusinessDateTimePipe } from '../../../../formatting/business-date-time.pipe';
import {
  ImportService,
  ReimbursementImportFileOutcome,
  ReimbursementImportJobFileSummary,
  ReimbursementImportJobSummary
} from '../../../../services/import.service';
import {
  anyReimbursementJobRunning,
  reimbursementJobStateBadge,
  reimbursementJobStateLabel
} from './reimbursement-import-job-display';

/**
 * How long the list waits before asking again while a job is still queued or processing.
 *
 * It is exported so the specs drive the refresh from the same number the component does, rather
 * than from a copy of it that can drift.
 */
export const REIMBURSEMENT_JOBS_REFRESH_MS = 5000;

const LIST_FAILURE = 'The recent uploads could not be read.';

const DETAIL_FAILURE = 'The files in this upload could not be read.';

/**
 * This business's recent reimbursement report uploads (issue #1152), read from #1149's two status
 * endpoints.
 *
 * <p>It is its own feature component for the reason the upload card beside it is: it owns a read, a
 * refresh lifecycle, a per-row expansion with a second read, and the failures of both
 * (docs/architecture.md &sect; Page composition boundary). The page composes it and tells it only
 * when a new upload has been accepted.</p>
 *
 * <p><strong>It decides nothing about an import.</strong> Every state, count and reason on screen is
 * the server's answer - the counts are counted there from its own per-file outcome rows - and this
 * component only names them (`reimbursement-import-job-display.ts`). It renders no XML content, no
 * figure and no payment detail, because the status contract carries none.</p>
 *
 * <p><strong>The refresh stops by itself.</strong> A job is live while the server reports `Queued` or
 * `Processing`; while one is, the list asks again every
 * {@link REIMBURSEMENT_JOBS_REFRESH_MS} milliseconds, and as soon as none is it stops asking - so a
 * settled page polls nothing, a closed page polls nothing, and leaving and coming back reads the
 * current state once on arrival. A failed read also stops the refresh and offers to read again,
 * rather than hammering an endpoint that is already answering badly.</p>
 */
@Component({
  selector: 'app-reimbursement-import-jobs',
  standalone: true,
  imports: [CommonModule, BusinessDateTimePipe],
  template: `
    <section class="card">
      <div class="card-body">
        <h2 class="card-title">Recent uploads</h2>
        <p class="mt-1 text-sm value-muted">
          The uploads this business has made, newest first. An upload imports in the background, so
          you can leave this page and come back to it.
        </p>

        @if (status === 'loading') {
          <output class="mt-3 block text-sm value-muted" data-testid="jobs-loading">Loading...</output>
        }

        @if (errorMessage !== '') {
          <div class="alert alert-warning mt-3" role="alert" data-testid="jobs-error">
            <p>{{ errorMessage }}</p>
            <button type="button" class="btn btn-secondary btn-sm mt-2" data-testid="jobs-retry" (click)="retry()">
              Try again
            </button>
          </div>
        }

        @if (status === 'ready' && jobs.length === 0 && errorMessage === '') {
          <p class="mt-3 text-sm value-muted" data-testid="jobs-empty">
            Nothing has been uploaded yet. The uploads you make appear here.
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
                data-testid="job-row"
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
                    <span class="text-sm value-muted" data-testid="job-counts">{{ counts(job) }}</span>
                  </div>
                </div>

                @if (job.lastError) {
                  <p class="break-words text-sm value-muted" data-testid="job-last-error">{{ job.lastError }}</p>
                }

                @if (job.failedFiles > 0) {
                  <button
                    type="button"
                    class="btn-link self-start text-sm"
                    data-testid="job-expand"
                    [attr.aria-expanded]="expandedJobId === job.id"
                    (click)="toggle(job)"
                  >
                    {{ expandedJobId === job.id ? 'Hide the files that failed' : 'Show the files that failed' }}
                  </button>
                }

                @if (expandedJobId === job.id) {
                  <div class="flex flex-col gap-2" data-testid="job-detail">
                    @if (detailStatus === 'loading') {
                      <output class="block text-sm value-muted">Loading...</output>
                    }
                    @if (detailStatus === 'failed') {
                      <div class="alert alert-warning" role="alert" data-testid="job-detail-error">
                        <p>{{ detailError }}</p>
                      </div>
                    }
                    @if (detailStatus === 'ready') {
                      @if (detailFiles.length === 0) {
                        <p class="text-sm value-muted">No file in this upload recorded a failure.</p>
                      }
                      <ul class="flex flex-col gap-1">
                        @for (file of detailFiles; track file.file) {
                          <li class="text-sm" data-testid="job-failed-file">
                            <span class="break-all font-medium text-md-gray-800">{{ file.file }}</span>
                            @if (file.reason) {
                              <span class="value-muted"> - {{ file.reason }}</span>
                            }
                          </li>
                        }
                      </ul>
                    }
                  </div>
                }
              </li>
            }
          </ul>
        }
      </div>
    </section>
  `
})
export class ReimbursementImportJobsComponent implements OnInit, OnChanges, OnDestroy {
  /**
   * Changed by the page when an upload has been accepted, which is the whole of the coupling
   * between the two cards: the child is told that something happened, never reached into.
   */
  @Input() reloadToken = 0;

  /** `loading` until the first answer - success or failure - has arrived. */
  status: 'loading' | 'ready' = 'loading';

  jobs: readonly ReimbursementImportJobSummary[] = [];

  /** Why the last read could not be made, or `''` when the last one succeeded. */
  errorMessage = '';

  /** The one job whose failed files are shown, or `null` while none is expanded. */
  expandedJobId: number | null = null;

  detailStatus: 'loading' | 'ready' | 'failed' = 'loading';

  detailError = '';

  /** The expanded job's failed files, which is the only part of its detail this list shows. */
  detailFiles: readonly ReimbursementImportJobFileSummary[] = [];

  private refresh?: Subscription;

  /** The one list read that may still change the screen. See {@link load}. */
  private listRequest?: Subscription;

  private readonly destroying$ = new Subject<void>();

  constructor(private readonly imports: ImportService) {}

  ngOnInit(): void {
    this.load();
  }

  ngOnChanges(changes: SimpleChanges): void {
    // The first change arrives with the component, which `ngOnInit` reads for: reloading on it too
    // would make every arrival two requests.
    if (changes['reloadToken'] !== undefined && !changes['reloadToken'].firstChange) {
      this.load();
    }
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  stateLabel(job: ReimbursementImportJobSummary): string {
    return reimbursementJobStateLabel(job.state);
  }

  stateBadge(job: ReimbursementImportJobSummary): string {
    return reimbursementJobStateBadge(job.state);
  }

  /** What the upload turned into, counted on the server from its own per-file outcome rows. */
  counts(job: ReimbursementImportJobSummary): string {
    return `${job.importedFiles} imported, ${job.skippedFiles} skipped, ${job.failedFiles} failed`;
  }

  retry(): void {
    this.errorMessage = '';
    this.load();
  }

  /** Opens one job's failed files, or closes the open one. Only one is open at a time. */
  toggle(job: ReimbursementImportJobSummary): void {
    if (this.expandedJobId === job.id) {
      this.expandedJobId = null;
      this.detailFiles = [];
      return;
    }

    this.expandedJobId = job.id;
    this.detailFiles = [];
    this.detailStatus = 'loading';
    this.detailError = '';

    this.imports
      .reimbursementReportJob(job.id)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (detail) => {
          // An answer for a row that is no longer the open one describes nothing on screen.
          if (this.expandedJobId !== job.id) {
            return;
          }
          this.detailFiles = detail.files.filter((file) => file.outcome === ReimbursementImportFileOutcome.Failed);
          this.detailStatus = 'ready';
        },
        error: () => {
          if (this.expandedJobId !== job.id) {
            return;
          }
          this.detailStatus = 'failed';
          this.detailError = DETAIL_FAILURE;
        }
      });
  }

  /**
   * Reads the list, and makes that read the only one that may still change the screen.
   *
   * <p>Reads overlap: an accepted upload reloads immediately while an arrival read or a refresh is
   * still in flight. Whichever was asked for first can answer last, and an answer older than the
   * upload does not know about the job it accepted - so left alone it would put the pre-upload list
   * back, take the queued job off screen and, finding nothing running, stop the refresh that was
   * following it. Cancelling the superseded read ends that: it is unsubscribed before the new one is
   * made, so its answer and its failure both reach nobody, and the list, the error and the refresh
   * schedule can only be set by the newest read.</p>
   */
  private load(): void {
    this.refresh?.unsubscribe();
    this.refresh = undefined;
    this.listRequest?.unsubscribe();

    this.listRequest = this.imports
      .reimbursementReportJobs()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (jobs) => {
          this.jobs = jobs;
          this.status = 'ready';
          this.errorMessage = '';
          this.schedule();
        },
        error: () => {
          // What was already read stays on screen: it is still the last thing the server said.
          this.status = 'ready';
          this.errorMessage = LIST_FAILURE;
        }
      });
  }

  /** Arms the next read, and only while the server still has something to move on. */
  private schedule(): void {
    this.refresh?.unsubscribe();
    this.refresh = undefined;

    if (!anyReimbursementJobRunning(this.jobs)) {
      return;
    }

    this.refresh = timer(REIMBURSEMENT_JOBS_REFRESH_MS)
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.load());
  }
}
