import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
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
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap: of(convertToParamMap({})) } },
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

const subtitle = (host: HTMLElement) =>
  host.querySelector('[data-testid="expenses-subtitle"]')?.textContent?.trim();

afterEach(() => TestBed.resetTestingModule());

/**
 * The expense register without a usable provider (issue #728): the subtitle follows whether Nayax is
 * part of this business at all, and the Nayax monthly fee stays a category the register can show and
 * filter by, because recorded expenses still carry it.
 *
 * Which categories a **new or edited** expense may be recorded under moved to the Add/Edit expense
 * form in issue #1167; `expense-form.component.provider-optional.spec.ts` covers it there. The
 * **machine** filter no longer follows the provider at all: since issue #746 it lists the business's
 * own machine records, and `operating-expense.component.local-machines.spec.ts` covers it.
 */
describe('OperatingExpenseComponent provider-optional register (issue #728)', () => {
  it('shows the Nayax subtitle when connected', async () => {
    const { host } = await render();

    expect(subtitle(host)).toBe(
      'Manual expenses only. Nayax processing, commissions, delivery and packaging remain separately sourced.'
    );
  });

  it('drops the Nayax wording from the subtitle without a provider', async () => {
    const { host } = await render({ status: 'NotConfigured' });

    expect(subtitle(host)).toBe('Manual expenses only. Commissions, delivery and packaging remain separately sourced.');
  });

  it('keeps the Nayax subtitle for a connection that needs attention: the account still bills', async () => {
    const { host } = await render({ status: 'NeedsAttention' });

    expect(subtitle(host)).toContain('Nayax processing');
  });

  it('keeps the Nayax fee in the filter, because recorded expenses still carry it', async () => {
    const { component } = await render({ status: 'NotConfigured' });

    expect(component.categories.map((category) => component.categoryLabel(category))).toContain('Nayax Monthly Fee');
  });

  it('still labels a recorded Nayax monthly fee in the register without a provider', async () => {
    const recorded = expense({ category: OperatingExpenseCategory.NayaxMonthlyFee });
    const { host } = await render({ status: 'NotConfigured', expenses: [recorded] });

    expect(host.querySelector('tbody')?.textContent).toContain('Nayax Monthly Fee');
  });
});
