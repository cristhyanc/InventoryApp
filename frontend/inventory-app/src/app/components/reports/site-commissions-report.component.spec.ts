import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';
import { SiteCommissionsReportComponent } from './site-commissions-report.component';
import { ReportingService, SiteCommissionReport, SiteCommissionRow } from '../../services/reporting.service';
import { SiteService } from '../../services/site.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { SupportSessionService } from '../../support-session/support-session.service';

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
    paid: 0,
    outstanding: 100,
    status: 'Due',
    machines: [],
    products: [],
    payments: [],
    ...overrides
  };
}

function report(rows: SiteCommissionRow[] = [row()]): SiteCommissionReport {
  return { from: '2026-01-01', to: '2026-01-31', rows };
}

async function render(overrides: { sessionActive?: Observable<boolean>; recordCommissionPayment?: jest.Mock } = {}) {
  const recordCommissionPayment = overrides.recordCommissionPayment ?? jest.fn(() => of({ id: 1, siteId: 1, periodStart: '2026-01-01', periodEnd: '2026-01-31', paymentDate: '2026-01-15', amount: 50 }));

  await TestBed.configureTestingModule({
    imports: [SiteCommissionsReportComponent],
    providers: [
      {
        provide: ReportingService,
        useValue: {
          siteCommissions: jest.fn(() => of(report())),
          recordCommissionPayment
        }
      },
      { provide: SiteService, useValue: { getList: jest.fn(() => of([])) } },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SiteCommissionsReportComponent);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, recordCommissionPayment };
}

afterEach(() => TestBed.resetTestingModule());

/**
 * Hiding the Record Payment write control and guarding its handler during a support session
 * (issue #659, extending #554). A session grants `SiteCommissionsManage`, so this control would
 * otherwise still render.
 */
describe('SiteCommissionsReportComponent write control during a support session (issue #659)', () => {
  function selectRow(fixture: ComponentFixture<SiteCommissionsReportComponent>) {
    fixture.componentInstance.selected = row();
    fixture.detectChanges();
  }

  it('hides Record Payment for an outstanding balance while a session is active', async () => {
    const { fixture, host } = await render({ sessionActive: of(true) });
    selectRow(fixture);

    const button = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Record Payment');
    expect(button).toBeUndefined();
  });

  it('shows Record Payment for an outstanding balance while no session is active', async () => {
    const { fixture, host } = await render({ sessionActive: of(false) });
    selectRow(fixture);

    const button = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Record Payment');
    expect(button).toBeDefined();
  });

  it('does not record a payment while a session is active, even if the handler is invoked directly', async () => {
    const { fixture, component, recordCommissionPayment } = await render({ sessionActive: of(true) });
    selectRow(fixture);

    component.paymentAmount = 50;
    component.recordPayment();

    expect(recordCommissionPayment).not.toHaveBeenCalled();
  });

  it('records a payment normally while no session is active', async () => {
    const { fixture, component, recordCommissionPayment } = await render({ sessionActive: of(false) });
    selectRow(fixture);

    component.paymentAmount = 50;
    component.recordPayment();

    expect(recordCommissionPayment).toHaveBeenCalled();
  });
});
