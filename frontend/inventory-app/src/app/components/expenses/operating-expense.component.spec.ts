import { TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';
import { OperatingExpenseComponent } from './operating-expense.component';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { LocalMachineService } from '../../services/local-machine.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { SupportSessionService } from '../../support-session/support-session.service';

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

async function render(overrides: {
  sessionActive?: Observable<boolean>;
  create?: jest.Mock;
  update?: jest.Mock;
  remove?: jest.Mock;
  expenses?: OperatingExpense[];
} = {}) {
  const create = overrides.create ?? jest.fn(() => of(expense()));
  const update = overrides.update ?? jest.fn(() => of(expense()));
  const remove = overrides.remove ?? jest.fn(() => of(undefined));

  await TestBed.configureTestingModule({
    imports: [OperatingExpenseComponent],
    providers: [
      {
        provide: OperatingExpenseService,
        useValue: {
          getAll: jest.fn(() => of(overrides.expenses ?? [expense()])),
          create,
          update,
          delete: remove
        }
      },
      { provide: SupplierService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(OperatingExpenseComponent);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, create, update, remove };
}

afterEach(() => TestBed.resetTestingModule());

/**
 * Hiding expense write controls and guarding their handlers during a support session (issue #659,
 * extending #554). A session grants `ExpensesManage`, so this page's controls would otherwise
 * still render and its handlers would otherwise still call the API.
 */
describe('OperatingExpenseComponent write controls during a support session (issue #659)', () => {
  it('hides Add Expense, Edit and Delete while a session is active', async () => {
    const { host } = await render({ sessionActive: of(true) });

    const buttonText = Array.from(host.querySelectorAll('button')).map((b) => b.textContent?.trim());
    expect(buttonText).not.toContain('Add Expense');
    expect(buttonText).not.toContain('Edit');
    expect(buttonText).not.toContain('Delete');
  });

  it('shows Add Expense, Edit and Delete while no session is active', async () => {
    const { host } = await render({ sessionActive: of(false) });

    const buttonText = Array.from(host.querySelectorAll('button')).map((b) => b.textContent?.trim());
    expect(buttonText).toContain('Add Expense');
    expect(buttonText).toContain('Edit');
    expect(buttonText).toContain('Delete');
  });

  it('disables Save while a session is active and editing is forced open', async () => {
    const { fixture, component, host } = await render({ sessionActive: of(true) });

    component.editing = { ...expense(), id: 0 };
    fixture.detectChanges();

    const saveButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Save') as HTMLButtonElement;
    expect(saveButton.disabled).toBe(true);
  });

  it('does not create, edit, save or delete while a session is active, even if the handlers are invoked directly', async () => {
    const { component, create, update, remove } = await render({ sessionActive: of(true) });

    component.startCreate();
    expect(component.editing).toBeNull();

    component.startEdit(expense());
    expect(component.editing).toBeNull();

    component.form.description = 'Should not save';
    component.save();
    expect(create).not.toHaveBeenCalled();
    expect(update).not.toHaveBeenCalled();

    component.remove(expense());
    expect(remove).not.toHaveBeenCalled();
  });

  it('creates, edits and deletes normally while no session is active', async () => {
    const { component, create, remove } = await render({ sessionActive: of(false) });

    component.startCreate();
    expect(component.editing).not.toBeNull();

    component.form.description = 'New expense';
    component.save();
    expect(create).toHaveBeenCalled();

    jest.spyOn(window, 'confirm').mockReturnValue(true);
    component.remove(expense());
    expect(remove).toHaveBeenCalledWith(1);
  });
});
