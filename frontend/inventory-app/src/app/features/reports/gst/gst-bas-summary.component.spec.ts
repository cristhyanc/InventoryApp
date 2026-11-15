import { readFileSync } from 'fs';
import { join } from 'path';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { GstBasSummaryComponent } from './gst-bas-summary.component';
import { ReportingService } from '../../../services/reporting.service';
import { GstBasSummary, GstBasSummaryFigures } from './models/gst-bas-summary.model';
import { ReportAvailability } from '../shared/models/reporting.models';

const complete: ReportAvailability = { state: 'Complete', asOfUtc: null };
const missing: ReportAvailability = { state: 'Missing', asOfUtc: null };

function figures(overrides: Partial<GstBasSummaryFigures> = {}): GstBasSummaryFigures {
  return {
    totalSales: 330,
    gstOnSales: 30,
    gstCredits: 15,
    purchaseGstCredits: 8,
    operatingExpenseGstCredits: 4,
    nayaxFeeGstCredits: 3,
    netGst: 15,
    netGstOutcome: 'Payable',
    isIncomplete: false,
    gstFreeSales: 0,
    noGstRuleSales: 0,
    purchaseUnresolvedComponentCount: 0,
    purchaseUnresolvedAmount: 0,
    estimatedFeeGst: 0,
    feeGstIncludesEstimates: false,
    providerAvailability: { sales: complete, fees: complete, combined: complete },
    netGstAvailability: complete,
    ...overrides
  };
}

function summary(overrides: Partial<GstBasSummary> = {}): GstBasSummary {
  return {
    from: '2025-08-01T00:00:00',
    to: '2025-08-31T00:00:00',
    status: 'Available',
    figures: figures(),
    dataQuality: {
      missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
      commissionNotPersisted: false, containsUnmappedProducts: false, notes: []
    },
    ...overrides
  };
}

async function render(value: GstBasSummary | 'error') {
  const reports = {
    gstBasSummary: jest.fn(() => value === 'error' ? throwError(() => new Error('boom')) : of(value))
  } as unknown as ReportingService;

  await TestBed.configureTestingModule({
    imports: [GstBasSummaryComponent],
    providers: [{ provide: ReportingService, useValue: reports }]
  }).compileComponents();

  const fixture = TestBed.createComponent(GstBasSummaryComponent);
  fixture.componentRef.setInput('filter', { from: '2025-08-01', to: '2025-08-31', machineId: null });
  fixture.detectChanges();
  return { host: fixture.nativeElement as HTMLElement, reports };
}

const text = (host: HTMLElement, testId: string) =>
  host.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? '';

/**
 * Issue #1318: the GST and BAS summary inside Bookkeeping. Every figure here is the server's, so
 * these tests are about what is shown, what is hidden and what is said about an incomplete
 * estimate - never about arithmetic, which the backend policy tests own.
 */
