import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
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
    imports: [HistoricalCostRecoveryWorkflowComponent],
    providers: [{ provide: ReportingService, useValue: { backfillNayaxSaleCosts } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(HistoricalCostRecoveryWorkflowComponent);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find(candidate => candidate.textContent?.includes(text));
}

// The `window.confirm` spies are restored here, after each test's assertions: restoring them
// inside the test would also clear the recorded calls those assertions check.
afterEach(() => jest.restoreAllMocks());

/**
 * Moved from `historical-cost-recovery.component.spec.ts` with the workflow itself when the
 * dry-run/apply workflow was extracted from the routed page into this feature component
 * (docs/architecture.md § Page composition boundary, issue #191); the assertions are unchanged.
 */
describe('HistoricalCostRecoveryWorkflowComponent dry run (issue #390)', () => {
  it('runs the dry run through ReportingService.backfillNayaxSaleCosts without applying anything', async () => {
    const backfillNayaxSaleCosts = jest.fn(() => of(backfillResult()));
    const { host, component } = await render(backfillNayaxSaleCosts);

    button(host, 'Dry Run')?.click();

    expect(backfillNayaxSaleCosts).toHaveBeenCalledTimes(1);
    expect(backfillNayaxSaleCosts).toHaveBeenCalledWith(true);
    expect(component.loading).toBe(false);
  });

  it('reports the recoverable, pending, already-costed and invalid counts of the returned result', async () => {
    const backfillNayaxSaleCosts = jest.fn(() => of(backfillResult()));
    const { fixture, host } = await render(backfillNayaxSaleCosts);

    button(host, 'Dry Run')?.click();
    fixture.detectChanges();

    const text = host.textContent ?? '';
    // Pending completed sales is the recoverable plus still-pending total the Admin page showed.
    expect(text).toContain('Pending completed sales:');
    expect(text).toContain('7');
    expect(text).toContain('6');
    expect(text).toContain('3');
    expect(text).toContain('2');
    const pending = Array.from(host.querySelectorAll('div')).find(div => div.textContent?.startsWith('Pending completed sales:'));
    expect(pending?.querySelector('strong')?.textContent).toBe('7');
  });

  it('disables both actions while a run is in flight', async () => {
    const { fixture, component, host } = await render(jest.fn(() => of(backfillResult())));

    component.loading = true;
    fixture.detectChanges();

    expect(Array.from(host.querySelectorAll('button')).filter(candidate => candidate.disabled)).toHaveLength(2);
  });
});

describe('HistoricalCostRecoveryWorkflowComponent apply confirmation (issue #390)', () => {
  it('applies the recovery only after the operator confirms the mutating action', async () => {
    const backfillNayaxSaleCosts = jest.fn(() => of(backfillResult({ dryRun: false })));
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { host } = await render(backfillNayaxSaleCosts);

    button(host, 'Apply Nayax Cost Backfill')?.click();

    expect(confirm).toHaveBeenCalledWith('Apply transaction-level Nayax historical costs to eligible pending completed sales?');
    expect(backfillNayaxSaleCosts).toHaveBeenCalledWith(false);
  });

  it('does not call the API when the confirmation is dismissed', async () => {
    const backfillNayaxSaleCosts = jest.fn(() => of(backfillResult()));
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { host } = await render(backfillNayaxSaleCosts);

    button(host, 'Apply Nayax Cost Backfill')?.click();

    expect(confirm).toHaveBeenCalledTimes(1);
    expect(backfillNayaxSaleCosts).not.toHaveBeenCalled();
  });
});

describe('HistoricalCostRecoveryWorkflowComponent failures (issue #390)', () => {
  it('shows the API string error body and clears loading', async () => {
    const backfillNayaxSaleCosts = jest.fn(() => throwError(() => ({ error: 'No eligible pending sales.' })));
    const { fixture, component, host } = await render(backfillNayaxSaleCosts);

    component.backfillNayax(true);
    fixture.detectChanges();

    expect(host.textContent).toContain('No eligible pending sales.');
    expect(component.loading).toBe(false);
  });

  it('falls back to the generic failure message when the API returns no string body', async () => {
    const backfillNayaxSaleCosts = jest.fn(() => throwError(() => ({})));
    const { fixture, component, host } = await render(backfillNayaxSaleCosts);

    component.backfillNayax(true);
    fixture.detectChanges();

    expect(host.textContent).toContain('Unable to run the Nayax cost backfill.');
    expect(component.loading).toBe(false);
  });

  it('clears a previous error when a new run starts', async () => {
    const backfillNayaxSaleCosts = jest
      .fn()
      .mockReturnValueOnce(throwError(() => ({ error: 'No eligible pending sales.' })))
      .mockReturnValueOnce(of(backfillResult()));
    const { fixture, component, host } = await render(backfillNayaxSaleCosts);

    component.backfillNayax(true);
    component.backfillNayax(true);
    fixture.detectChanges();

    expect(component.error).toBe('');
    expect(host.textContent).not.toContain('No eligible pending sales.');
  });
});
