import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, Observable, of, throwError } from 'rxjs';
import { SupplierListComponent } from './supplier-list.component';
import { SupplierFormDialogComponent } from './supplier-form-dialog/supplier-form-dialog.component';
import { SupplierService } from '../../services/supplier.service';
import { AccessService } from '../../access/access.service';
import { AvailableSupplierPreset, GstClassification, Supplier } from '../../models/models';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { ToastService } from '../../services/toast.service';

function supplier(overrides: Partial<Supplier> = {}): Supplier {
  return {
    id: 1,
    name: 'Acme Supplies',
    contactName: null,
    phone: null,
    email: null,
    address: null,
    url: null,
    // The state the API publishes for a supplier the business created itself (issue #934), which
    // is what every supplier was before that issue.
    isLinked: false,
    isHidden: false,
    canRename: true,
    canDelete: true,
    ...overrides
  };
}

/** A copy the business received from the platform's supplier list (issue #934). */
function linkedSupplier(overrides: Partial<Supplier> = {}): Supplier {
  return supplier({
    id: 7,
    name: 'Coles',
    url: 'https://www.coles.com.au',
    isLinked: true,
    canRename: false,
    canDelete: false,
    ...overrides
  });
}

/** One supplier from the platform's list this business may add (issue #935). */
function availablePreset(overrides: Partial<AvailableSupplierPreset> = {}): AvailableSupplierPreset {
  return { id: 12, name: 'JB Metro South Coast & ACT', url: 'https://jbmetro.test', ...overrides };
}

interface RenderOptions {
  /** Whether a read-only support session is active (issue #657). */
  sessionActive?: Observable<boolean>;

  /** Whether the signed-in role may change a supplier's GST defaults (issue #430/#528). */
  canManageGstDefaults?: boolean;

  /** Whether this tab is previewing another role (issue #721). */
  previewActive?: Observable<boolean>;

  listed?: Supplier[];

  /** What `GET /api/suppliers/available-presets` answers (issue #935). */
  available?: AvailableSupplierPreset[];

  /** What the add answers; an error exercises the refusal path. */
  addFromPreset?: jest.Mock;
}

async function renderWith(options: RenderOptions = {}) {
  const listed = options.listed ?? [supplier()];
  const create = jest.fn(() => of(supplier()));
  const update = jest.fn(() => of(supplier()));
  const deleteFn = jest.fn(() => of(undefined));
  const setNotUsed = jest.fn(() => of(undefined));
  const getAllIncludingHidden = jest.fn(() => of(listed));
  const getAll = jest.fn(() => of(listed));
  const getAvailablePresets = jest.fn(() => of(options.available ?? []));
  const addFromPreset = options.addFromPreset ?? jest.fn(() => of(supplier()));
  const getGstDefaults = jest.fn(() =>
    of({
      productLineGstDefault: GstClassification.Unknown,
      deliveryGstDefault: GstClassification.Unknown,
      packageGstDefault: GstClassification.Unknown
    })
  );
  const toasts = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  await TestBed.configureTestingModule({
    imports: [SupplierListComponent],
    providers: [
      {
        provide: SupplierService,
        useValue: {
          getAll,
          getAllIncludingHidden,
          create,
          update,
          delete: deleteFn,
          setNotUsed,
          getAvailablePresets,
          addFromPreset,
          getGstDefaults
        }
      },
      { provide: AccessService, useValue: { allows: () => of(options.canManageGstDefaults ?? true) } },
      { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } },
      { provide: RolePreviewService, useValue: { active$: options.previewActive ?? of(false) } },
      { provide: ToastService, useValue: toasts }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierListComponent);
  fixture.detectChanges();
  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    create,
    update,
    delete: deleteFn,
    setNotUsed,
    getAll,
    getAllIncludingHidden,
    getAvailablePresets,
    addFromPreset,
    getGstDefaults,
    toasts
  };
}

/**
 * The Add/Edit dialog this page composes (issue #1174). The page itself only says whether it is
 * open and for which supplier, so a test that is about the form drives the dialog's own instance -
 * which is also what proves the page is not holding that form state any more.
 */
