import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, Observable, of } from 'rxjs';
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
    ...overrides
  };
}

async function render(sessionActive: Observable<boolean> = of(false), listed: Supplier[] = [supplier()]) {
  const create = jest.fn(() => of(supplier()));
  const update = jest.fn(() => of(supplier()));
  const deleteFn = jest.fn(() => of(undefined));

  await TestBed.configureTestingModule({
    imports: [SupplierListComponent],
    providers: [
      {
        provide: SupplierService,
        useValue: { getAll: () => of(listed), create, update, delete: deleteFn, getGstDefaults: () => of(null) }
      },
      { provide: AccessService, useValue: { allows: () => of(true) } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierListComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, create, update, delete: deleteFn };
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
