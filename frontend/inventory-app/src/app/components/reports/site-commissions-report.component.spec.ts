import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { SiteCommissionsReportComponent } from './site-commissions-report.component';
import { ReportingService, SiteCommissionReport, SiteCommissionRow } from '../../services/reporting.service';
import { SiteService } from '../../services/site.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';

function row(overrides: Partial<SiteCommissionRow> = {}): SiteCommissionRow {
  return {
    siteId: 1,
    siteName: 'Main Office',
    periodStart: '2026-01-01',
    periodEnd: '2026-01-31',
    frequency: 'Monthly',
    basis: 'Gross Sales',
    grossSales: 1000,
    cardSales: 800,
    cashSales: 200,
    eligibleSales: 1000,
    commissionRate: 0.1,
    commissionDue: 100,
    machines: [{ machineId: 7, machineName: 'Machine 7', transactionCount: 4, grossSales: 1000, cardSales: 800, cashSales: 200, eligibleSales: 1000, commissionDue: 100 }],
    products: [{ productName: 'Water', totalVends: 4, totalSales: 1000 }],
    ...overrides
  };
}

function report(rows: SiteCommissionRow[] = [row()]): SiteCommissionReport {
  return { from: '2026-01-01', to: '2026-01-31', rows };
}

/**
 * `SupportSessionService` is deliberately **not** provided here, and `ReportingService` is a stub
 * that answers the report and nothing else: the page makes no write call, so every test below would
 * fail to construct the component if either came back.
 */
async function render(timeZoneId?: string | null) {
  const reporting = { siteCommissions: jest.fn(() => of(report())) };

  await TestBed.configureTestingModule({
    imports: [SiteCommissionsReportComponent],
    providers: [
      { provide: ReportingService, useValue: reporting },
      { provide: SiteService, useValue: { getList: jest.fn(() => of([])) } },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  if (timeZoneId !== undefined) {
    TestBed.inject(BusinessTimeZoneService).publish(timeZoneId);
  }

  const fixture = TestBed.createComponent(SiteCommissionsReportComponent);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, reporting };
}

afterEach(() => TestBed.resetTestingModule());

/**
 * The page reports commission due and nothing about payment (issue #1251, the repository owner's
 * decision of 13 November 2026). A recorded payment only ever counted for the exact period it was
 * recorded against, so Paid, Outstanding and Status showed paid commission as owing for any other
 * range - and Record Payment accepted a second payment for the same sales. The whole of that is
 * gone from this screen, and the figures it never derived from a payment are untouched.
 */
describe('SiteCommissionsReportComponent without payment recording (issue #1251)', () => {
  function selectRow(fixture: ComponentFixture<SiteCommissionsReportComponent>) {
    fixture.componentInstance.selected = row();
    fixture.detectChanges();
  }

  it('shows no Paid, Outstanding or Status column in the table', async () => {
    const { host } = await render();

    const headers = Array.from(host.querySelectorAll('th')).map((cell) => cell.textContent?.trim());
    expect(headers).not.toContain('Paid');
    expect(headers).not.toContain('Outstanding');
    expect(headers).not.toContain('Status');
  });

  it('still shows the site, its rate and the commission due', async () => {
    const { host } = await render();

    const headers = Array.from(host.querySelectorAll('th')).map((cell) => cell.textContent?.trim());
    expect(headers).toContain('Due');
    expect(headers).toContain('Eligible Sales');
    const cells = Array.from(host.querySelectorAll('tbody td')).map((cell) => cell.textContent?.trim());
    expect(cells).toContain('Main Office');
    expect(cells).toContain('$100.00');
    expect(cells).toContain('10.00%');
  });

  it('shows no Outstanding figure, no Payment history and no Record Payment control in the details', async () => {
    const { fixture, host } = await render();
    selectRow(fixture);

    expect(host.textContent).not.toContain('Outstanding');
    expect(host.textContent).not.toContain('Payment history');
    expect(Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim()))
      .not.toContain('Record Payment');
  });

  it('keeps the machine breakdown and the products sold in the details', async () => {
    const { fixture, host } = await render();
    selectRow(fixture);

    expect(host.textContent).toContain('Machine breakdown');
    expect(host.textContent).toContain('Machine 7');
    expect(host.textContent).toContain('Products sold');
    expect(host.textContent).toContain('Water');
  });

  /**
   * The printable page is the same answer on paper, so it drops the same two sections and keeps the
   * rest. `window.open` is replaced with a document that records what was written to it.
   */
  it('prints the details without Outstanding or Payment history', async () => {
    const { fixture, component } = await render();
    selectRow(fixture);
    let printed = '';
    const popup = {
      document: { write: (html: string) => (printed += html), close: () => undefined },
      focus: () => undefined,
      print: () => undefined,
    };
    const open = jest.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);

    component.printDetails();
    open.mockRestore();

    expect(printed).toContain('Commission Due');
    expect(printed).toContain('Machine breakdown');
    expect(printed).toContain('Products sold');
    expect(printed).not.toContain('Outstanding');
    expect(printed).not.toContain('Payment history');
  });
});

/**
 * The period presets read "today" from the business's own time zone, never the UTC date (issue
 * #1273).
 */
describe('SiteCommissionsReportComponent period presets read the business time zone (issue #1273)', () => {
  afterEach(() => jest.useRealTimers());

  it('defaults This month to the Sydney date at construction, when UTC has not yet crossed into it', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-12T21:30:00Z')); // 08:30 AEDT on 13 Nov
    const { component } = await render('Australia/Sydney');

    expect(component.period).toBe('thisMonth');
    expect(component.from).toBe('2026-11-01');
    expect(component.to).toBe('2026-11-13');
  });

  it('gives the Sydney month for This month and Last month on the last day of the month, while UTC is still the day before', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-29T21:30:00Z')); // 08:30 AEDT on 30 Nov
    const { component } = await render('Australia/Sydney');

    component.selectPeriod('thisMonth');
    expect(component.from).toBe('2026-11-01');
    expect(component.to).toBe('2026-11-30');

    component.selectPeriod('lastMonth');
    expect(component.from).toBe('2026-10-01');
    expect(component.to).toBe('2026-10-31');
  });

  it('starts Current FY on 1 July of the new year and ends Previous FY on 30 June, at 08:30 Sydney on 1 July while UTC is still 30 June', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-06-30T22:30:00Z')); // 08:30 AEST on 1 July
    const { component } = await render('Australia/Sydney');

    component.selectPeriod('currentFy');
    expect(component.from).toBe('2026-07-01');
    expect(component.to).toBe('2026-07-01');

    component.selectPeriod('previousFy');
    expect(component.from).toBe('2025-07-01');
    expect(component.to).toBe('2026-06-30');
  });

  it('keeps Current FY in the old financial year at 23:30 Sydney on 30 June', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-06-30T13:30:00Z')); // 23:30 AEST on 30 June
    const { component } = await render('Australia/Sydney');

    component.selectPeriod('currentFy');
    expect(component.from).toBe('2025-07-01');
    expect(component.to).toBe('2026-06-30');
  });

  it('falls back to the device local date, never the UTC date, while the time zone is not known yet', async () => {
    jest.useFakeTimers().setSystemTime(new Date(2026, 10, 13, 8, 30)); // local 13 Nov 2026 08:30
    const { component } = await render(null);

    expect(component.from).toBe('2026-11-01');
    expect(component.to).toBe('2026-11-13');
  });
});
