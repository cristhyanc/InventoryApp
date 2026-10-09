import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { NayaxSaleTimestampRepairWorkflowComponent } from './nayax-sale-timestamp-repair-workflow.component';
import {
  NayaxSaleTimestampEvidenceSource,
  NayaxSaleTimestampRepairApplied,
  NayaxSaleTimestampRepairOutcome,
  NayaxSaleTimestampRepairPreview,
  NayaxSaleTimestampRepairPreviewRequest,
  NayaxSaleTimestampRepairRow,
  NayaxSaleTimestampRepairService,
  NayaxSaleTimestampUnresolvedReason
} from '../../../services/nayax-sale-timestamp-repair.service';
import { ToastService } from '../../../services/toast.service';

const PREVIEW_ID = '6f1b2c4e-0000-4000-8000-000000000001';
const HOUR = 60 * 60 * 1000;

function repairableRow(overrides: Partial<NayaxSaleTimestampRepairRow> = {}): NayaxSaleTimestampRepairRow {
  return {
    transactionId: 2108816976,
    machineId: 942488501,
    machineName: 'Sample Machine',
    settlementValue: 4.5,
    transactionStatusId: 12,
    completedSale: true,
    storedInstantUtc: '2026-10-06T23:14:00Z',
    storedBusinessDate: '2026-10-07T00:00:00',
    repairedInstantUtc: '2026-10-06T12:14:00Z',
    repairedBusinessDate: '2026-10-06T00:00:00',
    outcome: NayaxSaleTimestampRepairOutcome.Repairable,
    unresolvedReason: null,
    evidenceSource: NayaxSaleTimestampEvidenceSource.OperatorExport,
    evidenceReference: 'operator export week.csv',
    ...overrides
  };
}

function unresolvedRow(): NayaxSaleTimestampRepairRow {
  return repairableRow({
    transactionId: 2108816977,
    repairedInstantUtc: null,
    repairedBusinessDate: null,
    outcome: NayaxSaleTimestampRepairOutcome.Unresolved,
    unresolvedReason: NayaxSaleTimestampUnresolvedReason.NoSourceEvidence,
    evidenceSource: null,
    evidenceReference: null
  });
}

function preview(overrides: Partial<NayaxSaleTimestampRepairPreview> = {}): NayaxSaleTimestampRepairPreview {
  const rows = overrides.rows ?? [repairableRow(), unresolvedRow()];
  return {
    previewId: PREVIEW_ID,
    evidenceRecords: 12,
    examinedFromUtc: '2026-10-05T13:00:00Z',
    examinedToUtc: '2026-10-08T04:00:00Z',
    salesExamined: rows.length,
    repairable: 1,
    alreadyCorrect: 0,
    unresolved: 1,
    revenueMovement: [],
    affectedProducts: [],
    missingFromDatabase: [],
    reconciliation: null,
    createdAt: new Date(Date.now() - 60_000).toISOString(),
    expiresAt: new Date(Date.now() + 2 * HOUR).toISOString(),
    ...overrides,
    rows
  };
}

function applied(overrides: Partial<NayaxSaleTimestampRepairApplied> = {}): NayaxSaleTimestampRepairApplied {
  return {
    previewId: PREVIEW_ID,
    salesRepaired: 1,
    productsRebuilt: 1,
    recostedSales: 3,
    repairs: [],
    ...overrides
  };
}

function exportFile(name = 'transactions.xlsx', size = 1024): File {
  const file = new File(['TransactionID,AuthorizationDateTimeGMT'], name, { type: 'text/csv' });
  Object.defineProperty(file, 'size', { value: size });
  return file;
}

interface Rendered {
  fixture: ComponentFixture<NayaxSaleTimestampRepairWorkflowComponent>;
  component: NayaxSaleTimestampRepairWorkflowComponent;
  host: HTMLElement;
  previewFn: jest.Mock;
  applyFn: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
}

const fixtures: ComponentFixture<NayaxSaleTimestampRepairWorkflowComponent>[] = [];

