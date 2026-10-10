import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import {
  NayaxSaleTimestampEvidenceSource,
  NayaxSaleTimestampRepairOutcome,
  NayaxSaleTimestampRepairRow,
  NayaxSaleTimestampUnresolvedReason
} from '../../../services/nayax-sale-timestamp-repair.service';

/** The outcome the operator has narrowed the listing to. `all` is the default. */
export type RowOutcomeFilter = 'all' | 'repairable' | 'alreadyCorrect' | 'unresolved';

/**
 * The examined-sales table of a Nayax sale timestamp repair preview (issue #487), composed by
 * `NayaxSaleTimestampRepairPreviewComponent`.
 *
 * It owns the client-side narrowing of a potentially long list - an outcome filter, a
 * transaction/machine search and paging - and nothing else. **Narrowing is presentation only.** The
 * Apply confirms the server's whole plan, every repairable row included, because the API has no
 * selective-row repair: the note above the table says so, and no filter, page or search value is
 * ever sent to the server or used to build a request.
 *
 * Every value in a cell is the server's own: the stored and repaired UTC instants, the
 * business dates the server resolved, the outcome, the unresolved reason, the evidence provenance
 * and whether the sale is a completed one. No timestamp is parsed or shifted, no business date is
 * re-converted and no status is reclassified here.
 */
@Component({
  selector: 'app-nayax-sale-timestamp-repair-rows',
  standalone: true,
  imports: [CommonModule, FormsModule, BusinessDateTimePipe],
  template: `
    <h3 class="text-sm font-semibold text-md-gray-800">Examined sales</h3>
    <p class="mt-1 text-sm value-muted">
      Every stored sale in the examined range, with the instant and business date it holds
      now and the ones the repair would give it. Filtering, searching and paging change only what is
      listed here: Apply confirms the server's whole plan, including every repairable row that is
      not currently on screen. There is no way to repair a selection of rows.
    </p>

    <div class="mt-3 grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
      <div class="field">
        <label class="field-label" for="repair-rows-outcome">Outcome</label>
        <select id="repair-rows-outcome" [(ngModel)]="outcomeFilter" (ngModelChange)="resetPage()">
          <option value="all">All outcomes ({{ rows.length }})</option>
          <option value="repairable">Repairable ({{ count(Outcome.Repairable) }})</option>
          <option value="alreadyCorrect">Already correct ({{ count(Outcome.AlreadyCorrect) }})</option>
          <option value="unresolved">Unresolved ({{ count(Outcome.Unresolved) }})</option>
        </select>
      </div>
      <div class="field">
        <label class="field-label" for="repair-rows-search">Transaction or machine</label>
        <input
          id="repair-rows-search"
          type="search"
          placeholder="Transaction id, machine id or machine name"
          [(ngModel)]="search"
          (ngModelChange)="resetPage()"
        />
      </div>
    </div>

    @if (!filtered().length) {
      <p class="mt-3 text-sm value-muted" data-testid="repair-rows-empty">
        @if (rows.length) {
          No examined sale matches this filter. Clear it to see all {{ rows.length }} examined row(s).
        } @else {
          The preview examined no stored sale at all. Nothing is repairable.
        }
      </p>
    } @else {
      <div class="mt-3 overflow-x-auto">
        <table class="table" data-testid="repair-rows-table">
          <caption class="sr-only">
            Examined Nayax sales, with their stored and repaired UTC instants, business dates,
            outcome and evidence source
          </caption>
          <thead class="table-head">
            <tr>
              <th scope="col" class="table-cell">Transaction / machine</th>
              <th scope="col" class="table-cell">Amount / status</th>
              <th scope="col" class="table-cell">Stored instant (UTC)</th>
              <th scope="col" class="table-cell">Repaired instant (UTC)</th>
              <th scope="col" class="table-cell">Stored business date</th>
              <th scope="col" class="table-cell">Repaired business date</th>
              <th scope="col" class="table-cell">Outcome</th>
              <th scope="col" class="table-cell">Source / reason</th>
            </tr>
          </thead>
          <tbody>
            @for (row of page(); track row.transactionId) {
              <tr class="table-row">
                <td class="table-cell">
                  #{{ row.transactionId }}
                  <div class="value-muted break-words">
                    {{ row.machineName || 'Unnamed machine' }} ({{ row.machineId }})
                  </div>
                </td>
                <td class="table-cell">
                  {{ row.settlementValue | currency:'AUD' }}
                  <div class="value-muted">{{ statusLabel(row) }}</div>
                </td>
                <td class="table-cell">{{ row.storedInstantUtc | businessDateTime }}</td>
                <td class="table-cell">
                  @if (row.repairedInstantUtc) {
                    {{ row.repairedInstantUtc | businessDateTime }}
                  } @else {
                    <span class="value-muted">Unchanged</span>
                  }
                </td>
                <td class="table-cell">{{ row.storedBusinessDate | date:'dd/MM/yyyy' }}</td>
                <td class="table-cell">
                  @if (row.repairedBusinessDate) {
                    {{ row.repairedBusinessDate | date:'dd/MM/yyyy' }}
                  } @else {
                    <span class="value-muted">Unchanged</span>
                  }
                </td>
                <td class="table-cell">
                  <span [class]="outcomeBadgeClass(row.outcome)">{{ outcomeLabel(row.outcome) }}</span>
                </td>
                <td class="table-cell">
                  <div>{{ sourceLabel(row.evidenceSource) }}</div>
                  @if (row.unresolvedReason !== null) {
                    <div class="value-muted break-words">{{ unresolvedLabel(row.unresolvedReason) }}</div>
                  }
                  @if (row.evidenceReference) {
                    <div class="value-muted break-words">{{ row.evidenceReference }}</div>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      <div class="page-actions mt-3">
        <p class="text-sm value-muted" data-testid="repair-rows-range" role="status">
          Showing {{ firstShown() }}&ndash;{{ lastShown() }} of {{ filtered().length }} matching row(s),
          out of {{ rows.length }} examined.
        </p>
        @if (pageCount() > 1) {
          <button
            type="button"
            class="btn btn-secondary btn-sm"
            [disabled]="pageIndex === 0"
            (click)="previousPage()"
          >Previous</button>
          <span class="text-sm value-muted">Page {{ pageIndex + 1 }} of {{ pageCount() }}</span>
          <button
            type="button"
            class="btn btn-secondary btn-sm"
            [disabled]="pageIndex >= pageCount() - 1"
            (click)="nextPage()"
          >Next</button>
        }
      </div>
    }
  `
})
export class NayaxSaleTimestampRepairRowsComponent {
  /** The server's examined rows, exactly as the preview returned them. */
  @Input({ required: true })
  set rows(value: NayaxSaleTimestampRepairRow[]) {
    this.allRows = value ?? [];
    this.pageIndex = 0;
  }

