import { Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { ToastService } from '../../../services/toast.service';
import {
  EVIDENCE_EXPORT_MAX_BYTES,
  EVIDENCE_EXPORT_SUPPORTED_EXTENSIONS,
  NayaxSaleTimestampRepairApplied,
  NayaxSaleTimestampRepairPreview,
  NayaxSaleTimestampRepairPreviewRequest,
  NayaxSaleTimestampRepairService
} from '../../../services/nayax-sale-timestamp-repair.service';
import { NayaxSaleTimestampRepairPreviewComponent } from './nayax-sale-timestamp-repair-preview.component';
import { NayaxSaleTimestampRepairResultComponent } from './nayax-sale-timestamp-repair-result.component';

/**
 * One outcome of the last action the operator took, with what to do about it.
 *
 * `kind` decides how long it survives. A `validation` notice describes the form as it was when
 * Preview was pressed, so editing the form clears it. An `outcome` notice describes something that
 * happened at the API - a refusal, or an apply whose result is unconfirmed - which an edit does not
 * make untrue, so it stays until the next request.
 */
export interface RepairNotice {
  readonly kind: 'validation' | 'outcome';
  readonly tone: 'danger' | 'warning';
  readonly title: string;
  readonly detail: string;
}

/** A notice an API response produced; `kind` is added where it is assigned. */
type RepairFailure = Omit<RepairNotice, 'kind'>;

/**
 * An explicitly labelled ISO UTC instant: a date, a time and a literal `Z`. A value without the
 * designator is a browser-local instant pretending to be a UTC one, which would silently reconcile
 * a window shifted by the viewer's own offset, so it is refused rather than interpreted.
 */
const UTC_INSTANT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d{1,7})?Z$/;

/**
 * An apply whose request produced no answer from this API. These are the only outcomes where
 * "nothing was written" would be a guess rather than a fact, so they are reported as unconfirmed.
 */
const AMBIGUOUS_TRANSPORT_STATUSES = [0, 408, 502, 503, 504];

/**
 * The Nayax sale timestamp repair workflow (issue #487), composed by its routed page per
 * docs/architecture.md § Page composition boundary: the source form, the optional reconciliation
 * window, the user-triggered Preview, the explicit confirmation, the Apply and every outcome of
 * both requests live here rather than on the page.
 *
 * **It decides nothing about the repair.** No timestamp is parsed or shifted, no business date is
 * converted, no outcome is classified, no revenue or costing figure is derived and no eligibility
 * rule exists here: the server's preview is displayed as returned, and the Apply carries back the
 * server's own `previewId` with an explicit confirmation and nothing else (see
 * docs/architecture.md § Nayax sale timestamp repair: Preview then Apply).
 *
 * **A displayed plan is only ever applicable for the inputs it was computed from.** Changing the
 * API source, the uploaded export or any reconciliation value withdraws the plan and the
 * acknowledgement, and so does applying it, so a second Apply is impossible from this page as well
 * as refused by the server. The plan's server-side two-hour lifetime is honoured too: an expired
 * plan offers no Apply and asks for a fresh preview.
 *
 * **Nothing is persisted and nothing is retried.** The plan, the preview id and the uploaded file
 * exist in this component's state while the page is open and nowhere else - no `localStorage`, no
 * `sessionStorage`, no URL - and an apply is never resubmitted automatically. An apply whose
 * request produced no answer is reported as an *unconfirmed outcome*: this page does not claim that
 * nothing was written, because it cannot know.
 */
