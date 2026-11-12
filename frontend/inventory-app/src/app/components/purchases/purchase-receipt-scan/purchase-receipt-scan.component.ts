import { Component, ElementRef, EventEmitter, Input, OnDestroy, Output, Renderer2, ViewChild } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { Subject, Subscription, interval } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { AccessService } from '../../../access/access.service';
import { planRequiredOfError, requiredPlanOf } from '../../../access/plan-feature';
import { planLockSummary } from '../../../access/plan-lock';
import { PurchaseScanDraft } from '../../../models/purchase-scan';
import { PurchaseScanService } from '../../../services/purchase-scan.service';
import { apiErrorMessage } from '../../shared/api-error-message';
import { IconComponent } from '../../shared/icon.component';
import { prepareImageForUpload } from '../../shared/image-upload-preparation';

/** The scanned draft and the exact file it was read from, which becomes the purchase's document. */
export interface PurchaseReceiptScanned {
  readonly draft: PurchaseScanDraft;
  /** The prepared file - the bytes that were scanned - so the save uploads these and no others. */
  readonly file: File;
}

/**
 * Where the Scan receipt workflow stands.
 *
 * - `idle` - the button is there to press;
 * - `preparing` - a chosen photo is being shrunk, before anything is sent (issue #1161);
 * - `confirming` - the form already holds lines, and the person is being asked before they are replaced;
 * - `scanning` - the request is out, and Cancel stops waiting for it;
 * - `failed` - the attempt failed and the form is exactly as it was;
 * - `planLocked` - the business's plan does not include receipt scanning.
 */
export type PurchaseReceiptScanState = 'idle' | 'preparing' | 'confirming' | 'scanning' | 'failed' | 'planLocked';

/** The destination a plan lock names, for the one sentence `planLockSummary` builds. */
const SCAN_DESTINATION = 'Scanning a receipt';

/** What the person is told for each refusal the endpoint can answer with, when it sends no sentence. */
const FALLBACK_MESSAGES: Readonly<Record<number, string>> = {
  400: 'That file could not be scanned. Try a JPEG, PNG or WebP photo, or a PDF.',
  422: 'Could not read this receipt. Enter the purchase manually.',
  429: 'You have used all of this month’s receipt scans. Enter the purchase manually.',
  503: 'Receipt scanning is not available.',
  504: 'Reading the receipt took too long. Try again, or enter the purchase manually.'
};

/**
 * How the estimated progress bar fills while a receipt is read. Nothing reports real progress -
 * the reader answers once, at the end - so the bar is an honest estimate: it rises quickly at
 * first, slows as it approaches {@link PROGRESS_CEILING} and never claims to be finished before the
 * answer arrives. With a time constant of 15 s a typical 20-30 s read shows about 75-85 %.
 */
const PROGRESS_TIME_CONSTANT_MS = 15_000;
const PROGRESS_CEILING = 95;
const PROGRESS_TICK_MS = 250;

/** After this long the hint says a long receipt can take up to a minute (the server's timeout). */
const SLOW_READ_MS = 35_000;

/** What anything else - a transport failure, an unexpected status, no body at all - is told as. */
const FALLBACK_MESSAGE = 'Could not scan the receipt. Try again, or enter the purchase manually.';

/** The question asked before a scan replaces lines that are already in the form. */
export const REPLACE_LINES_QUESTION =
  'Scanning this receipt replaces the items already in this purchase. The receipt’s items, supplier, ' +
  'date, invoice number and totals take their place; nothing is saved until you press Save purchase.';

/**
 * The Scan receipt workflow of Add Purchase (issue #1166, sub-issue D of #975): choose a photo or a
 * PDF, have it read, and hand the suggested draft to the form for the operator to check.
 *
 * <p>It is a dedicated feature component rather than markup grown inside `PurchaseUploadComponent`,
 * per the page composition boundary (docs/architecture.md § Frontend architecture: page
 * composition): it is a distinct workflow with its own plan lock, file preparation, request
 * lifecycle, cancellation, confirmation dialog and error states, and the page composes it through
 * two inputs and one output.</p>
 *
 * <p><strong>It fills nothing in and saves nothing.</strong> It emits the draft and the exact file
 * that was scanned; what that does to the form is the page's business, and the purchase is still
 * created by the ordinary create request when the person presses Save. A failed or cancelled scan
 * emits nothing at all, so the form is left exactly as the person left it.</p>
 *
 * <p><strong>The plan lock is the entitlement, not a guess.</strong> Receipt scanning is the one
 * feature that is refused while plan enforcement is switched off as well as below Pro
 * (`PlanFeaturePolicy.FailsClosedWhileUnenforced`), which `planFeatureAllowed` mirrors - so the
 * button is locked rather than offered for a request the endpoint would refuse. A `403 plan_required`
 * answered to a scan anyway locks it the same way, with the plan the refusal itself named.</p>
 */