async function render(previewFn: jest.Mock = jest.fn(() => of(preview())), applyFn: jest.Mock = jest.fn()): Promise<Rendered> {
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
  await TestBed.configureTestingModule({
    imports: [NayaxSaleTimestampRepairWorkflowComponent],
    providers: [
      { provide: NayaxSaleTimestampRepairService, useValue: { preview: previewFn, apply: applyFn } },
      { provide: ToastService, useValue: toast }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(NayaxSaleTimestampRepairWorkflowComponent);
  fixtures.push(fixture);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, previewFn, applyFn, toast };
}

/** Takes a preview with the API source named and the operator's acknowledgement given. */
async function readyToApply(previewFn: jest.Mock, applyFn: jest.Mock): Promise<Rendered> {
  const rendered = await render(previewFn, applyFn);
  rendered.component.includeLatestSalesApiEvidence = true;
  rendered.component.runPreview();
  rendered.component.acknowledged = true;
  rendered.fixture.detectChanges();
  return rendered;
}

function sentRequest(previewFn: jest.Mock, call = 0): NayaxSaleTimestampRepairPreviewRequest {
  return previewFn.mock.calls[call][0] as NayaxSaleTimestampRepairPreviewRequest;
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes(text));
}

function noticeText(host: HTMLElement): string {
  return host.querySelector('[data-testid="repair-notice"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

afterEach(() => {
  while (fixtures.length) {
    fixtures.pop()?.destroy();
  }
  TestBed.resetTestingModule();
});

describe('NayaxSaleTimestampRepairWorkflowComponent sources (issue #487)', () => {
  it('previews nothing on arrival: the operator has to ask for it', async () => {
    const { previewFn, host } = await render();

    expect(previewFn).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="repair-preview"]')).toBeNull();
  });

  /**
   * Both sources are optional individually and required together: a preview with neither has no
   * authoritative evidence at all, and the server refuses it. Refusing here spends no request.
   */
  it('refuses a preview with no source named and sends nothing', async () => {
    const { component, fixture, host, previewFn } = await render();

    component.runPreview();
    fixture.detectChanges();

    expect(previewFn).not.toHaveBeenCalled();
    expect(noticeText(host)).toContain('at least one authoritative source');
  });

  it('sends an API-only preview request', async () => {
    const { component, previewFn } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();

    expect(previewFn).toHaveBeenCalledTimes(1);
    expect(sentRequest(previewFn)).toEqual({
      includeLatestSalesApiEvidence: true,
      evidenceExport: null,
      reconciliation: null
    });
  });

  it('sends an export-only preview request', async () => {
    const { component, previewFn } = await render();
    const file = exportFile();

    component.selectExport(file);
    component.runPreview();

    expect(sentRequest(previewFn)).toEqual({
      includeLatestSalesApiEvidence: false,
      evidenceExport: file,
      reconciliation: null
    });
  });

  it('sends a combined-source preview request with the reconciliation window', async () => {
    const { component, previewFn } = await render();
    const file = exportFile('week.csv');

    component.includeLatestSalesApiEvidence = true;
    component.selectExport(file);
    component.reconcileWindow = true;
    component.cutoffUtc = '2026-10-08T04:00:00Z';
    component.fromBusinessDate = '2026-10-05';
    component.toBusinessDate = '2026-10-08';
    component.runPreview();

    expect(sentRequest(previewFn)).toEqual({
      includeLatestSalesApiEvidence: true,
      evidenceExport: file,
      reconciliation: {
        cutoffUtc: '2026-10-08T04:00:00Z',
        fromBusinessDate: '2026-10-05',
        toBusinessDate: '2026-10-08'
      }
    });
  });

  it('refuses an export the reader does not support, before any request', async () => {
    const { component, fixture, host, previewFn } = await render();

    component.selectExport(exportFile('transactions.pdf'));
    fixture.detectChanges();

    expect(component.evidenceExport).toBeNull();
    expect(host.querySelector('[data-testid="repair-export-error"]')?.textContent).toContain('.xlsx');
    component.runPreview();
    expect(previewFn).not.toHaveBeenCalled();
  });

  it("refuses an export larger than the server's request cap, before any request", async () => {
    const { component, fixture, host, previewFn } = await render();

    component.selectExport(exportFile('huge.xlsx', 8_000_001));
    fixture.detectChanges();

    expect(component.evidenceExport).toBeNull();
    expect(host.querySelector('[data-testid="repair-export-error"]')?.textContent).toContain('8,000,000');
    component.runPreview();
    expect(previewFn).not.toHaveBeenCalled();
  });

  /**
   * The operator has to see why the API source cannot stand alone for an old transaction, and that
   * an update time is never substituted for an authorization time.
   */
  it('explains the rolling API coverage and the required export column', async () => {
    const { host } = await render();
    const text = host.textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('AuthorizationDateTimeGMT');
    expect(text).toContain('Updated Date and Time (GMT)');
    expect(text).toContain('rolling');
  });
});

describe('NayaxSaleTimestampRepairWorkflowComponent reconciliation window (issue #487)', () => {
  it('refuses a partial window and sends nothing', async () => {
    const { component, fixture, host, previewFn } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.reconcileWindow = true;
    component.cutoffUtc = '2026-10-08T04:00:00Z';
    component.runPreview();
    fixture.detectChanges();

    expect(previewFn).not.toHaveBeenCalled();
    expect(noticeText(host)).toContain('all three');
  });

  /**
   * A cutoff without an explicit `Z` is a browser-local instant pretending to be a UTC one, which
   * would silently reconcile a window shifted by the viewer's own offset.
   */
  it('refuses a cutoff that is not an explicit UTC instant', async () => {
    const { component, fixture, host, previewFn } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.reconcileWindow = true;
    component.cutoffUtc = '2026-10-08T04:00:00';
    component.fromBusinessDate = '2026-10-05';
    component.toBusinessDate = '2026-10-08';
    component.runPreview();
    fixture.detectChanges();

    expect(previewFn).not.toHaveBeenCalled();
    expect(noticeText(host)).toContain('Z');
  });

  it('refuses a window whose last business date is before its first', async () => {
    const { component, fixture, host, previewFn } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.reconcileWindow = true;
    component.cutoffUtc = '2026-10-08T04:00:00Z';
    component.fromBusinessDate = '2026-10-08';
    component.toBusinessDate = '2026-10-05';
    component.runPreview();
    fixture.detectChanges();

    expect(previewFn).not.toHaveBeenCalled();
    expect(noticeText(host)).toContain('cannot be before');
  });

  it('sends no reconciliation at all once the window is switched off', async () => {
    const { component, previewFn } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.reconcileWindow = true;
    component.cutoffUtc = '2026-10-08T04:00:00Z';
    component.fromBusinessDate = '2026-10-05';
    component.toBusinessDate = '2026-10-08';
    component.reconcileWindow = false;
    component.runPreview();

    expect(sentRequest(previewFn).reconciliation).toBeNull();
  });
});

describe('NayaxSaleTimestampRepairWorkflowComponent preview outcomes (issue #487)', () => {
  it('renders the server plan and its counts', async () => {
    const { component, fixture, host } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="repair-preview"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="repair-count-repairable"]')?.textContent).toContain('1');
    expect(host.querySelector('[data-testid="repair-count-unresolved"]')?.textContent).toContain('1');
    expect(component.previewLoading).toBe(false);
  });

  it('shows a loading state while the preview is in flight and disables a second submission', async () => {
    const responses = new Subject<NayaxSaleTimestampRepairPreview>();
    const previewFn = jest.fn(() => responses.asObservable());
    const { component, fixture, host } = await render(previewFn);

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="repair-previewing"]')).not.toBeNull();
    component.runPreview();
    expect(previewFn).toHaveBeenCalledTimes(1);
    expect(button(host, 'Preview repair')?.disabled).toBe(true);
  });

  it('reports a validation refusal with the server message', async () => {
    const message =
      'The uploaded export has no AuthorizationDateTimeGMT column, so it carries no authorization times.';
    const { component, fixture, host } = await render(
      jest.fn(() => throwError(() => ({ status: 400, error: { message } })))
    );

    component.selectExport(exportFile());
    component.runPreview();
    fixture.detectChanges();

    expect(noticeText(host)).toContain(message);
    expect(component.preview).toBeNull();
  });

  it('reports an upstream Nayax failure distinctly from a validation refusal', async () => {
    const { component, fixture, host } = await render(
      jest.fn(() =>
        throwError(() => ({
          status: 502,
          error: { title: 'Nayax service error', detail: 'The Nayax service could not complete the request.' }
        }))
      )
    );

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    fixture.detectChanges();

    expect(noticeText(host)).toContain('Nayax');
    expect(noticeText(host)).toContain('export');
    expect(component.preview).toBeNull();
  });

  it('reports an upload the server refused as too large', async () => {
    const { component, fixture, host } = await render(jest.fn(() => throwError(() => ({ status: 413 }))));

    component.selectExport(exportFile());
    component.runPreview();
    fixture.detectChanges();

    expect(noticeText(host)).toContain('8,000,000');
  });

  /**
   * A preview changes no sale timestamp whatever happens to the request, so saying so is truthful
   * here - unlike on the apply, where an unanswered request is genuinely unconfirmed.
   */
  it('reports an unreachable API on preview and states that nothing was repaired', async () => {
    const { component, fixture, host } = await render(jest.fn(() => throwError(() => ({ status: 0 }))));

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    fixture.detectChanges();

    expect(noticeText(host)).toContain('did not reach the server');
    expect(noticeText(host)).toContain('no sale timestamp');
  });

  it('discards a preview response that arrives after the component is destroyed', async () => {
    const responses = new Subject<NayaxSaleTimestampRepairPreview>();
    const { component, fixture } = await render(jest.fn(() => responses.asObservable()));

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    fixture.destroy();
    responses.next(preview());

    expect(component.preview).toBeNull();
    expect(component.previewLoading).toBe(false);
  });
});