@Component({
  selector: 'app-nayax-sale-timestamp-repair-workflow',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NayaxSaleTimestampRepairPreviewComponent,
    NayaxSaleTimestampRepairResultComponent
  ],
  template: `
    <section class="card">
      <div class="card-header">
        <h2 class="card-title">Authoritative sources</h2>
      </div>
      <div class="card-body">
        <p class="text-sm value-muted">
          The repair moves a stored sale only to an authorization instant a source actually reported
          for that transaction, one transaction at a time. There is no global offset, and a sale no
          source covers is left exactly as it is.
        </p>

        <div class="mt-4 flex flex-col gap-4">
          <div>
            <label class="flex items-start gap-2 text-sm text-md-gray-800" for="repair-api-source">
              <input
                id="repair-api-source"
                type="checkbox"
                class="mt-1"
                [disabled]="busy"
                [(ngModel)]="includeLatestSalesApiEvidence"
                (ngModelChange)="onInputChanged()"
              />
              <span>Read the live Nayax last-sales window</span>
            </label>
            <p class="mt-1 text-sm value-muted">
              Every machine's rolling last-sales window, read through the Nayax API. It is
              authoritative for the transactions that window still returns and for nothing else: it
              is a rolling window, not a date range, so an older transaction simply is not in it and
              will be reported as unresolved. There is no date-ranged Nayax sales endpoint that
              carries the authorization instant.
            </p>
          </div>

          <div class="field">
            <label class="field-label" for="repair-evidence-export">
              Nayax transaction export (optional)
            </label>
            <input
              id="repair-evidence-export"
              #exportInput
              type="file"
              accept=".xlsx,.xls,.csv"
              aria-describedby="repair-evidence-export-help"
              [disabled]="busy"
              (change)="onExportSelected($event)"
            />
            @if (evidenceExport) {
              <p class="mt-1 text-sm text-md-gray-800" data-testid="repair-export-chosen">
                {{ evidenceExport.name }}
                <button type="button" class="btn-link text-sm" [disabled]="busy" (click)="clearExport()">
                  Remove
                </button>
              </p>
            }
            @if (exportError) {
              <p class="field-error" data-testid="repair-export-error">{{ exportError }}</p>
            }
            <p id="repair-evidence-export-help" class="field-hint">
              The export must carry the <code>AuthorizationDateTimeGMT</code> column. That is the
              authorization time; <strong>Updated Date and Time (GMT)</strong> is an update time and
              is never substituted for it, and a machine-local column cannot be converted at all, so
              an export without the right column is refused rather than half-read. This is the only
              supported source for a transaction older than the rolling API window. Supported
              formats: {{ supportedExtensions }}. The request is capped at
              {{ maxExportBytesLabel }} bytes; the server enforces that itself.
            </p>
          </div>
        </div>
      </div>
    </section>

    <section class="card mt-6">
      <div class="card-header">
        <h2 class="card-title">Fixed-cutoff reconciliation (optional)</h2>
      </div>
      <div class="card-body">
        <label class="flex items-start gap-2 text-sm text-md-gray-800" for="repair-reconcile">
          <input
            id="repair-reconcile"
            type="checkbox"
            class="mt-1"
            [disabled]="busy"
            [(ngModel)]="reconcileWindow"
            (ngModelChange)="onInputChanged()"
          />
          <span>Reconcile a Sydney business-date window against a fixed cutoff</span>
        </label>
        <p class="mt-1 text-sm value-muted">
          A daily or weekly total is only comparable with an export's when both sides cover the same
          Sydney dates and exclude the sales authorized after the same instant, so all three values
          are required together or not at all. The cutoff is the instant the compared export was
          taken at &mdash; it is not guessed from the export, and the two business dates are calendar
          dates, never converted into instants here.
        </p>

        @if (reconcileWindow) {
          <div class="mt-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
            <div class="field">
              <label class="field-label" for="repair-cutoff">Cutoff &mdash; UTC instant, ending in Z</label>
              <input
                id="repair-cutoff"
                type="text"
                inputmode="text"
                spellcheck="false"
                placeholder="2026-10-08T04:00:00Z"
                aria-describedby="repair-cutoff-help"
                [disabled]="busy"
                [(ngModel)]="cutoffUtc"
                (ngModelChange)="onInputChanged()"
              />
              <p id="repair-cutoff-help" class="field-hint">
                Entered as an explicit UTC instant so no browser timezone can shift it.
              </p>
            </div>
            <div class="field">
              <label class="field-label" for="repair-from-date">First business date (Sydney, inclusive)</label>
              <input
                id="repair-from-date"
                type="date"
                [disabled]="busy"
                [(ngModel)]="fromBusinessDate"
                (ngModelChange)="onInputChanged()"
              />
            </div>
            <div class="field">
              <label class="field-label" for="repair-to-date">Last business date (Sydney, inclusive)</label>
              <input
                id="repair-to-date"
                type="date"
                [disabled]="busy"
                [(ngModel)]="toBusinessDate"
                (ngModelChange)="onInputChanged()"
              />
            </div>
          </div>
        }
      </div>
    </section>

    <div class="page-actions mt-6">
      <button type="button" class="btn btn-primary" [disabled]="busy" (click)="runPreview()">
        Preview repair
      </button>
    </div>

    @if (previewLoading) {
      <p role="status" class="mt-4 text-sm value-muted" data-testid="repair-previewing">
        Reading the named sources and building the plan. No sale timestamp changes while a preview
        runs.
      </p>
    }

    @if (notice) {
      <div
        [class]="notice.tone === 'danger' ? 'alert alert-danger mt-4' : 'alert alert-warning mt-4'"
        role="alert"
        data-testid="repair-notice"
      >
        <p class="alert-title">{{ notice.title }}</p>
        <p class="mt-1">{{ notice.detail }}</p>
      </div>
    }

    @if (preview) {
      <app-nayax-sale-timestamp-repair-preview [preview]="preview"></app-nayax-sale-timestamp-repair-preview>

      @if (previewExpired) {
        <div class="alert alert-warning mt-6" role="alert" data-testid="repair-expired">
          <p class="alert-title">This server plan has expired and can no longer be applied.</p>
          <p class="mt-1">
            A plan is applicable for two hours from the moment the server built it, because the
            stored sales it was computed from can change in the meantime. Nothing was repaired.
            Preview again and review the fresh plan before applying it.
          </p>
        </div>
      } @else if (preview.repairable > 0) {
        <div class="alert alert-warning mt-6" data-testid="repair-confirmation">
          <p class="alert-title">
            Applying writes {{ preview.repairable }} historical sale timestamp(s) and replays the
            planned products' costing.
          </p>
          <p class="mt-1">
            Apply writes each repairable sale's authorization instant, appends its audit row and
            replays the planned products' cost history, all as one transaction that either completes
            or is rolled back. There is no undo in this application: reversing a committed repair is
            the human-run database restore, so take a verified snapshot first &mdash; runbook step
            one, <code>backup-database --upload</code>, confirming it exited 0 and reported
            <code>ok</code> for <code>PRAGMA integrity_check</code>. Ticking the box records that you
            have done that and reviewed every row above; it does not create or verify a backup, and
            nothing on this page can.
          </p>
          <label class="mt-3 flex items-start gap-2 text-sm" for="repair-acknowledge">
            <input
              id="repair-acknowledge"
              type="checkbox"
              class="mt-1"
              [disabled]="busy"
              [(ngModel)]="acknowledged"
            />
            <span>
              I have reviewed every row above, and a verified backup of this database is available.
            </span>
          </label>
          <div class="page-actions mt-3">
            <button type="button" class="btn btn-danger" [disabled]="!canApply" (click)="applyRepair()">
              {{ applying ? 'Applying…' : 'Apply repair' }}
            </button>
          </div>
        </div>
      }
    }

    @if (applied) {
      <app-nayax-sale-timestamp-repair-result
        [applied]="applied"
        [unresolvedAtPreview]="unresolvedAtPreview"
        [missingAtPreview]="missingAtPreview"
        [busy]="busy"
        (verifyRequested)="runPreview()"
      ></app-nayax-sale-timestamp-repair-result>
    }
  `
})
export class NayaxSaleTimestampRepairWorkflowComponent implements OnDestroy {
  @ViewChild('exportInput') exportInput?: ElementRef<HTMLInputElement>;