  get rows(): NayaxSaleTimestampRepairRow[] {
    return this.allRows;
  }

  /** Exposed for the template's option values; the enum itself is the server's vocabulary. */
  readonly Outcome = NayaxSaleTimestampRepairOutcome;

  readonly pageSize = 25;

  outcomeFilter: RowOutcomeFilter = 'all';
  search = '';
  pageIndex = 0;

  private allRows: NayaxSaleTimestampRepairRow[] = [];

  count(outcome: NayaxSaleTimestampRepairOutcome): number {
    return this.allRows.filter((row) => row.outcome === outcome).length;
  }

  filtered(): NayaxSaleTimestampRepairRow[] {
    const term = this.search.trim().toLowerCase();
    return this.allRows.filter((row) => this.matchesOutcome(row) && this.matchesTerm(row, term));
  }

  page(): NayaxSaleTimestampRepairRow[] {
    const start = this.pageIndex * this.pageSize;
    return this.filtered().slice(start, start + this.pageSize);
  }

  pageCount(): number {
    return Math.max(1, Math.ceil(this.filtered().length / this.pageSize));
  }

  firstShown(): number {
    return this.filtered().length === 0 ? 0 : this.pageIndex * this.pageSize + 1;
  }

  lastShown(): number {
    return Math.min((this.pageIndex + 1) * this.pageSize, this.filtered().length);
  }

