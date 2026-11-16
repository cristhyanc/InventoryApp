import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { CategoryListComponent } from './category-list.component';
import { CategoryService } from '../../services/category.service';
import { ToastService } from '../../services/toast.service';
import { Category } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';

function category(overrides: Partial<Category> = {}): Category {
  return { id: 1, name: 'Drinks', description: 'Cold beverages', providerManagedFields: [], ...overrides };
}

async function render(
  overrides: {
    categories?: Category[];
    /** A list that answers differently per call, for a reload that no longer has the deleted row. */
    getAll?: jest.Mock;
    create?: jest.Mock;
    update?: jest.Mock;
    deleteFn?: jest.Mock;
    sessionActive?: Observable<boolean>;
  } = {}
) {
  const categories = overrides.categories ?? [category()];
  const getAll = overrides.getAll ?? jest.fn(() => of(categories));
  const create = overrides.create ?? jest.fn(() => of(category({ id: 2 })));
  const update = overrides.update ?? jest.fn(() => of(undefined));
  const deleteFn = overrides.deleteFn ?? jest.fn(() => of(undefined));
  const toastService = { success: jest.fn(), error: jest.fn() };

  await TestBed.configureTestingModule({
    imports: [CategoryListComponent],
    providers: [
      {
        provide: CategoryService,
        useValue: { getAll, create, update, delete: deleteFn }
      },
      { provide: ToastService, useValue: toastService },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(CategoryListComponent);
  fixture.detectChanges();

  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    getAll,
    create,
    update,
    deleteFn,
    toastService
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('CategoryListComponent listing and creating (issue #737)', () => {
  it('lists every category, with no blanket read-only text', async () => {
    const { host } = await render({ categories: [category({ id: 1, name: 'Drinks' }), category({ id: 2, name: 'Snacks' })] });

    expect(host.textContent).toContain('Drinks');
    expect(host.textContent).toContain('Snacks');
    expect(host.textContent).not.toContain('read-only');
  });

  it('creates a category from the name and description entered, then reloads the list', async () => {
    const { fixture, component, create } = await render();

    component.form.name = 'Frozen';
    component.form.description = 'Ice cream and frozen meals';
    component.save();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledWith({ name: 'Frozen', description: 'Ice cream and frozen meals' });
    expect(component.form).toEqual({ name: '', description: '' });
  });

  it('refuses to create a category with no name', async () => {
    const { component, create } = await render();

    component.form.name = '   ';
    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(component.error).toBe('Category name is required.');
  });

  it("shows the server's refusal message when the create is refused", async () => {
    const create = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: 'This business already has a category called "Drinks".' }))
    );
    const { component } = await render({ create });

    component.form.name = 'Drinks';
    component.save();

    expect(component.error).toBe('This business already has a category called "Drinks".');
  });
});

describe('CategoryListComponent renaming a category (issue #737)', () => {
  it('starts the form from the selected category and updates it on save', async () => {
    const { fixture, component, host, update } = await render({ categories: [category({ id: 5, name: 'Drinks', description: 'Cold' })] });

    const editButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Edit') as HTMLButtonElement;
    editButton.click();
    fixture.detectChanges();

    expect(component.form).toEqual({ name: 'Drinks', description: 'Cold' });

    component.form.name = 'Beverages';
    component.save();

    expect(update).toHaveBeenCalledWith(5, { name: 'Beverages', description: 'Cold' });
  });

  it("locks the name field, and names Nayax, for a category the provider manages", async () => {
    const { fixture, component, host } = await render({
      categories: [category({ id: 9, name: 'Drinks', providerManagedFields: ['name'] })]
    });

    const editButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Edit') as HTMLButtonElement;
    editButton.click();
    fixture.detectChanges();

    expect(component.nameLocked).toBe(true);
    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(true);
    expect(host.querySelector('.alert-info .alert-title')?.textContent).toContain(
      "This category's name is managed by Nayax and cannot be edited here."
    );
  });

  it('leaves the name field editable for a category this business maintains itself', async () => {
    const { fixture, host } = await render({ categories: [category({ id: 9, name: 'Drinks', providerManagedFields: [] })] });

    const editButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Edit') as HTMLButtonElement;
    editButton.click();
    fixture.detectChanges();

    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(false);
  });
});

