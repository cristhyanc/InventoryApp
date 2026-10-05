import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HistoricalCostRecoveryComponent } from './historical-cost-recovery.component';
import { HistoricalCostRecoveryWorkflowComponent } from './historical-cost-recovery-workflow.component';
import { NayaxCostBackfillResult, ReportingService } from '../../../services/reporting.service';

function backfillResult(overrides: Partial<NayaxCostBackfillResult> = {}): NayaxCostBackfillResult {
  return {
    salesReviewed: 10,
    salesWithNayaxCost: 7,
    salesWouldBeCosted: 6,
    salesAlreadyCosted: 3,
    salesStillPending: 1,
    invalidCostRows: 2,
    errorRows: 0,
    dryRun: true,
    ...overrides
  };
}

async function render(backfillNayaxSaleCosts: jest.Mock) {
  await TestBed.configureTestingModule({
    imports: [HistoricalCostRecoveryComponent],
    providers: [provideRouter([]), { provide: ReportingService, useValue: { backfillNayaxSaleCosts } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(HistoricalCostRecoveryComponent);
  fixture.detectChanges();
  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    workflow: fixture.debugElement.query(By.directive(HistoricalCostRecoveryWorkflowComponent))
      ?.componentInstance as HistoricalCostRecoveryWorkflowComponent
  };
}

/**
 * The page is a composition boundary (docs/architecture.md § Page composition boundary, issue
 * #191): it renders the page heading and hosts `HistoricalCostRecoveryWorkflowComponent`, which
 * owns the dry-run/apply workflow and is tested in
 * `historical-cost-recovery-workflow.component.spec.ts`.
 */
describe('HistoricalCostRecoveryComponent page composition (issues #390, #191)', () => {
  it('composes the historical cost recovery workflow component instead of owning the workflow itself', async () => {
    const backfillNayaxSaleCosts = jest.fn(() => of(backfillResult()));
    const { host, workflow } = await render(backfillNayaxSaleCosts);

    expect(workflow).toBeTruthy();
    expect(host.textContent).toContain('Nayax Historical Cost Recovery');
    expect(backfillNayaxSaleCosts).not.toHaveBeenCalled();
  });

  it('links back to the Admin hub and warns that the action can change historical cost of goods sold', async () => {
    const { host } = await render(jest.fn(() => of(backfillResult())));

    expect(host.querySelector('a')?.getAttribute('href')).toBe('/admin');
    expect(host.textContent).toContain('This action can change historical cost of goods sold.');
  });
});