  resetPage(): void {
    this.pageIndex = 0;
  }

  previousPage(): void {
    this.pageIndex = Math.max(0, this.pageIndex - 1);
  }

  nextPage(): void {
    this.pageIndex = Math.min(this.pageCount() - 1, this.pageIndex + 1);
  }

  outcomeLabel(outcome: NayaxSaleTimestampRepairOutcome): string {
    switch (outcome) {
      case NayaxSaleTimestampRepairOutcome.Repairable:
        return 'Repairable';
      case NayaxSaleTimestampRepairOutcome.AlreadyCorrect:
        return 'Already correct';
      case NayaxSaleTimestampRepairOutcome.Unresolved:
        return 'Unresolved';
      default:
        return 'Unrecognised outcome';
    }
  }

  /** The whole class list, because a static `class` beside a `[class]` binding is a duplicate. */
  outcomeBadgeClass(outcome: NayaxSaleTimestampRepairOutcome): string {
    switch (outcome) {
      case NayaxSaleTimestampRepairOutcome.Repairable:
        return 'badge badge-info';
      case NayaxSaleTimestampRepairOutcome.AlreadyCorrect:
        return 'badge badge-success';
      case NayaxSaleTimestampRepairOutcome.Unresolved:
        return 'badge badge-warning';
      default:
        return 'badge badge-neutral';
    }
  }

  /**
   * The source that decided the row. A row with no evidence at all carries none, which is the
   * `NoSourceEvidence` case the reason beside it explains.
   */
  sourceLabel(source: NayaxSaleTimestampEvidenceSource | null): string {
    switch (source) {
      case NayaxSaleTimestampEvidenceSource.NayaxLastSalesApi:
        return 'Nayax last-sales API';
      case NayaxSaleTimestampEvidenceSource.OperatorExport:
        return 'Operator export';
      default:
        return 'No source';
    }
  }

  /** The server's own refusal to guess, in the operator's terms. */
  unresolvedLabel(reason: NayaxSaleTimestampUnresolvedReason): string {
    switch (reason) {
      case NayaxSaleTimestampUnresolvedReason.NoSourceEvidence:
        return 'No source covered this transaction - typically older than the rolling API window.';
      case NayaxSaleTimestampUnresolvedReason.UnreadableEvidence:
        return 'The source covered it but its authorization value could not be read as an instant.';
      case NayaxSaleTimestampUnresolvedReason.ConflictingEvidence:
        return 'Two sources named different authorization instants; resolve which export is right.';
      case NayaxSaleTimestampUnresolvedReason.MachineMismatch:
        return 'The evidence names another machine, so it is evidence about another sale.';
      case NayaxSaleTimestampUnresolvedReason.AmountMismatch:
        return "The evidence's settled amount does not identify this stored sale.";
      default:
        return 'Unrecognised unresolved reason.';
    }
  }

  /**
   * The raw Nayax status identifier and the server's own completed-sale decision. A pending,
   * refunded, cancelled or unknown-status row stays listed and visibly not a completed sale; only a
   * completed sale moves revenue between business days.
   */
  statusLabel(row: NayaxSaleTimestampRepairRow): string {
    const status = row.transactionStatusId === null ? 'no status' : `status ${row.transactionStatusId}`;
    return row.completedSale ? `${status} - completed sale` : `${status} - not a completed sale`;
  }

  private matchesOutcome(row: NayaxSaleTimestampRepairRow): boolean {
    switch (this.outcomeFilter) {
      case 'repairable':
        return row.outcome === NayaxSaleTimestampRepairOutcome.Repairable;
      case 'alreadyCorrect':
        return row.outcome === NayaxSaleTimestampRepairOutcome.AlreadyCorrect;
      case 'unresolved':
        return row.outcome === NayaxSaleTimestampRepairOutcome.Unresolved;
      default:
        return true;
    }
  }

  private matchesTerm(row: NayaxSaleTimestampRepairRow, term: string): boolean {
    if (!term) {
      return true;
    }
    return (
      String(row.transactionId).includes(term) ||
      String(row.machineId).includes(term) ||
      (row.machineName ?? '').toLowerCase().includes(term)
    );
  }
}
