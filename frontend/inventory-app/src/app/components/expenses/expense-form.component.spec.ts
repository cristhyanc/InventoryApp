import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Supplier } from '../../models/models';
import { OperatingExpenseCategory } from '../../services/operating-expense.service';
import {
  FakeImageDecoding,
  fileOfSize,
  installFakeImageDecoding
} from '../shared/image-upload-preparation.spec-helpers';
import {
  EXPENSE_ATTACHMENT_ACCEPT,
  ExpenseFormComponent,
  ExpenseFormValue,
  ExpenseSaveRequest,
  newExpenseFormValue
} from './expense-form.component';

/** `value`'s calendar date in the browser's own local timezone, as `yyyy-MM-dd`. */
function localDateString(value: Date): string {
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`;
}

/** A stored expense as the Edit page hands it to the form, with every carried-through field set. */
function storedValue(overrides: Partial<ExpenseFormValue['payload']> = {}): ExpenseFormValue {
  return {
    payload: {
      expenseDate: '2026-01-15',
      category: OperatingExpenseCategory.Insurance,
      description: 'Annual policy',
      amountExGst: 100,
      gstAmount: 10,
      totalAmount: 110,
      supplierId: 3,
      siteId: 12,
      machineId: 41,
      servicePeriodStart: '2026-02-01T00:00:00Z',
      servicePeriodEnd: '2027-01-31T00:00:00Z',
      notes: 'Renewed',
      ...overrides
    },
    recordedMachineId: 41,
    attachmentFileName: null
  };
}

async function render(
  inputs: {
    mode?: 'create' | 'edit';
    value?: ExpenseFormValue;
    suppliers?: Supplier[];
    saving?: boolean;
    readOnly?: boolean;
  } = {}
) {
  await TestBed.configureTestingModule({
    imports: [ExpenseFormComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(ExpenseFormComponent);
  fixture.componentRef.setInput('mode', inputs.mode ?? 'create');
  fixture.componentRef.setInput('value', inputs.value ?? newExpenseFormValue());
  fixture.componentRef.setInput('suppliers', inputs.suppliers ?? []);
  fixture.componentRef.setInput('saving', inputs.saving ?? false);
  fixture.componentRef.setInput('readOnly', inputs.readOnly ?? false);

  const saved: ExpenseSaveRequest[] = [];
  fixture.componentInstance.saveRequested.subscribe((request) => saved.push(request));
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, saved };
}

const submitButton = (host: HTMLElement) =>
  host.querySelector('[data-testid="expense-form-submit"]') as HTMLButtonElement | null;
const sectionHeadings = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('h2')).map((heading) => heading.textContent?.trim());
const fieldLabels = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('.field-label')).map((label) => label.textContent?.trim());

afterEach(() => TestBed.resetTestingModule());

/**
 * The one expense form Add expense and Edit expense share (issue #1167). It carries the register
 * card's behaviour forward unchanged, so these specs pin the rules that behaviour consisted of
 * rather than the layout it is now drawn in.
 */
describe('ExpenseFormComponent layout and labels (issue #1167)', () => {
  it('shows the three sections in order', async () => {
    const { host } = await render();

    expect(sectionHeadings(host)).toEqual(['Expense details', 'Amounts', 'Supporting details']);
  });

  it('keeps the field and option labels the register card used', async () => {
    const { host } = await render({ suppliers: [{ id: 3, name: 'Acme Insurance' } as Supplier] });

    expect(fieldLabels(host)).toEqual([
      'Date',
      'Category',
      'Description *',
      'Amount ex GST',
      'GST',
      'Total',
      'Supplier',
      'Supporting document',
      'Notes'
    ]);
    const supplierOptions = Array.from(host.querySelectorAll('#expense-supplier option')).map((o) => o.textContent?.trim());
    expect(supplierOptions).toEqual(['Whole business', 'Acme Insurance']);
  });

  it('opens a new expense on today, category Other and three zero amounts', async () => {
    const { component } = await render();

    expect(component.form.expenseDate).toBe(localDateString(new Date()));
    expect(component.form.category).toBe(OperatingExpenseCategory.Other);
    expect(component.form.amountExGst).toBe(0);
    expect(component.form.gstAmount).toBe(0);
    expect(component.form.totalAmount).toBe(0);
    expect(component.form.supplierId).toBeNull();
    expect(component.form.machineId).toBeNull();
  });

  it('names the primary action for what it would do, and offers no Delete', async () => {
    const { host: addHost } = await render({ mode: 'create' });
    expect(submitButton(addHost)?.textContent?.trim()).toBe('Save expense');

    TestBed.resetTestingModule();
    const { host: editHost } = await render({ mode: 'edit', value: storedValue() });
    expect(submitButton(editHost)?.textContent?.trim()).toBe('Save changes');
    expect(Array.from(editHost.querySelectorAll('button')).map((b) => b.textContent?.trim())).not.toContain('Delete');
  });

  it('offers Cancel back to the register, carrying the return context', async () => {
    const { fixture, host } = await render();
    fixture.componentRef.setInput('returnQueryParams', { from: '2026-01-01', supplierId: '3' });
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="expense-form-cancel"]')?.getAttribute('href')).toBe(
      '/expenses?from=2026-01-01&supplierId=3'
    );
  });
});

/**
 * A new expense's default date is today in the business's own time zone, never the UTC date (issue
 * #1171): in a zone ahead of UTC, such as Sydney, the UTC date is still yesterday until mid-morning.
 */
describe("newExpenseFormValue's default date (issue #1171)", () => {
  it("uses the business's calendar date even where that is a day ahead of the UTC date", () => {
    // 2026-11-10 21:30 UTC is 2026-11-11 08:30 in Sydney (UTC+11 in November, daylight saving).
    const instant = new Date('2026-11-10T21:30:00Z');

    const value = newExpenseFormValue('Australia/Sydney', instant);

    expect(value.payload.expenseDate).toBe('2026-11-11');
  });

  it("uses the business's calendar date even where that is a day behind the UTC date", () => {
    // 2026-11-10 21:30 UTC is 2026-11-10 13:30 in Los Angeles (UTC-8 in November).
    const instant = new Date('2026-11-10T21:30:00Z');

    const value = newExpenseFormValue('America/Los_Angeles', instant);

    expect(value.payload.expenseDate).toBe('2026-11-10');
  });

  it('falls back to the browser\'s own local date, never the UTC date, while the zone is not known yet', () => {
    // The same instant as above, with no business time zone published.
    const instant = new Date('2026-11-10T21:30:00Z');

    const value = newExpenseFormValue(null, instant);

    expect(value.payload.expenseDate).toBe(localDateString(instant));
  });
});

/**
 * The three amounts are independent inputs (issue #1167): the amounts on a supplier's document are
 * the record, so nothing here calculates, rounds or warns, and what is typed is what is submitted.
 */
describe('ExpenseFormComponent amounts (issue #1167)', () => {
  it('leaves the other two amounts alone when one is typed', async () => {
    const { component } = await render();

    component.form.amountExGst = 42.5;

    expect(component.form.gstAmount).toBe(0);
    expect(component.form.totalAmount).toBe(0);
  });

  it('submits the three amounts exactly as entered, mismatch and all', async () => {
    const { component, saved } = await render();

    component.form.description = 'Repair call-out';
    component.form.amountExGst = 100;
    component.form.gstAmount = 3.33;
    component.form.totalAmount = 120;
    component.submit();

    expect(saved).toHaveLength(1);
    expect(saved[0].payload.amountExGst).toBe(100);
    expect(saved[0].payload.gstAmount).toBe(3.33);
    expect(saved[0].payload.totalAmount).toBe(120);
  });
});

/**
 * The one refusal this form makes itself. The register card dropped a blank Description silently;
 * now it says so beside the field, and still sends nothing (issue #1167).
 */
describe('ExpenseFormComponent blank description (issue #1167)', () => {
  it('shows the refusal beside the input, linked to it, and submits nothing', async () => {
    const { fixture, host, saved } = await render();

    submitButton(host)!.click();
    fixture.detectChanges();

    const error = host.querySelector('[data-testid="expense-description-error"]');
    expect(error?.textContent?.trim()).toBe('Description is required.');
    const input = host.querySelector('#expense-description') as HTMLInputElement;
    expect(input.getAttribute('aria-describedby')).toBe(error?.id);
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(saved).toEqual([]);
  });

  it('treats whitespace as no description at all', async () => {
    const { component, saved } = await render();

    component.form.description = '   ';
    component.submit();

    expect(saved).toEqual([]);
  });

  it('says nothing before the primary action has been pressed', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="expense-description-error"]')).toBeNull();
    expect((host.querySelector('#expense-description') as HTMLInputElement).getAttribute('aria-describedby')).toBeNull();
  });

  it('clears the refusal once a description is entered', async () => {
    const { fixture, component, host, saved } = await render();

    submitButton(host)!.click();
    component.form.description = 'Stationery';
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="expense-description-error"]')).toBeNull();
    component.submit();
    expect(saved).toHaveLength(1);
  });
});

/**
 * The fields the form has no control for - `siteId` and the service period - are carried back
 * exactly as loaded (issue #1167). Editing a description must never clear an expense's site or the
 * period the cost covers.
 */
describe('ExpenseFormComponent carried-through fields (issue #1167)', () => {
  it('submits siteId, servicePeriodStart and servicePeriodEnd exactly as loaded', async () => {
    const { component, saved } = await render({ mode: 'edit', value: storedValue() });

    component.form.description = 'Annual policy (renewed)';
    component.submit();

    expect(saved[0].payload).toMatchObject({
      siteId: 12,
      servicePeriodStart: '2026-02-01T00:00:00Z',
      servicePeriodEnd: '2027-01-31T00:00:00Z'
    });
  });

  it('offers no control for any of them', async () => {
    const { host } = await render({ mode: 'edit', value: storedValue() });

    expect(fieldLabels(host)).not.toContain('Site');
    expect(host.querySelector('[name="siteId"]')).toBeNull();
    expect(host.querySelector('[name="servicePeriodStart"]')).toBeNull();
    expect(host.querySelector('[name="servicePeriodEnd"]')).toBeNull();
  });
});

/**
 * One request per press (issue #1167): the primary action is unavailable while the page's save is in
 * flight, so a double click cannot send a second create or update.
 */
describe('ExpenseFormComponent duplicate submission (issue #1167)', () => {
  it('disables the primary action while a save is in flight, and submits nothing if pressed anyway', async () => {
    const { fixture, component, host, saved } = await render();

    component.form.description = 'Repair call-out';
    fixture.componentRef.setInput('saving', true);
    fixture.detectChanges();

    expect(submitButton(host)!.disabled).toBe(true);
    expect(submitButton(host)!.textContent?.trim()).toBe('Saving...');
    component.submit();
    expect(saved).toEqual([]);
  });

  it('holds every field still while the save is in flight', async () => {
    const { fixture, host } = await render();

    fixture.componentRef.setInput('saving', true);
    fixture.detectChanges();

    expect((host.querySelector('[data-testid="expense-form-fields"]') as HTMLFieldSetElement).disabled).toBe(true);
  });
});

/**
 * A read-only expense (a support session, issue #659): the form is shown, nothing may be typed, and
 * there is no primary action at all. The page says why.
 */
describe('ExpenseFormComponent read-only (issue #1167)', () => {
  it('shows the expense with no primary action and every field held still', async () => {
    const { host } = await render({ mode: 'edit', value: storedValue(), readOnly: true });

    expect(submitButton(host)).toBeNull();
    expect((host.querySelector('[data-testid="expense-form-fields"]') as HTMLFieldSetElement).disabled).toBe(true);
    expect((host.querySelector('#expense-description') as HTMLInputElement).value).toBe('Annual policy');
  });

  it('submits nothing even if the handler is invoked directly', async () => {
    const { component, saved } = await render({ mode: 'edit', value: storedValue(), readOnly: true });

    component.form.description = 'Should not save';
    component.submit();

    expect(saved).toEqual([]);
  });
});

/**
 * The stored supporting document (issue #1167): Edit names it, offers it, and says plainly that
 * choosing a new file replaces it. There is no Remove - the API offers none.
 */
describe('ExpenseFormComponent stored supporting document (issue #1167)', () => {
  it("names the stored document, offers it, and explains the replacement", async () => {
    const value: ExpenseFormValue = { ...storedValue(), attachmentFileName: 'policy.pdf' };
    const { host } = await render({ mode: 'edit', value });

    expect(host.querySelector('[data-testid="expense-current-attachment"]')?.textContent).toContain('policy.pdf');
    expect(host.querySelector('[data-testid="expense-attachment-replacement-note"]')?.textContent).toContain(
      'Choosing a new file replaces the stored document.'
    );
  });

  it('asks the page for the document rather than linking to the protected address', async () => {
    const value: ExpenseFormValue = { ...storedValue(), attachmentFileName: 'policy.pdf' };
    const { component, host } = await render({ mode: 'edit', value });
    const asked = jest.fn();
    component.viewDocumentRequested.subscribe(asked);

    (host.querySelector('[data-testid="expense-view-document"]') as HTMLButtonElement).click();

    expect(asked).toHaveBeenCalledTimes(1);
    expect(host.querySelector('[data-testid="expense-current-attachment"] a')).toBeNull();
  });

  it('offers no document controls and no Remove for an expense that has none', async () => {
    const { host } = await render({ mode: 'edit', value: storedValue() });

    expect(host.querySelector('[data-testid="expense-current-attachment"]')).toBeNull();
    expect(Array.from(host.querySelectorAll('button')).map((b) => b.textContent?.trim())).not.toContain('Remove');
  });

  it('keeps the accept list the field has always offered', async () => {
    const { host } = await render();

    expect((host.querySelector('#expense-attachment') as HTMLInputElement).getAttribute('accept')).toBe(
      EXPENSE_ATTACHMENT_ACCEPT
    );
  });
});

/**
 * Shrinking and converting a photo in the browser before an expense's supporting document is
 * uploaded (issue #1161), through the same shared helper the purchase uploads use. These rules moved
 * here with the form in issue #1167; nothing about them changed.
 */
describe('ExpenseFormComponent supporting-document preparation (issue #1161)', () => {
  let decoding: FakeImageDecoding | null = null;

  afterEach(() => {
    decoding?.restore();
    decoding = null;
  });

  /** The change event the file input raises, with one chosen file. */
  function choose(component: ExpenseFormComponent, file: File): Promise<void> {
    return component.onAttachmentSelected({ target: { files: [file] } } as unknown as Event);
  }

  it('uploads the shrunk photo rather than the 5 MB one that was chosen', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
    const { component, saved } = await render();

    component.form.description = 'Repair call-out';
    await choose(component, fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg'));
    component.submit();

    expect(saved).toHaveLength(1);
    expect(saved[0].attachment?.size).toBe(450_000);
    expect(saved[0].attachment?.type).toBe('image/jpeg');
  });

  it('converts a HEIC photo the browser can decode into a .jpg', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 3024, height: 4032 }, encodedBytes: 400_000 });
    const { component, saved } = await render();

    component.form.description = 'Insurance';
    await choose(component, fileOfSize(3_900_000, 'IMG_0007.HEIC'));
    component.submit();

    expect(saved[0].attachment?.name).toBe('IMG_0007.jpg');
  });

  it('uploads the original HEIC unchanged when the browser cannot decode it', async () => {
    decoding = installFakeImageDecoding({ decoded: 'undecodable' });
    const { component, saved } = await render();
    const heic = fileOfSize(3_400_000, 'IMG_0008.heic', 'image/heic');

    component.form.description = 'Insurance';
    await choose(component, heic);
    component.submit();

    expect(saved[0].attachment).toBe(heic);
  });

  it('leaves a PDF alone', async () => {
    decoding = installFakeImageDecoding();
    const { component, saved } = await render();
    const pdf = fileOfSize(3 * 1024 * 1024, 'tax-invoice.pdf', 'application/pdf');

    component.form.description = 'Accounting';
    await choose(component, pdf);
    component.submit();

    expect(saved[0].attachment).toBe(pdf);
    expect(decoding.decodeCalls).toEqual([]);
  });

  it('submits nothing while the photo is still being prepared, says so, and disables the action', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
    const { fixture, component, host, saved } = await render();

    component.form.description = 'Repair call-out';
    const selection = choose(component, fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg'));
    fixture.detectChanges();

    expect(component.attachmentPreparing).toBe(true);
    expect(host.querySelector('[data-testid="expense-attachment-preparing"]')).not.toBeNull();
    expect(submitButton(host)!.disabled).toBe(true);
    component.submit();
    expect(saved).toEqual([]);

    await selection;
    fixture.detectChanges();

    expect(component.attachmentPreparing).toBe(false);
    expect(host.querySelector('[data-testid="expense-attachment-preparing"]')).toBeNull();
    expect(submitButton(host)!.disabled).toBe(false);
    component.submit();
    expect(saved).toHaveLength(1);
  });

  it('submits no attachment at all when none was chosen', async () => {
    const { component, saved } = await render({ mode: 'edit', value: storedValue() });

    component.submit();

    expect(saved[0].attachment).toBeNull();
  });
});

/**
 * A preparation belongs to the selection that started it (issue #1161). Shrinking a photo is
 * asynchronous, so a photo chosen for this expense can finish after another file - or a reload of
 * the expense after a conflicting save - has moved the form on, and must then neither replace the
 * newer selection's file nor clear the preparing state guarding it. Each of these specs holds the
 * decode open to put the completion exactly where the defect was.
 */
describe('ExpenseFormComponent preparation outliving its selection (issue #1161)', () => {
  let decoding: FakeImageDecoding | null = null;

  afterEach(() => {
    decoding?.restore();
    decoding = null;
  });

  function installDeferredPreparation(): FakeImageDecoding {
    decoding = installFakeImageDecoding({
      decoded: { width: 4000, height: 3000 },
      encodedBytes: 450_000,
      completion: 'deferred'
    });
    return decoding;
  }

  function choose(component: ExpenseFormComponent, name: string): Promise<void> {
    const file = fileOfSize(5 * 1024 * 1024, name, 'image/jpeg');
    return component.onAttachmentSelected({ target: { files: [file] } } as unknown as Event);
  }

  it('keeps the newer photo preparing when an earlier one finishes first', async () => {
    const deferred = installDeferredPreparation();
    const { component, saved } = await render();

    component.form.description = 'Repair call-out';
    const firstPhoto = choose(component, 'IMG_FIRST.jpg');
    const secondPhoto = choose(component, 'IMG_SECOND.jpg');

    deferred.releaseDecode(0);
    await firstPhoto;

    expect(component.selectedAttachment).toBeNull();
    expect(component.attachmentPreparing).toBe(true);
    component.submit();
    expect(saved).toEqual([]);

    deferred.releaseDecode(1);
    await secondPhoto;

    expect(component.attachmentPreparing).toBe(false);
    expect(component.selectedAttachment?.name).toBe('IMG_SECOND.jpg');
    component.submit();
    expect(saved).toHaveLength(1);
    expect(saved[0].attachment?.name).toBe('IMG_SECOND.jpg');
  });

  it('does not let an earlier photo replace the one the current selection already prepared', async () => {
    const deferred = installDeferredPreparation();
    const { component, saved } = await render();

    component.form.description = 'Repair call-out';
    const firstPhoto = choose(component, 'IMG_FIRST.jpg');
    const secondPhoto = choose(component, 'IMG_SECOND.jpg');

    deferred.releaseDecode(1);
    await secondPhoto;
    expect(component.selectedAttachment?.name).toBe('IMG_SECOND.jpg');

    deferred.releaseDecode(0);
    await firstPhoto;

    expect(component.selectedAttachment?.name).toBe('IMG_SECOND.jpg');
    expect(component.attachmentPreparing).toBe(false);
    component.submit();
    expect(saved[0].attachment?.name).toBe('IMG_SECOND.jpg');
  });

  it('never attaches a photo that finished after the expense was reloaded', async () => {
    const deferred = installDeferredPreparation();
    const { fixture, component, saved } = await render({ mode: 'edit', value: storedValue() });

    const abandonedPhoto = choose(component, 'IMG_ABANDONED.jpg');
    expect(component.attachmentPreparing).toBe(true);

    // What "Reload the expense" does after a conflicting save: the form opens afresh.
    fixture.componentRef.setInput('value', storedValue({ description: 'Annual policy, as stored now' }));
    deferred.releaseDecode(0);
    await abandonedPhoto;

    expect(component.selectedAttachment).toBeNull();
    expect(component.attachmentPreparing).toBe(false);
    component.submit();
    expect(saved[0].attachment).toBeNull();
  });
});

/**
 * Whether leaving would lose something (issue #1167). The leave guard asks only while it would, so
 * the question has to mean an actual edit rather than "this page was open".
 */
describe('ExpenseFormComponent unsaved work (issue #1167)', () => {
  let decoding: FakeImageDecoding | null = null;

  afterEach(() => {
    decoding?.restore();
    decoding = null;
  });

  it('is clean when nothing has been touched, on both a new and a stored expense', async () => {
    const { component } = await render();
    expect(component.dirty).toBe(false);

    TestBed.resetTestingModule();
    const { component: editing } = await render({ mode: 'edit', value: storedValue() });
    expect(editing.dirty).toBe(false);
  });

  it('is dirty once a field is edited', async () => {
    const { component } = await render({ mode: 'edit', value: storedValue() });

    component.form.totalAmount = 111;

    expect(component.dirty).toBe(true);
  });

  it('is dirty while a document is being prepared, and once one is chosen', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
    const { component } = await render({ mode: 'edit', value: storedValue() });

    const selection = component.onAttachmentSelected({
      target: { files: [fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg')] }
    } as unknown as Event);
    expect(component.dirty).toBe(true);

    await selection;

    expect(component.dirty).toBe(true);
  });

  it('is clean again once the expense is reopened', async () => {
    const { fixture, component } = await render({ mode: 'edit', value: storedValue() });
    component.form.description = 'Edited';
    expect(component.dirty).toBe(true);

    fixture.componentRef.setInput('value', storedValue());

    expect(component.dirty).toBe(false);
  });
});
