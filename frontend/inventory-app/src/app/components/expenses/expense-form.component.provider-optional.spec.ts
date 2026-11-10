import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OperatingExpenseCategory } from '../../services/operating-expense.service';
import { ExpenseFormComponent, ExpenseFormValue, newExpenseFormValue } from './expense-form.component';

/** A stored expense as the Edit page hands it to the form, recorded under `category`. */
function storedValue(category: OperatingExpenseCategory): ExpenseFormValue {
  return {
    payload: {
      expenseDate: '2026-01-15',
      category,
      description: 'Monthly billing',
      amountExGst: 100,
      gstAmount: 10,
      totalAmount: 110,
      supplierId: null,
      siteId: null,
      machineId: null,
      notes: null
    },
    recordedMachineId: null,
    attachmentFileName: null
  };
}

async function render(
  options: { value?: ExpenseFormValue; mode?: 'create' | 'edit'; providerRecordsAvailable?: boolean } = {}
) {
  await TestBed.configureTestingModule({
    imports: [ExpenseFormComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(ExpenseFormComponent);
  fixture.componentRef.setInput('mode', options.mode ?? 'create');
  fixture.componentRef.setInput('value', options.value ?? newExpenseFormValue());
  fixture.componentRef.setInput('providerRecordsAvailable', options.providerRecordsAvailable ?? true);
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

/** The categories the form offers, in order, as the dropdown renders them. */
const categoryOptionLabels = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('#expense-category option')).map((option) => option.textContent?.trim());

afterEach(() => TestBed.resetTestingModule());

/**
 * The expense form without a usable provider (issue #728). The Nayax monthly fee follows whether
 * Nayax is part of this business at all, and an already-recorded Nayax fee is never taken away from
 * the expense that carries it - dropping it would silently reclassify that expense the next time
 * somebody saved the form. These rules moved here from the register card with the form itself
 * (issue #1167); none of them changed.
 */
describe('ExpenseFormComponent provider-optional categories (issue #728)', () => {
  it('offers the Nayax fee category when Nayax is part of the business', async () => {
    const { host } = await render();

    expect(categoryOptionLabels(host)).toContain('Nayax Monthly Fee');
  });

  it('drops the Nayax fee from a new expense without a provider, and keeps every other category', async () => {
    const { component, host } = await render({ providerRecordsAvailable: false });

    expect(categoryOptionLabels(host)).not.toContain('Nayax Monthly Fee');
    expect(categoryOptionLabels(host)).toContain('Insurance');
    expect(categoryOptionLabels(host)).toContain('Other');
    expect(component.formCategories).toHaveLength(8);
  });

  it('keeps the Nayax fee on an existing expense already recorded under it', async () => {
    const { component, host } = await render({
      mode: 'edit',
      value: storedValue(OperatingExpenseCategory.NayaxMonthlyFee),
      providerRecordsAvailable: false
    });

    expect(categoryOptionLabels(host)).toContain('Nayax Monthly Fee');
    expect(component.form.category).toBe(OperatingExpenseCategory.NayaxMonthlyFee);
  });

  it('submits that recorded Nayax fee unchanged, so saving the form never reclassifies it', async () => {
    const { component } = await render({
      mode: 'edit',
      value: storedValue(OperatingExpenseCategory.NayaxMonthlyFee),
      providerRecordsAvailable: false
    });
    const saved: OperatingExpenseCategory[] = [];
    component.saveRequested.subscribe((request) => saved.push(request.payload.category));

    component.submit();

    expect(saved).toEqual([OperatingExpenseCategory.NayaxMonthlyFee]);
  });

  it('drops the Nayax fee again once an existing expense is moved to another category', async () => {
    const { fixture, component, host } = await render({
      mode: 'edit',
      value: storedValue(OperatingExpenseCategory.NayaxMonthlyFee),
      providerRecordsAvailable: false
    });

    component.form.category = OperatingExpenseCategory.Software;
    fixture.detectChanges();

    expect(categoryOptionLabels(host)).not.toContain('Nayax Monthly Fee');
  });
});