@Component({
  selector: 'app-purchase-receipt-scan',
  standalone: true,
  imports: [IconComponent, RouterLink],
  templateUrl: './purchase-receipt-scan.component.html'
})
export class PurchaseReceiptScanComponent implements OnDestroy {
  private static instances = 0;

  /**
   * Whether the form already holds items, so a scan would replace them. The page answers it,
   * because only the page knows what is in the form.
   */
  @Input() hasExistingLines = false;

  /** Whether the page is busy or the write is unavailable (a save in flight, a support session). */
  @Input() disabled = false;

  /** A draft and the file it was read from, emitted only on a scan that actually succeeded. */
  @Output() readonly scanned = new EventEmitter<PurchaseReceiptScanned>();

  state: PurchaseReceiptScanState = 'idle';

  /** The estimated progress of the read in progress, 0-{@link PROGRESS_CEILING}, while `scanning`. */
  progressPercent = 0;

  /** The hint under the progress bar, which changes once the read is taking longer than usual. */
  progressHint = '';

  /** Why the last attempt failed, shown while {@link state} is `failed`. */
  errorMessage = '';

  readonly replaceQuestion = REPLACE_LINES_QUESTION;

  /** This dialog's own heading id, so it is labelled by its own heading. */
  readonly titleId = `purchase-receipt-scan-title-${++PurchaseReceiptScanComponent.instances}`;

  /** The sentence a locked button carries, naming the plan the refusal named, or the declared one. */
  planLockMessage = planLockSummary('ReceiptScanning', SCAN_DESTINATION);

  @ViewChild('fileInput', { static: true }) private readonly fileInput!: ElementRef<HTMLInputElement>;
  @ViewChild('replaceButton') private readonly replaceButton?: ElementRef<HTMLButtonElement>;

  /** The prepared file waiting for the replace question to be answered. */
  private pendingFile: File | null = null;

  private readonly cancelled$ = new Subject<void>();
  private readonly destroyed$ = new Subject<void>();
  private readonly planFeature: Subscription;

  /** The running estimate's timer, owned by the one scan it belongs to and stopped when that scan ends. */
  private progressTimer?: Subscription;

  /** The open confirmation panel, while there is one, so withdrawing it leaves the top layer. */
  private panel?: HTMLDialogElement;
  private readonly panelListeners: (() => void)[] = [];

  constructor(
    private readonly purchaseScan: PurchaseScanService,
    access: AccessService,
    private readonly renderer: Renderer2
  ) {
    this.planFeature = access
      .allowsPlanFeature('ReceiptScanning')
      .pipe(takeUntil(this.destroyed$))
      .subscribe((allowed) => {
        if (!allowed) {
          this.lock(requiredPlanOf('ReceiptScanning'));
        } else if (this.state === 'planLocked') {
          this.state = 'idle';
        }
      });
  }

