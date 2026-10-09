import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import {
  NayaxSaleTimestampEvidenceSource,
  NayaxSaleTimestampRepairApplied
} from '../../../services/nayax-sale-timestamp-repair.service';

/**
 * What a committed Nayax sale timestamp repair actually wrote (issue #487), composed by
 * `NayaxSaleTimestampRepairWorkflowComponent`.
 *
 * It states only the server's own counts and its append-only audit rows, and it deliberately makes
 * two things impossible to misread:
 *
 * - **a repair is not a reconciliation.** The rows the applied plan left unresolved, and the sales
 *   the evidence named that this business never held, were not changed by the apply. Their counts
 *   are repeated here so a successful apply is never mistaken for a complete explanation of a
 *   reported difference.
 * - **verification is a fresh preview, and a person asks for it.** `(verifyRequested)` lets the
 *   operator re-run the preview over the inputs still on the form; nothing re-previews or re-applies
 *   on its own, and this component never claims the unresolved rows have gone.
 */
@Component({
  selector: 'app-nayax-sale-timestamp-repair-result',
  standalone: true,
  imports: [CommonModule, BusinessDateTimePipe],
  template: `
    <section class="card mt-6" data-testid="repair-result">
      <div class="card-header">
        <h2 class="card-title">Repair applied</h2>
      </div>
      <div class="card-body">
        <div class="grid gap-2 text-sm text-md-gray-800 sm:grid-cols-2 xl:grid-cols-3">
          <div data-testid="repair-applied-sales">Sales repaired: <strong>{{ applied.salesRepaired }}</strong></div>
          <div data-testid="repair-applied-products">Products rebuilt: <strong>{{ applied.productsRebuilt }}</strong></div>
          <div data-testid="repair-applied-recosted">Sales recosted: <strong>{{ applied.recostedSales }}</strong></div>
          <div class="sm:col-span-2 xl:col-span-3">
            Applied plan: <strong>{{ applied.previewId }}</strong>
          </div>
        </div>

        @if (applied.salesRepaired === 0) {
          <p class="mt-3 text-sm value-muted" data-testid="repair-applied-nothing">
            The confirmed plan had nothing repairable left, so no sale timestamp, audit row or
            costing value was written. Confirming an already-applied repair is a no-op by design.
          </p>
        }

        @if (unresolvedAtPreview > 0 || missingAtPreview > 0) {
          <div class="alert alert-warning mt-4" data-testid="repair-applied-gaps">
            <p class="alert-title">This repair did not resolve everything in the period.</p>
            <p class="mt-1">
              The plan you confirmed also listed
              <strong>{{ unresolvedAtPreview }}</strong> unresolved sale(s) and
              <strong>{{ missingAtPreview }}</strong> transaction(s) with no stored sale. The apply
              changed neither: the unresolved sales keep their stored instants, and no sale was
              imported. Obtain source coverage for the unresolved transactions, import the missing
              ones through the ordinary Nayax sales import, and preview again to see where the period
              stands.
            </p>
          </div>
        }

        @if (applied.repairs.length) {
          <div class="mt-4 overflow-x-auto">
            <table class="table" data-testid="repair-audit-records">
              <caption class="sr-only">
                The append-only audit rows this apply recorded, with the previous and repaired instants
                and business dates
              </caption>
              <thead class="table-head">
                <tr>
                  <th scope="col" class="table-cell">Transaction / machine</th>
                  <th scope="col" class="table-cell">Previous instant (UTC)</th>
                  <th scope="col" class="table-cell">Repaired instant (UTC)</th>
                  <th scope="col" class="table-cell">Business date</th>
                  <th scope="col" class="table-cell">Source</th>
                  <th scope="col" class="table-cell">Applied</th>
                </tr>
              </thead>
              <tbody>
                @for (repair of applied.repairs; track repair.id) {
                  <tr class="table-row">
                    <td class="table-cell">
                      #{{ repair.transactionId }}
                      <div class="value-muted">machine {{ repair.machineId }}</div>
                    </td>
                    <td class="table-cell">{{ repair.previousInstantUtc | businessDateTime }}</td>
                    <td class="table-cell">{{ repair.repairedInstantUtc | businessDateTime }}</td>
                    <td class="table-cell">
                      {{ repair.previousBusinessDate | date:'dd/MM/yyyy' }}
                      &rarr; {{ repair.repairedBusinessDate | date:'dd/MM/yyyy' }}
                    </td>
                    <td class="table-cell">
                      <div>{{ sourceLabel(repair.evidenceSource) }}</div>
                      <div class="value-muted break-words">{{ repair.evidenceReference }}</div>
                    </td>
                    <td class="table-cell">{{ repair.appliedAt | businessDateTime }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        <div class="alert alert-info mt-4">
          <p class="alert-title">Verify before you close this page.</p>
          <p class="mt-1">
            Check the rebuilt products' cost of goods sold and inventory value, and that no completed
            sale became uncosted. Then take a fresh preview over the same sources and window: every
            repaired row should come back as <em>already correct</em>, and each day's source-verified
            figure should match the export's own daily total. A remaining difference is unresolved
            coverage or a sale authorized after the cutoff, not a failed repair. Rolling a committed
            repair back is the human-run database restore, not an action on this page.
          </p>
        </div>

        <div class="page-actions mt-4">
          <button
            type="button"
            class="btn btn-secondary"
            [disabled]="busy"
            (click)="verifyRequested.emit()"
          >Preview again to verify</button>
        </div>
      </div>
    </section>
  `
})
export class NayaxSaleTimestampRepairResultComponent {
  /** The server's own applied result. */
  @Input({ required: true }) applied!: NayaxSaleTimestampRepairApplied;

  /** The unresolved count of the plan that was confirmed. The apply did not change these rows. */
  @Input() unresolvedAtPreview = 0;

  /** The missing-sale count of the plan that was confirmed. The apply imported none of them. */
  @Input() missingAtPreview = 0;

  /** True while the workflow has a request in flight, so verification cannot be double-submitted. */
  @Input() busy = false;

  /** Asks the workflow to run a fresh preview over the inputs still on its form. */
  @Output() readonly verifyRequested = new EventEmitter<void>();

  sourceLabel(source: NayaxSaleTimestampEvidenceSource): string {
    return source === NayaxSaleTimestampEvidenceSource.OperatorExport
      ? 'Operator export'
      : 'Nayax last-sales API';
  }
}
