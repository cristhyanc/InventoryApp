import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LocalMachine } from '../../models/models';
import { OperatingExpenseCategory } from '../../services/operating-expense.service';
import { ExpenseFormComponent, ExpenseFormValue, newExpenseFormValue } from './expense-form.component';

function localMachine(id: number, name: string, isActive = true): LocalMachine {
  return { id, name, isActive, isProviderNamed: false };
}

/** A stored expense as the Edit page hands it to the form, attributed to `machineId`. */
function storedValue(machineId: number | null): ExpenseFormValue {
  return {
    payload: {
      expenseDate: '2026-01-15',
      category: OperatingExpenseCategory.Insurance,
      description: 'Annual policy',
      amountExGst: 100,
      gstAmount: 10,
      totalAmount: 110,
      supplierId: null,
      siteId: null,
      machineId,
      notes: null
    },
    recordedMachineId: machineId,
    attachmentFileName: null
  };
}

async function render(
  options: {
    value?: ExpenseFormValue;
    mode?: 'create' | 'edit';
    machines?: LocalMachine[];
    machinesError?: string;
    machinesLoading?: boolean;
  } = {}
) {
  await TestBed.configureTestingModule({
    imports: [ExpenseFormComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(ExpenseFormComponent);
  fixture.componentRef.setInput('mode', options.mode ?? 'create');
  fixture.componentRef.setInput('value', options.value ?? newExpenseFormValue());
  fixture.componentRef.setInput('machines', options.machines ?? [localMachine(41, 'Lobby')]);
  fixture.componentRef.setInput('machinesError', options.machinesError ?? '');
  fixture.componentRef.setInput('machinesLoading', options.machinesLoading ?? false);
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

const machineField = (host: HTMLElement) => host.querySelector('#expense-machine') as HTMLSelectElement | null;
const machineOptionLabels = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('#expense-machine option')).map((option) => option.textContent?.trim());

afterEach(() => TestBed.resetTestingModule());

/**
 * The expense form attributes an expense to one of the business's **own** machines (issue #746, G2b
 * of #329), read from `/api/local-machines`, so the field works with no provider at all. These rules
 * moved here from the register card with the form itself (issue #1167); none of them changed.
 */
describe('ExpenseFormComponent local machine field (issue #746)', () => {
  it('offers the active machines, with None first', async () => {
    const { component, host } = await render();

    expect(component.machineFieldShown).toBe(true);
    expect(machineOptionLabels(host)).toEqual(['None', 'Lobby']);
  });

  it('submits the local machine id', async () => {
    const { component, host } = await render();

    const select = machineField(host)!;
    select.value = select.options[1].value;
    select.dispatchEvent(new Event('change'));

    expect(component.form.machineId).toBe(41);
  });

  it('does not offer a deactivated machine on a new expense', async () => {
    const { component, host } = await render({
      machines: [localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)]
    });

    expect(machineOptionLabels(host)).toEqual(['None', 'Lobby']);
    expect(component.machineOptions.map((machine) => machine.id)).toEqual([41]);
  });

  it('keeps a deactivated machine as the selected value of the expense being edited', async () => {
    const { fixture, component, host } = await render({
      mode: 'edit',
      value: storedValue(42),
      machines: [localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)]
    });
    // `ngModel` writes the control's value in a microtask, so the selected option settles after it.
    await fixture.whenStable();

    expect(component.form.machineId).toBe(42);
    expect(machineOptionLabels(host)).toEqual(['None', 'Lobby', 'Retired corridor']);
    const select = machineField(host)!;
    expect(select.value).toBe(select.options[2].value);
  });

  it("keeps offering the recorded machine after None has been chosen, so the choice is reversible", async () => {
    const { component } = await render({
      mode: 'edit',
      value: storedValue(42),
      machines: [localMachine(42, 'Retired corridor', false)]
    });

    component.form.machineId = null;

    expect(component.machineOptions.map((machine) => machine.id)).toEqual([42]);
  });

  it('shows the field for an edited expense whose only machine is deactivated', async () => {
    const { component, host } = await render({
      mode: 'edit',
      value: storedValue(42),
      machines: [localMachine(42, 'Retired corridor', false)]
    });

    expect(component.machineFieldShown).toBe(true);
    expect(machineOptionLabels(host)).toEqual(['None', 'Retired corridor']);
  });

  it('hides the field when the business has recorded no active machine, with no error', async () => {
    const { component, host } = await render({ machines: [] });

    expect(component.machineFieldShown).toBe(false);
    expect(machineField(host)).toBeNull();
    expect(host.querySelector('[data-testid="machine-field-error"]')).toBeNull();
  });

  it('shows an explicit error in place of the field when the machine read failed', async () => {
    const { component, host } = await render({
      machinesError: 'The machine list could not be loaded, so an expense cannot be attributed to a machine.'
    });

    expect(component.machineFieldShown).toBe(false);
    expect(host.querySelector('[data-testid="machine-field-error"]')?.textContent).toContain(
      'The machine list could not be loaded'
    );
    expect(machineField(host)).toBeNull();
  });

  it('says the machines are still loading rather than offering no field', async () => {
    const { host } = await render({ machines: [], machinesLoading: true });

    expect(machineField(host)).toBeNull();
    expect(host.querySelector('[data-testid="machine-field-error"]')).toBeNull();
    expect(host.querySelector('[data-testid="machine-field-loading"]')).not.toBeNull();
  });
});