describe('NayaxSaleTimestampRepairWorkflowComponent invalidation (issue #487)', () => {
  /**
   * A displayed plan belongs to the inputs it was computed from. Changing any of them makes it a
   * plan for a question nobody asked, so it stops being actionable and the acknowledgement is
   * withdrawn with it.
   */
  it.each([
    ['the API source', (component: NayaxSaleTimestampRepairWorkflowComponent) => { component.includeLatestSalesApiEvidence = false; }],
    ['the export file', (component: NayaxSaleTimestampRepairWorkflowComponent) => { component.selectExport(exportFile()); }],
    ['the reconciliation toggle', (component: NayaxSaleTimestampRepairWorkflowComponent) => { component.reconcileWindow = true; component.onInputChanged(); }],
    ['the cutoff', (component: NayaxSaleTimestampRepairWorkflowComponent) => { component.cutoffUtc = '2026-10-09T04:00:00Z'; component.onInputChanged(); }],
    ['a business date', (component: NayaxSaleTimestampRepairWorkflowComponent) => { component.toBusinessDate = '2026-10-09'; component.onInputChanged(); }]
  ])('invalidates the displayed plan and the confirmation when %s changes', async (_label, change) => {
    const { component, fixture, host, applyFn } = await readyToApply(jest.fn(() => of(preview())), jest.fn(() => of(applied())));

    expect(component.canApply).toBe(true);
    change(component);
    if (_label === 'the API source') {
      component.onInputChanged();
    }
    fixture.detectChanges();

    expect(component.preview).toBeNull();
    expect(component.acknowledged).toBe(false);
    expect(component.canApply).toBe(false);
    expect(host.querySelector('[data-testid="repair-preview"]')).toBeNull();
    component.applyRepair();
    expect(applyFn).not.toHaveBeenCalled();
  });

  /**
   * A validation complaint describes the form, so editing the form answers it. An outcome the API
   * reported does not stop being true because an input changed, and its recovery guidance has to
   * survive until the next request.
   */
  it('clears a validation notice when the form is edited', async () => {
    const { component, fixture, host } = await render();

    component.runPreview();
    fixture.detectChanges();
    expect(noticeText(host)).toContain('at least one authoritative source');

    component.includeLatestSalesApiEvidence = true;
    component.onInputChanged();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="repair-notice"]')).toBeNull();
  });

  it('keeps an unconfirmed-apply notice when the form is edited', async () => {
    const { component, fixture, host } = await readyToApply(
      jest.fn(() => of(preview())),
      jest.fn(() => throwError(() => ({ status: 0 })))
    );

    component.applyRepair();
    fixture.detectChanges();
    expect(noticeText(host)).toContain('not known');

    component.toBusinessDate = '2026-10-09';
    component.onInputChanged();
    fixture.detectChanges();

    expect(noticeText(host)).toContain('not known');
  });

  /** Nothing about an actionable plan or an uploaded export may outlive the page. */
  it('writes no plan, preview id or export to browser storage', async () => {
    const localSpy = jest.spyOn(Storage.prototype, 'setItem');
    const sessionSpy = jest.spyOn(window.sessionStorage, 'setItem');
    const { component, fixture } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.selectExport(exportFile());
    component.runPreview();
    fixture.detectChanges();

    expect(localSpy).not.toHaveBeenCalled();
    expect(sessionSpy).not.toHaveBeenCalled();
    localSpy.mockRestore();
    sessionSpy.mockRestore();
  });
});

