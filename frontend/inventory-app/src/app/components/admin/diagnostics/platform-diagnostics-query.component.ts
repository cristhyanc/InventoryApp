import { Component, Input, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import {
  PlatformDiagnosticsLimits,
  PlatformDiagnosticsQueryResult,
  PlatformDiagnosticsService
} from '../../../services/platform-diagnostics.service';

/** One permitted table and the exact columns a diagnostics query may read from it. */
interface PermittedTable {
  readonly table: string;
  readonly columns: string;
}

/** A read-only example statement and the question it answers. */
interface QueryExample {
  readonly purpose: string;
  readonly sql: string;
}

/**
 * The super-admin diagnostics query workflow (issue #335), composed by the routed page per
 * docs/architecture.md § Page composition boundary (issue #191): the statement, the submission,
 * the loading state and every outcome the API can report live here rather than on the page.
 *
 * **It holds no authority and no limit of its own.** `POST /api/admin/diagnostics/query` is
 * authorized on every request against the configured platform-admin identity, enforces the
 * permitted table/column surface inside SQLite, and caps the duration, the row count, the
 * response bytes and the submitted SQL (issue #336). This component submits one statement and
 * reports exactly what came back: it never raises a limit, never retries a refusal, and never
 * presents a truncated read as a complete answer.
 *
 * **Nothing is persisted and nothing is logged.** The statement and its results exist in this
 * component's state for as long as the page is open and nowhere else - not in `localStorage`,
 * `sessionStorage`, a cookie, a URL, a toast or the console - because they describe data across
 * every business. The server's audit event is the record of a query, and it carries a fingerprint
 * of the query shape rather than the statement; that fingerprint is shown so a result on screen
 * can be matched to its log entry.
 */
@Component({
  selector: 'app-platform-diagnostics-query',
  standalone: true,
  imports: [FormsModule],
  template: `
    <section class="card">
      <div class="card-header">
        <h2 class="card-title">Run a read-only query</h2>
      </div>
      <div class="card-body">
        <p class="text-sm value-muted">
          One read-only <code>SELECT</code> (or <code>WITH &hellip; SELECT</code>) statement, read
          across every business. The server refuses anything else &mdash; a write, a schema change,
          a second statement or a column outside the permitted surface below &mdash; and stops a
          query at {{ limits.maxDurationSeconds }} seconds, {{ limits.maxRows }} rows or
          {{ maxResponseLabel }} of response, whichever comes first. None of those limits can be
          raised from here.
        </p>

        <div class="field mt-4">
          <label class="field-label" for="diagnostics-sql">Read-only statement</label>
          <textarea
            id="diagnostics-sql"
            rows="6"
            spellcheck="false"
            aria-describedby="diagnostics-sql-help"
            placeholder="SELECT Id, BusinessId FROM Products"
            [disabled]="submitting"
            [(ngModel)]="sql"
          ></textarea>
          <p id="diagnostics-sql-help" class="text-sm value-muted">
            Up to {{ maxSqlLabel }} of SQL. The statement is sent to the API and is not saved in
            this browser.
          </p>
        </div>

        <div class="page-actions">
          <button type="button" class="btn btn-primary" [disabled]="!canSubmit" (click)="run()">
            {{ submitting ? 'Running…' : 'Run query' }}
          </button>
          <button type="button" class="btn btn-secondary" [disabled]="submitting" (click)="clear()">
            Clear
          </button>
        </div>

        @if (submitting) {
          <p role="status" class="mt-4 text-sm value-muted" data-testid="diagnostics-running">
            Running the query. The server stops it after {{ limits.maxDurationSeconds }} seconds.
          </p>
        }

        @if (denied) {
          <div class="alert alert-danger mt-4" role="alert" data-testid="diagnostics-denied">
            <p class="alert-title">The diagnostics API refused this request.</p>
            <p class="mt-1">
              Platform-admin access is a separately configured identity held outside the business
              data, so it cannot be granted from this page. Nothing was read.
            </p>
          </div>
        }

        @if (errorMessage) {
          <div class="alert alert-danger mt-4" role="alert" data-testid="diagnostics-error">
            <p class="alert-title">{{ errorMessage }}</p>
            <p class="mt-1">Nothing was read. Try again, or check that the API is reachable.</p>
          </div>
        }

        @if (result) {
          @if (interrupted) {
            <div class="alert alert-warning mt-4" role="alert" data-testid="diagnostics-timeout">
              <p class="alert-title">{{ interruptedTitle }}</p>
              <p class="mt-1">
                No rows were returned after {{ result.durationMilliseconds }} ms. The server
                interrupts the query itself at its {{ limits.maxDurationSeconds }}-second ceiling,
                which cannot be extended; narrow the query and run it again.
              </p>
            </div>
          } @else if (rejected) {
            <div class="alert alert-danger mt-4" role="alert" data-testid="diagnostics-rejected">
              <p class="alert-title">Refused before anything was read: {{ denialTitle }}</p>
              @if (result.message) {
                <p class="mt-1">{{ result.message }}</p>
              }
            </div>
          } @else if (failed) {
            <div class="alert alert-danger mt-4" role="alert" data-testid="diagnostics-failed">
              <p class="alert-title">The database reported an error for this statement.</p>
              @if (result.message) {
                <p class="mt-1">{{ result.message }}</p>
              }
            </div>
          } @else {
            @if (result.truncated) {
              <div class="alert alert-warning mt-4" role="alert" data-testid="diagnostics-truncated">
                <p class="alert-title">Incomplete result &mdash; {{ truncationTitle }}</p>
                <p class="mt-1">{{ truncationDetail }}</p>
              </div>
            } @else {
              <p class="mt-4 text-sm text-md-success-text" data-testid="diagnostics-complete">
                Complete result: {{ result.rowCount }} row(s) read in
                {{ result.durationMilliseconds }} ms.
              </p>
            }

            @if (result.rows.length > 0) {
              <div class="mt-3 overflow-x-auto">
                <table class="table" data-testid="diagnostics-results">
                  <thead class="table-head">
                    <tr>
                      @for (column of result.columns; track $index) {
                        <th scope="col" class="table-cell">{{ column }}</th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of result.rows; track $index) {
                      <tr class="table-row">
                        @for (cell of row; track $index) {
                          <td class="table-cell">
                            @if (cell === null) {
                              <span class="value-muted">(null)</span>
                            } @else {
                              {{ cell }}
                            }
                          </td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <p class="mt-3 text-sm value-muted" data-testid="diagnostics-no-rows">
                No rows matched. Across every business, this query found nothing.
              </p>
            }

            <p class="mt-3 text-sm value-muted">
              Audit fingerprint <code>{{ result.queryFingerprint }}</code> identifies this query's
              shape in the server's audit log, which records who ran it and never the statement
              itself.
            </p>
          }
        }
      </div>
    </section>

    <section class="card mt-6">
      <div class="card-header">
        <h2 class="card-title">What a query may read</h2>
      </div>
      <div class="card-body">
        <p class="text-sm value-muted">
          These tables and columns are the whole permitted surface: identity and foreign-key
          columns only, with no name, note, amount, quantity, timestamp, imported payload or
          credential on it, and no wildcard over columns added later. This is not unrestricted
          database access, and the server &mdash; not this list &mdash; decides: a statement
          reaching anything else is refused while it is being prepared.
        </p>

        <div class="mt-4 overflow-x-auto">
          <table class="table">
            <thead class="table-head">
              <tr>
                <th scope="col" class="table-cell">Table</th>
                <th scope="col" class="table-cell">Readable columns</th>
              </tr>
            </thead>
            <tbody>
              @for (permitted of permittedTables; track permitted.table) {
                <tr class="table-row">
                  <td class="table-cell">{{ permitted.table }}</td>
                  <td class="table-cell">{{ permitted.columns }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <h3 class="mt-5 text-sm font-semibold text-md-gray-800">Read-only examples</h3>
        <ul class="mt-2 flex flex-col gap-3">
          @for (example of examples; track example.purpose) {
            <li>
              <p class="text-sm text-md-gray-800">{{ example.purpose }}</p>
              <pre class="mt-1 overflow-x-auto rounded-md-control bg-md-gray-100 p-2 text-xs">{{ example.sql }}</pre>
              <button type="button" class="btn-link text-sm" [disabled]="submitting" (click)="useExample(example)">
                Use this example
              </button>
            </li>
          }
        </ul>
      </div>
    </section>
  `
})
export class PlatformDiagnosticsQueryComponent implements OnDestroy {
  /** The server's own maxima, from `GET /api/admin/diagnostics/access`. Never restated here. */
  @Input({ required: true }) limits!: PlatformDiagnosticsLimits;

  sql = '';
  submitting = false;
  result: PlatformDiagnosticsQueryResult | null = null;
  denied = false;
  errorMessage: string | null = null;

  /**
   * The permitted surface as documented by issue #336 (docs/architecture.md § Platform
   * diagnostics). It is help text, not a rule: `PlatformDiagnosticsDataSurface` is the
   * authoritative allow-list and SQLite's own authorizer enforces it, so a statement outside the
   * real surface is refused by the server whatever is listed here.
   */
  readonly permittedTables: readonly PermittedTable[] = [
    { table: 'Businesses', columns: 'Id' },
    { table: 'Categories', columns: 'Id, BusinessId' },
    { table: 'Suppliers', columns: 'Id, BusinessId' },
    { table: 'Products', columns: 'Id, BusinessId, CategoryId, SupplierId' },
    { table: 'Receipts (purchases)', columns: 'Id, BusinessId, SupplierId' },
    { table: 'ReceiptItems (purchase lines)', columns: 'Id, BusinessId, ReceiptId, ProductId' },
    { table: 'StockAdjustments', columns: 'Id, BusinessId, ProductId, ReceiptItemId' }
  ];

  /** The investigations this surface exists for: orphaned rows and cross-business ownership. */
  readonly examples: readonly QueryExample[] = [
    {
      purpose: 'Products whose category belongs to a different business',
      sql:
        'SELECT p.Id, p.BusinessId, c.Id, c.BusinessId\n' +
        'FROM Products p\n' +
        'JOIN Categories c ON c.Id = p.CategoryId\n' +
        'WHERE c.BusinessId <> p.BusinessId'
    },
    {
      purpose: 'Purchase lines whose purchase no longer exists',
      sql:
        'SELECT ri.Id, ri.BusinessId, ri.ReceiptId\n' +
        'FROM ReceiptItems ri\n' +
        'LEFT JOIN Receipts r ON r.Id = ri.ReceiptId\n' +
        'WHERE r.Id IS NULL'
    },
    {
      purpose: 'How many products each business owns',
      sql: 'SELECT BusinessId, COUNT(Id)\nFROM Products\nGROUP BY BusinessId'
    }
  ];

  private querySubscription: Subscription | null = null;

  constructor(private readonly diagnostics: PlatformDiagnosticsService) {}

  get canSubmit(): boolean {
    return !this.submitting && this.sql.trim().length > 0;
  }

  get maxSqlLabel(): string {
    return this.formatBytes(this.limits.maxSqlBytes);
  }

  get maxResponseLabel(): string {
    return this.formatBytes(this.limits.maxResponseBytes);
  }

  /** The server interrupted the query itself: its deadline passed, or the request was cancelled. */
  get interrupted(): boolean {
    return this.result?.outcome === 'TimedOut' || this.result?.outcome === 'Cancelled';
  }

  get rejected(): boolean {
    return this.result?.outcome === 'Rejected';
  }

  get failed(): boolean {
    return this.result?.outcome === 'Failed';
  }

  get interruptedTitle(): string {
    return this.result?.outcome === 'Cancelled'
      ? 'The request was cancelled before the query finished.'
      : `Timed out at the server's ${this.limits.maxDurationSeconds}-second ceiling.`;
  }

  /** The refusal, in the operator's terms. The server's own message is shown alongside it. */
  get denialTitle(): string {
    switch (this.result?.denialReason) {
      case 'SqlMissing':
        return 'no statement was submitted.';
      case 'SqlTooLarge':
        return `the statement is larger than the server's ${this.maxSqlLabel} limit.`;
      case 'MultipleStatements':
        return 'more than one statement was submitted; submit exactly one.';
      case 'NotAReadOnlyStatement':
        return 'this is not a single read-only SELECT or WITH … SELECT statement.';
      case 'ForbiddenSchemaAccess':
        return 'the statement reached a table or column outside the permitted surface below.';
      case 'ForbiddenOperation':
        return 'the statement attempted an operation this read-only connection does not permit.';
      default:
        return 'the server refused the statement.';
    }
  }

  /**
   * Which cap stopped the read. The two are reported separately on purpose: a byte cap reached
   * before the row cap means the rows on screen are a prefix even though fewer than the maximum
   * number of rows came back, which is exactly the case a row count alone would misrepresent.
   */
  get truncationTitle(): string {
    return this.result?.truncationReason === 'ResponseByteLimit'
      ? `the ${this.maxResponseLabel} response cap was reached`
      : `the ${this.limits.maxRows}-row cap was reached`;
  }

  get truncationDetail(): string {
    const rowCount = this.result?.rowCount ?? 0;
    if (this.result?.truncationReason === 'ResponseByteLimit') {
      return (
        `Reading stopped after ${rowCount} row(s) because the next row would have taken the ` +
        `response past ${this.maxResponseLabel}, before the ${this.limits.maxRows}-row cap. ` +
        'These rows are a prefix, not the answer: there may be more. Return fewer columns or ' +
        'narrow the query, then run it again.'
      );
    }
    return (
      `Reading stopped at the server's ${this.limits.maxRows}-row cap, so these ${rowCount} ` +
      'row(s) are a prefix, not the answer: there may be more. Narrow the query, then run it again.'
    );
  }

  ngOnDestroy(): void {
    this.querySubscription?.unsubscribe();
  }

  run(): void {
    if (!this.canSubmit) {
      return;
    }

    this.querySubscription?.unsubscribe();
    this.resetOutcome();
    this.submitting = true;

    this.querySubscription = this.diagnostics.query(this.sql).subscribe({
      next: (result) => {
        this.submitting = false;
        this.result = result;
      },
      error: (error: unknown) => {
        this.submitting = false;
        this.applyErrorResponse(error);
      }
    });
  }

  /** Drops the statement and every result from memory; there is nowhere else they could be. */
  clear(): void {
    this.querySubscription?.unsubscribe();
    this.querySubscription = null;
    this.sql = '';
    this.resetOutcome();
  }

  useExample(example: QueryExample): void {
    this.sql = example.sql;
    this.resetOutcome();
  }

  /**
   * A refusal, a timeout and a provider failure all arrive as a non-success status carrying the
   * same result body, so the outcome is read from that body rather than guessed from the status
   * code. A `401`/`403` is the policy itself refusing, and anything without a result body is a
   * transport failure.
   */
  private applyErrorResponse(error: unknown): void {
    const response = error as { status?: number; error?: unknown } | null;

    if (response?.status === 401 || response?.status === 403) {
      this.denied = true;
      return;
    }

    const body = response?.error;
    if (isQueryResult(body)) {
      this.result = body;
      return;
    }

    this.errorMessage = 'The diagnostics query could not be completed.';
  }

  private resetOutcome(): void {
    this.result = null;
    this.denied = false;
    this.errorMessage = null;
  }

  private formatBytes(bytes: number): string {
    if (bytes >= 1024 * 1024) {
      return `${Math.round(bytes / (1024 * 1024))} MiB`;
    }
    if (bytes >= 1024) {
      return `${Math.round(bytes / 1024)} KiB`;
    }
    return `${bytes} bytes`;
  }
}

function isQueryResult(body: unknown): body is PlatformDiagnosticsQueryResult {
  const candidate = body as PlatformDiagnosticsQueryResult | null | undefined;
  return (
    typeof candidate?.outcome === 'string' && Array.isArray(candidate.columns) && Array.isArray(candidate.rows)
  );
}
