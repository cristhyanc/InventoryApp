import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, Observable, of, throwError } from 'rxjs';
import { SupplierListComponent } from './supplier-list.component';
import { SupplierService } from '../../services/supplier.service';
import { AccessService } from '../../access/access.service';
import { Supplier } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  SUPPLIER_URL_INVALID_MESSAGE,
  SUPPLIER_URL_MAX_LENGTH,
  SUPPLIER_URL_TOO_LONG_MESSAGE
} from './supplier-url';

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

async function render(sessionActive: Observable<boolean> = of(false), listed: Supplier[] = [supplier()]) {
  const create = jest.fn(() => of(supplier()));
  const update = jest.fn(() => of(supplier()));
  const deleteFn = jest.fn(() => of(undefined));
  const setNotUsed = jest.fn(() => of(undefined));
  const getAllIncludingHidden = jest.fn(() => of(listed));
  const getAll = jest.fn(() => of(listed));

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
          getGstDefaults: () => of(null)
        }
      },
      { provide: AccessService, useValue: { allows: () => of(true) } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
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
    getAllIncludingHidden
  };
}

afterEach(() => TestBed.resetTestingModule());

/**
 * Create/Update and Delete while a support session is active (issue #657, extending #554). A
 * session grants `PurchasingOperate`, so these would otherwise still render - disabling and hiding
 * them is this page's own job, not the capability check's.
 */
describe('SupplierListComponent write controls during a support session (issue #657)', () => {
  it('disables Create and hides Delete while a session is active', async () => {
    const { host } = await render(of(true));

    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim().includes('Delete'));
    expect(deleteButton).toBeUndefined();
  });

  it('leaves Create enabled and Delete shown while no session is active', async () => {
    const { host } = await render(of(false));

    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(false);

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim().includes('Delete'));
    expect(deleteButton).toBeDefined();
  });

  it('writes nothing on save while a session is active, even if the write method is invoked directly', async () => {
    const { fixture, create, update } = await render(of(true));
    const component = fixture.componentInstance;
    component.form.name = 'New Supplier';

    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(update).not.toHaveBeenCalled();
  });

  it('writes nothing on delete while a session is active, even if the write method is invoked directly', async () => {
    const { fixture, delete: deleteFn } = await render(of(true));
    const confirmSpy = jest.spyOn(window, 'confirm');

    fixture.componentInstance.remove(supplier());

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(deleteFn).not.toHaveBeenCalled();
  });

  it('still saves and deletes once a session ends', async () => {
    const sessionActive = new BehaviorSubject(true);
    const { fixture, host, create } = await render(sessionActive);

    sessionActive.next(false);
    fixture.detectChanges();

    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(false);

    fixture.componentInstance.form.name = 'New Supplier';
    fixture.componentInstance.save();

    expect(create).toHaveBeenCalledTimes(1);
  });
});

/**
 * The optional website (issue #847): the form applies the same rule the server does, and the list
 * links only to a value that rule still accepts.
 */