  includeLatestSalesApiEvidence = false;
  evidenceExport: File | null = null;
  exportError: string | null = null;

  reconcileWindow = false;
  cutoffUtc = '';
  fromBusinessDate = '';
  toBusinessDate = '';

  previewLoading = false;
  applying = false;
  preview: NayaxSaleTimestampRepairPreview | null = null;
  applied: NayaxSaleTimestampRepairApplied | null = null;
  acknowledged = false;
  notice: RepairNotice | null = null;

  /**
   * The unresolved and missing counts of the plan that was confirmed, kept so a success can never
   * be read as a complete explanation of a period. The apply changed neither set.
   */
  unresolvedAtPreview = 0;
  missingAtPreview = 0;

  readonly supportedExtensions = EVIDENCE_EXPORT_SUPPORTED_EXTENSIONS.join(', ');
  readonly maxExportBytesLabel = NayaxSaleTimestampRepairWorkflowComponent.groupDigits(EVIDENCE_EXPORT_MAX_BYTES);

  // One sequence number per request kind: a response is accepted only while its own sequence is
  // still the latest, so a response arriving after a newer request - or after this component is
  // destroyed - is discarded instead of landing on state it no longer describes.
  private previewSequence = 0;
  private applySequence = 0;
  private previewSubscription: Subscription | null = null;
  private applySubscription: Subscription | null = null;
  private expiryTimer: number | null = null;

  constructor(
    private readonly repairs: NayaxSaleTimestampRepairService,
    private readonly toast: ToastService
  ) {}

  get busy(): boolean {
    return this.previewLoading || this.applying;
  }

