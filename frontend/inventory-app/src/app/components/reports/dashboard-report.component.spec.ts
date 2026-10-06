import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { DashboardReportComponent } from './dashboard-report.component';
import { ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';

function createComponent(): DashboardReportComponent {
  const route = {} as unknown as ActivatedRoute;
  const reports = {} as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;
  return new DashboardReportComponent(route, reports, machines);
}

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
