import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import {
  PlatformDiagnosticsLimits,
  PlatformDiagnosticsQueryResult,
  PlatformDiagnosticsService
} from '../../../services/platform-diagnostics.service';
import { PlatformDiagnosticsQueryComponent } from './platform-diagnostics-query.component';

const limits: PlatformDiagnosticsLimits = {
  maxSqlBytes: 16384,
  maxRows: 500,
  maxResponseBytes: 1048576,
  maxDurationSeconds: 5
};

function result(overrides: Partial<PlatformDiagnosticsQueryResult> = {}): PlatformDiagnosticsQueryResult {
  return {
    outcome: 'Succeeded',
    denialReason: null,
    message: null,
    crossBusinessScope: true,
    columns: ['Id', 'BusinessId'],
    rows: [
      ['1', '7'],
      ['2', '7']
    ],
    rowCount: 2,
    truncated: false,
    truncationReason: null,
    durationMilliseconds: 12,
    queryFingerprint: 'a1b2c3',
    ...overrides
  };
}

/** The API answers a refusal, a timeout and a provider failure with the result body and a status. */
function httpError(status: number, body: unknown): unknown {
  return { status, error: body };
}

interface Rendered {
  fixture: ComponentFixture<PlatformDiagnosticsQueryComponent>;
  component: PlatformDiagnosticsQueryComponent;
  host: HTMLElement;
  queryFn: jest.Mock;
}

async function render(
  response: Observable<PlatformDiagnosticsQueryResult> = of(result())
): Promise<Rendered> {
  const queryFn = jest.fn(() => response);

  await TestBed.configureTestingModule({
    imports: [PlatformDiagnosticsQueryComponent],
    providers: [{ provide: PlatformDiagnosticsService, useValue: { access: jest.fn(), query: queryFn } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(PlatformDiagnosticsQueryComponent);
  fixture.componentRef.setInput('limits', limits);
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, queryFn };
}

function submit(rendered: Rendered, sql = 'SELECT Id, BusinessId FROM Products'): void {
  rendered.component.sql = sql;
  rendered.fixture.detectChanges();
  rendered.host.querySelector<HTMLButtonElement>('button.btn-primary')?.click();
  rendered.fixture.detectChanges();
}

function testId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
}

describe('PlatformDiagnosticsQueryComponent submission (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('submits the statement the operator typed, and nothing else', async () => {
    const rendered = await render();
    submit(rendered, 'SELECT Id FROM Businesses');

    expect(rendered.queryFn).toHaveBeenCalledTimes(1);
    expect(rendered.queryFn).toHaveBeenCalledWith('SELECT Id FROM Businesses');
  });

  it('runs nothing on arrival or for a blank statement', async () => {
    const rendered = await render();
    expect(rendered.queryFn).not.toHaveBeenCalled();

    submit(rendered, '   ');

    expect(rendered.queryFn).not.toHaveBeenCalled();
    expect(rendered.host.querySelector<HTMLButtonElement>('button.btn-primary')?.disabled).toBe(true);
  });

  it('shows a loading state naming the server deadline while the query is in flight', async () => {
    const rendered = await render(new Observable<PlatformDiagnosticsQueryResult>());
    submit(rendered);

    expect(rendered.component.submitting).toBe(true);
    expect(testId(rendered.host, 'diagnostics-running')?.textContent).toContain('after 5 seconds');
    expect(rendered.host.querySelector<HTMLButtonElement>('button.btn-primary')?.disabled).toBe(true);

    // `ngModel` applies a disabled binding on a microtask, so the textarea is checked after it.
    await Promise.resolve();
    rendered.fixture.detectChanges();
    expect(rendered.host.querySelector<HTMLTextAreaElement>('textarea')?.disabled).toBe(true);
  });

  it('publishes the server limits rather than restating them', async () => {
    const { host } = await render();

    expect(host.textContent).toContain('500 rows');
    expect(host.textContent).toContain('1 MiB of response');
    expect(host.textContent).toContain('16 KiB of SQL');
    expect(host.textContent).toContain('5 seconds');
  });

  it('clears the statement and the result on request, leaving nothing behind', async () => {
    const rendered = await render();
    submit(rendered);
    expect(testId(rendered.host, 'diagnostics-results')).not.toBeNull();

    rendered.host.querySelector<HTMLButtonElement>('button.btn-secondary')?.click();
    rendered.fixture.detectChanges();

    expect(rendered.component.sql).toBe('');
    expect(rendered.component.result).toBeNull();
    expect(testId(rendered.host, 'diagnostics-results')).toBeNull();
  });
});

