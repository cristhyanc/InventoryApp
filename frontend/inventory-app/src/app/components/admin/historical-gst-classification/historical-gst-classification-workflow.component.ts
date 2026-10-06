import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subscription } from 'rxjs';
import { ToastService } from '../../../services/toast.service';
import {
  HistoricalGstClassificationApplied,
  HistoricalGstClassificationPreview,
  HistoricalGstClassificationService
} from '../../../services/historical-gst-classification.service';

/**
 * The Historical GST Classification workflow as a dedicated feature component (issue #433),
 * composed by the routed page per docs/architecture.md § Page composition boundary (issue #191):
 * the Preview and Apply actions, the confirmation, the reported counts and the loading/error
 * lifecycle live here rather than on the page.
 *
 * It calculates nothing. Every count, every charge count by type and every GST figure it shows is
 * the API's own, and the fingerprint it sends back is carried unchanged - the precedence, the
 * eligibility and the rounding are the server's one authoritative calculation
 * (`HistoricalGstClassificationPolicy`), and the frontend has no GST divisor, rounding rule or
 * unresolved rule of its own.
 *
 * Apply is only ever reachable from a preview that is still on screen, and the preview is cleared
 * whenever it can no longer be trusted: after a successful apply, and after a failed one. A refused
 * apply means the purchase data or the rules changed, so the only safe next step is a fresh preview
 * - the server enforces that too, and this is the UI saying the same thing.
 */