describe('NayaxSaleTimestampRepairWorkflowComponent confirmation (issue #487)', () => {
  it('keeps Apply unavailable until the review and backup acknowledgement is given', async () => {
    const { component, fixture, host, applyFn } = await render(jest.fn(() => of(preview())), jest.fn(() => of(applied())));

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    fixture.detectChanges();

    expect(component.acknowledged).toBe(false);
    expect(component.canApply).toBe(false);
    expect(button(host, 'Apply repair')?.disabled).toBe(true);
    component.applyRepair();
    expect(applyFn).not.toHaveBeenCalled();

    component.acknowledged = true;
    fixture.detectChanges();
    expect(component.canApply).toBe(true);
    expect(button(host, 'Apply repair')?.disabled).toBe(false);
  });

  /** The acknowledgement is a statement about a backup, not an action that makes one. */
  it('states that the acknowledgement neither creates nor verifies a backup, and names the runbook step', async () => {
    const { component, fixture, host } = await render();

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    fixture.detectChanges();

    const text = host.querySelector('[data-testid="repair-confirmation"]')?.textContent?.replace(/\s+/g, ' ') ?? '';
    expect(text).toContain('does not create or verify a backup');
    expect(text).toContain('backup-database');
  });

  it('offers no Apply action for a plan with nothing repairable', async () => {
    const { component, fixture, host } = await render(
      jest.fn(() => of(preview({ repairable: 0, rows: [unresolvedRow()], unresolved: 1 })))
    );

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    component.acknowledged = true;
    fixture.detectChanges();

    expect(component.canApply).toBe(false);
    expect(host.querySelector('[data-testid="repair-no-change"]')).not.toBeNull();
  });

  /**
   * The server binds a plan to a two-hour lifetime and refuses an expired one. The page must not
   * offer an action the server will refuse, and must say what to do instead.
   */
  it('withdraws Apply once the server plan has expired and asks for a fresh preview', async () => {
    const expired = preview({ expiresAt: new Date(Date.now() - 1000).toISOString() });
    const { component, fixture, host, applyFn } = await render(jest.fn(() => of(expired)), jest.fn(() => of(applied())));

    component.includeLatestSalesApiEvidence = true;
    component.runPreview();
    component.acknowledged = true;
    fixture.detectChanges();

    expect(component.previewExpired).toBe(true);
    expect(component.canApply).toBe(false);
    expect(host.querySelector('[data-testid="repair-expired"]')?.textContent).toContain('expired');
    component.applyRepair();
    expect(applyFn).not.toHaveBeenCalled();
  });

  it('disables a duplicate Apply while one is in flight', async () => {
    const responses = new Subject<NayaxSaleTimestampRepairApplied>();
    const applyFn = jest.fn(() => responses.asObservable());
    const { component, fixture, host } = await readyToApply(jest.fn(() => of(preview())), applyFn);

    component.applyRepair();
    fixture.detectChanges();
    component.applyRepair();

    expect(applyFn).toHaveBeenCalledTimes(1);
    expect(button(host, 'Applying')?.disabled).toBe(true);
  });
});

