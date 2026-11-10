import { TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';
import { OperatingExpenseComponent } from './operating-expense.component';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { LocalMachineService } from '../../services/local-machine.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  FakeImageDecoding,
  fileOfSize,
  installFakeImageDecoding
} from '../shared/image-upload-preparation.spec-helpers';

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

/**
 * Shrinking and converting a photo in the browser before an expense's supporting document is
 * uploaded (issue #1161), through the same shared helper the purchase uploads use.
 */
describe('OperatingExpenseComponent supporting-document preparation (issue #1161)', () => {
  let decoding: FakeImageDecoding | null = null;

  afterEach(() => {
    decoding?.restore();
    decoding = null;
  });

  /** The change event the file input raises, with one chosen file. */
  function choose(component: OperatingExpenseComponent, file: File): Promise<void> {
    return component.onAttachmentSelected({ target: { files: [file] } } as unknown as Event);
  }

  it('uploads the shrunk photo rather than the 5 MB one that was chosen', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
    const { component, create } = await render();

    component.startCreate();
    component.form.description = 'Repair call-out';
    await choose(component, fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg'));
    component.save();

    expect(create).toHaveBeenCalledTimes(1);
    const sent = create.mock.calls[0][1] as File;
    expect(sent.size).toBe(450_000);
    expect(sent.type).toBe('image/jpeg');
  });

  it('converts a HEIC photo the browser can decode into a .jpg', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 3024, height: 4032 }, encodedBytes: 400_000 });
    const { component, create } = await render();

    component.startCreate();
    component.form.description = 'Insurance';
    await choose(component, fileOfSize(3_900_000, 'IMG_0007.HEIC'));
    component.save();

    expect((create.mock.calls[0][1] as File).name).toBe('IMG_0007.jpg');
  });

  it('uploads the original HEIC unchanged when the browser cannot decode it', async () => {
    decoding = installFakeImageDecoding({ decoded: 'undecodable' });
    const { component, create } = await render();
    const heic = fileOfSize(3_400_000, 'IMG_0008.heic', 'image/heic');

    component.startCreate();
    component.form.description = 'Insurance';
    await choose(component, heic);
    component.save();

    expect(create.mock.calls[0][1]).toBe(heic);
  });

  it('leaves a PDF alone', async () => {
    decoding = installFakeImageDecoding();
    const { component, create } = await render();
    const pdf = fileOfSize(3 * 1024 * 1024, 'tax-invoice.pdf', 'application/pdf');

    component.startCreate();
    component.form.description = 'Accounting';
    await choose(component, pdf);
    component.save();

    expect(create.mock.calls[0][1]).toBe(pdf);
    expect(decoding.decodeCalls).toEqual([]);
  });

  it('saves nothing while the photo is still being prepared, and says so', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
    const { fixture, component, host, create } = await render();

    component.startCreate();
    component.form.description = 'Repair call-out';
    const selection = choose(component, fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg'));
    fixture.detectChanges();

    expect(component.attachmentPreparing).toBe(true);
    expect(host.querySelector('[data-testid="expense-attachment-preparing"]')).not.toBeNull();
    component.save();
    expect(create).not.toHaveBeenCalled();

    await selection;
    fixture.detectChanges();

    expect(component.attachmentPreparing).toBe(false);
    expect(host.querySelector('[data-testid="expense-attachment-preparing"]')).toBeNull();
    component.save();
    expect(create).toHaveBeenCalledTimes(1);
  });

  it('forgets a prepared photo when the form is cancelled', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
    const { component } = await render();

    component.startCreate();
    await choose(component, fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg'));
    expect(component.selectedAttachment).not.toBeNull();

    component.cancel();

    expect(component.selectedAttachment).toBeNull();
  });
});

/**
 * A preparation belongs to the selection that started it (issue #1161). Shrinking a photo is
 * asynchronous and this page edits every expense through one shared `selectedAttachment`, so a
 * photo chosen for one expense could otherwise finish after Cancel, Add Expense or Edit Expense had
 * moved the form on - and attach that receipt to a different expense, or clear the preparing state
 * guarding a newer photo so that Save ran before it existed. Each of these specs holds the decode
 * open to put the completion exactly where the defect was.
 */
