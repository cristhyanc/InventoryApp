import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { Machine } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { MachineService } from '../../services/machine.service';
import {
  OperatingExpense,
  OperatingExpenseCategory,
  OperatingExpenseService
} from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { OperatingExpenseComponent } from './operating-expense.component';

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

function notConnected(): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: { code: 'nayax_not_connected', nayaxConnectionStatus: 'NotConfigured' }
  });
}

async function render(options: {
  status?: SpecConnectionStatus;
  machines?: Observable<Machine[]>;
  expenses?: OperatingExpense[];
} = {}) {
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
        provide: MachineService,
        useValue: {
          getAll: jest.fn(
            () => options.machines ?? of([{ machineID: 9, localMachineId: 41, machineName: 'Lobby' } as Machine])
          )
        }
      },
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
    machineService: TestBed.inject(MachineService) as unknown as { getAll: jest.Mock }
  };
}

/** The categories the **form** offers, in order. */
function formCategoryLabels(component: OperatingExpenseComponent): string[] {
  return component.formCategories.map((category) => component.categoryLabel(category));
}

const subtitle = (host: HTMLElement) =>
  host.querySelector('[data-testid="expenses-subtitle"]')?.textContent?.trim();

afterEach(() => TestBed.resetTestingModule());

/**
 * Operating Expenses without a usable provider (issue #728). The machine field follows the machine
 * list; the Nayax monthly fee and the subtitle follow whether Nayax is part of this business at
 * all; and an already-recorded Nayax fee is never taken away from the expense that carries it.
 */
describe('OperatingExpenseComponent provider-optional form (issue #728)', () => {
  it('offers the machine field, the Nayax fee category and the Nayax subtitle when connected', async () => {
    const { component, host } = await render();
    component.startCreate();

    expect(component.machineFieldShown).toBe(true);
    expect(formCategoryLabels(component)).toContain('Nayax Monthly Fee');
    expect(subtitle(host)).toBe(
      'Manual expenses only. Nayax processing, commissions, delivery and packaging remain separately sourced.'
    );
  });

  it('drops the Nayax fee from a new expense and the Nayax wording from the subtitle without a provider', async () => {
    const { component, host } = await render({ status: 'NotConfigured' });
    component.startCreate();

    expect(formCategoryLabels(component)).not.toContain('Nayax Monthly Fee');
    expect(formCategoryLabels(component)).toContain('Insurance');
    expect(subtitle(host)).toBe('Manual expenses only. Commissions, delivery and packaging remain separately sourced.');
  });

  it('keeps the Nayax fee on an existing expense already recorded under it', async () => {
    const recorded = expense({ category: OperatingExpenseCategory.NayaxMonthlyFee });
    const { component } = await render({ status: 'NotConfigured', expenses: [recorded] });

    component.startEdit(recorded);

    expect(formCategoryLabels(component)).toContain('Nayax Monthly Fee');
    expect(component.form.category).toBe(OperatingExpenseCategory.NayaxMonthlyFee);
  });

  it('keeps the Nayax fee available for a connection that needs attention: the account still bills', async () => {
    const { component, host } = await render({ status: 'NeedsAttention' });
    component.startCreate();

    expect(formCategoryLabels(component)).toContain('Nayax Monthly Fee');
    expect(subtitle(host)).toContain('Nayax processing');
  });

  it('keeps the Nayax fee in the filter, because recorded expenses still carry it', async () => {
    const { component } = await render({ status: 'NotConfigured' });

    expect(component.categories.map((category) => component.categoryLabel(category))).toContain('Nayax Monthly Fee');
  });

  it('hides the machine field without a provider, and asks for no machines', async () => {
    const { component, host, machineService } = await render({ status: 'NotConfigured' });
    component.startCreate();

    expect(machineService.getAll).not.toHaveBeenCalled();
    expect(component.machineFieldShown).toBe(false);
    expect(component.machinesError).toBe('');
    expect(host.querySelector('[data-testid="machine-field-error"]')).toBeNull();
  });

  it('hides the machine field when the machine read itself is refused', async () => {
    const { component } = await render({ status: 'unreadable', machines: throwError(() => notConnected()) });

    expect(component.machineFieldShown).toBe(false);
    expect(component.machinesError).toBe('');
  });

  /**
   * Issue #780: an expense stores the business's own machine record, so the option's value is the
   * machine's `localMachineId` and not the provider's `machineID`, and a listed machine with no
   * local record is not offered at all - there is nothing to store for it.
   */
  it('offers the local machine id as the machine option value', async () => {
    const { component, fixture, host } = await render();
    component.startCreate();
    fixture.detectChanges();

    // The machine is offered, under its own name, and what it carries is the local id.
    expect(Array.from(host.querySelectorAll('option')).map((option) => option.textContent?.trim())).toContain('Lobby');
    expect(component.machineOptions.map((machine) => machine.machineID)).toEqual([9]);
    expect(component.machineOptions.map((machine) => machine.localMachineId)).toEqual([41]);

    // Selecting it puts the local id - not the provider's number 9 - on the submitted form.
    component.form.machineId = component.machineOptions[0].localMachineId ?? null;
    expect(component.form.machineId).toBe(41);
  });

  it('hides the machine field when no listed machine has a local record', async () => {
    const { component } = await render({ machines: of([{ machineID: 9, machineName: 'Lobby' } as Machine]) });

    expect(component.machines).toHaveLength(1);
    expect(component.machineOptions).toEqual([]);
    expect(component.machineFieldShown).toBe(false);
    expect(component.machinesError).toBe('');
  });

  it('reports an error in place of the machine field when the machine read failed', async () => {
    const { component, fixture, host } = await render({
      machines: throwError(() => new HttpErrorResponse({ status: 500 }))
    });
    component.startCreate();
    fixture.detectChanges();

    expect(component.machinesError).toContain('The machine list could not be loaded');
    expect(host.querySelector('[data-testid="machine-field-error"]')?.textContent).toContain(
      'The machine list could not be loaded'
    );
  });
});
