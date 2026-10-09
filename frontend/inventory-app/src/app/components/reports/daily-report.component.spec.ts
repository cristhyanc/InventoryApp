import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { DailyReportComponent } from './daily-report.component';
import { ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';

function createComponent(): DailyReportComponent {
  const route = {} as unknown as ActivatedRoute;
  const reports = {} as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;
  return new DailyReportComponent(route, reports, machines);
}

describe('DailyReportComponent reconciliationStatusClass (issue #419 Material restyle)', () => {
  it('maps Reconciled to the shared success badge class', () => {
    expect(createComponent().reconciliationStatusClass('Reconciled')).toBe('badge-success');
  });

  it('maps Warning and Pending to the shared warning badge class', () => {
    const component = createComponent();
    expect(component.reconciliationStatusClass('Warning')).toBe('badge-warning');
    expect(component.reconciliationStatusClass('Pending')).toBe('badge-warning');
  });

  it('maps Mismatch to the shared danger badge class', () => {
    expect(createComponent().reconciliationStatusClass('Mismatch')).toBe('badge-danger');
  });

  it('applies no colour class for an unknown or missing status, exactly as before', () => {
    const component = createComponent();
    expect(component.reconciliationStatusClass(undefined)).toBe('');
    expect(component.reconciliationStatusClass('SomethingElse')).toBe('');
  });
});