describe('CategoryListComponent delete vs refusal (issue #737)', () => {
  it('deletes the category after confirmation and reloads the list', async () => {
    const { fixture, host, deleteFn } = await render({ categories: [category({ id: 3, name: 'Snacks' })] });

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete') as HTMLButtonElement;
    deleteButton.click();
    fixture.detectChanges();
    expect(host.textContent).toContain('Confirm delete');

    const confirmButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete' && b.className.includes('btn-danger') && b.closest('.confirm-modal')) as HTMLButtonElement;
    confirmButton.click();

    expect(deleteFn).toHaveBeenCalledWith(3);
  });

  /**
   * Delete is offered unconditionally (issue #737): the UI cannot know in advance whether a
   * category is in use, so attempting the delete and explaining a refusal is the whole feature -
   * there is no deactivate alternative for a category, unlike a product.
   */
  it("explains why, through a toast, when the API refuses the delete because products still use the category", async () => {
    const deleteFn = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: 'This category cannot be deleted because 2 products use it. Move those products to another category first. Nothing was deleted.'
          })
      )
    );
    const { fixture, component, host, toastService } = await render({ categories: [category({ id: 3, name: 'Snacks' })], deleteFn });

    component.confirmDelete(category({ id: 3, name: 'Snacks' }));
    component.deleteConfirmed();
    fixture.detectChanges();

    expect(toastService.error).toHaveBeenCalledWith(
      'This category cannot be deleted because 2 products use it. Move those products to another category first. Nothing was deleted.'
    );
    // The category is still on screen: a failed delete must not be mistaken for a successful one.
    expect(host.textContent).toContain('Snacks');
  });
});

/**
 * Keyboard focus across a confirmed delete (issue #1277). The shared dialog returns focus to the
 * row's own Delete button the instant it closes, which is what a cancelled or a refused delete
 * needs. A confirmed delete, though, is still in flight at that moment, and the list the success
 * re-reads no longer has that button - so focus must be handed on rather than dropped on the
 * document body, where Tab would start again at the top of the page.
 */
describe('CategoryListComponent focus after a confirmed delete (issue #1277)', () => {
  /** A control on the one row, by its label: the Delete button the dialog captures, or Edit beside it. */
  const rowButton = (host: HTMLElement, label: string) =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('tbody button')).find((b) => b.textContent?.trim() === label)!;

  /** The confirmation's own destructive button, not the row's. */
  const confirmDelete = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('.confirm-modal .btn-danger')!;

  it('keeps focus on the row while the delete is in flight, then hands it to the page heading once the reloaded list drops the row', async () => {
    const deleted = new Subject<void>();
    const answers = [[category({ id: 3, name: 'Snacks' })], []];
    const { fixture, host } = await render({
      getAll: jest.fn(() => of(answers.shift() ?? [])),
      deleteFn: jest.fn(() => deleted.asObservable())
    });

    const rowDelete = rowButton(host, 'Delete');
    rowDelete.focus();
    rowDelete.click();
    fixture.detectChanges();

    confirmDelete(host).click();
    fixture.detectChanges();

    // The delete has not answered yet: the row is still there, and so is the focus a refusal needs.
    expect(host.textContent).toContain('Snacks');
    expect(document.activeElement).toBe(rowDelete);

    deleted.next();
    deleted.complete();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Snacks');
    const focused = document.activeElement as HTMLElement;
    expect(focused.tagName).toBe('H1');
    expect(focused.textContent).toContain('Categories');
    // Given a tabindex only because a heading carries none, so it can be focused at all.
    expect(focused.getAttribute('tabindex')).toBe('-1');
  });

  it('leaves focus on the row when the delete is refused, so the toast is about where the user still is', async () => {
    const deleted = new Subject<void>();
    const { fixture, host } = await render({
      categories: [category({ id: 3, name: 'Snacks' })],
      deleteFn: jest.fn(() => deleted.asObservable())
    });

    const rowDelete = rowButton(host, 'Delete');
    rowDelete.focus();
    rowDelete.click();
    fixture.detectChanges();

    confirmDelete(host).click();
    fixture.detectChanges();

    deleted.error(new HttpErrorResponse({ status: 409, error: 'Nothing was deleted.' }));
    fixture.detectChanges();

    expect(host.textContent).toContain('Snacks');
    expect(document.activeElement).toBe(rowDelete);
  });

  /**
   * The user confirms and then, while the request is still in flight, Tabs on to Edit on the same
   * row. That row is about to go, so its Edit button is as doomed as the Delete button the dialog
   * returned focus to: leaving focus there drops it on the document body a moment later.
   */
  it('hands focus to the page heading when the user moved it to another control in the row being deleted', async () => {
    const deleted = new Subject<void>();
    const answers = [[category({ id: 3, name: 'Snacks' })], []];
    const { fixture, host } = await render({
      getAll: jest.fn(() => of(answers.shift() ?? [])),
      deleteFn: jest.fn(() => deleted.asObservable())
    });

    const rowDelete = rowButton(host, 'Delete');
    rowDelete.focus();
    rowDelete.click();
    fixture.detectChanges();

    confirmDelete(host).click();
    fixture.detectChanges();

    const rowEdit = rowButton(host, 'Edit');
    rowEdit.focus();
    expect(document.activeElement).toBe(rowEdit);

    deleted.next();
    deleted.complete();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Snacks');
    const focused = document.activeElement as HTMLElement;
    expect(focused.tagName).toBe('H1');
    expect(focused.getAttribute('tabindex')).toBe('-1');
  });

  /** Focus the user moved outside that row lands on something that stays, so it is theirs to keep. */
  it('leaves focus the user moved outside the row being deleted where they put it', async () => {
    const deleted = new Subject<void>();
    const answers = [[category({ id: 3, name: 'Snacks' })], []];
    const { fixture, host } = await render({
      getAll: jest.fn(() => of(answers.shift() ?? [])),
      deleteFn: jest.fn(() => deleted.asObservable())
    });

    const rowDelete = rowButton(host, 'Delete');
    rowDelete.focus();
    rowDelete.click();
    fixture.detectChanges();

    confirmDelete(host).click();
    fixture.detectChanges();

    const nameField = host.querySelector<HTMLInputElement>('#categoryName')!;
    nameField.focus();

    deleted.next();
    deleted.complete();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Snacks');
    expect(document.activeElement).toBe(nameField);
  });
});

