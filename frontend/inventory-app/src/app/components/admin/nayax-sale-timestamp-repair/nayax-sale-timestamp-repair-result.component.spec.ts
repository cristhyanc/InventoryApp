import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NayaxSaleTimestampRepairResultComponent } from './nayax-sale-timestamp-repair-result.component';
import {
  NayaxSaleTimestampEvidenceSource,
  NayaxSaleTimestampRepairApplied
} from '../../../services/nayax-sale-timestamp-repair.service';

function applied(overrides: Partial<NayaxSaleTimestampRepairApplied> = {}): NayaxSaleTimestampRepairApplied {
  return {
    previewId: '6f1b2c4e-0000-4000-8000-000000000001',
    salesRepaired: 2,
    productsRebuilt: 1,
    recostedSales: 5,
    repairs: [
      {
        id: 41,
        transactionId: 2108816976,
        machineId: 942488501,
        previousInstantUtc: '2026-10-06T23:14:00Z',
        repairedInstantUtc: '2026-10-06T12:14:00Z',
        previousBusinessDate: '2026-10-07T00:00:00',
        repairedBusinessDate: '2026-10-06T00:00:00',
        evidenceSource: NayaxSaleTimestampEvidenceSource.OperatorExport,
        evidenceReference: 'operator export week.csv',
        previewId: '6f1b2c4e-0000-4000-8000-000000000001',
        appliedAt: '2026-10-08T05:10:00Z',
        appliedByDirectoryTenantId: 'tenant',
        appliedByObjectId: 'object'
      }
    ],
    ...overrides
  };
}

async function render(
  result: NayaxSaleTimestampRepairApplied,
  inputs: { unresolvedAtPreview?: number; missingAtPreview?: number } = {}
): Promise<{
  fixture: ComponentFixture<NayaxSaleTimestampRepairResultComponent>;
  component: NayaxSaleTimestampRepairResultComponent;
  host: HTMLElement;
}> {
  await TestBed.configureTestingModule({ imports: [NayaxSaleTimestampRepairResultComponent] }).compileComponents();
  const fixture = TestBed.createComponent(NayaxSaleTimestampRepairResultComponent);
  fixture.componentRef.setInput('applied', result);
  fixture.componentRef.setInput('unresolvedAtPreview', inputs.unresolvedAtPreview ?? 0);
  fixture.componentRef.setInput('missingAtPreview', inputs.missingAtPreview ?? 0);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

function text(host: HTMLElement, testId: string): string {
  return host.querySelector(`[data-testid="${testId}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('NayaxSaleTimestampRepairResultComponent (issue #487)', () => {
  it('shows the actual repaired, rebuilt and recosted counts the server returned', async () => {
    const { host } = await render(applied());

    expect(text(host, 'repair-applied-sales')).toContain('2');
    expect(text(host, 'repair-applied-products')).toContain('1');
    expect(text(host, 'repair-applied-recosted')).toContain('5');
  });

  it('lists the audit records the apply recorded, with both instants and both business dates', async () => {
    const { host } = await render(applied());
    const cells = Array.from(host.querySelectorAll('[data-testid="repair-audit-records"] tbody td')).map(
      (cell) => cell.textContent?.replace(/\s+/g, ' ').trim() ?? ''
    );

    expect(cells[0]).toContain('#2108816976');
    expect(cells[3]).toContain('07/10/2026');
    expect(cells[3]).toContain('06/10/2026');
    expect(cells[4]).toContain('Operator export');
    expect(cells[4]).toContain('operator export week.csv');
  });

  /** A no-op apply is a designed outcome, not a silent failure. */
  it('explains an apply that wrote nothing', async () => {
    const { host } = await render(applied({ salesRepaired: 0, productsRebuilt: 0, recostedSales: 0, repairs: [] }));

    expect(text(host, 'repair-applied-nothing')).toContain('nothing repairable left');
    expect(host.querySelector('[data-testid="repair-audit-records"]')).toBeNull();
  });

  /**
   * A success must never read as a complete explanation of the period: the plan's unresolved rows
   * and the sales it never held were not changed by the apply.
   */
  it('keeps the unresolved and missing counts of the confirmed plan visible', async () => {
    const { host } = await render(applied(), { unresolvedAtPreview: 12, missingAtPreview: 8 });
    const gaps = text(host, 'repair-applied-gaps');

    expect(gaps).toContain('did not resolve everything');
    expect(gaps).toContain('12');
    expect(gaps).toContain('8');
    expect(gaps).toContain('The apply changed neither');
  });

  it('shows no gap warning when the confirmed plan had none', async () => {
    const { host } = await render(applied());

    expect(host.querySelector('[data-testid="repair-applied-gaps"]')).toBeNull();
  });

  it('asks for a fresh preview to verify, and only on request', async () => {
    const { component, fixture, host } = await render(applied());
    const requested = jest.fn();
    component.verifyRequested.subscribe(requested);

    expect(host.textContent?.replace(/\s+/g, ' ')).toContain('already correct');
    expect(requested).not.toHaveBeenCalled();

    const verify = Array.from(host.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Preview again to verify')
    );
    verify?.click();
    fixture.detectChanges();

    expect(requested).toHaveBeenCalledTimes(1);
  });

  it('disables verification while another request is in flight', async () => {
    const { fixture, host } = await render(applied());
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    const verify = Array.from(host.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Preview again to verify')
    );
    expect(verify?.disabled).toBe(true);
  });

  /** Reversing a committed repair is the human-run restore, not an action on this page. */
  it('states that rolling a committed repair back is the human-run database restore', async () => {
    const { host } = await render(applied());

    expect(host.textContent?.replace(/\s+/g, ' ')).toContain('human-run database restore');
    expect(host.querySelectorAll('button')).toHaveLength(1);
  });
});
