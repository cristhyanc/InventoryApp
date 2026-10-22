import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { BookkeepingReportComponent } from './bookkeeping-report.component';
import { GstReportComponent } from './gst-report.component';
import { availabilityBadge, moneyOrState } from './report-formatting';
import {
  BookkeepingReport, GstReport, ReportAvailability, ReportProviderAvailability, ReportingService,
} from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';

/**
 * Issue #730 (parent #329 decision P9): both reports show the four availability states beside the
 * figures they govern, and never present an unaccounted-for period as a real zero. The figures the
 * business records itself stay visible and exact whatever the provider data looks like.
 */

const complete: ReportAvailability = { state: 'Complete', asOfUtc: null, isComplete: true };
const missing: ReportAvailability = { state: 'Missing', asOfUtc: null, isComplete: false };
const unverified: ReportAvailability = { state: 'Unverified', asOfUtc: null, isComplete: false };
const staleTo18July: ReportAvailability = { state: 'Stale', asOfUtc: '2026-07-18T06:00:00Z', isComplete: false };

/** The severity order the server combines states by, mirrored here only to build fixtures. */
const severity: Record<ReportAvailability['state'], number> = {
  Missing: 0, Unverified: 1, Stale: 2, Complete: 3,
};

function availability(sales: ReportAvailability, fees: ReportAvailability): ReportProviderAvailability {
  return { sales, fees, combined: severity[fees.state] < severity[sales.state] ? fees : sales };
}

describe('report availability formatting', () => {
  it('names each incomplete state and says nothing for a complete one', () => {
    expect(availabilityBadge(complete)).toBe('');
    expect(availabilityBadge(undefined)).toBe('');
    expect(availabilityBadge(missing)).toBe('No provider data');
    expect(availabilityBadge(unverified)).toBe('Unverified');
    expect(availabilityBadge(staleTo18July)).toContain('As of');
  });

  it('renders a stale "as of" instant in UTC rather than the browser time zone', () => {
    // 06:00Z is the 18th in UTC and could be the 18th or 19th locally; the instant is not a
    // business day and must not be shifted into whatever zone the browser happens to be in.
    expect(availabilityBadge(staleTo18July)).toBe('As of 18 July 2026, 06:00 UTC');
  });

  it('never prints a zero that coverage did not measure', () => {
    expect(moneyOrState(0, missing)).toBe('No provider data');
    expect(moneyOrState(0, unverified)).toBe('Unverified');
    expect(moneyOrState(undefined, staleTo18July)).toContain('As of');
  });

  it('keeps a real partial amount visible in an incomplete state', () => {
    expect(moneyOrState(42.5, staleTo18July)).toBe('$42.50');
    expect(moneyOrState(42.5, unverified)).toBe('$42.50');
  });

  it('prints a complete zero as the real zero it is', () => {
    expect(moneyOrState(0, complete)).toBe('$0.00');
  });
});

function bookkeeping(overrides: Partial<BookkeepingReport> = {}): BookkeepingReport {
  return {
    from: '2026-07-01', to: '2026-07-31', financialYear: 'FY2026-27',
    sales: 0, cardSales: 0, cashSales: 0, fees: 0, netSettlement: 0, gstOnSales: 0, gstOnFees: 0,
    nayaxFeesIncludingGst: 0, deliveryCosts: 12.5, packageCosts: 3.25, structuredOperatingExpenses: 40,
    operatingExpensesByCategory: { Utilities: 40 },
    dataQuality: {
      missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
      commissionNotPersisted: false, containsUnmappedProducts: false
    },
    ...overrides
  };
}

