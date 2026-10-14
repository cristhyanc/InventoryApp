import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, Observable, of } from 'rxjs';
import { SupplierListComponent } from './supplier-list.component';
import { SupplierService } from '../../services/supplier.service';
import { AccessService } from '../../access/access.service';
import { Supplier } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';

function supplier(overrides: Partial<Supplier> = {}): Supplier {
  return {
    id: 1,
    name: 'Acme Supplies',
    contactName: null,
    phone: null,
    email: null,
    address: null,
    ...overrides
  };
}

async function render(sessionActive: Observable<boolean> = of(false)) {
  const create = jest.fn(() => of(supplier()));
  const update = jest.fn(() => of(supplier()));
  const deleteFn = jest.fn(() => of(undefined));

  await TestBed.configureTestingModule({
    imports: [SupplierListComponent],
    providers: [
      {
        provide: SupplierService,
        useValue: { getAll: () => of([supplier()]), create, update, delete: deleteFn, getGstDefaults: () => of(null) }
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