describe('CategoryListComponent delete hidden for a provider-managed category (issue #1026)', () => {
  it('hides Delete for a category the provider manages', async () => {
    const { host } = await render({ categories: [category({ id: 9, name: 'Nayax Drinks', providerManagedFields: ['name'] })] });

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    expect(deleteButton).toBeUndefined();
  });

  it('shows Delete for a category this business maintains itself', async () => {
    const { host } = await render({ categories: [category({ id: 9, name: 'Drinks', providerManagedFields: [] })] });

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    expect(deleteButton).toBeDefined();
  });

  it('does not delete a provider-managed category even if confirmDelete() is invoked directly', async () => {
    const { component, deleteFn } = await render({ categories: [category({ id: 9, name: 'Nayax Drinks', providerManagedFields: ['name'] })] });

    component.confirmDelete(category({ id: 9, name: 'Nayax Drinks', providerManagedFields: ['name'] }));
    component.deleteConfirmed();

    expect(deleteFn).not.toHaveBeenCalled();
    expect(component.confirmingCategory).toBeNull();
  });
});

/**
 * Create/rename and delete while a support session is active (issue #554, extended by #737). A
 * session grants `InventoryOperate`, so these would otherwise still render.
 */
describe('CategoryListComponent write controls during a support session (issue #554)', () => {
  it('disables Create/Update and hides Delete while a session is active', async () => {
    const { host } = await render({ sessionActive: of(true) });

    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    expect(deleteButton).toBeUndefined();
  });

  it('does not write while a session is active, even if save()/deleteConfirmed() are invoked directly', async () => {
    const { component, create, deleteFn } = await render({ sessionActive: of(true) });

    component.form.name = 'Frozen';
    component.save();
    component.confirmDelete(category());
    component.deleteConfirmed();

    expect(create).not.toHaveBeenCalled();
    expect(deleteFn).not.toHaveBeenCalled();
    expect(component.confirmingCategory).toBeNull();
  });
});

/**
 * A double click on Create/Update while the request is still in flight (issue #1280, audit #1225
 * finding FE-09). The duplicate check on the server is a read before insert, so two
 * near-simultaneous creates can both succeed; the guard here is that the second click never sends
 * a second request in the first place.
 */
describe('CategoryListComponent duplicate-submit guard (issue #1280)', () => {
  const submitButton = (host: HTMLElement) => host.querySelector('button[type="submit"]') as HTMLButtonElement;

  it('disables the submit button, shows a saving label, and ignores a second submit while the create request is in flight', async () => {
    const pending = new Subject<Category>();
    const { fixture, component, host, create } = await render({ create: jest.fn(() => pending.asObservable()) });

    component.form.name = 'Frozen';
    component.save();
    fixture.detectChanges();

    expect(submitButton(host).disabled).toBe(true);
    expect(submitButton(host).textContent?.trim()).toBe('Saving...');

    // A second click, or a direct second call, before the first request has answered.
    component.save();
    expect(create).toHaveBeenCalledTimes(1);

    pending.next(category({ id: 2, name: 'Frozen' }));
    pending.complete();
    fixture.detectChanges();

    expect(submitButton(host).disabled).toBe(false);
    expect(submitButton(host).textContent?.trim()).toBe('Create');
  });

  it('clears the saving state and shows the error when the create request fails, then sends exactly one new request on retry', async () => {
    const create = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: 'This business already has a category called "Drinks".' }))
    );
    const { fixture, component, host } = await render({ create });

    component.form.name = 'Drinks';
    component.save();
    fixture.detectChanges();

    expect(component.error).toBe('This business already has a category called "Drinks".');
    expect(submitButton(host).disabled).toBe(false);
    expect(submitButton(host).textContent?.trim()).toBe('Create');
    expect(create).toHaveBeenCalledTimes(1);

    component.save();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledTimes(2);
    expect(component.error).toBe('This business already has a category called "Drinks".');
  });
});
