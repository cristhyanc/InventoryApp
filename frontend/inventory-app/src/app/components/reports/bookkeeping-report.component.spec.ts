import { ActivatedRoute } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { BookkeepingReportComponent } from './bookkeeping-report.component';
import { ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { BookkeepingReport } from '../../features/reports/bookkeeping/models/bookkeeping-report.model';

const REPORT: BookkeepingReport = {
  from: '2026-01-01',
  to: '2026-01-31',
  financialYear: '2025/26',
  sales: 1000,
  grossProfit: 400,
  fees: 20,
  netSettlement: 980,
  gstOnSales: 90,
  gstOnFees: 2,
  dataQuality: {
    missingStatus: false,
    historicalCostUnavailable: false,
    gstClassificationMissing: false,
    commissionNotPersisted: false,
    containsUnmappedProducts: false
  }
};

/**
 * Regression coverage for issue #453: this component shares the `.stat-card-head`/`.icon-tile`
 * arrangement validated structurally by `stat-card-icon-position.spec.ts`, so it only needs to
 * confirm this specific production template was updated to the same order.
 */
describe('BookkeepingReportComponent stat-card icon tile position (issue #453)', () => {
  it('renders the label/value content before the icon tile in every summary stat card, so the icon sits at the right', async () => {
    const route = {} as unknown as ActivatedRoute;
    const reports = { bookkeeping: () => of(REPORT) } as unknown as ReportingService;
    const machines = { getAll: () => of([]) } as unknown as MachineService;

    await TestBed.configureTestingModule({
      imports: [BookkeepingReportComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route },
        { provide: ReportingService, useValue: reports },
        { provide: MachineService, useValue: machines }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(BookkeepingReportComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const heads = Array.from(host.querySelectorAll('.stat-card-head'));
    expect(heads.length).toBe(4);
    for (const head of heads) {
      const children = Array.from(head.children);
      expect(children[0].classList.contains('stat-card-content')).toBe(true);
      expect(children[children.length - 1].classList.contains('icon-tile')).toBe(true);
    }
  });
});
