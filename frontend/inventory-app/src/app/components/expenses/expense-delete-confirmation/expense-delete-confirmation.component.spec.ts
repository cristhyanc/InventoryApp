import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { OperatingExpense, OperatingExpenseCategory } from '../../../services/operating-expense.service';
import { ExpenseDeleteConfirmationComponent } from './expense-delete-confirmation.component';

function expense(overrides: Partial<OperatingExpense> = {}): OperatingExpense {
  return {
    id: 1,
    expenseDate: '2026-01-15T00:00:00',
    category: OperatingExpenseCategory.Insurance,
    description: 'Annual policy',
    amountExGst: 100,
    gstAmount: 10,
    totalAmount: 110,
    ...overrides
  };
}

/** A host with a button to open from, so focus restoration has somewhere real to return to. */
@Component({
  standalone: true,
  imports: [ExpenseDeleteConfirmationComponent],
  template: `
    <button type="button" id="trigger">Delete</button>
    <app-expense-delete-confirmation
      [expense]="pending"
      (confirmed)="confirmed = confirmed + 1"
      (canceled)="pending = null"
    ></app-expense-delete-confirmation>
  `
})
class HostComponent {
  pending: OperatingExpense | null = null;
  confirmed = 0;
}

async function render() {
  await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  // The fixture has to be in the document for focus to move at all.
  document.body.appendChild(host);

  const trigger = host.querySelector('#trigger') as HTMLButtonElement;
  const open = (pending: OperatingExpense) => {
    trigger.focus();
    fixture.componentInstance.pending = pending;
    fixture.detectChanges();
  };
  const buttons = () =>
    Array.from(host.querySelectorAll('.confirm-modal button')) as HTMLButtonElement[];
  const named = (label: string) => buttons().find((button) => button.textContent?.trim() === label)!;

  return { fixture, host, trigger, open, buttons, named, component: fixture.componentInstance };
}

afterEach(() => TestBed.resetTestingModule());

/**
 * The register's delete confirmation (issue #1170): the shared `confirmation-dialog`, named for the
 * expense it would delete, with the keyboard behaviour that dialog does not own itself - an initial
 * focus inside it, Tab contained within it, Escape to cancel, and focus returned to whatever opened
 * it. The shared component is unchanged (changing it is out of scope for issue #1170).
 */
describe('ExpenseDeleteConfirmationComponent (issue #1170)', () => {
  it('renders nothing until an expense is pending', async () => {
    const { host } = await render();

    expect(host.querySelector('.confirm-modal')).toBeNull();
  });

  it('names the expense by description, date and total', async () => {
    const { host, open } = await render();

    open(expense({ description: 'Public liability insurance', totalAmount: 1200 }));

    const message = host.querySelector('.confirm-modal p')?.textContent ?? '';
    expect(message).toContain('Public liability insurance');
    expect(message).toContain('2026-01-15');
    expect(message).toContain('$1,200.00');
  });

  it('puts focus inside the dialog, on Cancel rather than on Delete', async () => {
    const { open, named } = await render();

    open(expense());

    expect(document.activeElement).toBe(named('Cancel'));
  });

  it('keeps Tab inside the dialog, forwards and backwards', async () => {
    const { host, open, named } = await render();
    open(expense());
    const dialog = host.querySelector('.confirm-modal') as HTMLElement;

    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    expect(document.activeElement).toBe(named('Delete'));

    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    expect(document.activeElement).toBe(named('Cancel'));

    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true }));
    expect(document.activeElement).toBe(named('Delete'));
  });

  it('cancels on Escape, deleting nothing, and returns focus to what opened it', async () => {
    const { fixture, host, open, trigger, component } = await render();
    open(expense());

    (host.querySelector('.confirm-modal') as HTMLElement).dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })
    );
    fixture.detectChanges();

    expect(component.confirmed).toBe(0);
    expect(component.pending).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('returns focus to what opened it when Cancel is chosen', async () => {
    const { fixture, open, named, trigger, component } = await render();
    open(expense());

    named('Cancel').click();
    fixture.detectChanges();

    expect(component.confirmed).toBe(0);
    expect(document.activeElement).toBe(trigger);
  });

  it('reports the confirmation once, and leaves the focus after a delete to the page', async () => {
    const { fixture, open, named, trigger, component } = await render();
    open(expense());

    named('Delete').click();
    component.pending = null;
    fixture.detectChanges();

    expect(component.confirmed).toBe(1);
    // The row the trigger belonged to is about to disappear, so the page decides where focus goes.
    expect(document.activeElement).not.toBe(trigger);
  });
});