function formDialogOf(fixture: ComponentFixture<SupplierListComponent>): SupplierFormDialogComponent {
  const dialog = fixture.debugElement.query(By.directive(SupplierFormDialogComponent));
  expect(dialog).not.toBeNull();
  return dialog.componentInstance as SupplierFormDialogComponent;
}

function buttonNamed(host: ParentNode, label: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === label);
}

async function render(sessionActive: Observable<boolean> = of(false), listed: Supplier[] = [supplier()]) {
  return renderWith({ sessionActive, listed });
}

afterEach(() => TestBed.resetTestingModule());

/**
 * Create/Update and Delete while a support session is active (issue #657, extending #554). A
 * session grants `PurchasingOperate`, so these would otherwise still render - disabling and hiding
 * them is this page's own job, not the capability check's.
 */
describe('SupplierListComponent write controls during a support session (issue #657)', () => {
  it('disables the dialog save and hides Delete while a session is active', async () => {
    const { fixture, host } = await render(of(true));

    fixture.componentInstance.addSupplier();
    fixture.detectChanges();

    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim().includes('Delete'));
    expect(deleteButton).toBeUndefined();
  });

  it('leaves the dialog save enabled and Delete shown while no session is active', async () => {
    const { fixture, host } = await render(of(false));

    fixture.componentInstance.addSupplier();
    fixture.detectChanges();

    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(false);

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim().includes('Delete'));
    expect(deleteButton).toBeDefined();
  });

  it('writes nothing on save while a session is active, even if the write method is invoked directly', async () => {
    const { fixture, create, update } = await render(of(true));

    fixture.componentInstance.addSupplier();
    fixture.detectChanges();
    const dialog = formDialogOf(fixture);
    dialog.form.name = 'New Supplier';
    dialog.save();

    expect(create).not.toHaveBeenCalled();
    expect(update).not.toHaveBeenCalled();
  });

  it('writes nothing on delete while a session is active, even if the write method is invoked directly', async () => {
    const { fixture, host, delete: deleteFn } = await render(of(true));

    fixture.componentInstance.askDelete(supplier());
    fixture.componentInstance.remove(supplier());
    fixture.detectChanges();

    expect(host.querySelector('app-confirmation-dialog')).toBeNull();
    expect(deleteFn).not.toHaveBeenCalled();
  });

  it('still saves and deletes once a session ends', async () => {
    const sessionActive = new BehaviorSubject(true);
    const { fixture, host, create } = await render(sessionActive);

    sessionActive.next(false);
    fixture.componentInstance.addSupplier();
    fixture.detectChanges();

    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(false);

    const dialog = formDialogOf(fixture);
    dialog.form.name = 'New Supplier';
    dialog.save();

    expect(create).toHaveBeenCalledTimes(1);
  });
});

/**
 * The optional website (issue #847) as this page shows it: the list links only to a value the
 * client rule still accepts. The form's own half of that rule is
 * `supplier-form-dialog.component.spec.ts` since issue #1174 moved the form into a dialog.
 */
describe('SupplierListComponent website URL (issue #847)', () => {
  it('renders a stored http(s) url as a link that opens safely', async () => {
    const { host } = await render(of(false), [supplier({ url: 'https://coles.com.au/trade' })]);

    const link = host.querySelector('tbody a') as HTMLAnchorElement;
    expect(link).not.toBeNull();
    expect(link.getAttribute('href')).toBe('https://coles.com.au/trade');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.textContent?.trim()).toBe('https://coles.com.au/trade');
  });

  /**
   * Defence in depth: a value the server rule would never have stored is still never clickable.
   */
  it.each(['javascript:alert(1)', 'data:text/html,x', 'coles.com.au', '/suppliers/1'])(
    'renders a stored %p as plain text and never as a link',
    async (stored) => {
      const { host } = await render(of(false), [supplier({ url: stored })]);

      expect(host.querySelector('tbody a')).toBeNull();
      expect(host.querySelector('tbody tr')?.textContent).toContain(stored);
    }
  );

  it('renders no link and no text for a supplier with no url', async () => {
    const { host } = await render(of(false), [supplier({ url: null })]);

    expect(host.querySelector('tbody a')).toBeNull();
  });
});

