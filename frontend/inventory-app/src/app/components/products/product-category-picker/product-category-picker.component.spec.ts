import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { ProductCategoryPickerComponent } from './product-category-picker.component';
import { CategoryService } from '../../../services/category.service';
import { Category } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * The options as `GET api/categories` answers them: ordered by name
 * (`ICategoryStore.ListOrderedByNameAsync`), with ids the database assigned in another order.
 */
const existing: Category[] = [
  { id: 3, name: 'Chocolate' },
  { id: 1, name: 'Drinks' },
  { id: 2, name: 'Snacks' }
];

async function render(
  overrides: {
    getAll?: jest.Mock;
    create?: jest.Mock;
    sessionActive?: Observable<boolean>;
    categoryId?: number | null;
  } = {}
) {
  const getAll = overrides.getAll ?? jest.fn(() => of(existing.map((category) => ({ ...category }))));
  const create = overrides.create ?? jest.fn(() => of({ id: 9, name: 'Choc Bars' } as Category));

  await TestBed.configureTestingModule({
    imports: [ProductCategoryPickerComponent],
    providers: [
      { provide: CategoryService, useValue: { getAll, create } },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture: ComponentFixture<ProductCategoryPickerComponent> =
    TestBed.createComponent(ProductCategoryPickerComponent);
  const component = fixture.componentInstance;
  const selected: (number | null)[] = [];
  component.categoryIdChange.subscribe((id) => selected.push(id));
  component.categoryId = overrides.categoryId ?? null;
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const input = () => host.querySelector<HTMLInputElement>('input[role="combobox"]')!;

  /** Types into the combobox the way a person does: a real `input` event carrying the new text. */
  const type = (text: string) => {
    const element = input();
    element.value = text;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  const press = (key: string) => {
    input().dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    fixture.detectChanges();
  };

  const focus = () => {
    input().dispatchEvent(new Event('focus'));
    fixture.detectChanges();
  };

  /** Presses a row and lets the component render what that did, as a real click does. */
  const click = (element: HTMLButtonElement) => {
    element.click();
    fixture.detectChanges();
  };

  const optionElements = () => Array.from(host.querySelectorAll<HTMLButtonElement>('[role="option"]'));
  const optionLabels = () => optionElements().map((option) => option.textContent?.trim() ?? '');
  const option = (label: string) => optionElements().find((element) => element.textContent?.trim() === label);
  const createOption = () =>
    host.querySelector<HTMLButtonElement>('[role="option"][data-entry-kind="create"]');

  return {
    fixture,
    component,
    host,
    getAll,
    create,
    selected,
    input,
    type,
    press,
    focus,
    click,
    optionElements,
    optionLabels,
    option,
    createOption
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('ProductCategoryPickerComponent searching the existing categories (issue #830)', () => {
  it('reads the business\'s categories once and offers them all when the field is opened', async () => {
    const { getAll, focus, optionLabels } = await render();

    focus();

    expect(getAll).toHaveBeenCalledTimes(1);
    expect(optionLabels()).toEqual(['No category', 'Chocolate', 'Drinks', 'Snacks']);
  });

  it('filters the options case-insensitively as the name is typed', async () => {
    const { type, optionLabels } = await render();

    // A partial name still offers the create action, because a partial name is not an existing
    // category: only an exact match (ignoring case and surrounding whitespace) withdraws it.
    type('dri');
    expect(optionLabels()).toEqual(['No category', 'Drinks', 'Create category “dri”']);

    type('DRINK');
    expect(optionLabels()).toEqual(['No category', 'Drinks', 'Create category “DRINK”']);

    type('oCoL');
    expect(optionLabels()).toEqual(['No category', 'Chocolate', 'Create category “oCoL”']);

    type('drinks');
    expect(optionLabels()).toEqual(['No category', 'Drinks']);
  });

  it('says so, and offers no match, when nothing contains the typed text', async () => {
    const { type, host, optionLabels } = await render();

    type('zzz');

    expect(optionLabels()).toEqual(['No category', 'Create category “zzz”']);
    expect(host.textContent).toContain('No category matches “zzz”');
  });

  it('submits the selected category id, never the typed text', async () => {
    const { type, option, click, selected, component, input, create } = await render();

    type('dri');
    click(option('Drinks')!);

    expect(selected).toEqual([1]);
    expect(component.categoryId).toBe(1);
    // The box shows the selected category, so the text on screen can never be mistaken for a
    // selection, and nothing was created by typing.
    expect(input().value).toBe('Drinks');
    expect(create).not.toHaveBeenCalled();
  });

  it('keeps the optional-category semantics: "No category" clears the selection to null', async () => {
    const { type, option, click, selected, component } = await render({ categoryId: 2 });

    type('');
    click(option('No category')!);

    expect(selected).toEqual([null]);
    expect(component.categoryId).toBeNull();
  });

  it('reverts the typed text to the current selection when the list is dismissed', async () => {
    const { type, option, click, press, input } = await render();

    type('sn');
    click(option('Snacks')!);
    type('half-typed');
    press('Escape');

    expect(input().value).toBe('Snacks');
    expect(input().getAttribute('aria-expanded')).toBe('false');
  });
});

describe('ProductCategoryPickerComponent creating a missing category (issue #830)', () => {
  it('offers an explicit Create action labelled with the proposed name, and creates nothing by typing', async () => {
    const { type, createOption, create } = await render();

    type('Choc Bars');

    expect(createOption()?.textContent?.trim()).toBe('Create category “Choc Bars”');
    expect(create).not.toHaveBeenCalled();
  });

  it('offers no Create action while the typed name is an existing one, ignoring case and surrounding whitespace', async () => {
    const { type, createOption } = await render();

    type('  drinks  ');

    expect(createOption()).toBeNull();
  });

  it('offers no Create action for a blank name', async () => {
    const { type, createOption, focus } = await render();

    focus();
    expect(createOption()).toBeNull();

    type('   ');
    expect(createOption()).toBeNull();
  });

  it('creates through the categories API, then selects it and offers it among the options', async () => {
    const { type, createOption, click, create, selected, component, input, focus, optionLabels } = await render();

    type('Choc Bars');
    click(createOption()!);

    expect(create).toHaveBeenCalledTimes(1);
    expect(create).toHaveBeenCalledWith({ name: 'Choc Bars', description: null });
    expect(selected).toEqual([9]);
    expect(component.categoryId).toBe(9);
    expect(input().value).toBe('Choc Bars');

    focus();
    expect(optionLabels()).toContain('Choc Bars');
  });

  it('sends the trimmed name, because trailing space is not a second category', async () => {
    const { type, createOption, click, create } = await render();

    type('  Choc Bars  ');
    click(createOption()!);

    expect(create).toHaveBeenCalledWith({ name: 'Choc Bars', description: null });
  });

  it('creates once however often the action is pressed while the create is pending', async () => {
    const pending = new Subject<Category>();
    const create = jest.fn(() => pending.asObservable());
    const { type, createOption, click } = await render({ create });

    type('Choc Bars');
    click(createOption()!);

    expect(createOption()?.disabled).toBe(true);
    click(createOption()!);
    click(createOption()!);

    expect(create).toHaveBeenCalledTimes(1);

    pending.next({ id: 9, name: 'Choc Bars' });
    pending.complete();
  });

  it('shows the API\'s refusal, keeps the typed name and selects nothing when the create fails', async () => {
    const refusal =
      'This business already has a category called "Choc Bars". Category names must be unique within a business. Nothing was saved.';
    const create = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: refusal })));
    const { type, createOption, click, host, input, selected, component } = await render({ create });

    type('Choc Bars');
    click(createOption()!);

    expect(host.querySelector('[role="alert"]')?.textContent).toContain(refusal);
    expect(input().value).toBe('Choc Bars');
    expect(selected).toEqual([]);
    expect(component.categoryId).toBeNull();
  });

  it('re-reads the options after a refusal, so a concurrently created category can be selected instead', async () => {
    const create = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: 'This business already has a category called "Choc Bars".'
          })
      )
    );
    const getAll = jest
      .fn()
      .mockReturnValueOnce(of(existing.map((category) => ({ ...category }))))
      .mockReturnValueOnce(of([...existing.map((category) => ({ ...category })), { id: 9, name: 'Choc Bars' }]));
    const { type, createOption, option, click, selected } = await render({ create, getAll });

    type('Choc Bars');
    click(createOption()!);

    expect(getAll).toHaveBeenCalledTimes(2);
    // The name now exists, so there is nothing left to create - only the existing category to pick.
    expect(createOption()).toBeNull();
    click(option('Choc Bars')!);
    expect(selected).toEqual([9]);
  });

  it('reports a failed option read and still lets the product be saved without a category', async () => {
    const getAll = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { host, focus, optionLabels } = await render({ getAll });

    focus();

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('existing categories could not be loaded');
    expect(optionLabels()).toEqual(['No category']);
  });

  /**
   * The create row is the answer to "no category of yours is called this", so it is offered only
   * when the categories are known. A read that failed knows nothing about them - the typed name may
   * well be one of them - and the load error already says nothing can be created here, so offering
   * the action anyway would be the UI contradicting both itself and the server's duplicate-name
   * rule.
   */
  it('offers no Create action while the options could not be read, as its own load error says', async () => {
    const getAll = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { type, createOption, host, optionLabels } = await render({ getAll });

    type('Choc Bars');

    expect(createOption()).toBeNull();
    expect(optionLabels()).toEqual(['No category']);
    expect(host.querySelector('[role="alert"]')?.textContent).toContain(
      'none can be searched or created here'
    );
    // Nor does the list claim the name matches nothing, which it cannot know either.
    expect(host.textContent).not.toContain('No category matches');
  });

  it('offers no Create action while the options are still being read, and offers it once they arrive', async () => {
    const pending = new Subject<Category[]>();
    const { type, createOption, host, fixture } = await render({
      getAll: jest.fn(() => pending.asObservable())
    });

    type('Choc Bars');
    expect(createOption()).toBeNull();
    expect(host.querySelector('[role="status"]')?.textContent).toContain('Loading categories');

    pending.next(existing.map((category) => ({ ...category })));
    pending.complete();
    fixture.detectChanges();

    expect(createOption()?.textContent?.trim()).toBe('Create category “Choc Bars”');
  });

  it('offers the Create action once a retry has read the options', async () => {
    const getAll = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(existing.map((category) => ({ ...category }))));
    const { host, fixture, type, createOption, create, click } = await render({ getAll });

    type('Choc Bars');
    expect(createOption()).toBeNull();

    host.querySelector<HTMLButtonElement>('button[data-retry-categories]')!.click();
    fixture.detectChanges();

    // The typed name and the open list survive the retry, so the create it now knows is missing is
    // offered without the name being typed again.
    expect(createOption()?.textContent?.trim()).toBe('Create category “Choc Bars”');
    click(createOption()!);
    expect(create).toHaveBeenCalledWith({ name: 'Choc Bars', description: null });
  });

  it('retries the option read on request', async () => {
    const getAll = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(existing.map((category) => ({ ...category }))));
    const { host, fixture, focus, optionLabels } = await render({ getAll });

    host.querySelector<HTMLButtonElement>('button[data-retry-categories]')!.click();
    fixture.detectChanges();

    expect(getAll).toHaveBeenCalledTimes(2);
    focus();
    expect(optionLabels()).toEqual(['No category', 'Chocolate', 'Drinks', 'Snacks']);
  });
});

