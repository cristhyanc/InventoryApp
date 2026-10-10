import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import {
  NayaxSaleTimestampEvidenceSource,
  NayaxSaleTimestampMissingSale,
  NayaxSaleTimestampRepairPreview,
  NayaxSaleTimestampRepairProduct
} from '../../../services/nayax-sale-timestamp-repair.service';
import { NayaxSaleTimestampRepairRowsComponent } from './nayax-sale-timestamp-repair-rows.component';

/**
 * The whole preview of a Nayax sale timestamp repair, as the operator reviews it before confirming
 * anything (issue #487, over #472's preview contract). Composed by
 * `NayaxSaleTimestampRepairWorkflowComponent`, which owns the request, the confirmation and the
 * apply; this component is display only and holds no action and no API call.
 *
 * **It reimplements no decision.** Every count, instant, business date, amount, movement, rebuild
 * gate and reconciliation figure is the server's own. In particular:
 *
 * - an *affected* product is not a *rebuilt* one: `rebuildPlanned` is the server's baseline-cutoff
 *   gate, and the table shows it per product rather than implying every affected product replays;
 * - `missingFromDatabase` transactions are missing sales, not timestamp defects. The repair cannot
 *   create one, so they are presented separately with the import guidance and no import action;
 * - `totalAfter` is never called reconciled while `unresolvedCompletedCount` or
 *   `missingFromDatabaseCount` is above zero. `sourceVerifiedTotalAfter` is the figure comparable
 *   with the source export, and the gaps are stated next to it.
 */
