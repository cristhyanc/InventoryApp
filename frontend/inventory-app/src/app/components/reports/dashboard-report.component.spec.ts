import { ActivatedRoute } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { DashboardReportComponent } from './dashboard-report.component';
import { ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { DashboardReport } from '../../features/reports/dashboard/models/dashboard-report.model';

function createComponent(): DashboardReportComponent {
  const route = {} as unknown as ActivatedRoute;
  const reports = {} as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;
  return new DashboardReportComponent(route, reports, machines);
}

const REPORT: DashboardReport = {
  from: '2026-01-01',
  to: '2026-01-31',
  sales: 1000,
  grossProfit: 400,
  transactions: 50,
  quantity: 200,
  machineCount: 2,
  productCount: 10,
  unmappedProductCount: 0,
  dataQuality: {
    missingStatus: false,
    historicalCostUnavailable: false,
    gstClassificationMissing: false,
    commissionNotPersisted: false,
    containsUnmappedProducts: false
  }
};

describe('DashboardReportComponent statusClass (issue #419 Material restyle)', () => {
  it('maps a Reconciled status to the shared success badge class', () => {
    expect(createComponent().statusClass('Reconciled')).toBe('badge-success');
  });

  it('maps any other status to the shared warning badge class', () => {
    const component = createComponent();
    expect(component.statusClass('Pending')).toBe('badge-warning');
    expect(component.statusClass(undefined)).toBe('badge-warning');
  });
});

describe('DashboardReportComponent stat-card icon tile position (issue #453)', () => {
  it('renders the label/value content before the icon tile in every summary stat card, so the icon sits at the right', async () => {
    const route = {} as unknown as ActivatedRoute;
    const reports = { dashboard: () => of(REPORT) } as unknown as ReportingService;
    const machines = { getAll: () => of([]) } as unknown as MachineService;

    await TestBed.configureTestingModule({
      imports: [DashboardReportComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route },
        { provide: ReportingService, useValue: reports },
        { provide: MachineService, useValue: machines }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(DashboardReportComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const heads = Array.from(host.querySelectorAll('.stat-card-head'));
    expect(heads).toHaveLength(4);
    for (const head of heads) {
      const children = Array.from(head.children);
      expect(children[0].classList.contains('stat-card-content')).toBe(true);
      expect(children[children.length - 1].classList.contains('icon-tile')).toBe(true);
    }
  });
});