describe('ProductCategoryPickerComponent keyboard and accessibility (issue #830)', () => {
  it('declares itself a combobox over a labelled list of options', async () => {
    const { input, host, focus } = await render();

    focus();

    const combobox = input();
    expect(combobox.getAttribute('role')).toBe('combobox');
    expect(combobox.getAttribute('aria-autocomplete')).toBe('list');
    expect(combobox.getAttribute('aria-expanded')).toBe('true');

    const listbox = host.querySelector('[role="listbox"]')!;
    expect(combobox.getAttribute('aria-controls')).toBe(listbox.id);
    expect(listbox.getAttribute('aria-label')).toBe('Categories');
    expect(host.querySelector('label')?.getAttribute('for')).toBe(combobox.id);
  });

  it('marks the currently selected category as the selected option', async () => {
    const { focus, option } = await render({ categoryId: 2 });

    focus();

    expect(option('Snacks')?.getAttribute('aria-selected')).toBe('true');
    expect(option('Drinks')?.getAttribute('aria-selected')).toBe('false');
  });

  it('announces the loading state while the options are being read', async () => {
    const pending = new Subject<Category[]>();
    const { host, focus } = await render({ getAll: jest.fn(() => pending.asObservable()) });

    focus();

    expect(host.querySelector('[role="status"]')?.textContent).toContain('Loading categories');

    pending.next(existing);
    pending.complete();
  });

  it('moves through the options with the arrow keys and selects the active one with Enter', async () => {
    const { press, input, selected, component } = await render();

    press('ArrowDown');
    expect(input().getAttribute('aria-expanded')).toBe('true');
    press('ArrowDown');
    press('Enter');

    // The second row: "No category" first, then the options in the order the API answered them.
    expect(selected).toEqual([3]);
    expect(component.categoryId).toBe(3);
  });

  it('points aria-activedescendant at the active option', async () => {
    const { press, input, host } = await render();

    press('ArrowDown');
    press('ArrowDown');

    const active = input().getAttribute('aria-activedescendant');
    expect(active).toBeTruthy();
    expect(host.querySelector(`#${active}`)?.textContent?.trim()).toBe('Chocolate');
  });

  it('wraps around the ends of the list', async () => {
    const { press, input, host } = await render();

    press('ArrowUp');
    expect(host.querySelector(`#${input().getAttribute('aria-activedescendant')}`)?.textContent?.trim()).toBe(
      'Snacks'
    );

    press('ArrowDown');
    expect(host.querySelector(`#${input().getAttribute('aria-activedescendant')}`)?.textContent?.trim()).toBe(
      'No category'
    );
  });

  it('makes the best match active as the name is typed, so Enter selects it', async () => {
    const { type, press, selected } = await render();

    type('snac');
    press('Enter');

    expect(selected).toEqual([2]);
  });

  it('makes the Create action active when the typed name matches nothing, so Enter creates it explicitly', async () => {
    const { type, press, create, selected } = await render();

    type('Choc Bars');
    press('Enter');

    expect(create).toHaveBeenCalledWith({ name: 'Choc Bars', description: null });
    expect(selected).toEqual([9]);
  });

  it('keeps Enter inside the open list instead of submitting the product form', async () => {
    const { type, input } = await render();

    type('dri');
    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    input().dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
  });

  it('leaves Enter to the form while the list is closed', async () => {
    const { input } = await render();

    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    input().dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
  });

  it('dismisses the list with Escape without changing the selection', async () => {
    const { press, host, selected, input } = await render({ categoryId: 1 });

    press('ArrowDown');
    press('Escape');

    expect(host.querySelector('[role="listbox"]')).toBeNull();
    expect(input().getAttribute('aria-expanded')).toBe('false');
    expect(selected).toEqual([]);
  });

  it('closes the list when the click goes elsewhere on the page', async () => {
    const { focus, host, fixture } = await render();

    focus();
    expect(host.querySelector('[role="listbox"]')).not.toBeNull();

    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();

    expect(host.querySelector('[role="listbox"]')).toBeNull();
  });
});

/**
 * Creating a category is a write, and a read-only support session must not offer one (issue #551):
 * the session grants `InventoryOperate` - the capability `POST api/categories` is gated on - and the
 * backend refuses the write itself only by its read-only method rule.
 */
describe('ProductCategoryPickerComponent during a support session (issue #830)', () => {
  it('offers no Create action and still lets an existing category be chosen', async () => {
    const { type, createOption, option, click, selected, host } = await render({ sessionActive: of(true) });

    type('Choc Bars');
    expect(createOption()).toBeNull();
    expect(host.textContent).toContain('No category matches “Choc Bars”');

    type('dri');
    click(option('Drinks')!);
    expect(selected).toEqual([1]);
  });

  it('creates nothing even when the create is invoked directly', async () => {
    const { component, create } = await render({ sessionActive: of(true) });

    component.createCategory('Choc Bars');

    expect(create).not.toHaveBeenCalled();
  });
});