  ngOnDestroy(): void {
    this.stopProgress();
    this.destroyed$.next();
    this.destroyed$.complete();
    this.cancelled$.complete();
    this.planFeature.unsubscribe();
    this.closePanel();
  }

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLDialogElement> | undefined) {
    if (panel === undefined) {
      this.closePanel();
      return;
    }

    this.openAsModal(panel.nativeElement);
  }

  /** Whether a scan can be started at all right now. */
  get available(): boolean {
    return !this.disabled && this.state !== 'planLocked' && this.state !== 'preparing' && this.state !== 'scanning';
  }

  /** Scan receipt, or Scan again once something has already been scanned into the form. */
  get buttonLabel(): string {
    return this.hasExistingLines ? 'Scan again' : 'Scan receipt';
  }

  chooseFile(): void {
    if (!this.available) return;
    this.fileInput.nativeElement.click();
  }

  async onFileSelected(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.length ? input.files[0] : null;
    // Cleared so choosing the same file again after a failure still reports a change.
    input.value = '';
    if (!file || !this.available) return;

    this.errorMessage = '';
    this.state = 'preparing';
    // The prepared file is what is scanned and what the purchase will hold, so the receipt the
    // reader saw and the receipt that is stored are the same bytes (issue #1161).
    const prepared = await prepareImageForUpload(file);

    // Asked before anything is sent, so a person who meant to keep what they typed has not spent a
    // scan finding that out: a scan costs money and counts against the month's allowance (#1164).
    if (this.hasExistingLines) {
      this.pendingFile = prepared;
      this.state = 'confirming';
      return;
    }

    this.startScan(prepared);
  }

  /** "Replace items": the answer that lets the scan go ahead. */
  replaceLines(): void {
    const file = this.pendingFile;
    this.pendingFile = null;
    if (file === null) {
      this.state = 'idle';
      return;
    }

    this.startScan(file);
  }

  /** "Keep what I typed": nothing is sent, nothing is spent, and the form is untouched. */
  keepLines(): void {
    this.pendingFile = null;
    this.state = 'idle';
  }

  /**
   * Cancel stops waiting: the request is aborted and the form is left exactly as it was. The scan
   * may already have been read and counted at the other end, so nothing here claims otherwise.
   */
  cancel(): void {
    if (this.state !== 'scanning') return;
    this.stopProgress();
    this.cancelled$.next();
    this.state = 'idle';
  }

  /** Dismisses a failure message, so the form is not left with a stale reason on it. */
  dismissError(): void {
    if (this.state !== 'failed') return;
    this.errorMessage = '';
    this.state = 'idle';
  }

  private startScan(file: File): void {
    this.state = 'scanning';
    this.errorMessage = '';
    this.startProgress();

    this.purchaseScan
      .scan(file)
      .pipe(takeUntil(this.cancelled$), takeUntil(this.destroyed$))
      .subscribe({
        next: (draft) => {
          this.stopProgress();
          this.state = 'idle';
          this.scanned.emit({ draft, file });
        },
        error: (error: unknown) => {
          this.stopProgress();
          // A plan refusal is this business's own answer rather than a failure to retry, exactly as
          // an inline optional part treats one (issue #979).
          const requiredPlan = planRequiredOfError(error);
          if (requiredPlan !== null) {
            this.lock(requiredPlan);
            return;
          }

          this.errorMessage = apiErrorMessage(error, fallbackFor(error));
          this.state = 'failed';
        }
      });
  }

  /**
   * Starts the estimated progress bar for the scan that is starting. Any earlier timer is stopped
   * first, and every way a scan ends (an answer, a failure, a plan lock, Cancel, destruction) stops
   * this one synchronously, so a finished scan's timer can never tick into the next scan's bar.
   */
  private startProgress(): void {
    this.stopProgress();
    const started = Date.now();
    this.progressPercent = 0;
    this.progressHint = 'This usually takes 20 to 30 seconds.';

    this.progressTimer = interval(PROGRESS_TICK_MS).subscribe(() => {
      const elapsed = Date.now() - started;
      this.progressPercent = Math.round(PROGRESS_CEILING * (1 - Math.exp(-elapsed / PROGRESS_TIME_CONSTANT_MS)));
      if (elapsed >= SLOW_READ_MS) {
        this.progressHint = 'Still reading. A long receipt can take up to a minute.';
      }
    });
  }

  private stopProgress(): void {
    this.progressTimer?.unsubscribe();
    this.progressTimer = undefined;
  }

  private lock(requiredPlan: string): void {
    this.stopProgress();
    this.planLockMessage = `${SCAN_DESTINATION} needs the ${requiredPlan} plan.`;
    this.errorMessage = '';
    this.pendingFile = null;
    this.state = 'planLocked';
  }

  /**
   * Hands the question to the browser as a modal: the top layer, the inert form behind it and the
   * `::backdrop` are the platform's. Focus goes to the safe answer, and the browser's own close
   * request is answered as keeping what was typed.
   */
  private openAsModal(panel: HTMLDialogElement): void {
    this.panel = panel;
    if (typeof panel.showModal === 'function') {
      panel.showModal();
    }

    this.replaceButton?.nativeElement.focus();

    this.panelListeners.push(
      this.renderer.listen(panel, 'cancel', (event: Event) => {
        event.preventDefault();
        this.keepLines();
      }),
      this.renderer.listen(panel, 'click', (event: MouseEvent) => {
        if (event.target === panel) {
          this.keepLines();
        }
      })
    );
  }

  private closePanel(): void {
    for (const unlisten of this.panelListeners) {
      unlisten();
    }
    this.panelListeners.length = 0;

    const panel = this.panel;
    this.panel = undefined;
    panel?.close();
  }
}

/** The sentence to show when the response carried none of its own. */
function fallbackFor(error: unknown): string {
  const status = error instanceof HttpErrorResponse ? error.status : 0;
  return FALLBACK_MESSAGES[status] ?? FALLBACK_MESSAGE;
}
