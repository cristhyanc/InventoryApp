import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
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
    create?: jest.Mock;
    update?: jest.Mock;
    deleteFn?: jest.Mock;
    sessionActive?: Observable<boolean>;
  } = {}
) {
  const categories = overrides.categories ?? [category()];
  const create = overrides.create ?? jest.fn(() => of(category({ id: 2 })));
  const update = overrides.update ?? jest.fn(() => of(undefined));
  const deleteFn = overrides.deleteFn ?? jest.fn(() => of(undefined));
  const toastService = { success: jest.fn(), error: jest.fn() };

  await TestBed.configureTestingModule({
    imports: [CategoryListComponent],
    providers: [
      {
        provide: CategoryService,
        useValue: { getAll: jest.fn(() => of(categories)), create, update, delete: deleteFn }
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