describe('OperatingExpenseComponent supporting-document preparation outliving its selection (issue #1161)', () => {
  let decoding: FakeImageDecoding | null = null;

  afterEach(() => {
    decoding?.restore();
    decoding = null;
  });

  /** A deferred preparation: nothing finishes until the spec says which decode may. */
  function installDeferredPreparation(): FakeImageDecoding {
    decoding = installFakeImageDecoding({
      decoded: { width: 4000, height: 3000 },
      encodedBytes: 450_000,
      completion: 'deferred'
    });
    return decoding;
  }

  function choose(component: OperatingExpenseComponent, name: string): Promise<void> {
    const file = fileOfSize(5 * 1024 * 1024, name, 'image/jpeg');
    return component.onAttachmentSelected({ target: { files: [file] } } as unknown as Event);
  }

  it('never uploads a photo chosen for one expense to the expense Edit Expense moved to', async () => {
    const deferred = installDeferredPreparation();
    const vanService = expense({ id: 7, description: 'Van service' });
    const liability = expense({ id: 9, description: 'Public liability' });
    const { component, update } = await render({ expenses: [vanService, liability] });

    component.startEdit(vanService);
    const vanPhoto = choose(component, 'IMG_VAN.jpg');
    expect(component.attachmentPreparing).toBe(true);

    component.startEdit(liability);
    deferred.releaseDecode(0);
    await vanPhoto;

    expect(component.selectedAttachment).toBeNull();
    expect(component.attachmentPreparing).toBe(false);

    component.save();

    expect(update).toHaveBeenCalledTimes(1);
    expect(update.mock.calls[0][0]).toBe(9);
    expect(update.mock.calls[0][2]).toBeNull();
  });

  it('never uploads a photo chosen for an expense being edited to the one Add Expense opened', async () => {
    const deferred = installDeferredPreparation();
    const vanService = expense({ id: 7, description: 'Van service' });
    const { component, create, update } = await render({ expenses: [vanService] });

    component.startEdit(vanService);
    const vanPhoto = choose(component, 'IMG_VAN.jpg');

    component.startCreate();
    deferred.releaseDecode(0);
    await vanPhoto;

    expect(component.selectedAttachment).toBeNull();
    expect(component.attachmentPreparing).toBe(false);

    component.form.description = 'Stationery';
    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(create).toHaveBeenCalledTimes(1);
    expect(create.mock.calls[0][1]).toBeNull();
  });

  it('never attaches a photo that finished after Cancel to the next expense', async () => {
    const deferred = installDeferredPreparation();
    const { component, create } = await render();

    component.startCreate();
    const abandonedPhoto = choose(component, 'IMG_ABANDONED.jpg');

    component.cancel();
    deferred.releaseDecode(0);
    await abandonedPhoto;

    expect(component.editing).toBeNull();
    expect(component.selectedAttachment).toBeNull();
    expect(component.attachmentPreparing).toBe(false);

    component.startCreate();
    component.form.description = 'Stationery';
    component.save();

    expect(create).toHaveBeenCalledTimes(1);
    expect(create.mock.calls[0][1]).toBeNull();
  });

  it('keeps the newer photo preparing when an earlier one finishes first', async () => {
    const deferred = installDeferredPreparation();
    const { component, create } = await render();

    component.startCreate();
    component.form.description = 'Repair call-out';
    const firstPhoto = choose(component, 'IMG_FIRST.jpg');
    const secondPhoto = choose(component, 'IMG_SECOND.jpg');

    deferred.releaseDecode(0);
    await firstPhoto;

    // The first photo no longer belongs to the selection, so it may neither be attached nor
    // report that the second photo - still being shrunk - is ready to save.
    expect(component.selectedAttachment).toBeNull();
    expect(component.attachmentPreparing).toBe(true);
    component.save();
    expect(create).not.toHaveBeenCalled();

    deferred.releaseDecode(1);
    await secondPhoto;

    expect(component.attachmentPreparing).toBe(false);
    expect(component.selectedAttachment?.name).toBe('IMG_SECOND.jpg');

    component.save();

    expect(create).toHaveBeenCalledTimes(1);
    expect((create.mock.calls[0][1] as File).name).toBe('IMG_SECOND.jpg');
  });

  it('does not let an earlier photo replace the one the current selection already prepared', async () => {
    const deferred = installDeferredPreparation();
    const { component, create } = await render();

    component.startCreate();
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

    component.save();

    expect((create.mock.calls[0][1] as File).name).toBe('IMG_SECOND.jpg');
  });
});