@Component({
  selector: 'app-nayax-sale-timestamp-repair-preview',
  standalone: true,
  imports: [CommonModule, BusinessDateTimePipe, NayaxSaleTimestampRepairRowsComponent],
  template: `
    <section class="card mt-6" data-testid="repair-preview">
      <div class="card-header">
        <h2 class="card-title">Preview &mdash; nothing has been repaired yet</h2>
      </div>
      <div class="card-body">
        <div class="grid gap-2 text-sm text-md-gray-800 sm:grid-cols-2 xl:grid-cols-3">
          <div data-testid="repair-count-repairable">Repairable: <strong>{{ preview.repairable }}</strong></div>
          <div data-testid="repair-count-already-correct">Already correct: <strong>{{ preview.alreadyCorrect }}</strong></div>
          <div data-testid="repair-count-unresolved">Unresolved: <strong>{{ preview.unresolved }}</strong></div>
          <div>Sales examined: <strong>{{ preview.salesExamined }}</strong></div>
          <div>Evidence records read: <strong>{{ preview.evidenceRecords }}</strong></div>
          <div>Missing from database: <strong>{{ preview.missingFromDatabase.length }}</strong></div>
          <div class="sm:col-span-2 xl:col-span-3">
            Examined range (UTC):
            <strong>
              @if (preview.examinedFromUtc && preview.examinedToUtc) {
                {{ preview.examinedFromUtc | businessDateTime }} &ndash; {{ preview.examinedToUtc | businessDateTime }}
              } @else {
                No range &mdash; the sources covered nothing to examine
              }
            </strong>
          </div>
          <div class="sm:col-span-2 xl:col-span-3">
            Plan expires: <strong>{{ preview.expiresAt | businessDateTime }}</strong>
            <span class="value-muted"> (server plan {{ preview.previewId }})</span>
          </div>
        </div>

        @if (preview.repairable === 0) {
          <div class="alert alert-info mt-4" data-testid="repair-no-change">
            <p class="alert-title">No stored sale would change.</p>
            <p class="mt-1">
              The sources named no instant that differs from what is stored, so there is nothing to
              apply. This is also what a completed repair looks like when it is previewed again.
            </p>
          </div>
        }

        @if (preview.unresolved > 0) {
          <div class="alert alert-warning mt-4" data-testid="repair-unresolved-warning">
            <p class="alert-title">
              {{ preview.unresolved }} examined sale(s) stay exactly as they are.
            </p>
            <p class="mt-1">
              No source verified them, so the repair neither moves nor explains them, and applying it
              is not a complete repair of this period. Each one's reason is in the table below;
              obtain an export with the <code>AuthorizationDateTimeGMT</code> column covering those
              transactions and preview again.
            </p>
          </div>
        }

        <div class="mt-5">
          <app-nayax-sale-timestamp-repair-rows [rows]="preview.rows"></app-nayax-sale-timestamp-repair-rows>
        </div>

        <div class="mt-6">
          <h3 class="text-sm font-semibold text-md-gray-800">Daily revenue movement</h3>
          <p class="mt-1 text-sm value-muted">
            What each business day loses and gains because a completed
            sale is re-dated. A repair never creates or destroys revenue; it only moves it between
            days. A pending, refunded, cancelled or unknown-status row is re-dated too but moves no
            revenue.
          </p>
          @if (preview.revenueMovement.length) {
            <div class="mt-3 overflow-x-auto">
              <table class="table" data-testid="repair-revenue-movement">
                <caption class="sr-only">Revenue leaving and arriving on each business day</caption>
                <thead class="table-head">
                  <tr>
                    <th scope="col" class="table-cell">Business date</th>
                    <th scope="col" class="table-cell">Leaving</th>
                    <th scope="col" class="table-cell">Arriving</th>
                    <th scope="col" class="table-cell">Net</th>
                  </tr>
                </thead>
                <tbody>
                  @for (movement of preview.revenueMovement; track movement.businessDate) {
                    <tr class="table-row">
                      <td class="table-cell">{{ movement.businessDate | date:'dd/MM/yyyy' }}</td>
                      <td class="table-cell">{{ movement.amountLeaving | currency:'AUD' }}</td>
                      <td class="table-cell">{{ movement.amountArriving | currency:'AUD' }}</td>
                      <td class="table-cell">{{ movement.netMovement | currency:'AUD' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <p class="mt-3 text-sm value-muted" data-testid="repair-revenue-movement-empty">
              No completed sale changes its business date, so no day's revenue moves.
            </p>
          }
        </div>

        <div class="mt-6">
          <h3 class="text-sm font-semibold text-md-gray-800">Affected products and costing replay</h3>
          <p class="mt-1 text-sm value-muted">
            The products a re-dated completed sale belongs to, and the instant their cost history
            would be replayed from. <strong>An affected product is not necessarily a rebuilt
            one:</strong> the server replays a product only when it has an inventory-cost transition
            baseline and the replay starts after that baseline's cutoff, which is the same gate the
            sales import uses. A product it does not replay keeps its current costing.
          </p>
          @if (preview.affectedProducts.length) {
            <div class="mt-3 overflow-x-auto">
              <table class="table" data-testid="repair-affected-products">
                <caption class="sr-only">
                  Affected products, their replay start instant, baseline cutoff and whether a rebuild is planned
                </caption>
                <thead class="table-head">
                  <tr>
                    <th scope="col" class="table-cell">Product</th>
                    <th scope="col" class="table-cell">Replay starts (UTC)</th>
                    <th scope="col" class="table-cell">Transition baseline</th>
                    <th scope="col" class="table-cell">Rebuild planned</th>
                  </tr>
                </thead>
                <tbody>
                  @for (product of preview.affectedProducts; track product.productId) {
                    <tr class="table-row">
                      <td class="table-cell">
                        {{ product.productName || 'Unnamed product' }}
                        <div class="value-muted">product {{ product.productId }}</div>
                      </td>
                      <td class="table-cell">{{ product.rebuildFromUtc | businessDateTime }}</td>
                      <td class="table-cell">
                        @if (product.hasTransitionBaseline) {
                          {{ product.transitionCutoffAt | businessDateTime }}
                        } @else {
                          <span class="value-muted">None recorded</span>
                        }
                      </td>
                      <td class="table-cell">
                        <span [class]="product.rebuildPlanned ? 'badge badge-info' : 'badge badge-neutral'">
                          {{ rebuildLabel(product) }}
                        </span>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <p class="mt-2 text-sm value-muted" data-testid="repair-rebuild-summary">
              {{ plannedRebuildCount() }} of {{ preview.affectedProducts.length }} affected product(s)
              would be replayed.
            </p>
          } @else {
            <p class="mt-3 text-sm value-muted" data-testid="repair-affected-products-empty">
              No product's costing would be replayed: no re-dated completed sale is matched to a
              local product.
            </p>
          }
        </div>

        <div class="mt-6">
          <h3 class="text-sm font-semibold text-md-gray-800">Transactions this business holds no sale for</h3>
          @if (preview.missingFromDatabase.length) {
            <div class="alert alert-warning mt-2" data-testid="repair-missing-warning">
              <p class="alert-title">
                {{ preview.missingFromDatabase.length }} transaction(s) in the evidence have no stored
                sale at all.
              </p>
              <p class="mt-1">
                These are <strong>missing sales, not timestamp defects</strong>, and this repair
                neither imports nor creates them &mdash; applying it will not make them appear. Import
                them through the ordinary Nayax sales import, scoped to those transactions, and then
                take a fresh preview. Re-uploading the whole export through the import would move
                stored instants with no preview, which is exactly what this operation exists to
                replace.
              </p>
            </div>
            <div class="mt-3 overflow-x-auto">
              <table class="table" data-testid="repair-missing-sales">
                <caption class="sr-only">Evidence transactions with no stored sale in this business</caption>
                <thead class="table-head">
                  <tr>
                    <th scope="col" class="table-cell">Transaction / machine</th>
                    <th scope="col" class="table-cell">Amount / status</th>
                    <th scope="col" class="table-cell">Authorized (UTC)</th>
                    <th scope="col" class="table-cell">Business date</th>
                    <th scope="col" class="table-cell">Source</th>
                  </tr>
                </thead>
                <tbody>
                  @for (missing of preview.missingFromDatabase; track missing.transactionId) {
                    <tr class="table-row">
                      <td class="table-cell">
                        #{{ missing.transactionId }}
                        <div class="value-muted">machine {{ missing.machineId }}</div>
                      </td>
                      <td class="table-cell">
                        {{ missing.settlementValue | currency:'AUD' }}
                        <div class="value-muted">{{ missingStatusLabel(missing) }}</div>
                      </td>
                      <td class="table-cell">
                        @if (missing.authorizationInstantUtc) {
                          {{ missing.authorizationInstantUtc | businessDateTime }}
                        } @else {
                          <span class="value-muted">Unreadable</span>
                        }
                      </td>
                      <td class="table-cell">
                        @if (missing.authorizationBusinessDate) {
                          {{ missing.authorizationBusinessDate | date:'dd/MM/yyyy' }}
                        } @else {
                          <span class="value-muted">Unknown</span>
                        }
                      </td>
                      <td class="table-cell">{{ sourceLabel(missing.evidenceSource) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <p class="mt-2 text-sm value-muted" data-testid="repair-missing-sales-empty">
              Every transaction the sources carry already has a stored sale in this business.
            </p>
          }
        </div>

        @if (preview.reconciliation) {
          <div class="mt-6" data-testid="repair-reconciliation">
            <h3 class="text-sm font-semibold text-md-gray-800">Fixed-cutoff reconciliation</h3>
            <p class="mt-1 text-sm value-muted">
              Completed sales between {{ preview.reconciliation.fromBusinessDate | date:'dd/MM/yyyy' }}
              and {{ preview.reconciliation.toBusinessDate | date:'dd/MM/yyyy' }} (business time zone, inclusive),
              excluding everything authorized at or after
              {{ preview.reconciliation.cutoffUtc | businessDateTime }}.
            </p>

            <div class="mt-3 grid gap-2 text-sm text-md-gray-800 sm:grid-cols-2 xl:grid-cols-3">
              <div>Total before: <strong>{{ preview.reconciliation.totalBefore | currency:'AUD' }}</strong>
                <span class="value-muted"> ({{ preview.reconciliation.completedCountBefore }} sale(s))</span></div>
              <div>Total after: <strong>{{ preview.reconciliation.totalAfter | currency:'AUD' }}</strong>
                <span class="value-muted"> ({{ preview.reconciliation.completedCountAfter }} sale(s))</span></div>
              <div data-testid="repair-source-verified">Source-verified after:
                <strong>{{ preview.reconciliation.sourceVerifiedTotalAfter | currency:'AUD' }}</strong>
                <span class="value-muted"> ({{ preview.reconciliation.sourceVerifiedCountAfter }} sale(s))</span></div>
              <div>Unresolved in window:
                <strong>{{ preview.reconciliation.unresolvedCompletedAmount | currency:'AUD' }}</strong>
                <span class="value-muted"> ({{ preview.reconciliation.unresolvedCompletedCount }} sale(s))</span></div>
              <div>Missing from database:
                <strong>{{ preview.reconciliation.missingFromDatabaseAmount | currency:'AUD' }}</strong>
                <span class="value-muted"> ({{ preview.reconciliation.missingFromDatabaseCount }} sale(s))</span></div>
              <div>Excluded after cutoff:
                <strong>{{ preview.reconciliation.excludedAfterCutoffAmount | currency:'AUD' }}</strong>
                <span class="value-muted"> ({{ preview.reconciliation.excludedAfterCutoffCount }} sale(s))</span></div>
            </div>

            @if (hasSourceGaps()) {
              <div class="alert alert-warning mt-3" data-testid="repair-reconciliation-incomplete">
                <p class="alert-title">This window is not fully source-verified.</p>
                <p class="mt-1">
                  "Total after" still carries
                  {{ preview.reconciliation.unresolvedCompletedAmount | currency:'AUD' }} of unresolved
                  completed sales, and the evidence names
                  {{ preview.reconciliation.missingFromDatabaseAmount | currency:'AUD' }} of sales this
                  business does not hold. Compare the source export against
                  <strong>source-verified after</strong>; its own period total only equals
                  source-verified after plus the missing amount once those sales are imported and this
                  repair is applied.
                </p>
              </div>
            } @else {
              <p class="mt-3 text-sm text-md-success-text" data-testid="repair-reconciliation-complete">
                Every completed sale in this window was covered by a source and is held in the
                database, so "total after" and "source-verified after" are the same figure.
              </p>
            }

            @if (preview.reconciliation.excludedAfterCutoffCount > 0) {
              <p class="mt-2 text-sm value-muted">
                {{ preview.reconciliation.excludedAfterCutoffCount }} completed sale(s) were authorized
                at or after the cutoff, so the compared export was taken too early to contain them.
                That is not a discrepancy.
              </p>
            }

            @if (preview.reconciliation.days.length) {
              <div class="mt-3 overflow-x-auto">
                <table class="table" data-testid="repair-reconciliation-days">
                  <caption class="sr-only">
                    Completed-sale count and value per business day, before and after the repair
                  </caption>
                  <thead class="table-head">
                    <tr>
                      <th scope="col" class="table-cell">Business date</th>
                      <th scope="col" class="table-cell">Before</th>
                      <th scope="col" class="table-cell">After</th>
                      <th scope="col" class="table-cell">Unresolved</th>
                      <th scope="col" class="table-cell">Source-verified after</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (day of preview.reconciliation.days; track day.businessDate) {
                      <tr class="table-row">
                        <td class="table-cell">{{ day.businessDate | date:'dd/MM/yyyy' }}</td>
                        <td class="table-cell">
                          {{ day.completedSalesBefore | currency:'AUD' }}
                          <div class="value-muted">{{ day.completedCountBefore }} sale(s)</div>
                        </td>
                        <td class="table-cell">
                          {{ day.completedSalesAfter | currency:'AUD' }}
                          <div class="value-muted">{{ day.completedCountAfter }} sale(s)</div>
                        </td>
                        <td class="table-cell">
                          {{ day.unresolvedAmount | currency:'AUD' }}
                          <div class="value-muted">{{ day.unresolvedCount }} sale(s)</div>
                        </td>
                        <td class="table-cell">
                          {{ day.sourceVerifiedAfter | currency:'AUD' }}
                          <div class="value-muted">{{ day.sourceVerifiedCountAfter }} sale(s)</div>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <p class="mt-3 text-sm value-muted">
                The window holds no completed sale before or after the repair.
              </p>
            }
          </div>
        }
      </div>
    </section>
  `
})
export class NayaxSaleTimestampRepairPreviewComponent {
  /** The server's plan, displayed exactly as returned. */
  @Input({ required: true }) preview!: NayaxSaleTimestampRepairPreview;