describe('NayaxSaleTimestampRepairWorkflowComponent apply (issue #487)', () => {
  it('confirms the server plan by id alone and reports the actual counts', async () => {
    const applyFn = jest.fn(() => of(applied()));
    const { component, fixture, host, toast } = await readyToApply(jest.fn(() => of(preview())), applyFn);

    component.applyRepair();
    fixture.detectChanges();

    expect(applyFn).toHaveBeenCalledWith(PREVIEW_ID);
    expect(applyFn.mock.calls[0]).toHaveLength(1);
    expect(host.querySelector('[data-testid="repair-applied-sales"]')?.textContent).toContain('1');
    expect(host.querySelector('[data-testid="repair-applied-recosted"]')?.textContent).toContain('3');
    expect(toast.success).toHaveBeenCalled();
  });

  /** The plan is consumed: it must not be offered again, and the server would refuse it anyway. */
  it('withdraws the applied plan so it cannot be applied twice', async () => {
    const applyFn = jest.fn(() => of(applied()));
    const { component, fixture, host } = await readyToApply(jest.fn(() => of(preview())), applyFn);

    component.applyRepair();
    fixture.detectChanges();

    expect(component.preview).toBeNull();
    expect(component.acknowledged).toBe(false);
    expect(component.canApply).toBe(false);
    expect(host.querySelector('[data-testid="repair-preview"]')).toBeNull();
    component.applyRepair();
    expect(applyFn).toHaveBeenCalledTimes(1);
  });

  /** A repair is not a reconciliation: the rows it could not resolve were not changed by it. */
  it('keeps the unresolved and missing counts of the confirmed plan visible after success', async () => {
    const { component, fixture, host } = await readyToApply(
      jest.fn(() =>
        of(
          preview({
            missingFromDatabase: [
              {
                transactionId: 2108816999,
                machineId: 942488501,
                authorizationInstantUtc: '2026-10-06T12:00:00Z',
                authorizationBusinessDate: '2026-10-06T00:00:00',
                settlementValue: 3.5,
                transactionStatusId: 12,
                completedSale: true,
                evidenceSource: NayaxSaleTimestampEvidenceSource.OperatorExport
              }
            ]
          })
        )
      ),
      jest.fn(() => of(applied()))
    );

    component.applyRepair();
    fixture.detectChanges();

    const gaps = host.querySelector('[data-testid="repair-applied-gaps"]')?.textContent?.replace(/\s+/g, ' ') ?? '';
    expect(gaps).toContain('did not resolve everything');
    expect(component.unresolvedAtPreview).toBe(1);
    expect(component.missingAtPreview).toBe(1);
  });

  /**
   * Every refusal the server answers with - stale, expired, already applied, or a costing replay
   * that could not complete - rolled the whole apply back, and the only safe next step is a fresh
   * preview. The server's own caller-safe message is what the operator reads.
   */
  it.each([
    [
      'a stale plan',
      'The stored Nayax sales changed after this preview, so nothing was repaired. Run the preview again and review it before applying.'
    ],
    [
      'a costing replay failure',
      'Nothing was repaired: the inventory cost of Coke Zero (product 7) could not be replayed from 2026-10-06T12:14:00Z.'
    ],
    ['an already-applied plan', 'This sale timestamp repair preview has already been applied.']
  ])('reports %s with the server message and requires a fresh preview', async (_label, message) => {
    const applyFn = jest.fn(() => throwError(() => ({ status: 400, error: { message } })));
    const { component, fixture, host, toast } = await readyToApply(jest.fn(() => of(preview())), applyFn);

    component.applyRepair();
    fixture.detectChanges();

    expect(noticeText(host)).toContain(message);
    expect(noticeText(host)).toContain('rolled back');
    expect(component.preview).toBeNull();
    expect(component.acknowledged).toBe(false);
    expect(component.applied).toBeNull();
    expect(toast.error).toHaveBeenCalled();
  });

  /**
   * An unanswered or ambiguous transport failure is the one case where "nothing was written" would
   * be a guess. It must not be claimed, the apply must not be retried automatically, and the
   * recovery is a fresh preview plus verification.
   */
  it.each([[0], [408], [502], [503], [504]])(
    'reports an ambiguous transport failure (status %i) as an unconfirmed outcome',
    async (status) => {
      const applyFn = jest.fn(() => throwError(() => ({ status })));
      const { component, fixture, host } = await readyToApply(jest.fn(() => of(preview())), applyFn);

      component.applyRepair();
      fixture.detectChanges();

      const notice = noticeText(host);
      expect(notice).toContain('not known');
      expect(notice).not.toContain('Nothing was repaired');
      expect(notice).toContain('fresh preview');
      expect(applyFn).toHaveBeenCalledTimes(1);
      expect(component.preview).toBeNull();
      expect(component.acknowledged).toBe(false);
    }
  );

  it('discards an apply response that arrives after the component is destroyed', async () => {
    const responses = new Subject<NayaxSaleTimestampRepairApplied>();
    const { component, fixture } = await readyToApply(jest.fn(() => of(preview())), jest.fn(() => responses.asObservable()));

    component.applyRepair();
    fixture.destroy();
    responses.next(applied());

    expect(component.applied).toBeNull();
    expect(component.applying).toBe(false);
  });
});

