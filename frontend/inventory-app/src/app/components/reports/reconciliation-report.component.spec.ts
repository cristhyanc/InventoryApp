import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { ReconciliationReportComponent } from './reconciliation-report.component';
import { ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';

function createComponent(): ReconciliationReportComponent {
  const route = {} as unknown as ActivatedRoute;
  const reports = {} as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;
  return new ReconciliationReportComponent(route, reports, machines);
}

describe('ReconciliationReportComponent statusClass (issue #419 Material restyle)', () => {
  it('maps Reconciled to the shared success badge class', () => {
    expect(createComponent().statusClass('Reconciled')).toBe('badge-success');
  });

  it('maps Mismatch to the shared danger badge class', () => {
    expect(createComponent().statusClass('Mismatch')).toBe('badge-danger');
  });

  it('maps any other status to the shared warning badge class', () => {
    expect(createComponent().statusClass('Pending')).toBe('badge-warning');
  });
});
