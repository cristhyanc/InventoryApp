import { TestBed } from '@angular/core/testing';
import { MultiSelectDropdownComponent } from './multi-select-dropdown.component';

async function render(selectedIds: number[] = []) {
  await TestBed.configureTestingModule({ imports: [MultiSelectDropdownComponent] }).compileComponents();

  const fixture = TestBed.createComponent(MultiSelectDropdownComponent);
  fixture.componentInstance.label = 'Products';
  fixture.componentInstance.itemLabelSingular = 'product';
  fixture.componentInstance.itemLabelPlural = 'products';
  fixture.componentInstance.options = [
    { id: 1, label: 'Coke' },
    { id: 2, label: 'Chips' },
    { id: 3, label: 'Water' }
  ];
  fixture.componentInstance.selectedIds = selectedIds;
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const trigger = () => host.querySelector<HTMLButtonElement>('button')!;
  const checkboxes = () => Array.from(host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
  const selectAllCheckbox = () => checkboxes()[0];
  const optionCheckbox = (label: string) =>
    checkboxes().find((c) => c.parentElement?.textContent?.trim() === label)!;
  const openPanel = () => {
    trigger().click();
    fixture.detectChanges();
  };
  const emitted: number[][] = [];
  fixture.componentInstance.selectedIdsChange.subscribe((ids) => emitted.push(ids));

  return { fixture, host, trigger, checkboxes, selectAllCheckbox, optionCheckbox, openPanel, emitted };
}

afterEach(() => TestBed.resetTestingModule());

describe('MultiSelectDropdownComponent trigger text', () => {
  it('summarizes an empty selection', async () => {
    const { trigger } = await render([]);
    expect(trigger().textContent).toContain('No products selected');
  });

  it('summarizes a full selection as "All"', async () => {
    const { trigger } = await render([1, 2, 3]);
    expect(trigger().textContent).toContain('All products');
  });

  it('summarizes a partial selection with a count', async () => {
    const { trigger } = await render([1, 3]);
    expect(trigger().textContent).toContain('2 products selected');
  });

  it('uses the singular noun for a single selected item', async () => {
    const { trigger } = await render([1]);
    expect(trigger().textContent).toContain('1 product selected');
  });
});

describe('MultiSelectDropdownComponent panel', () => {
  it('stays closed until the trigger is clicked, then shows a checkbox per option plus Select all', async () => {
    const { host, openPanel } = await render([]);
    expect(host.querySelector('[role="group"]')).toBeNull();

    openPanel();

    expect(host.querySelector('[role="group"]')).not.toBeNull();
    expect(host.querySelectorAll('input[type="checkbox"]').length).toBe(4);
  });

  it('closes again on a second trigger click', async () => {
    const { host, openPanel } = await render([]);
    openPanel();
    openPanel();

    expect(host.querySelector('[role="group"]')).toBeNull();
  });

  it('closes on Escape', async () => {
    const { fixture, host, openPanel } = await render([]);
    openPanel();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(host.querySelector('[role="group"]')).toBeNull();
  });

  it('closes on an outside click', async () => {
    const { fixture, host, openPanel } = await render([]);
    openPanel();

    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();

    expect(host.querySelector('[role="group"]')).toBeNull();
  });

  it('does not close on a click inside the panel', async () => {
    const { fixture, host, openPanel } = await render([]);
    openPanel();

    host.querySelector('[role="group"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();

    expect(host.querySelector('[role="group"]')).not.toBeNull();
  });
});

describe('MultiSelectDropdownComponent selection', () => {
  it('emits the option added to the selection when an unchecked option is clicked', async () => {
    const { openPanel, optionCheckbox, emitted } = await render([1]);
    openPanel();

    optionCheckbox('Chips').click();

    expect(emitted).toEqual([[1, 2]]);
  });

  it('emits the option removed from the selection when a checked option is clicked', async () => {
    const { openPanel, optionCheckbox, emitted } = await render([1, 2]);
    openPanel();

    optionCheckbox('Coke').click();

    expect(emitted).toEqual([[2]]);
  });

  it('Select all emits every option id when none are selected', async () => {
    const { openPanel, selectAllCheckbox, emitted } = await render([]);
    openPanel();

    selectAllCheckbox().click();

    expect(emitted).toEqual([[1, 2, 3]]);
  });

  it('Select all emits an empty selection when every option is already selected', async () => {
    const { openPanel, selectAllCheckbox, emitted } = await render([1, 2, 3]);
    openPanel();

    selectAllCheckbox().click();

    expect(emitted).toEqual([[]]);
  });

  it('shows the Select all checkbox as unchecked, not indeterminate, when nothing is selected', async () => {
    const { openPanel, selectAllCheckbox } = await render([]);
    openPanel();

    expect(selectAllCheckbox().checked).toBe(false);
    expect(selectAllCheckbox().indeterminate).toBe(false);
  });

  it('shows the Select all checkbox as indeterminate when some options are selected', async () => {
    const { openPanel, selectAllCheckbox } = await render([1]);
    openPanel();

    expect(selectAllCheckbox().checked).toBe(false);
    expect(selectAllCheckbox().indeterminate).toBe(true);
  });

  it('shows the Select all checkbox as checked, not indeterminate, when every option is selected', async () => {
    const { openPanel, selectAllCheckbox } = await render([1, 2, 3]);
    openPanel();

    expect(selectAllCheckbox().checked).toBe(true);
    expect(selectAllCheckbox().indeterminate).toBe(false);
  });
});