describe('NayaxSaleTimestampRepairWorkflowComponent verification (issue #487)', () => {
  /**
   * After a success the only honest next view is a fresh preview, and a person asks for it: nothing
   * re-previews or re-applies by itself, and the retained inputs are reused so the same window is
   * compared.
   */
  it('runs a fresh preview over the retained inputs when verification is requested', async () => {
    const previewFn = jest.fn(() => of(preview()));
    const { component, fixture, host } = await readyToApply(previewFn, jest.fn(() => of(applied())));

    component.reconcileWindow = true;
    component.cutoffUtc = '2026-10-08T04:00:00Z';
    component.fromBusinessDate = '2026-10-05';
    component.toBusinessDate = '2026-10-08';
    component.acknowledged = true;
    component.applyRepair();
    fixture.detectChanges();
    expect(previewFn).toHaveBeenCalledTimes(1);

    button(host, 'Preview again to verify')?.click();
    fixture.detectChanges();

    expect(previewFn).toHaveBeenCalledTimes(2);
    expect(sentRequest(previewFn, 1)).toEqual({
      includeLatestSalesApiEvidence: true,
      evidenceExport: null,
      reconciliation: {
        cutoffUtc: '2026-10-08T04:00:00Z',
        fromBusinessDate: '2026-10-05',
        toBusinessDate: '2026-10-08'
      }
    });
    expect(component.applied).toBeNull();
    expect(component.preview).not.toBeNull();
  });

  /** Success evidence stays on screen until the operator chooses another action. */
  it('keeps the success evidence when an input changes, and clears it only on a fresh preview', async () => {
    const previewFn = jest.fn(() => of(preview()));
    const { component, fixture, host } = await readyToApply(previewFn, jest.fn(() => of(applied())));

    component.applyRepair();
    fixture.detectChanges();
    component.toBusinessDate = '2026-10-09';
    component.onInputChanged();
    fixture.detectChanges();

    expect(component.applied).not.toBeNull();
    expect(host.querySelector('[data-testid="repair-result"]')).not.toBeNull();

    component.runPreview();
    fixture.detectChanges();
    expect(component.applied).toBeNull();
  });
});