/**
 * The link state and the "Not used" switch (issue #934). Everything here reads the four fields the
 * API publishes from its one policy: the page renders the server's answer and never a rule of its
 * own, and the API refuses the write whatever this page shows.
 */
describe('SupplierListComponent linked suppliers and "Not used" (issue #934)', () => {
  it('asks for the hidden suppliers too, because this is where one is switched back on', async () => {
    const { getAllIncludingHidden, getAll } = await render();

    expect(getAllIncludingHidden).toHaveBeenCalledTimes(1);
    expect(getAll).not.toHaveBeenCalled();
  });

  it('marks a linked row and offers no Delete for it', async () => {
    const { host } = await render(of(false), [linkedSupplier()]);

    expect(host.querySelector('[data-testid="supplier-linked-7"]')).not.toBeNull();
    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) =>
      b.textContent?.trim().includes('Delete')
    );
    expect(deleteButton).toBeUndefined();
  });

  it('leaves an unlinked row unmarked and still offers Delete', async () => {
    const { host } = await render(of(false), [supplier()]);

    expect(host.querySelector('[data-testid="supplier-linked-1"]')).toBeNull();
    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) =>
      b.textContent?.trim().includes('Delete')
    );
    expect(deleteButton).toBeDefined();
  });

  it('deletes nothing for a linked supplier even if the method is invoked directly', async () => {
    const { fixture, host, delete: deleteFn } = await render(of(false), [linkedSupplier()]);

    fixture.componentInstance.askDelete(linkedSupplier());
    fixture.componentInstance.remove(linkedSupplier());
    fixture.detectChanges();

    expect(host.querySelector('app-confirmation-dialog')).toBeNull();
    expect(deleteFn).not.toHaveBeenCalled();
  });

  it('locks the name and website inputs while editing a linked supplier', async () => {
    const stored = linkedSupplier();
    const { fixture, host } = await render(of(false), [stored]);

    fixture.componentInstance.editSupplier(stored);
    fixture.detectChanges();

    expect((host.querySelector('#supplierName') as HTMLInputElement).readOnly).toBe(true);
    expect((host.querySelector('#supplierUrl') as HTMLInputElement).readOnly).toBe(true);
    expect(host.querySelector('[data-testid="supplier-locked-note"]')).not.toBeNull();
  });

  it('leaves the name and website inputs editable for an unlinked supplier', async () => {
    const stored = supplier();
    const { fixture, host } = await render(of(false), [stored]);

    fixture.componentInstance.editSupplier(stored);
    fixture.detectChanges();

    expect((host.querySelector('#supplierName') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('#supplierUrl') as HTMLInputElement).readOnly).toBe(false);
    expect(host.querySelector('[data-testid="supplier-locked-note"]')).toBeNull();
  });

  it('switches "Not used" on and off for a linked supplier and reloads', async () => {
    const stored = linkedSupplier();
    const { fixture, host, setNotUsed, getAllIncludingHidden } = await render(of(false), [stored]);

    const toggle = host.querySelector('[data-testid="supplier-not-used-7"]') as HTMLInputElement;
    expect(toggle).not.toBeNull();
    expect(toggle.checked).toBe(false);

    toggle.dispatchEvent(new Event('change'));

    expect(setNotUsed).toHaveBeenCalledWith(7, true);
    expect(getAllIncludingHidden).toHaveBeenCalledTimes(2);

    fixture.componentInstance.setNotUsed(linkedSupplier({ isHidden: true }), false);
    expect(setNotUsed).toHaveBeenLastCalledWith(7, false);
  });

  it('shows a hidden linked supplier with the switch on, so it can be switched back off', async () => {
    const { host } = await render(of(false), [linkedSupplier({ isHidden: true })]);

    const toggle = host.querySelector('[data-testid="supplier-not-used-7"]') as HTMLInputElement;
    expect(toggle.checked).toBe(true);
  });

  it('offers no switch for a supplier the business created itself', async () => {
    const { fixture, host, setNotUsed } = await render(of(false), [supplier()]);

    expect(host.querySelector('[data-testid="supplier-not-used-1"]')).toBeNull();

    fixture.componentInstance.setNotUsed(supplier(), true);
    expect(setNotUsed).not.toHaveBeenCalled();
  });

  it('hides the switch while a support session is active and writes nothing if invoked', async () => {
    const { fixture, host, setNotUsed } = await render(of(true), [linkedSupplier()]);

    expect(host.querySelector('[data-testid="supplier-not-used-7"]')).toBeNull();

    fixture.componentInstance.setNotUsed(linkedSupplier(), true);
    expect(setNotUsed).not.toHaveBeenCalled();
  });

  it('shows the API refusal sentence verbatim when a write is refused', async () => {
    const stored = linkedSupplier();
    const refusal = 'This supplier came from the platform\'s supplier list, so it cannot be deleted.';
    const { fixture, host, getAllIncludingHidden } = await render(of(false), [stored]);
    const service = TestBed.inject(SupplierService) as unknown as {
      delete: jest.Mock;
    };
    service.delete = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: { message: refusal } }))
    );

    // Invoked on a row the page believes is deletable, which is what a stale list looks like: the
    // server is the authority and its sentence is what the operator reads.
    fixture.componentInstance.remove(supplier({ id: 7, canDelete: true }));
    fixture.detectChanges();

    expect(fixture.componentInstance.error).toBe(refusal);
    expect(host.querySelector('[role="alert"]')?.textContent?.trim()).toBe(refusal);
    // A refused delete leaves the list exactly as it was rather than reloading it.
    expect(getAllIncludingHidden).toHaveBeenCalledTimes(1);
  });
});

