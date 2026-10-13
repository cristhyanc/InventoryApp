import { TestBed } from '@angular/core/testing';
import { NayaxConnectionCheckLine } from '../../../services/nayax-connection.service';
import {
  NayaxConnectionChecklistComponent,
  nayaxConnectionAreaLabel,
  nayaxConnectionFailureSentence
} from './nayax-connection-checklist.component';

function line(overrides: Partial<NayaxConnectionCheckLine> = {}): NayaxConnectionCheckLine {
  return { check: 'SignIn', status: 'Passed', failure: null, ...overrides };
}

async function render(checks: NayaxConnectionCheckLine[], machineCount: number | null) {
  await TestBed.configureTestingModule({ imports: [NayaxConnectionChecklistComponent] }).compileComponents();

  const fixture = TestBed.createComponent(NayaxConnectionChecklistComponent);
  fixture.componentRef.setInput('checks', checks);
  fixture.componentRef.setInput('machineCount', machineCount);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

/**
 * Step 4 of the connection wizard (issue #538): the checklist built from the Test or Re-test
 * response. It is presentation only - every status, cause and count on screen is the server's own
 * (issues #536 and #537).
 */
describe('NayaxConnectionChecklistComponent (issue #538)', () => {
  it('marks a passed line with a tick and names the area it proved', async () => {
    const { host } = await render([line({ check: 'ProductCatalogue' })], 4);

    const row = host.querySelector('[data-testid="check-ProductCatalogue"]');
    expect(row?.textContent).toContain('✓');
    expect(row?.textContent).toContain('Passed');
    expect(row?.textContent).toContain('Operator product catalogue');
  });

  it('marks a failed line with a cross and the cause the server reported', async () => {
    const { host } = await render(
      [line({ check: 'MachineProducts', status: 'Failed', failure: 'PermissionNotGranted' })],
      2
    );

    const row = host.querySelector('[data-testid="check-MachineProducts"]');
    expect(row?.textContent).toContain('✗');
    expect(row?.textContent).toContain('does not have permission');
  });

  it('reads each declared failure cause as its own sentence, so a refusal is never mistaken for an expiry', async () => {
    const { host } = await render(
      [
        line({ check: 'SignIn', status: 'Failed', failure: 'InvalidOrExpiredToken' }),
        line({ check: 'Machines', status: 'Failed', failure: 'NayaxUnavailable' }),
        line({ check: 'ProductCatalogue', status: 'Failed', failure: 'OperatorIdMismatch' }),
        line({ check: 'ProductGroups', status: 'Failed', failure: 'UnexpectedResponse' })
      ],
      null
    );

    expect(host.querySelector('[data-testid="check-SignIn"]')?.textContent).toContain('invalid or has expired');
    expect(host.querySelector('[data-testid="check-Machines"]')?.textContent).toContain('could not be reached');
    expect(host.querySelector('[data-testid="check-ProductCatalogue"]')?.textContent).toContain(
      'not one this token covers'
    );
    expect(host.querySelector('[data-testid="check-ProductGroups"]')?.textContent).toContain(
      'does not recognise'
    );
  });

  it('says a machine-scoped line was not checked because the account has no machines yet', async () => {
    const { host } = await render([line({ check: 'Machine', status: 'NotChecked' })], 0);

    const row = host.querySelector('[data-testid="check-Machine"]');
    expect(row?.textContent).toContain('Not checked: no machines in this Nayax account yet');
    expect(row?.textContent).not.toContain('✓');
  });

  it('does not claim there are no machines when the machine list itself could not be read', async () => {
    const { host } = await render(
      [
        line({ check: 'Machines', status: 'Failed', failure: 'PermissionNotGranted' }),
        line({ check: 'MachineLastSales', status: 'NotChecked' })
      ],
      null
    );

    const row = host.querySelector('[data-testid="check-MachineLastSales"]');
    expect(row?.textContent).toContain('Not checked: the machine list could not be read');
    expect(row?.textContent).not.toContain('no machines in this Nayax account yet');
  });

  it('notes that the restock write call is checked on first real use, never by a test', async () => {
    const { host } = await render([line()], 1);

    expect(host.textContent).toContain('restock');
    expect(host.textContent).toContain('first real use');
  });

  it('reports how many machines the account returned when the machine list was read', async () => {
    const { host } = await render([line({ check: 'Machines' })], 7);

    expect(host.textContent).toContain('7');
  });

  it('renders an unknown line the API may add without claiming it passed', async () => {
    const { host } = await render(
      [{ check: 'SomethingNew', status: 'Failed', failure: 'BrandNewCause' } as unknown as NayaxConnectionCheckLine],
      1
    );

    const row = host.querySelector('[data-testid="check-SomethingNew"]');
    expect(row?.textContent).toContain('SomethingNew');
    expect(row?.textContent).toContain('✗');
  });

  it('shares one vocabulary for areas and causes with the support request of step 5', () => {
    expect(nayaxConnectionAreaLabel('MachineLastAlerts')).toContain('alerts');
    expect(nayaxConnectionFailureSentence('PermissionNotGranted')).toContain('permission');
  });
});