  plannedRebuildCount(): number {
    return this.preview.affectedProducts.filter((product) => product.rebuildPlanned).length;
  }

  /** Why a product would or would not be replayed, in the server's own terms. */
  rebuildLabel(product: NayaxSaleTimestampRepairProduct): string {
    if (product.rebuildPlanned) {
      return 'Yes';
    }
    return product.hasTransitionBaseline ? 'No - covered by its baseline' : 'No - no baseline recorded';
  }

  /**
   * A gap is anything a timestamp repair cannot explain: a completed sale no source covered, or one
   * the evidence names that this business never imported. While either is above zero, `totalAfter`
   * is not a reconciled figure.
   */
  hasSourceGaps(): boolean {
    const reconciliation = this.preview.reconciliation;
    if (!reconciliation) {
      return false;
    }
    return reconciliation.unresolvedCompletedCount > 0 || reconciliation.missingFromDatabaseCount > 0;
  }

  sourceLabel(source: NayaxSaleTimestampEvidenceSource): string {
    return source === NayaxSaleTimestampEvidenceSource.OperatorExport
      ? 'Operator export'
      : 'Nayax last-sales API';
  }

  missingStatusLabel(missing: NayaxSaleTimestampMissingSale): string {
    const status = missing.transactionStatusId === null ? 'no status' : `status ${missing.transactionStatusId}`;
    return missing.completedSale ? `${status} - completed sale` : `${status} - not a completed sale`;
  }
}