describe('PlatformDiagnosticsQueryComponent results (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('renders a complete result as a table of the columns and rows the API returned', async () => {
    const rendered = await render();
    submit(rendered);

    const table = testId(rendered.host, 'diagnostics-results');
    expect(table?.querySelectorAll('thead th')).toHaveLength(2);
    expect(Array.from(table?.querySelectorAll('thead th') ?? []).map((cell) => cell.textContent?.trim())).toEqual([
      'Id',
      'BusinessId'
    ]);
    expect(table?.querySelectorAll('tbody tr')).toHaveLength(2);
    expect(testId(rendered.host, 'diagnostics-complete')?.textContent).toContain('Complete result: 2 row(s)');
  });

  it('says so plainly when a query matched nothing, rather than rendering an empty table', async () => {
    const rendered = await render(of(result({ rows: [], rowCount: 0 })));
    submit(rendered);

    expect(testId(rendered.host, 'diagnostics-results')).toBeNull();
    expect(testId(rendered.host, 'diagnostics-no-rows')?.textContent).toContain('No rows matched');
  });

  it('renders a null cell as a null marker instead of an empty cell', async () => {
    const rendered = await render(of(result({ rows: [['1', null]], rowCount: 1 })));
    submit(rendered);

    const cells = testId(rendered.host, 'diagnostics-results')?.querySelectorAll('tbody td');
    expect(cells?.[1].textContent?.trim()).toBe('(null)');
  });

  it('shows the audit fingerprint so a result can be matched to its log entry', async () => {
    const rendered = await render();
    submit(rendered);

    expect(rendered.host.textContent).toContain('a1b2c3');
    expect(rendered.host.textContent).toMatch(/never the statement itself/);
  });
});

/**
 * Every truncation, refusal, timeout and failure the contract can report has its own visible
 * state. A prefix must never read as a complete answer, and the two caps are reported separately
 * because a byte cap reached before the row cap is the case a row count alone misrepresents.
 */
