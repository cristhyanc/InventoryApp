import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { LocalMachine } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
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

async function render(options: {
  status?: SpecConnectionStatus;
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
        provide: LocalMachineService,
        useValue: {
          getAll: jest.fn(() => of([{ id: 41, name: 'Lobby', isActive: true, isProviderNamed: false } as LocalMachine]))
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
    host: fixture.nativeElement as HTMLElement
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
 * Operating Expenses without a usable provider (issue #728). The Nayax monthly fee and the subtitle
 * follow whether Nayax is part of this business at all, and an already-recorded Nayax fee is never
 * taken away from the expense that carries it.
 *
 * The **machine** field no longer follows the provider at all: since issue #746 it lists the
 * business's own machine records, and `operating-expense.component.local-machines.spec.ts` covers
 * it.
 */
describe('OperatingExpenseComponent provider-optional form (issue #728)', () => {
  it('offers the Nayax fee category and the Nayax subtitle when connected', async () => {
    const { component, host } = await render();
    component.startCreate();

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

  it("keeps the machine field whatever the connection says: the machines are this business's own", async () => {
    const { component } = await render({ status: 'NotConfigured' });
    component.startCreate();

    expect(component.machineFieldShown).toBe(true);
    expect(component.machinesError).toBe('');
  });
});