  /**
   * Whether the displayed plan has passed the lifetime the server bound it to. Computed from the
   * server's own `expiresAt` on every evaluation rather than latched, so it can never report an
   * expired plan as applicable.
   */
  get previewExpired(): boolean {
    return this.preview !== null && Date.parse(this.preview.expiresAt) <= Date.now();
  }

  /**
   * Apply is offered only for a successful, unexpired plan that has something to repair, with the
   * acknowledgement given and no request in flight. The server enforces each of these itself; this
   * is the page refusing to offer an action that would be refused.
   */
  get canApply(): boolean {
    return (
      this.preview !== null &&
      this.preview.repairable > 0 &&
      !this.previewExpired &&
      this.acknowledged &&
      !this.busy
    );
  }

  ngOnDestroy(): void {
    this.cancelPreview();
    this.cancelApply();
    this.clearExpiryTimer();
  }

  /**
   * Any change to a source or a reconciliation value makes the displayed plan a plan for a question
   * nobody asked, so it stops being actionable and the acknowledgement is withdrawn with it. The
   * last apply's own outcome is left on screen: an input change does not make it untrue.
   */
  onInputChanged(): void {
    this.preview = null;
    this.acknowledged = false;
    this.clearExpiryTimer();
    // An edit answers a validation complaint about the form, and nothing else: a refusal or an
    // unconfirmed apply stays on screen, because changing an input does not make it untrue.
    if (this.notice?.kind === 'validation') {
      this.notice = null;
    }
  }

  onExportSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectExport(input.files?.[0] ?? null);
    if (!this.evidenceExport) {
      input.value = '';
    }
  }

  /**
   * Accepts an export the server's reader could read, and refuses one it certainly could not before
   * a request is spent on it. Both checks mirror the server's own and weaken neither: an accepted
   * file is still validated, read and refused server-side on its own terms.
   */
  selectExport(file: File | null): void {
    this.exportError = null;
    this.evidenceExport = null;
    this.onInputChanged();
    if (!file) {
      return;
    }
    if (!EVIDENCE_EXPORT_SUPPORTED_EXTENSIONS.some((extension) => file.name.toLowerCase().endsWith(extension))) {
      this.exportError = `Only ${this.supportedExtensions} files are supported.`;
      return;
    }
    if (file.size > EVIDENCE_EXPORT_MAX_BYTES) {
      this.exportError =
        `That file is ${NayaxSaleTimestampRepairWorkflowComponent.groupDigits(file.size)} bytes. The preview ` +
        `request is capped at ${this.maxExportBytesLabel} bytes, so export a narrower date range.`;
      return;
    }
    this.evidenceExport = file;
  }

  clearExport(): void {
    this.selectExport(null);
    if (this.exportInput) {
      this.exportInput.nativeElement.value = '';
    }
  }

  /**
   * Asks the server for a plan. Always user-triggered: nothing previews on arrival, after an input
   * change, or after an apply.
   */
  runPreview(): void {
    if (this.busy) {
      return;
    }
    const request = this.buildRequest();
    if (!request) {
      return;
    }

    this.cancelPreview();
    const sequence = ++this.previewSequence;
    this.previewLoading = true;
    this.preview = null;
    this.applied = null;
    this.acknowledged = false;
    this.notice = null;
    this.clearExpiryTimer();

    this.previewSubscription = this.repairs.preview(request).subscribe({
      next: (plan) => {
        if (sequence !== this.previewSequence) return;
        this.previewLoading = false;
        this.preview = plan;
        this.scheduleExpiryRefresh(plan.expiresAt);
      },
      error: (error: unknown) => {
        if (sequence !== this.previewSequence) return;
        this.previewLoading = false;
        this.notice = { kind: 'outcome', ...this.describePreviewFailure(error) };
        this.toast.error(this.notice.title);
      }
    });
  }

  /** Confirms the server's own plan. The request carries its id and the confirmation, nothing else. */
  applyRepair(): void {
    if (!this.canApply) {
      return;
    }
    const plan = this.preview!;

    this.cancelApply();
    const sequence = ++this.applySequence;
    this.applying = true;
    this.notice = null;

    this.applySubscription = this.repairs.apply(plan.previewId).subscribe({
      next: (result) => {
        if (sequence !== this.applySequence) return;
        this.applying = false;
        this.applied = result;
        this.unresolvedAtPreview = plan.unresolved;
        this.missingAtPreview = plan.missingFromDatabase.length;
        // The plan is consumed: withdrawing it is what makes a second Apply impossible here as
        // well as refused by the server.
        this.withdrawPlan();
        this.toast.success(
          `Repaired ${result.salesRepaired} sale timestamp(s), rebuilt ${result.productsRebuilt} ` +
            `product(s) and recosted ${result.recostedSales} sale(s).`
        );
      },
      error: (error: unknown) => {
        if (sequence !== this.applySequence) return;
        this.applying = false;
        this.applied = null;
        // Whether the server refused the plan or never answered, this plan can never be applied
        // again: a fresh preview is the only safe next step, so the displayed one is withdrawn.
        this.withdrawPlan();
        this.notice = { kind: 'outcome', ...this.describeApplyFailure(error) };
        this.toast.error(this.notice.title);
      }
    });
  }

  /** The request, or `null` with the reason on screen and no request spent. */
  private buildRequest(): NayaxSaleTimestampRepairPreviewRequest | null {
    if (!this.includeLatestSalesApiEvidence && !this.evidenceExport) {
      this.notice = {
        kind: 'validation',
        tone: 'danger',
        title: 'Name at least one authoritative source.',
        detail:
          'A repair needs evidence: the live Nayax last-sales window, an operator export carrying ' +
          'the AuthorizationDateTimeGMT column, or both. Nothing was read.'
      };
      return null;
    }

    const request: NayaxSaleTimestampRepairPreviewRequest = {
      includeLatestSalesApiEvidence: this.includeLatestSalesApiEvidence,
      evidenceExport: this.evidenceExport,
      reconciliation: null
    };
    if (!this.reconcileWindow) {
      return request;
    }

    const cutoffUtc = this.cutoffUtc.trim();
    const fromBusinessDate = this.fromBusinessDate.trim();
    const toBusinessDate = this.toBusinessDate.trim();
    if (!cutoffUtc || !fromBusinessDate || !toBusinessDate) {
      this.notice = {
        kind: 'validation',
        tone: 'danger',
        title: 'A reconciliation needs all three values, or none of them.',
        detail:
          'Enter the cutoff instant and both inclusive Sydney business dates, or switch the ' +
          'reconciliation off. A partial window would compare a different period without saying so.'
      };
      return null;
    }
    if (!UTC_INSTANT.test(cutoffUtc) || Number.isNaN(Date.parse(cutoffUtc))) {
      this.notice = {
        kind: 'validation',
        tone: 'danger',
        title: 'The cutoff must be an explicit UTC instant ending in Z.',
        detail:
          'For example 2026-10-08T04:00:00Z. Without the Z the value would be read in whichever ' +
          'timezone the browser happens to be in, which would silently reconcile a shifted window.'
      };
      return null;
    }
    if (toBusinessDate < fromBusinessDate) {
      this.notice = {
        kind: 'validation',
        tone: 'danger',
        title: "The window's last business date cannot be before its first.",
        detail: 'Enter an inclusive Sydney date range that runs forwards, then preview again.'
      };
      return null;
    }

    return { ...request, reconciliation: { cutoffUtc, fromBusinessDate, toBusinessDate } };
  }

  /**
   * A preview reads sources and writes only its own plan draft, so no outcome of a preview request
   * can have changed a sale timestamp. That is why every message here can say so.
   */
  private describePreviewFailure(error: unknown): RepairFailure {
    const status = this.statusOf(error);
    if (status === 401 || status === 403) {
      return {
        tone: 'danger',
        title: 'This request was not authorized.',
        detail:
          'Nothing was read and no sale timestamp changed. Sign in again, then preview once the ' +
          'session is current.'
      };
    }
    if (status === 413) {
      return {
        tone: 'danger',
        title: 'That export is larger than the server will accept.',
        detail:
          `The preview request is capped at ${this.maxExportBytesLabel} bytes and the upload was ` +
          'refused at the HTTP boundary, so it was never read. Export a narrower date range and ' +
          'preview again.'
      };
    }
    if (status === 502) {
      return {
        tone: 'danger',
        title: 'The Nayax service could not complete the preview.',
        detail:
          'The live last-sales window could not be read, so no evidence was collected from it and ' +
          'no sale timestamp changed. Try again, or preview with an operator export alone, which ' +
          'does not call Nayax.'
      };
    }
    if (status === 0 || status === 408 || status === 503 || status === 504) {
      return {
        tone: 'danger',
        title: 'The preview request did not reach the server, or did not come back.',
        detail:
          'A preview changes no sale timestamp, so nothing was repaired. Check the connection and ' +
          'preview again.'
      };
    }
    const message = this.serverMessage(error);
    if (status === 400 || status === 409) {
      return {
        tone: 'danger',
        title: message ?? 'The preview was refused.',
        detail:
          'No sale timestamp changed: a preview never writes one. Correct the source or the window ' +
          'the message names and preview again.'
      };
    }
    return {
      tone: 'danger',
      title: message ?? 'The preview could not be completed.',
      detail: 'No sale timestamp changed. Preview again, or check that the API is reachable.'
    };
  }

  /**
   * The apply is the one request whose failure can be genuinely ambiguous. A refusal the server
   * answered with rolled the whole transaction back and is reported as such; a request that
   * produced no answer is reported as an **unconfirmed outcome** and is never retried
   * automatically, because this page cannot know whether the write committed.
   */
  private describeApplyFailure(error: unknown): RepairFailure {
    const status = this.statusOf(error);
    if (AMBIGUOUS_TRANSPORT_STATUSES.includes(status)) {
      return {
        tone: 'warning',
        title: "The repair's outcome could not be confirmed.",
        detail:
          'The request produced no answer from the API, so it is not known whether any sale ' +
          'timestamp was written. Do not apply again: take a fresh preview over the same sources ' +
          'and window, and verify the affected sales and products before deciding what to do next. ' +
          'If the repair did commit, the fresh preview reports those rows as already correct.'
      };
    }
    if (status === 401 || status === 403) {
      return {
        tone: 'danger',
        title: 'This request was not authorized, so nothing was applied.',
        detail:
          'The apply was rolled back before anything was written. Sign in again, then take a fresh ' +
          'preview and review it before applying.'
      };
    }
    if (status === 400 || status === 409) {
      return {
        tone: 'danger',
        title: this.serverMessage(error) ?? 'The server refused this plan.',
        detail:
          'The whole apply was rolled back: no sale timestamp, no audit row and no costing change ' +
          'was kept. Fix the cause the message names and take a fresh preview before applying ' +
          'again. A costing replay that cannot complete is fixed by completing that product\'s ' +
          'cost history first.'
      };
    }
    return {
      tone: 'warning',
      title: 'The server reported an unexpected failure while applying the repair.',
      detail:
        'The apply runs as one transaction, so a failure inside it is rolled back; even so, take a ' +
        'fresh preview and verify the affected sales and products before applying again.'
    };
  }

  private withdrawPlan(): void {
    this.preview = null;
    this.acknowledged = false;
    this.clearExpiryTimer();
  }

  /**
   * Wakes change detection once the server's lifetime has passed, so an expired plan stops offering
   * Apply without the operator having to touch anything. The timer carries no decision: expiry is
   * always recomputed from `expiresAt` by `previewExpired`.
   */
  private scheduleExpiryRefresh(expiresAt: string): void {
    this.clearExpiryTimer();
    const remaining = Date.parse(expiresAt) - Date.now();
    if (Number.isNaN(remaining) || remaining <= 0) {
      return;
    }
    this.expiryTimer = window.setTimeout(() => {
      this.expiryTimer = null;
    }, remaining + 1000);
  }

  private clearExpiryTimer(): void {
    if (this.expiryTimer !== null) {
      window.clearTimeout(this.expiryTimer);
      this.expiryTimer = null;
    }
  }

  private cancelPreview(): void {
    this.previewSequence++;
    this.previewSubscription?.unsubscribe();
    this.previewSubscription = null;
    this.previewLoading = false;
  }

  private cancelApply(): void {
    this.applySequence++;
    this.applySubscription?.unsubscribe();
    this.applySubscription = null;
    this.applying = false;
  }

  private statusOf(error: unknown): number {
    return (error as { status?: number } | null)?.status ?? 0;
  }

  /**
   * The caller-safe message the API composed. `ProblemDetails` from `DomainExceptionHandler`
   * carries it as both `message` and `detail`; the reconciliation-window guard on the controller
   * answers with a bare string body.
   */
  private serverMessage(error: unknown): string | null {
    const body = (error as { error?: unknown } | null)?.error;
    if (typeof body === 'string' && body.trim()) {
      return body;
    }
    const problem = body as { message?: string; detail?: string; title?: string } | null | undefined;
    return problem?.message ?? problem?.detail ?? problem?.title ?? null;
  }

  /** `8000000` as `8,000,000`, without depending on the runtime's locale data. */
  private static groupDigits(value: number): string {
    return value.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  }
}