describe('PlatformDiagnosticsQueryComponent bounded-result states (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('labels a row-capped result as incomplete and names the row cap', async () => {
    const rendered = await render(
      of(result({ outcome: 'Truncated', truncated: true, truncationReason: 'RowLimit', rowCount: 500 }))
    );
    submit(rendered);

    const notice = testId(rendered.host, 'diagnostics-truncated');
    expect(notice?.textContent).toContain('Incomplete result');
    expect(notice?.textContent).toContain('500-row cap');
    expect(notice?.textContent).toMatch(/prefix, not the answer/);
    expect(testId(rendered.host, 'diagnostics-complete')).toBeNull();
    // The rows that were read are still shown; they are just not presented as the answer.
    expect(testId(rendered.host, 'diagnostics-results')).not.toBeNull();
  });

  it('labels a byte-capped result as incomplete and says the cap hit before the row cap', async () => {
    const rendered = await render(
      of(result({ outcome: 'Truncated', truncated: true, truncationReason: 'ResponseByteLimit', rowCount: 143 }))
    );
    submit(rendered);

    const notice = testId(rendered.host, 'diagnostics-truncated');
    expect(notice?.textContent).toContain('Incomplete result');
    expect(notice?.textContent).toContain('1 MiB response cap');
    expect(notice?.textContent).toContain('after 143 row(s)');
    expect(notice?.textContent).toContain('before the 500-row cap');
    expect(testId(rendered.host, 'diagnostics-complete')).toBeNull();
  });

  it('reports a server timeout distinctly from a refusal or an error', async () => {
    const rendered = await render(
      throwError(() =>
        httpError(408, result({ outcome: 'TimedOut', columns: [], rows: [], rowCount: 0, durationMilliseconds: 5001 }))
      )
    );
    submit(rendered);

    const timeout = testId(rendered.host, 'diagnostics-timeout');
    expect(timeout?.textContent).toContain('Timed out');
    expect(timeout?.textContent).toContain("5-second ceiling");
    expect(timeout?.textContent).toContain('5001 ms');
    expect(testId(rendered.host, 'diagnostics-rejected')).toBeNull();
    expect(testId(rendered.host, 'diagnostics-error')).toBeNull();
    expect(testId(rendered.host, 'diagnostics-results')).toBeNull();
  });

  it('reports a cancelled request as cancelled rather than as a timeout', async () => {
    const rendered = await render(
      throwError(() => httpError(408, result({ outcome: 'Cancelled', columns: [], rows: [], rowCount: 0 })))
    );
    submit(rendered);

    expect(testId(rendered.host, 'diagnostics-timeout')?.textContent).toContain('cancelled');
  });

  it.each([
    ['ForbiddenSchemaAccess', 'outside the permitted surface'],
    ['NotAReadOnlyStatement', 'not a single read-only SELECT'],
    ['MultipleStatements', 'more than one statement'],
    ['ForbiddenOperation', 'does not permit'],
    ['SqlTooLarge', 'larger than'],
    ['SqlMissing', 'no statement was submitted']
  ])('explains a %s refusal and shows the server message with it', async (denialReason, expected) => {
    const rendered = await render(
      throwError(() =>
        httpError(
          400,
          result({
            outcome: 'Rejected',
            denialReason,
            message: 'SQL logic error: access to Products.Name is prohibited',
            columns: [],
            rows: [],
            rowCount: 0
          })
        )
      )
    );
    submit(rendered);

    const rejected = testId(rendered.host, 'diagnostics-rejected');
    expect(rejected?.textContent).toContain('Refused before anything was read');
    expect(rejected?.textContent).toContain(expected);
    expect(rejected?.textContent).toContain('access to Products.Name is prohibited');
    expect(testId(rendered.host, 'diagnostics-results')).toBeNull();
  });

  it('reports a provider failure as a database error, with the provider message', async () => {
    const rendered = await render(
      throwError(() =>
        httpError(400, result({ outcome: 'Failed', message: 'no such column: Nope', columns: [], rows: [], rowCount: 0 }))
      )
    );
    submit(rendered);

    expect(testId(rendered.host, 'diagnostics-failed')?.textContent).toContain('no such column: Nope');
  });

  it('reports a refused request as a refusal, with no query result and no form change', async () => {
    const rendered = await render(throwError(() => httpError(403, null)));
    submit(rendered);

    expect(testId(rendered.host, 'diagnostics-denied')?.textContent).toContain('refused this request');
    expect(rendered.component.result).toBeNull();
    expect(testId(rendered.host, 'diagnostics-results')).toBeNull();
  });

  it('reports an unreachable API as an error rather than as an empty result', async () => {
    const rendered = await render(throwError(() => new Error('network down')));
    submit(rendered);

    expect(testId(rendered.host, 'diagnostics-error')?.textContent).toContain('could not be completed');
    expect(rendered.component.result).toBeNull();
  });

  it('replaces a previous outcome when the next query is submitted', async () => {
    const rendered = await render(throwError(() => httpError(403, null)));
    submit(rendered);
    expect(testId(rendered.host, 'diagnostics-denied')).not.toBeNull();

    rendered.queryFn.mockReturnValue(of(result()));
    submit(rendered);

    expect(testId(rendered.host, 'diagnostics-denied')).toBeNull();
    expect(testId(rendered.host, 'diagnostics-results')).not.toBeNull();
  });
});

/**
 * Result values come from every business on the platform and are never trusted as markup. Angular
 * interpolation escapes them, and this spec pins that down with the payloads that would matter.
 */
describe('PlatformDiagnosticsQueryComponent safe rendering (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('renders a script payload in a cell as text, creating no element', async () => {
    const payload = '<script>window.hacked = true;</script>';
    const rendered = await render(of(result({ columns: ['Id'], rows: [[payload]], rowCount: 1 })));
    submit(rendered);

    const cell = testId(rendered.host, 'diagnostics-results')?.querySelector('tbody td');
    expect(cell?.textContent?.trim()).toBe(payload);
    expect(cell?.querySelector('script')).toBeNull();
    expect(cell?.innerHTML).toContain('&lt;script&gt;');
    expect((window as unknown as { hacked?: boolean }).hacked).toBeUndefined();
  });

  it('renders an event-handler payload in a cell as text, creating no element', async () => {
    const payload = '<img src=x onerror="window.hacked = true">';
    const rendered = await render(of(result({ columns: ['Id'], rows: [[payload]], rowCount: 1 })));
    submit(rendered);

    const cell = testId(rendered.host, 'diagnostics-results')?.querySelector('tbody td');
    expect(cell?.textContent?.trim()).toBe(payload);
    expect(cell?.querySelector('img')).toBeNull();
    expect((window as unknown as { hacked?: boolean }).hacked).toBeUndefined();
  });

  it('renders a column name as text too', async () => {
    const rendered = await render(
      of(result({ columns: ['<b>Id</b>'], rows: [['1']], rowCount: 1 }))
    );
    submit(rendered);

    const header = testId(rendered.host, 'diagnostics-results')?.querySelector('thead th');
    expect(header?.textContent?.trim()).toBe('<b>Id</b>');
    expect(header?.querySelector('b')).toBeNull();
  });

  it('renders a server message as text, not as markup', async () => {
    const rendered = await render(
      throwError(() =>
        httpError(
          400,
          result({
            outcome: 'Failed',
            message: '<img src=x onerror="window.hacked = true">',
            columns: [],
            rows: [],
            rowCount: 0
          })
        )
      )
    );
    submit(rendered);

    const failed = testId(rendered.host, 'diagnostics-failed');
    expect(failed?.querySelector('img')).toBeNull();
    expect(failed?.textContent).toContain('<img src=x');
    expect((window as unknown as { hacked?: boolean }).hacked).toBeUndefined();
  });
});

