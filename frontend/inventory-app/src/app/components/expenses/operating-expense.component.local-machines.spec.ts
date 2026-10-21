import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { NEVER, Observable, of, throwError } from 'rxjs';
import { LocalMachine } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
import { MachineService } from '../../services/machine.service';
import {
  OperatingExpense,
  OperatingExpenseCategory,
  OperatingExpenseService
} from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { OperatingExpenseComponent } from './operating-expense.component';

function localMachine(id: number, name: string, isActive = true): LocalMachine {
  return { id, name, isActive, isProviderNamed: false };
}

function expense(overrides: Partial<OperatingExpense> = {}): OperatingExpense {
  return {
    id: 1,
    expenseDate: '2026-01-15T00:00:00Z',
    category: OperatingExpenseCategory.Insurance,
    description: 'Annual policy',
    amountExGst: 100,
    gstAmount: 10,
    totalAmount: 110,
    ...overrides
  };
}

async function render(
  options: {
    status?: SpecConnectionStatus;
    machines?: Observable<LocalMachine[]>;
    expenses?: OperatingExpense[];
  } = {}
) {
  await TestBed.configureTestingModule({
    imports: [OperatingExpenseComponent],
    providers: [
      {
        provide: OperatingExpenseService,
        useValue: {
          getAll: jest.fn(() => of(options.expenses ?? [expense()])),
          create: jest.fn(() => of(expense())),
          update: jest.fn(() => of(expense())),
          delete: jest.fn(() => of(undefined))
        }
      },
      { provide: SupplierService, useValue: { getAll: jest.fn(() => of([])) } },
      {
        provide: LocalMachineService,
        useValue: { getAll: jest.fn(() => options.machines ?? of([localMachine(41, 'Lobby')])) }
      },
      // Registered so that a page that still read the live fleet fails the assertions below rather
      // than failing to resolve a dependency.
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(OperatingExpenseComponent);
  fixture.detectChanges();

  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    localMachines: TestBed.inject(LocalMachineService) as unknown as { getAll: jest.Mock },
    fleet: TestBed.inject(MachineService) as unknown as { getAll: jest.Mock }
  };
}

const machineField = (host: HTMLElement) => host.querySelector('#expense-machine') as HTMLSelectElement | null;
const machineOptionLabels = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('#expense-machine option')).map((option) => option.textContent?.trim());

afterEach(() => TestBed.resetTestingModule());

/**
 * Operating Expenses attributes an expense to one of the business's **own** machines (issue #746,
 * G2b of #329), read from `/api/local-machines`, so the field works with no provider at all.
 */
describe('OperatingExpenseComponent local machine field (issue #746)', () => {
  it('offers the active local machines without a provider, and asks the fleet for nothing', async () => {
    const { component, fixture, host, localMachines, fleet } = await render({ status: 'NotConfigured' });
    component.startCreate();
    fixture.detectChanges();

    expect(localMachines.getAll).toHaveBeenCalledTimes(1);
    expect(fleet.getAll).not.toHaveBeenCalled();
    expect(component.machineFieldShown).toBe(true);
    expect(machineOptionLabels(host)).toEqual(['None', 'Lobby']);
  });

  it('offers the same machines with a provider connected', async () => {
    const { component, fixture, host, fleet } = await render({ status: 'Ready' });
    component.startCreate();
    fixture.detectChanges();

    expect(fleet.getAll).not.toHaveBeenCalled();
    expect(machineOptionLabels(host)).toEqual(['None', 'Lobby']);
  });

  it('submits the local machine id', async () => {
    const { component, fixture, host } = await render();
    component.startCreate();
    fixture.detectChanges();

    const select = machineField(host)!;
    select.value = select.options[1].value;
    select.dispatchEvent(new Event('change'));

    expect(component.form.machineId).toBe(41);
  });

  it('does not offer a deactivated machine on a new expense', async () => {
    const { component, fixture, host } = await render({
      machines: of([localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)])
    });
    component.startCreate();
    fixture.detectChanges();

    expect(machineOptionLabels(host)).toEqual(['None', 'Lobby']);
    expect(component.machineOptions.map((machine) => machine.id)).toEqual([41]);
  });

  it('keeps a deactivated machine as the selected value of the expense being edited', async () => {
    const recorded = expense({ machineId: 42 });
    const { component, fixture, host } = await render({
      machines: of([localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)]),
      expenses: [recorded]
    });

    component.startEdit(recorded);
    fixture.detectChanges();
    // `ngModel` writes the control's value in a microtask, so the selected option settles after it.
    await fixture.whenStable();

    expect(component.form.machineId).toBe(42);
    expect(machineOptionLabels(host)).toEqual(['None', 'Lobby', 'Retired corridor']);
    const select = machineField(host)!;
    expect(select.value).toBe(select.options[2].value);
  });

  it('shows the field for an edited expense whose only machine is deactivated', async () => {
    const recorded = expense({ machineId: 42 });
    const { component, fixture, host } = await render({
      machines: of([localMachine(42, 'Retired corridor', false)]),
      expenses: [recorded]
    });

    component.startEdit(recorded);
    fixture.detectChanges();

    expect(component.machineFieldShown).toBe(true);
    expect(machineOptionLabels(host)).toEqual(['None', 'Retired corridor']);
  });

  it('hides the field when the business has recorded no active machine, with no error', async () => {
    const { component, fixture, host } = await render({ machines: of([]) });
    component.startCreate();
    fixture.detectChanges();

    expect(component.machineFieldShown).toBe(false);
    expect(component.machinesError).toBe('');
    expect(machineField(host)).toBeNull();
    expect(host.querySelector('[data-testid="machine-field-error"]')).toBeNull();
  });

  it('shows an explicit error in place of the field when the local machine read failed', async () => {
    const { component, fixture, host } = await render({
      machines: throwError(() => new HttpErrorResponse({ status: 500 }))
    });
    component.startCreate();
    fixture.detectChanges();

    expect(component.machineFieldShown).toBe(false);
    expect(component.machinesError).toContain('The machine list could not be loaded');
    expect(host.querySelector('[data-testid="machine-field-error"]')?.textContent).toContain(
      'The machine list could not be loaded'
    );
    expect(machineField(host)).toBeNull();
  });

  it('says the machines are still loading rather than offering no field', async () => {
    const { component, fixture, host } = await render({ machines: NEVER });
    component.startCreate();
    fixture.detectChanges();

    expect(machineField(host)).toBeNull();
    expect(host.querySelector('[data-testid="machine-field-error"]')).toBeNull();
    expect(host.querySelector('[data-testid="machine-field-loading"]')).not.toBeNull();
    expect(component.machinesLoading).toBe(true);
  });
});