@Component({
  selector: 'app-historical-gst-classification-workflow',
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="card">
      <div class="card-header">
        <h2 class="card-title">Historical GST Classification</h2>
      </div>
      <div class="card-body">
        <p class="text-sm value-muted">
          Classifies purchase components that are still <strong>Not classified</strong> from the GST
          rules configured on your products and suppliers: a product's own rule first, then the
          supplier's product-line default for a purchased item, and the supplier's delivery or
          package default for that charge. A component no rule covers stays Not classified, and a
          classification that already exists &mdash; chosen by a person or applied by an earlier run
          &mdash; is never changed. It never touches a purchase amount, a unit cost, inventory
          costing or stock.
        </p>

        <div class="mt-4 flex flex-wrap gap-2">
          <button
            type="button"
            class="btn btn-primary"
            [disabled]="loading"
            (click)="runPreview()"
          >Preview classification</button>
        </div>

        @if (preview) {
          <div class="mt-5 rounded-lg border border-md-gray-200 p-4">
            <h3 class="text-sm font-semibold text-md-gray-800">What applying these rules would do</h3>
            <div class="mt-2 grid gap-2 text-sm text-md-gray-800 sm:grid-cols-3">
              <div>Purchases examined: <strong>{{ preview.summary.purchasesExamined }}</strong></div>
              <div>Components examined: <strong>{{ preview.summary.componentsExamined }}</strong></div>
              <div>Becoming taxable: <strong>{{ preview.summary.becomingTaxable }}</strong></div>
              <div>Becoming GST-free: <strong>{{ preview.summary.becomingGstFree }}</strong></div>
              <div>Staying not classified: <strong>{{ preview.summary.stayingUnknown }}</strong></div>
              <div>Still unresolved amount: <strong>{{ preview.summary.stayingUnknownAmount | currency:'AUD' }}</strong></div>
              <div>Purchased item GST: <strong>{{ preview.summary.lineGst | currency:'AUD' }}</strong></div>
              <div>Delivery and package GST: <strong>{{ preview.summary.chargeGst | currency:'AUD' }}</strong></div>
              <div>Resulting input GST: <strong>{{ preview.summary.inputGst | currency:'AUD' }}</strong></div>
            </div>

            <div class="mt-4 overflow-x-auto">
              <table class="table">
                <thead class="table-head">
                  <tr>
                    <th scope="col" class="table-cell">Component</th>
                    <th scope="col" class="table-cell">Examined</th>
                    <th scope="col" class="table-cell">Taxable</th>
                    <th scope="col" class="table-cell">GST-free</th>
                    <th scope="col" class="table-cell">Staying not classified</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of componentRows(); track row.label) {
                    <tr class="table-row">
                      <td class="table-cell">{{ row.label }}</td>
                      <td class="table-cell">{{ row.counts.examined }}</td>
                      <td class="table-cell">{{ row.counts.becomingTaxable }}</td>
                      <td class="table-cell">{{ row.counts.becomingGstFree }}</td>
                      <td class="table-cell">{{ row.counts.stayingUnknown }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>

            @if (preview.summary.stayingUnknown > 0) {
              <div class="alert alert-warning mt-4">
                <p class="alert-title">
                  {{ preview.summary.stayingUnknown }} component(s) worth
                  {{ preview.summary.stayingUnknownAmount | currency:'AUD' }} are not covered by any configured rule.
                </p>
                <p class="mt-1">
                  They stay Not classified, contribute no input GST and keep those purchases visibly
                  unresolved. Configure a product rule or a supplier default, or classify them on the
                  purchase itself.
                </p>
              </div>
            }

            @if (preview.summary.classifiesAnything) {
              <button
                type="button"
                class="btn btn-primary mt-4"
                [disabled]="loading"
                (click)="applyPreview()"
              >Apply classification</button>
            } @else {
              <p class="mt-4 text-sm value-muted">
                Nothing to apply: no configured rule classifies any of the components examined.
              </p>
            }
          </div>
        }

        @if (applied) {
          <p class="mt-4 text-sm text-md-success-text">
            Classified {{ applied.componentsClassified }} component(s): {{ applied.summary.becomingTaxable }}
            taxable and {{ applied.summary.becomingGstFree }} GST-free, adding
            {{ applied.summary.inputGst | currency:'AUD' }} of input GST. Preview again to see what is
            left.
          </p>
        }
      </div>
    </section>
  `
})
export class HistoricalGstClassificationWorkflowComponent implements OnDestroy {
  previewLoading = false;
  applying = false;
  preview: HistoricalGstClassificationPreview | null = null;
  applied: HistoricalGstClassificationApplied | null = null;

  private previewSequence = 0;
  private previewSubscription: Subscription | null = null;

  constructor(
    private readonly classificationService: HistoricalGstClassificationService,
    private readonly toast: ToastService
  ) {}

  get loading(): boolean {
    return this.previewLoading || this.applying;
  }

  ngOnDestroy(): void {
    this.cancelPreview();
  }

  /** The per-kind rows, in the order the acceptance criteria name them. */
  componentRows(): { label: string; counts: HistoricalGstClassificationPreview['summary']['productLines'] }[] {
    const summary = this.preview?.summary;
    if (!summary) {
      return [];
    }
    return [
      { label: 'Purchased items', counts: summary.productLines },
      { label: 'Delivery charges', counts: summary.deliveryCharges },
      { label: 'Package charges', counts: summary.packageCharges }
    ];
  }

  runPreview(): void {
    this.cancelPreview();
    const sequence = ++this.previewSequence;
    this.previewLoading = true;
    this.preview = null;
    this.applied = null;
    this.previewSubscription = this.classificationService.preview().subscribe({
      next: preview => {
        if (sequence !== this.previewSequence) return;
        this.preview = preview;
        this.previewLoading = false;
      },
      error: err => {
        if (sequence !== this.previewSequence) return;
        this.previewLoading = false;
        this.toast.error(this.extractErrorMessage(err, 'Unable to preview the historical GST classification.'));
      }
    });
  }

  applyPreview(): void {
    const preview = this.preview;
    if (!preview || this.loading) return;
    if (!window.confirm(
          `Classify ${preview.summary.becomingTaxable + preview.summary.becomingGstFree} purchase ` +
            'component(s) from the configured GST rules? This changes recorded bookkeeping data. ' +
            'Classifications already chosen by a person are not affected.'
        )) return;

    this.applying = true;
    this.classificationService.apply(preview).subscribe({
      next: applied => {
        this.applying = false;
        // The preview describes work that is now done; a fresh one is the only honest next view.
        this.preview = null;
        this.applied = applied;
        this.toast.success(`Classified ${applied.componentsClassified} purchase component(s).`);
      },
      error: err => {
        this.applying = false;
        // A refusal means the purchase data or the rules changed, so this preview can never be
        // applied again. Clearing it forces a fresh one, which is what the server's own message asks
        // for.
        this.preview = null;
        this.applied = null;
        this.toast.error(this.extractErrorMessage(err, 'Unable to apply the historical GST classification.'));
      }
    });
  }

  private cancelPreview(): void {
    this.previewSequence++;
    this.previewSubscription?.unsubscribe();
    this.previewSubscription = null;
    this.previewLoading = false;
  }

  // Some admin actions return a plain string error body rather than a ProblemDetails object; both
  // shapes are handled here, matching the fallback the other Admin workflows already use.
  private extractErrorMessage(err: unknown, fallback: string): string {
    const body = (err as { error?: unknown } | undefined)?.error;
    if (typeof body === 'string') return body;
    const problem = body as { message?: string; title?: string } | undefined;
    return problem?.message ?? problem?.title ?? fallback;
  }
}