describe('SupplierListComponent website URL (issue #847)', () => {
  it('sends the trimmed URL and clears the field error on a valid save', async () => {
    const { fixture, host, create } = await render();
    const component = fixture.componentInstance;
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
      const { fixture, host, create } = await render();
      const component = fixture.componentInstance;
      component.form.name = 'New Supplier';
      component.form.url = typed;

      component.save();
      fixture.detectChanges();

      expect(create).not.toHaveBeenCalled();
      expect(component.urlError).toBe(SUPPLIER_URL_INVALID_MESSAGE);
      const error = host.querySelector('#supplierUrlError');
      expect(error?.textContent?.trim()).toBe(SUPPLIER_URL_INVALID_MESSAGE);
      expect(host.querySelector('#supplierUrl')?.getAttribute('aria-describedby')).toBe('supplierUrlError');
    }
  );

  it('refuses a URL over the maximum length with the length message', async () => {
    const { fixture, create } = await render();
    const component = fixture.componentInstance;
    component.form.name = 'New Supplier';
    component.form.url = `https://coles.com.au/${'a'.repeat(SUPPLIER_URL_MAX_LENGTH)}`;

    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(component.urlError).toBe(SUPPLIER_URL_TOO_LONG_MESSAGE);
  });

  it('edits a supplier whose url is null without touching the field', async () => {
    const stored = supplier({ id: 4, name: 'Acme Supplies', url: null });
    const { fixture, update } = await render(of(false), [stored]);
    const component = fixture.componentInstance;

    component.startEdit(stored);
    expect(component.form.url).toBe('');

    component.save();

    expect(update).toHaveBeenCalledWith(4, expect.objectContaining({ url: null }));
    expect(component.urlError).toBeNull();
  });

  it('loads an existing url into the form and can clear it', async () => {
    const stored = supplier({ id: 4, url: 'https://acme.test' });
    const { fixture, update } = await render(of(false), [stored]);
    const component = fixture.componentInstance;

    component.startEdit(stored);
    expect(component.form.url).toBe('https://acme.test');

    component.form.url = '   ';
    component.save();

    expect(update).toHaveBeenCalledWith(4, expect.objectContaining({ url: null }));
  });

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
    const { fixture, delete: deleteFn } = await render(of(false), [linkedSupplier()]);
    const confirmSpy = jest.spyOn(window, 'confirm');

    fixture.componentInstance.remove(linkedSupplier());

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(deleteFn).not.toHaveBeenCalled();
  });

  it('locks the name and website inputs while editing a linked supplier', async () => {
    const stored = linkedSupplier();
    const { fixture, host } = await render(of(false), [stored]);

    fixture.componentInstance.startEdit(stored);
    fixture.detectChanges();

    expect((host.querySelector('#supplierName') as HTMLInputElement).readOnly).toBe(true);
    expect((host.querySelector('#supplierUrl') as HTMLInputElement).readOnly).toBe(true);
    expect(host.querySelector('[data-testid="supplier-locked-note"]')).not.toBeNull();
  });

  it('leaves the name and website inputs editable for an unlinked supplier', async () => {
    const stored = supplier();
    const { fixture, host } = await render(of(false), [stored]);

    fixture.componentInstance.startEdit(stored);
    fixture.detectChanges();

    expect((host.querySelector('#supplierName') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('#supplierUrl') as HTMLInputElement).readOnly).toBe(false);
    expect(host.querySelector('[data-testid="supplier-locked-note"]')).toBeNull();
  });

  /**
   * The contact details are the business's own, and saving them must go through. The stored name
   * and website are sent back unchanged, which is what the API compares against.
   */
  it('saves a linked supplier with its stored name and website whatever the locked inputs hold', async () => {
    const stored = linkedSupplier();
    const { fixture, update } = await render(of(false), [stored]);
    const component = fixture.componentInstance;

    component.startEdit(stored);
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
    const { fixture, host } = await render(of(false), [stored]);
    const service = TestBed.inject(SupplierService) as unknown as {
      delete: jest.Mock;
    };
    service.delete = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: { message: refusal } }))
    );
    jest.spyOn(window, 'confirm').mockReturnValue(true);

    // Invoked on a row the page believes is deletable, which is what a stale list looks like: the
    // server is the authority and its sentence is what the operator reads.
    fixture.componentInstance.remove(supplier({ id: 7, canDelete: true }));
    fixture.detectChanges();

    expect(fixture.componentInstance.error).toBe(refusal);
    expect(host.querySelector('[role="alert"]')?.textContent?.trim()).toBe(refusal);
  });

  it('shows a field-level refusal from the errors map', async () => {
    const refusal = 'This supplier came from the platform\'s supplier list, so its name cannot be changed.';
    const stored = linkedSupplier();
    const { fixture } = await render(of(false), [stored]);
    const service = TestBed.inject(SupplierService) as unknown as { update: jest.Mock };
    service.update = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { name: [refusal] } } }))
    );

    fixture.componentInstance.startEdit(stored);
    fixture.componentInstance.form.phone = '0400 000 000';
    fixture.componentInstance.save();

    expect(fixture.componentInstance.error).toBe(refusal);
  });
});