/**
 * The "Available to add" section (issue #935): the suppliers from the platform's list that serve
 * this business's state and that it has not added yet, and the one-click add beside each of them.
 *
 * The server decides what is on offer - this page renders that answer and holds no rule of its own
 * about states, enabled records or existing copies - and the server refuses the add in a support
 * session and in a role preview whatever this page shows.
 */
describe('SupplierListComponent "Available to add" (issue #935)', () => {
  it('asks the server what this business may add, and lists it with its website as a safe link', async () => {
    const { host, getAvailablePresets } = await renderWith({ available: [availablePreset()] });

    expect(getAvailablePresets).toHaveBeenCalledTimes(1);

    const section = host.querySelector('[data-testid="available-presets"]') as HTMLElement;
    expect(section).not.toBeNull();
    expect(section.textContent).toContain('JB Metro South Coast & ACT');

    const link = section.querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://jbmetro.test');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
  });

  it('shows no section at all when there is nothing left to add', async () => {
    const { host } = await renderWith({ available: [] });

    expect(host.querySelector('[data-testid="available-presets"]')).toBeNull();
  });

  it('renders a website that is not an http(s) address as plain text', async () => {
    const { host } = await renderWith({ available: [availablePreset({ url: 'javascript:alert(1)' })] });

    const section = host.querySelector('[data-testid="available-presets"]') as HTMLElement;
    expect(section.querySelector('a')).toBeNull();
    expect(section.textContent).toContain('javascript:alert(1)');
  });

  it('adds the record its own button belongs to and reloads both lists', async () => {
    const { fixture, host, addFromPreset, getAvailablePresets, getAllIncludingHidden } = await renderWith({
      available: [availablePreset({ id: 12 }), availablePreset({ id: 13, name: 'Coles', url: null })]
    });

    (host.querySelector('[data-testid="add-preset-13"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(addFromPreset).toHaveBeenCalledWith(13);
    expect(getAvailablePresets).toHaveBeenCalledTimes(2);
    expect(getAllIncludingHidden).toHaveBeenCalledTimes(2);
  });

  it('hides the Add action while a support session is active and writes nothing if invoked', async () => {
    const { fixture, host, addFromPreset } = await renderWith({
      sessionActive: of(true),
      available: [availablePreset()]
    });

    expect(host.querySelector('[data-testid="add-preset-12"]')).toBeNull();

    fixture.componentInstance.addFromPreset(availablePreset());
    expect(addFromPreset).not.toHaveBeenCalled();
  });

  it('hides the Add action while a role is being previewed and writes nothing if invoked', async () => {
    const { fixture, host, addFromPreset } = await renderWith({
      previewActive: of(true),
      available: [availablePreset()]
    });

    expect(host.querySelector('[data-testid="add-preset-12"]')).toBeNull();

    fixture.componentInstance.addFromPreset(availablePreset());
    expect(addFromPreset).not.toHaveBeenCalled();
  });

  it('offers the action again once the support session ends', async () => {
    const sessionActive = new BehaviorSubject(true);
    const { fixture, host, addFromPreset } = await renderWith({
      sessionActive,
      available: [availablePreset()]
    });

    sessionActive.next(false);
    fixture.detectChanges();

    const button = host.querySelector('[data-testid="add-preset-12"]') as HTMLButtonElement;
    expect(button).not.toBeNull();

    button.click();
    expect(addFromPreset).toHaveBeenCalledWith(12);
  });

  it('shows the server refusal sentence verbatim and refreshes what is on offer', async () => {
    const refusal = 'This supplier from the platform\'s supplier list has already been added.';
    const addFromPreset = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 409, error: { message: refusal } }))
    );
    const { fixture, host, getAvailablePresets } = await renderWith({
      available: [availablePreset()],
      addFromPreset
    });

    fixture.componentInstance.addFromPreset(availablePreset());
    fixture.detectChanges();

    expect(fixture.componentInstance.error).toBe(refusal);
    expect(host.querySelector('[role="alert"]')?.textContent?.trim()).toBe(refusal);
    expect(getAvailablePresets).toHaveBeenCalledTimes(2);
  });
});

