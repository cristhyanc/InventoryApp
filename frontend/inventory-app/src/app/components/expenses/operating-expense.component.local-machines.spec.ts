import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
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
    queryParams?: Record<string, string>;
  } = {}
) {
  await TestBed.configureTestingModule({
    imports: [OperatingExpenseComponent],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap: of(convertToParamMap(options.queryParams ?? {})) } },
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
    expenses: TestBed.inject(OperatingExpenseService) as unknown as { getAll: jest.Mock },
    localMachines: TestBed.inject(LocalMachineService) as unknown as { getAll: jest.Mock },
    fleet: TestBed.inject(MachineService) as unknown as { getAll: jest.Mock },
    router: TestBed.inject(Router)
  };
}

const machineFilter = (host: HTMLElement) =>
  host.querySelector('#expense-machine-filter') as HTMLSelectElement | null;
const machineFilterLabels = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('#expense-machine-filter option')).map((option) => option.textContent?.trim());
const applyFilters = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Apply filters')!;

afterEach(() => TestBed.resetTestingModule());

/**
 * The expense register's own machine filter (issue #746, criterion 4), over the business's **own**
 * machine records read from `/api/local-machines`, so it works with no provider at all.
 * `GET /api/operating-expenses` has accepted a local `machineId` since issue #780, so this narrows
 * the list through the existing parameter and changes no contract.
 *
 * The machine **field** moved to the Add/Edit expense form with the rest of the form in issue #1167;
 * `expense-form.component.local-machines.spec.ts` covers it there.
 */
describe('OperatingExpenseComponent local machine filter (issue #746)', () => {
  it('offers the filter from the local machines without a provider, and asks the fleet for nothing', async () => {
    const { component, host, localMachines, fleet } = await render({ status: 'NotConfigured' });

    expect(localMachines.getAll).toHaveBeenCalledTimes(1);
    expect(fleet.getAll).not.toHaveBeenCalled();
    expect(component.machineFilterShown).toBe(true);
    expect(machineFilterLabels(host)).toEqual(['All machines', 'Lobby']);
  });

  it('offers the same filter with a provider connected', async () => {
    const { component, host, fleet } = await render({ status: 'Ready' });

    expect(fleet.getAll).not.toHaveBeenCalled();
    expect(component.machineFilterShown).toBe(true);
    expect(machineFilterLabels(host)).toEqual(['All machines', 'Lobby']);
  });

  it('lists a deactivated machine in the filter, so its expenses stay findable', async () => {
    const { host } = await render({
      machines: of([localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)])
    });

    expect(machineFilterLabels(host)).toEqual(['All machines', 'Lobby', 'Retired corridor']);
  });

  it('applies the chosen local machine id through the address (issue #1167)', async () => {
    const { component, fixture, host, router } = await render({
      machines: of([localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)])
    });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    const select = machineFilter(host)!;
    select.value = select.options[2].value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    applyFilters(host).click();

    expect(component.filters.machineId).toBe(42);
    expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: { machineId: '42' } });
  });

  it('sends the local machine id as the machineId filter the address asks for', async () => {
    const { expenses } = await render({
      machines: of([localMachine(41, 'Lobby'), localMachine(42, 'Retired corridor', false)]),
      queryParams: { machineId: '42' }
    });

    expect(expenses.getAll).toHaveBeenLastCalledWith(expect.objectContaining({ machineId: 42 }));
  });

  it('asks for no machine at all until one is chosen', async () => {
    const { component, expenses } = await render();

    expect(component.filters.machineId).toBeUndefined();
    expect(expenses.getAll.mock.calls[0][0].machineId).toBeUndefined();
  });

  it('hides the filter when the business has recorded no active machine, with no error', async () => {
    const { component, host } = await render({ machines: of([]) });

    expect(component.machineFilterShown).toBe(false);
    expect(component.machinesError).toBe('');
    expect(machineFilter(host)).toBeNull();
    expect(host.querySelector('[data-testid="machine-filter-error"]')).toBeNull();
  });

  it('shows an explicit error in place of the filter when the local machine read failed', async () => {
    const { component, host } = await render({
      machines: throwError(() => new HttpErrorResponse({ status: 500 }))
    });

    expect(component.machineFilterShown).toBe(false);
    expect(host.querySelector('[data-testid="machine-filter-error"]')?.textContent).toContain(
      'The machine list could not be loaded'
    );
    expect(machineFilter(host)).toBeNull();
  });

  it('says the machines are still loading rather than offering no filter', async () => {
    const { component, host } = await render({ machines: NEVER });

    expect(machineFilter(host)).toBeNull();
    expect(host.querySelector('[data-testid="machine-filter-error"]')).toBeNull();
    expect(host.querySelector('[data-testid="machine-filter-loading"]')).not.toBeNull();
    expect(component.machinesLoading).toBe(true);
  });
});