/**
 * The statement and its results describe data across every business, so they live in component
 * state for as long as the page is open and nowhere else.
 */
describe('PlatformDiagnosticsQueryComponent persistence and disclosure (issue #335)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    jest.restoreAllMocks();
  });

  it('writes neither the statement nor the results to browser storage', async () => {
    const local = jest.spyOn(Storage.prototype, 'setItem');
    const session = jest.spyOn(window.sessionStorage, 'setItem');

    const rendered = await render();
    submit(rendered, 'SELECT Id, BusinessId FROM Products');

    expect(local).not.toHaveBeenCalled();
    expect(session).not.toHaveBeenCalled();
    expect(window.localStorage).toHaveLength(0);
    expect(window.sessionStorage).toHaveLength(0);
  });

  it('logs neither the statement nor the results', async () => {
    const log = jest.spyOn(console, 'log').mockImplementation(() => undefined);
    const error = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const warn = jest.spyOn(console, 'warn').mockImplementation(() => undefined);

    const rendered = await render(throwError(() => httpError(403, null)));
    submit(rendered, 'SELECT Id FROM Businesses');

    expect(log).not.toHaveBeenCalled();
    expect(error).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
  });

  it('shows no credential, token or connection detail anywhere on the surface', async () => {
    const rendered = await render();
    submit(rendered);

    expect(rendered.host.textContent?.toLowerCase()).not.toMatch(/token|secret|password|connection string/);
    expect(rendered.host.querySelector('input[type="password"]')).toBeNull();
  });
});

/**
 * The page says what may be read and offers read-only examples, so the surface is discoverable
 * without suggesting unrestricted production SQL access.
 */
describe('PlatformDiagnosticsQueryComponent permitted surface (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lists every permitted table with its readable columns', async () => {
    const { host } = await render();
    const text = host.textContent ?? '';

    for (const table of ['Businesses', 'Categories', 'Suppliers', 'Products', 'Receipts', 'ReceiptItems', 'StockAdjustments']) {
      expect(text).toContain(table);
    }
    expect(text).toContain('Id, BusinessId, CategoryId, SupplierId');
    expect(text).toContain('Id, BusinessId, ReceiptId, ProductId');
  });

  it('says the surface is narrow and server-enforced, not unrestricted database access', async () => {
    const { host } = await render();
    const text = host.textContent ?? '';

    expect(text).toMatch(/not unrestricted database access/);
    expect(text).toMatch(/no name, note, amount, quantity, timestamp, imported payload or\s+credential/);
    expect(text).toMatch(/the server .* decides/);
  });

  it('offers only read-only examples, and loads one into the statement without running it', async () => {
    const rendered = await render();
    for (const example of rendered.component.examples) {
      expect(example.sql).toMatch(/^SELECT /);
      expect(example.sql.toUpperCase()).not.toMatch(/\b(INSERT|UPDATE|DELETE|DROP|ALTER|PRAGMA|ATTACH)\b/);
    }

    const use = Array.from(rendered.host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
      (button.textContent ?? '').includes('Use this example')
    );
    use?.click();
    rendered.fixture.detectChanges();

    expect(rendered.component.sql).toBe(rendered.component.examples[0].sql);
    expect(rendered.queryFn).not.toHaveBeenCalled();
  });
});