/**
 * The page after issue #1174: the inline New/Edit form and the inline GST defaults card are gone,
 * and the page composes three dialogs instead. What the page owns is only *which* dialog is open
 * and for which supplier; the form, the GST panel and the delete question own the rest.
 */
describe('SupplierListComponent dialogs (issue #1174)', () => {
  it('renders no inline New/Edit form and no inline GST defaults card', async () => {
    const { host } = await render(of(false), [linkedSupplier()]);

    expect(host.querySelector('app-supplier-form-dialog')).toBeNull();
    expect(host.querySelector('app-supplier-gst-defaults')).toBeNull();
    expect(host.querySelector('#supplierName')).toBeNull();
  });

  it('opens the dialog empty from the header Add supplier action', async () => {
    const { fixture, host } = await render(of(false), [supplier()]);

    (host.querySelector('[data-testid="add-supplier"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(host.querySelector('app-supplier-form-dialog')).not.toBeNull();
    expect(host.querySelector('[data-testid="supplier-form-title"]')?.textContent?.trim()).toBe('Add supplier');
    expect((host.querySelector('#supplierName') as HTMLInputElement).value).toBe('');
    expect(buttonNamed(host, 'Create supplier')).toBeDefined();
  });

  it('opens the dialog filled in from a row Edit action', async () => {
    const stored = supplier({ id: 4, name: 'Acme Supplies', contactName: 'Dana', phone: '0400 000 000' });
    const { fixture, host } = await render(of(false), [stored]);

    (host.querySelector('[data-testid="supplier-edit-4"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    // ngModel writes the stored values into the inputs on the round after the dialog renders.
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="supplier-form-title"]')?.textContent?.trim()).toBe('Edit supplier');
    expect((host.querySelector('#supplierName') as HTMLInputElement).value).toBe('Acme Supplies');
    expect((host.querySelector('#supplierContactName') as HTMLInputElement).value).toBe('Dana');
    expect(buttonNamed(host, 'Save changes')).toBeDefined();
  });

  it('reloads the list, shows a success toast and closes the dialog only after a successful save', async () => {
    const { fixture, host, create, getAllIncludingHidden, toasts } = await render(of(false), [supplier()]);

    fixture.componentInstance.addSupplier();
    fixture.detectChanges();
    const dialog = formDialogOf(fixture);
    dialog.form.name = 'New Supplier';
    dialog.save();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledTimes(1);
    expect(getAllIncludingHidden).toHaveBeenCalledTimes(2);
    expect(toasts.success).toHaveBeenCalledTimes(1);
    expect(host.querySelector('app-supplier-form-dialog')).toBeNull();
  });

  it('keeps the dialog open, reloads nothing and shows no toast when the save is refused', async () => {
    const { fixture, host, getAllIncludingHidden, toasts } = await render(of(false), [supplier()]);
    const service = TestBed.inject(SupplierService) as unknown as { create: jest.Mock };
    service.create = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'No.' } })));

    fixture.componentInstance.addSupplier();
    fixture.detectChanges();
    const dialog = formDialogOf(fixture);
    dialog.form.name = 'New Supplier';
    dialog.save();
    fixture.detectChanges();

    expect(host.querySelector('app-supplier-form-dialog')).not.toBeNull();
    expect(getAllIncludingHidden).toHaveBeenCalledTimes(1);
    expect(toasts.success).not.toHaveBeenCalled();
  });

  it('opens the GST defaults of the supplier whose row action was pressed, with its own permission', async () => {
    const { fixture, host, getGstDefaults } = await render(of(false), [linkedSupplier()]);

    (host.querySelector('[data-testid="supplier-gst-defaults-7"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const dialog = host.querySelector('[data-testid="supplier-gst-defaults-dialog"]') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(dialog.querySelector('app-supplier-gst-defaults')).not.toBeNull();
    expect(getGstDefaults).toHaveBeenCalledWith(7);
    expect(buttonNamed(dialog, 'Save GST defaults')).toBeDefined();
  });

  it('offers no GST defaults save to a role that may only read them', async () => {
    const { fixture, host } = await renderWith({ listed: [supplier()], canManageGstDefaults: false });

    fixture.componentInstance.openGstDefaults(supplier());
    fixture.detectChanges();

    const dialog = host.querySelector('[data-testid="supplier-gst-defaults-dialog"]') as HTMLElement;
    expect(buttonNamed(dialog, 'Save GST defaults')).toBeUndefined();
    expect(dialog.textContent).toContain('Your role can see these defaults but not change them.');
  });

  it('asks the shared confirmation dialog before deleting, naming the supplier', async () => {
    const { fixture, host, delete: deleteFn, getAllIncludingHidden } = await render(of(false), [supplier()]);

    (host.querySelector('[data-testid="supplier-delete-1"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const confirmation = host.querySelector('app-confirmation-dialog') as HTMLElement;
    expect(confirmation).not.toBeNull();
    expect(confirmation.textContent).toContain('Acme Supplies');
    expect(deleteFn).not.toHaveBeenCalled();

    buttonNamed(confirmation, 'Delete supplier')!.click();
    fixture.detectChanges();

    expect(deleteFn).toHaveBeenCalledWith(1);
    expect(getAllIncludingHidden).toHaveBeenCalledTimes(2);
    expect(host.querySelector('app-confirmation-dialog')).toBeNull();
  });

  it('deletes nothing when the confirmation is dismissed', async () => {
    const { fixture, host, delete: deleteFn } = await render(of(false), [supplier()]);

    (host.querySelector('[data-testid="supplier-delete-1"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    buttonNamed(host.querySelector('app-confirmation-dialog') as HTMLElement, 'Cancel')!.click();
    fixture.detectChanges();

    expect(deleteFn).not.toHaveBeenCalled();
    expect(host.querySelector('app-confirmation-dialog')).toBeNull();
  });

  /**
   * Edit may open the dialog in a support session, exactly as it could before this issue: what a
   * session takes away is the save, not the form.
   */
  it('opens the dialog in a support session and still refuses the write', async () => {
    const { fixture, host, update } = await render(of(true), [supplier()]);

    fixture.componentInstance.editSupplier(supplier());
    fixture.detectChanges();

    const dialog = formDialogOf(fixture);
    dialog.form.phone = '0400 000 000';
    dialog.save();

    expect(update).not.toHaveBeenCalled();
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
  });
});