async function renderBookkeeping(value: BookkeepingReport) {
  await TestBed.configureTestingModule({
    imports: [BookkeepingReportComponent],
    providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } },
      { provide: ReportingService, useValue: { bookkeeping: jest.fn(() => of(value)) } },
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(BookkeepingReportComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('BookkeepingReportComponent provider availability (issue #730)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows no availability notice when both families are complete', async () => {
    const host = await renderBookkeeping(bookkeeping({ providerAvailability: availability(complete, complete) }));

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="gross-sales"]')!.textContent).toContain('$0.00');
  });

  it('shows missing sales as absent rather than as zero, and keeps local costs exact', async () => {
    const host = await renderBookkeeping(bookkeeping({ providerAvailability: availability(missing, missing) }));

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="gross-sales"]')!.textContent).toContain('No provider data');
    expect(host.querySelector('[data-testid="nayax-fees"]')!.textContent).toContain('No provider data');
    // The business's own records are unaffected and keep their exact figures.
    const text = host.textContent ?? '';
    expect(text).toContain('$12.50');
    expect(text).toContain('$3.25');
    expect(text).toContain('$40.00');
  });

  it('labels historical sales kept after a disconnect as stale, as of the covered instant', async () => {
    const host = await renderBookkeeping(bookkeeping({
      sales: 880, cardSales: 700, cashSales: 180,
      providerAvailability: availability(staleTo18July, missing)
    }));

    expect(host.querySelector('[data-testid="gross-sales"]')!.textContent).toContain('$880.00');
    expect(host.querySelector('[data-testid="sales-availability"]')!.textContent).toContain('As of 18 July 2026');
  });

  it('labels sales stored before coverage recording began as unverified, not complete and not zero', async () => {
    const host = await renderBookkeeping(bookkeeping({
      sales: 125, providerAvailability: availability(unverified, complete)
    }));

    expect(host.querySelector('[data-testid="gross-sales"]')!.textContent).toContain('$125.00');
    expect(host.querySelector('[data-testid="sales-availability"]')!.textContent).toContain('Unverified');
  });

  it('decides the two families separately', async () => {
    const host = await renderBookkeeping(bookkeeping({
      sales: 500, nayaxFeesIncludingGst: 0,
      providerAvailability: availability(complete, missing)
    }));

    expect(host.querySelector('[data-testid="sales-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="nayax-fees"]')!.textContent).toContain('No provider data');
  });

  /**
   * The headline cards were never the whole report. Every section that shows a provider-dependent
   * figure has to follow the same rule, or a reader scrolls past the banner into a Card Settlement
   * or Cash Reconciliation section that still says $0.00.
   */
  const everyProviderFigure = [
    'gross-sales', 'gross-profit', 'net-profit', 'net-margin', 'nayax-fees', 'site-commission',
    'settlement-card-sales', 'settlement-fees', 'net-reimbursement', 'reconciliation-cash-sales',
    // Issue #804: the completed-transaction count and the average sale over it are provider
    // figures too, so an uncovered period must not show either as a measured zero.
    'transactions', 'average-sale',
  ];

  it('shows no $0.00 for any provider figure anywhere in the report when both families are missing', async () => {
    const host = await renderBookkeeping(bookkeeping({ providerAvailability: availability(missing, missing) }));

    for (const id of everyProviderFigure) {
      const cell = host.querySelector(`[data-testid="${id}"]`);
      expect(cell).not.toBeNull();
      expect(cell!.textContent).toContain('No provider data');
      expect(cell!.textContent).not.toContain('$0.00');
      expect(cell!.textContent).not.toContain('0.0%');
    }

    // The business's own figures stay exact and unlabelled, including their total.
    expect(host.querySelector('[data-testid="delivery-costs"]')!.textContent).toContain('$12.50');
    expect(host.querySelector('[data-testid="package-costs"]')!.textContent).toContain('$3.25');
    expect(host.querySelector('[data-testid="total-operating-costs"]')!.textContent).toContain('$55.75');
  });

  it('shows no unmeasured zero anywhere when the period is only unverified', async () => {
    const host = await renderBookkeeping(bookkeeping({ providerAvailability: availability(unverified, unverified) }));

    for (const id of everyProviderFigure) {
      const cell = host.querySelector(`[data-testid="${id}"]`);
      expect(cell!.textContent).toContain('Unverified');
      expect(cell!.textContent).not.toContain('$0.00');
    }
  });

  it('keeps every real stale figure visible and labels the unmeasured ones, section by section', async () => {
    const host = await renderBookkeeping(bookkeeping({
      sales: 880, cardSales: 700, cashSales: 180, grossProfit: 540,
      netProfit: 300, netMarginPercent: 34.1, siteCommission: 20,
      nayaxFeesIncludingGst: 50, netSettlement: 650,
      providerAvailability: availability(staleTo18July, staleTo18July)
    }));

    const text = (id: string) => host.querySelector(`[data-testid="${id}"]`)!.textContent ?? '';
    expect(text('settlement-card-sales')).toContain('$700.00');
    expect(text('settlement-fees')).toContain('$50.00');
    expect(text('net-reimbursement')).toContain('$650.00');
    expect(text('reconciliation-cash-sales')).toContain('$180.00');
    expect(text('site-commission')).toContain('$20.00');
    expect(text('net-profit')).toContain('$300.00');
    expect(text('net-margin')).toContain('34.1%');
    expect(host.querySelector('[data-testid="sales-availability"]')!.textContent).toContain('As of 18 July 2026');
  });

  it('takes the least complete of both families for a figure that depends on both', async () => {
    // Card sales were read end to end; the settlement file never arrived. The net reimbursement
    // subtracts one from the other, so it cannot be reported as complete.
    const host = await renderBookkeeping(bookkeeping({
      sales: 500, cardSales: 400, netSettlement: 0, nayaxFeesIncludingGst: 0,
      providerAvailability: availability(complete, missing)
    }));

    expect(host.querySelector('[data-testid="settlement-card-sales"]')!.textContent).toContain('$400.00');
    expect(host.querySelector('[data-testid="net-reimbursement"]')!.textContent).toContain('No provider data');
  });

  it('never reads a figure as complete when an older response reported no combined state', async () => {
    // A response from before the combined state existed must fall back to a family's own state,
    // never to "complete": a state nothing decided may not license a zero.
    const host = await renderBookkeeping(bookkeeping({
      providerAvailability: { sales: missing, fees: missing } as ReportProviderAvailability
    }));

    expect(host.querySelector('[data-testid="net-reimbursement"]')!.textContent).toContain('No provider data');
    expect(host.querySelector('[data-testid="net-profit"]')!.textContent).toContain('No provider data');
  });
});

