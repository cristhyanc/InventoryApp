import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';
import { SUPPLIER_NAME_REQUIRED_MESSAGE, SupplierFormDialogComponent } from './supplier-form-dialog.component';
import { SupplierService } from '../../../services/supplier.service';
import { Supplier } from '../../../models/models';
import {
  SUPPLIER_URL_INVALID_MESSAGE,
  SUPPLIER_URL_MAX_LENGTH,
  SUPPLIER_URL_TOO_LONG_MESSAGE
} from '../supplier-url';

function supplier(overrides: Partial<Supplier> = {}): Supplier {
  return {
    id: 1,
    name: 'Acme Supplies',
    contactName: null,
    phone: null,
    email: null,
    address: null,
    url: null,
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

interface RenderOptions {
  /** The supplier being edited; `null` is an Add. */
  supplier?: Supplier | null;

  /** Whether a read-only support session is active (issue #657). */
  sessionActive?: boolean;

  create?: jest.Mock;
  update?: jest.Mock;
}

async function render(options: RenderOptions = {}) {
  const create = options.create ?? jest.fn(() => of(supplier()));
  const update = options.update ?? jest.fn(() => of(undefined));

  await TestBed.configureTestingModule({
    imports: [SupplierFormDialogComponent],
    providers: [{ provide: SupplierService, useValue: { create, update } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierFormDialogComponent);
  fixture.componentRef.setInput('supplier', options.supplier ?? null);
  fixture.componentRef.setInput('sessionActive', options.sessionActive ?? false);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const saved = jest.fn();
  const closed = jest.fn();
  fixture.componentInstance.saved.subscribe(saved);
  fixture.componentInstance.closed.subscribe(closed);

  return { fixture, host, component: fixture.componentInstance, create, update, saved, closed };
}

function panelOf(host: HTMLElement, testId = 'supplier-form-dialog'): HTMLDialogElement {
  const panel = host.querySelector(`[data-testid="${testId}"]`) as HTMLDialogElement;
  expect(panel).not.toBeNull();
  return panel;
}

function buttonNamed(host: ParentNode, label: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === label);
}

function pressEscape(panel: HTMLElement): void {
  panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
}

function clickBackdrop(panel: HTMLElement): void {
  panel.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

afterEach(() => TestBed.resetTestingModule());

/**
 * The Add/Edit dialog of issue #1174. The field rules, the payload and the refusals it reports are
 * the ones the inline form of issues #847 and #934 had; what is new is the dialog around them.
 */
describe('SupplierFormDialogComponent (issue #1174)', () => {
  it('is one form for both: Add titles and creates, Edit titles and updates', async () => {
    const adding = await render();

    expect(adding.component.title).toBe('Add supplier');
    expect(adding.component.submitLabel).toBe('Create supplier');
    adding.component.form.name = 'New Supplier';
    adding.component.save();
    expect(adding.create).toHaveBeenCalledTimes(1);
    expect(adding.saved).toHaveBeenCalledTimes(1);

    TestBed.resetTestingModule();
    const editing = await render({ supplier: supplier({ id: 4 }) });

    expect(editing.component.title).toBe('Edit supplier');
    expect(editing.component.submitLabel).toBe('Save changes');
    editing.component.form.phone = '0400 000 000';
    editing.component.save();
    expect(editing.update).toHaveBeenCalledWith(4, expect.objectContaining({ phone: '0400 000 000' }));
    expect(editing.saved).toHaveBeenCalledTimes(1);
  });

  it('fills the fields from the supplier being edited', async () => {
    const { host } = await render({
      supplier: supplier({ name: 'Acme Supplies', contactName: 'Dana', phone: '0400 000 000', url: 'https://acme.test' })
    });

    expect((host.querySelector('#supplierName') as HTMLInputElement).value).toBe('Acme Supplies');
    expect((host.querySelector('#supplierContactName') as HTMLInputElement).value).toBe('Dana');
    expect((host.querySelector('#supplierPhone') as HTMLInputElement).value).toBe('0400 000 000');
    expect((host.querySelector('#supplierUrl') as HTMLInputElement).value).toBe('https://acme.test');
  });

  it('writes nothing and names the field when the name is blank', async () => {
    const { fixture, host, component, create } = await render();
    component.form.name = '   ';

    component.save();
    fixture.detectChanges();

    expect(create).not.toHaveBeenCalled();
    expect(component.nameError).toBe(SUPPLIER_NAME_REQUIRED_MESSAGE);
    expect(host.querySelector('#supplierNameError')?.textContent?.trim()).toBe(SUPPLIER_NAME_REQUIRED_MESSAGE);
  });

  /** The website rule of issue #847, unchanged: the same sentences, on the same field. */
  describe('website URL (issue #847)', () => {
    it('sends the trimmed URL and clears the field error on a valid save', async () => {
      const { fixture, host, component, create } = await render();
      component.form.name = 'New Supplier';
      component.form.url = '  https://coles.com.au/trade  ';

      component.save();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith(expect.objectContaining({ url: 'https://coles.com.au/trade' }));
      expect(component.urlError).toBeNull();
      expect(host.querySelector('#supplierUrlError')).toBeNull();
    });

    it.each(['javascript:alert(1)', 'coles.com.au', 'ftp://files.example.test'])(
      'refuses %p, writes nothing and shows the message on the field',
      async (typed) => {
        const { fixture, host, component, create } = await render();
        component.form.name = 'New Supplier';
        component.form.url = typed;

        component.save();
        fixture.detectChanges();

        expect(create).not.toHaveBeenCalled();
        expect(component.urlError).toBe(SUPPLIER_URL_INVALID_MESSAGE);
        expect(host.querySelector('#supplierUrlError')?.textContent?.trim()).toBe(SUPPLIER_URL_INVALID_MESSAGE);
        expect(host.querySelector('#supplierUrl')?.getAttribute('aria-describedby')).toBe('supplierUrlError');
      }
    );

    it('refuses a URL over the maximum length with the length message', async () => {
      const { component, create } = await render();
      component.form.name = 'New Supplier';
      component.form.url = `https://coles.com.au/${'a'.repeat(SUPPLIER_URL_MAX_LENGTH)}`;

      component.save();

      expect(create).not.toHaveBeenCalled();
      expect(component.urlError).toBe(SUPPLIER_URL_TOO_LONG_MESSAGE);
    });

    it('edits a supplier whose url is null without touching the field', async () => {
      const { component, update } = await render({ supplier: supplier({ id: 4, url: null }) });

      expect(component.form.url).toBe('');
      component.save();

      expect(update).toHaveBeenCalledWith(4, expect.objectContaining({ url: null }));
      expect(component.urlError).toBeNull();
    });

    it('loads an existing url into the form and can clear it', async () => {
      const { component, update } = await render({ supplier: supplier({ id: 4, url: 'https://acme.test' }) });

      expect(component.form.url).toBe('https://acme.test');
      component.form.url = '   ';
      component.save();

      expect(update).toHaveBeenCalledWith(4, expect.objectContaining({ url: null }));
    });
  });

  /** The lock of issue #934: the server decides it, and the stored values go back unchanged. */
  describe('a linked supplier (issue #934)', () => {
    it('locks the name and website inputs and says why', async () => {
      const { host } = await render({ supplier: linkedSupplier() });

      expect((host.querySelector('#supplierName') as HTMLInputElement).readOnly).toBe(true);
      expect((host.querySelector('#supplierUrl') as HTMLInputElement).readOnly).toBe(true);
      expect(host.querySelector('[data-testid="supplier-locked-note"]')).not.toBeNull();
    });

    it('leaves both editable for a supplier the business created itself', async () => {
      const { host } = await render({ supplier: supplier() });

      expect((host.querySelector('#supplierName') as HTMLInputElement).readOnly).toBe(false);
      expect((host.querySelector('#supplierUrl') as HTMLInputElement).readOnly).toBe(false);
      expect(host.querySelector('[data-testid="supplier-locked-note"]')).toBeNull();
    });

    /**
     * The contact details are the business's own, and saving them must go through. The stored name
     * and website are sent back unchanged, which is what the API compares against.
     */
    it('saves with its stored name and website whatever the locked inputs hold', async () => {
      const { component, update } = await render({ supplier: linkedSupplier() });

      component.form.name = 'Tampered';
      component.form.url = 'https://tampered.test';
      component.form.phone = '0400 000 000';
      component.save();

      expect(update).toHaveBeenCalledWith(7, {
        name: 'Coles',
        contactName: null,
        phone: '0400 000 000',
        email: null,
        address: null,
        url: 'https://www.coles.com.au'
      });
    });
  });

  describe('saving', () => {
    it('shows progress, prevents a second submission and reports nothing until it answers', async () => {
      const answer = new Subject<void>();
      const update = jest.fn(() => answer.asObservable());
      const { fixture, host, component, saved } = await render({ supplier: supplier({ id: 4 }), update });

      component.form.phone = '0400 000 000';
      component.save();
      component.save();
      fixture.detectChanges();

      expect(update).toHaveBeenCalledTimes(1);
      expect(saved).not.toHaveBeenCalled();
      const submit = host.querySelector('[data-testid="supplier-form-submit"]') as HTMLButtonElement;
      expect(submit.disabled).toBe(true);
      expect(submit.textContent?.trim()).toBe('Saving…');
      expect(buttonNamed(host, 'Cancel')?.disabled).toBe(true);

      answer.next();
      answer.complete();
      fixture.detectChanges();

      expect(saved).toHaveBeenCalledTimes(1);
      expect(component.saving).toBe(false);
    });

    it('keeps every entered value and shows the server refusal verbatim when the save fails', async () => {
      const refusal = "This supplier came from the platform's supplier list, so its name cannot be changed.";
      const update = jest.fn(() =>
        throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { name: [refusal] } } }))
      );
      const { fixture, host, component, saved } = await render({ supplier: linkedSupplier(), update });

      component.form.phone = '0400 000 000';
      component.save();
      fixture.detectChanges();

      expect(saved).not.toHaveBeenCalled();
      expect(component.error).toBe(refusal);
      expect(host.querySelector('[data-testid="supplier-form-error"]')?.textContent?.trim()).toBe(refusal);
      expect(host.querySelector('[data-testid="supplier-form-error"]')?.getAttribute('role')).toBe('alert');
      expect(component.form.phone).toBe('0400 000 000');
      expect((host.querySelector('[data-testid="supplier-form-submit"]') as HTMLButtonElement).disabled).toBe(false);
    });

    it('reads a ProblemDetails refusal as well as a field-level one', async () => {
      const update = jest.fn(() =>
        throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'No.' } }))
      );
      const { component } = await render({ supplier: supplier({ id: 4 }), update });

      component.form.phone = '0400 000 000';
      component.save();

      expect(component.error).toBe('No.');
    });

    it('falls back to its own sentence when the response carries none', async () => {
      const update = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
      const { component } = await render({ supplier: supplier({ id: 4 }), update });

      component.form.phone = '0400 000 000';
      component.save();

      expect(component.error).toBe('Failed to save the supplier.');
    });

    it('writes nothing while a support session is active, and disables the save', async () => {
      const { host, component, create, update } = await render({ sessionActive: true });

      component.form.name = 'New Supplier';
      component.save();

      expect(create).not.toHaveBeenCalled();
      expect(update).not.toHaveBeenCalled();
      expect((host.querySelector('[data-testid="supplier-form-submit"]') as HTMLButtonElement).disabled).toBe(true);
    });
  });

  describe('closing', () => {
    it('dismisses an unchanged form at once, from Cancel and from Escape', async () => {
      const { fixture, host, closed } = await render({ supplier: supplier() });

      buttonNamed(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(closed).toHaveBeenCalledTimes(1);
      expect(host.querySelector('[data-testid="supplier-discard-dialog"]')).toBeNull();

      pressEscape(panelOf(host));
      expect(closed).toHaveBeenCalledTimes(2);
    });

    it.each([
      ['Cancel', (host: HTMLElement) => buttonNamed(host, 'Cancel')!.click()],
      ['the Close control', (host: HTMLElement) => (host.querySelector('.app-dialog-close') as HTMLButtonElement).click()],
      ['Escape', (host: HTMLElement) => pressEscape(panelOf(host))],
      ['the backdrop', (host: HTMLElement) => clickBackdrop(panelOf(host))]
    ])('asks before discarding a dirty form, from %s', async (_name, dismiss) => {
      const { fixture, host, closed } = await render({ supplier: supplier() });
      fixture.componentInstance.form.phone = '0400 000 000';
      fixture.detectChanges();

      dismiss(host);
      fixture.detectChanges();

      const question = host.querySelector('[data-testid="supplier-discard-dialog"]') as HTMLElement;
      expect(question).not.toBeNull();
      expect(buttonNamed(question, 'Keep editing')).toBeDefined();
      expect(buttonNamed(question, 'Discard changes')).toBeDefined();
      expect(closed).not.toHaveBeenCalled();
    });

    it('keeps the form and every entered value on "Keep editing"', async () => {
      const { fixture, host, component, closed } = await render({ supplier: supplier() });
      component.form.phone = '0400 000 000';
      fixture.detectChanges();

      buttonNamed(host, 'Cancel')!.click();
      fixture.detectChanges();
      buttonNamed(host.querySelector('[data-testid="supplier-discard-dialog"]') as HTMLElement, 'Keep editing')!.click();
      fixture.detectChanges();

      expect(host.querySelector('[data-testid="supplier-discard-dialog"]')).toBeNull();
      expect(closed).not.toHaveBeenCalled();
      expect(component.form.phone).toBe('0400 000 000');
      expect(host.querySelector('[data-testid="supplier-form-dialog"]')).not.toBeNull();
    });

    it('closes on "Discard changes"', async () => {
      const { fixture, host, component, closed } = await render({ supplier: supplier() });
      component.form.phone = '0400 000 000';
      fixture.detectChanges();

      buttonNamed(host, 'Cancel')!.click();
      fixture.detectChanges();
      buttonNamed(host.querySelector('[data-testid="supplier-discard-dialog"]') as HTMLElement, 'Discard changes')!.click();
      fixture.detectChanges();

      expect(closed).toHaveBeenCalledTimes(1);
    });

    it('cannot be dismissed while a save is in flight', async () => {
      const answer = new Subject<void>();
      const update = jest.fn(() => answer.asObservable());
      const { fixture, host, component, closed } = await render({ supplier: supplier({ id: 4 }), update });

      component.form.phone = '0400 000 000';
      component.save();
      fixture.detectChanges();

      pressEscape(panelOf(host));
      clickBackdrop(panelOf(host));
      fixture.detectChanges();

      expect(closed).not.toHaveBeenCalled();
      expect(host.querySelector('[data-testid="supplier-discard-dialog"]')).toBeNull();
      expect((host.querySelector('.app-dialog-close') as HTMLButtonElement).disabled).toBe(true);
    });
  });

  describe('dialog semantics', () => {
    it('opens as a modal labelled by its own heading, with the Name field focused', async () => {
      const { host } = await render();
      const panel = panelOf(host);

      expect(panel.hasAttribute('open')).toBe(true);
      const titleId = panel.getAttribute('aria-labelledby');
      expect(titleId).toBeTruthy();
      expect(panel.querySelector(`#${titleId}`)?.textContent?.trim()).toBe('Add supplier');
      expect(document.activeElement).toBe(host.querySelector('#supplierName'));
    });

    it('focuses the first field the operator can actually edit when the name is locked', async () => {
      const { host } = await render({ supplier: linkedSupplier() });

      expect(document.activeElement).toBe(host.querySelector('#supplierContactName'));
    });

    it('offers a labelled Close control', async () => {
      const { host } = await render();

      expect((host.querySelector('.app-dialog-close') as HTMLElement).getAttribute('aria-label')).toBe('Close');
    });
  });
});

/** The payload is exactly the one the inline form sent; no API or service contract changed. */
describe('SupplierFormDialogComponent payload (issue #1174)', () => {
  let fixture: ComponentFixture<SupplierFormDialogComponent>;

  it('sends every optional field as null when it is left empty', async () => {
    const rendered = await render();
    fixture = rendered.fixture;
    rendered.component.form.name = '  New Supplier  ';

    rendered.component.save();

    expect(rendered.create).toHaveBeenCalledWith({
      name: 'New Supplier',
      contactName: null,
      phone: null,
      email: null,
      address: null,
      url: null
    });
    expect(fixture.componentInstance.error).toBeNull();
  });
});
