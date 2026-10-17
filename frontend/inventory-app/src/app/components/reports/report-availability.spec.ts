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

/**
 * Issue #730 (parent #329 decision P9): both reports show the four availability states beside the
 * figures they govern, and never present an unaccounted-for period as a real zero. The figures the
 * business records itself stay visible and exact whatever the provider data looks like.
 */

const complete: ReportAvailability = { state: 'Complete', asOfUtc: null, isComplete: true };
const missing: ReportAvailability = { state: 'Missing', asOfUtc: null, isComplete: false };
const unverified: ReportAvailability = { state: 'Unverified', asOfUtc: null, isComplete: false };
const staleTo18July: ReportAvailability = { state: 'Stale', asOfUtc: '2026-07-18T06:00:00Z', isComplete: false };

function availability(sales: ReportAvailability, fees: ReportAvailability): ReportProviderAvailability {
  return { sales, fees };
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
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } }
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
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } }
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