function gst(overrides: Partial<GstReport> = {}): GstReport {
  return {
    from: '2026-07-01', to: '2026-07-31',
    taxableSales: 0, gstOnSales: 0, taxableFees: 0, gstOnFees: 0, netGst: -9.5,
    operatingExpenseGst: 4, inventoryPurchaseGst: 5.5, purchaseLineGst: 5, purchaseChargeGst: 0.5,
    purchaseUnresolvedComponentCount: 0, purchaseUnresolvedAmount: 0, purchaseGstIncomplete: false,
    dataQuality: {
      missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
      commissionNotPersisted: false, containsUnmappedProducts: false
    },
    ...overrides
  };
}

async function renderGst(value: GstReport) {
  await TestBed.configureTestingModule({
    imports: [GstReportComponent],
    providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } },
      { provide: ReportingService, useValue: { gst: jest.fn(() => of(value)) } },
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(GstReportComponent);
  fixture.detectChanges();
  return { host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

describe('GstReportComponent provider availability (issue #730)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps purchase and expense GST visible while the provider figures are missing', async () => {
    const { host } = await renderGst(gst({
      providerAvailability: availability(missing, missing), netGstAvailability: missing
    }));

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="availability-gst-on-sales"]')!.textContent).toContain('No provider data');
    expect(host.querySelector('[data-testid="availability-expense-gst"]')).toBeNull();
    expect(host.querySelector('[data-testid="availability-purchase-gst"]')).toBeNull();
    const text = host.textContent ?? '';
    expect(text).toContain('$4.00');
    expect(text).toContain('$5.50');
  });

  it('carries the state on each provider card and on net GST', async () => {
    const { component } = await renderGst(gst({
      providerAvailability: availability(staleTo18July, unverified), netGstAvailability: unverified
    }));

    const cards = component.cards(component.report!);
    expect(cards.find(card => card.key === 'taxable-sales')!.availability).toEqual(staleTo18July);
    expect(cards.find(card => card.key === 'gst-on-fees')!.availability).toEqual(unverified);
    expect(cards.find(card => card.key === 'net-gst')!.availability).toEqual(unverified);
    expect(cards.find(card => card.key === 'purchase-gst')!.availability).toBeUndefined();
  });

  it('shows no availability notice when both provider families are complete', async () => {
    const { host } = await renderGst(gst({
      taxableSales: 900, gstOnSales: 90, taxableFees: 20, gstOnFees: 2,
      providerAvailability: availability(complete, complete), netGstAvailability: complete
    }));

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.textContent).toContain('$90.00');
  });
});