describe('GstBasSummaryComponent (issue #1318)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows G1, 1A, 1B with its three sources, and the estimated net GST', async () => {
    const { host } = await render(summary());

    expect(host.querySelector('[data-testid="gst-bas-summary"]')).not.toBeNull();
    expect(text(host, 'bas-g1')).toBe('$330.00');
    expect(text(host, 'bas-1a')).toBe('$30.00');
    expect(text(host, 'bas-1b')).toBe('$15.00');
    expect(text(host, 'bas-1b-purchases')).toBe('$8.00');
    expect(text(host, 'bas-1b-expenses')).toBe('$4.00');
    expect(text(host, 'bas-1b-fees')).toBe('$3.00');
    expect(text(host, 'bas-net-gst')).toBe('$15.00');
    expect(host.textContent).toContain('Estimated GST payable');
  });

  it('shows nothing at all for a business that is not registered for GST', async () => {
    const { host } = await render(summary({ status: 'NotGstRegistered', figures: null }));

    expect(host.querySelector('[data-testid="gst-bas-summary"]')).toBeNull();
    expect(host.querySelector('[data-testid="gst-bas-summary-machine-filtered"]')).toBeNull();
    expect(host.textContent?.trim()).toBe('');
  });

  it('asks the reader to select all machines instead of showing a machine-scoped estimate', async () => {
    const { host } = await render(summary({ status: 'MachineFiltered', figures: null }));

    expect(host.querySelector('[data-testid="gst-bas-summary"]')).toBeNull();
    expect(text(host, 'gst-bas-summary-machine-filtered')).toContain('All machines');
  });

  it('labels a negative net estimate as a credit and a zero one as nil', async () => {
    const { host: credit } = await render(summary({ figures: figures({ netGst: -12, netGstOutcome: 'Credit' }) }));
    expect(credit.textContent).toContain('Estimated GST credit');
    expect(text(credit, 'bas-net-gst')).toBe('-$12.00');

    TestBed.resetTestingModule();
    const { host: nil } = await render(summary({
      figures: figures({ totalSales: 0, gstOnSales: 0, gstCredits: 0, purchaseGstCredits: 0,
        operatingExpenseGstCredits: 0, nayaxFeeGstCredits: 0, netGst: 0, netGstOutcome: 'Nil' })
    }));
    expect(nil.textContent).toContain('Estimated net GST (nil)');
    // A covered period that genuinely owed nothing is allowed to show its measured zero.
    expect(text(nil, 'bas-net-gst')).toBe('$0.00');
  });

  it('marks an incomplete estimate and lists the notes the server supplied', async () => {
    const { host } = await render(summary({
      figures: figures({ isIncomplete: true, noGstRuleSales: 220 }),
      dataQuality: {
        missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
        commissionNotPersisted: false, containsUnmappedProducts: false,
        notes: ['GST on sales includes 220.00 of sales of products with no GST rule, which are assumed taxable.']
      }
    }));

    expect(text(host, 'gst-bas-summary-state')).toBe('Estimate incomplete');
    expect(text(host, 'gst-bas-summary-incomplete')).toContain('not final');
    expect(text(host, 'gst-bas-summary-notes')).toContain('no GST rule');
    expect(text(host, 'bas-assumed-taxable')).toContain('$220.00');
  });

  it('reports unclassified purchase components rather than crediting them', async () => {
    const { host } = await render(summary({
      figures: figures({ isIncomplete: true, purchaseUnresolvedComponentCount: 2, purchaseUnresolvedAmount: 440 })
    }));

    expect(text(host, 'bas-unresolved-purchases')).toContain('2 purchase component');
    expect(text(host, 'bas-unresolved-purchases')).toContain('$440.00');
  });

  it('shows the availability state instead of a figure nothing measured, and never a dollar zero', async () => {
    const { host } = await render(summary({
      figures: figures({
        totalSales: 0, gstOnSales: 0, gstCredits: 0, nayaxFeeGstCredits: 0, netGst: 0,
        netGstOutcome: 'Nil', isIncomplete: true,
        providerAvailability: { sales: missing, fees: missing, combined: missing },
        netGstAvailability: missing
      })
    }));

    expect(text(host, 'bas-g1')).toBe('No provider data');
    expect(text(host, 'bas-1a')).toBe('No provider data');
    expect(text(host, 'bas-1b')).toBe('No provider data');
    expect(text(host, 'bas-net-gst')).toBe('No provider data');
    expect(text(host, 'bas-net-gst')).not.toContain('$0.00');
    expect(text(host, 'bas-net-gst-availability')).toBe('No provider data');
    // The figures the business records itself stay exact however incomplete the provider data is.
    expect(text(host, 'bas-1b-purchases')).toBe('$8.00');
    expect(text(host, 'bas-1b-expenses')).toBe('$4.00');
  });

  it('explains the period, the date basis, the scope and that the figures need reconciling', async () => {
    const { host } = await render(summary());

    const scope = text(host, 'bas-scope');
    expect(scope).toContain('1 Aug 2025');
    expect(scope).toContain('31 Aug 2025');
    expect(scope).toContain('business date');
    expect(scope).toContain('whole business');
    expect(host.textContent).toContain('estimate for reviewing the GST section of a BAS');
    expect(host.textContent).toContain('reconcile');
  });

  it('loads nothing until the page has a period to report on', async () => {
    const reports = { gstBasSummary: jest.fn(() => of(summary())) } as unknown as ReportingService;
    await TestBed.configureTestingModule({
      imports: [GstBasSummaryComponent],
      providers: [{ provide: ReportingService, useValue: reports }]
    }).compileComponents();

    const fixture = TestBed.createComponent(GstBasSummaryComponent);
    fixture.detectChanges();

    expect(reports.gstBasSummary).not.toHaveBeenCalled();
  });

  it('builds the summary from shared tokens the committed stylesheet already compiles', async () => {
    // The same rule the Bookkeeping page's own sections follow: a class the committed Tailwind
    // output does not hold would render unstyled, and regenerating that output is not this
    // feature's to do.
    const { host } = await render(summary({
      figures: figures({ isIncomplete: true, noGstRuleSales: 220, purchaseUnresolvedComponentCount: 1 })
    }));
    const css = readFileSync(join(__dirname, '..', '..', '..', '..', 'styles.css'), 'utf8');

    const section = host.querySelector('[data-testid="gst-bas-summary"]')!;
    const tokens = new Set<string>();
    for (const element of [section, ...Array.from(section.querySelectorAll('*'))]) {
      element.classList.forEach(token => tokens.add(token));
    }

    expect(tokens.size).toBeGreaterThan(0);
    for (const token of tokens) expect(css).toContain(`.${token}`);
  });

  it('reports a failed request as an error rather than as an empty estimate', async () => {
    const { host } = await render('error');

    expect(text(host, 'gst-bas-summary-error')).toContain('Unable to load the GST summary.');
    expect(host.querySelector('[data-testid="gst-bas-summary"]')).toBeNull();
  });
});
