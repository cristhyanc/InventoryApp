import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { MasterProductSuggestion, MasterProductSuggestionPage } from '../../../models/models';
import { ProductService } from '../../../services/product.service';
import { SEARCH_DEBOUNCE_MS } from '../../shared/filter-request-trigger';
import { ProductMasterProductPickerComponent } from './product-master-product-picker.component';

/**
 * The Name suggestions on New Product (issue #1052). The page's half - what a selection fills in,
 * when it is dropped, what is submitted and the create's `409` - is `product-form.component.spec.ts`;
 * this spec is the control itself: the debounced, superseded lookup, the three list states, the
 * combobox keyboard and what it reports.
 *
 * The lookup is debounced through the repository's one `SEARCH_DEBOUNCE_MS`, so a *settled* term and
 * not a keystroke is what asks. Jest's fake clock settles one, the same way the Add product search's
 * specs do; `advanceTimersByTime` runs outside Angular's zone, which is why every settle is followed
 * by an explicit `detectChanges()`.
 */
function suggestion(overrides: Partial<MasterProductSuggestion> = {}): MasterProductSuggestion {
  return {
    id: 12,
    brand: "CC's",
    name: 'Tasty Cheese Corn Chips',
    sizeValue: 45,
    sizeUnit: 'g',
    productName: "CC's Tasty Cheese Corn Chips",
    unitLabel: '45g',
    ...overrides
  };
}

function page(products: MasterProductSuggestion[], hasMore = false): MasterProductSuggestionPage {
  return { products, page: 1, pageSize: 10, hasMore };
}

/** The 175 g variant of the same product, which is the case near-identical sizes have to separate. */
const partySize = suggestion({
  id: 13,
  sizeValue: 175,
  unitLabel: '175g'
});

/**
 * The parent the picker is built for: it owns the Name it displays, exactly as the product form
 * does, so the control is exercised as the controlled input it is rather than as internal state.
 */
@Component({
  standalone: true,
  imports: [ProductMasterProductPickerComponent],
  template: `
    <form (ngSubmit)="submitted = submitted + 1">
      <app-product-master-product-picker
        [productName]="productName"
        (productNameChange)="productName = $event"
        (suggestionSelected)="selected.push($event)"
      ></app-product-master-product-picker>
    </form>
  `
})
class HostComponent {
  productName = '';
  readonly selected: MasterProductSuggestion[] = [];
  submitted = 0;
}

async function render(
  getMasterProductSuggestions: jest.Mock = jest.fn(() => of(page([suggestion()])))
) {
  await TestBed.configureTestingModule({
    imports: [HostComponent],
    providers: [{ provide: ProductService, useValue: { getMasterProductSuggestions } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const combobox = () => host.querySelector<HTMLInputElement>('input[role="combobox"]')!;

  const type = (value: string) => {
    const input = combobox();
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  const settle = () => {
    jest.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    fixture.detectChanges();
  };

  const search = (value: string) => {
    type(value);
    settle();
  };

  const press = (key: string) => {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    combobox().dispatchEvent(event);
    fixture.detectChanges();
    return event;
  };

  const options = () => Array.from(host.querySelectorAll<HTMLButtonElement>('[role="option"]'));
  const optionLabels = () => options().map((option) => option.textContent?.replace(/\s+/g, ' ').trim());
  const listbox = () => host.querySelector<HTMLElement>('[role="listbox"]');
  const state = (name: string) => host.querySelector<HTMLElement>(`[data-testid="master-product-suggestions-${name}"]`);
  const stateText = (name: string) => state(name)?.textContent?.replace(/\s+/g, ' ').trim();

  return {
    fixture,
    component: fixture.componentInstance,
    host,
    combobox,
    type,
    settle,
    search,
    press,
    options,
    optionLabels,
    listbox,
    state,
    stateText,
    getMasterProductSuggestions
  };
}

beforeEach(() => jest.useFakeTimers());
afterEach(() => {
  jest.useRealTimers();
  TestBed.resetTestingModule();
});

describe('ProductMasterProductPickerComponent (issue #1052)', () => {
  it('is a labelled combobox whose text is reported as it is typed, with no list and no lookup yet', async () => {
    const { host, combobox, type, getMasterProductSuggestions, component, listbox } = await render();

    expect(host.querySelector('.field-label')?.textContent?.trim()).toBe('Name *');
    expect(combobox().getAttribute('aria-expanded')).toBe('false');
    expect(combobox().getAttribute('aria-autocomplete')).toBe('list');
    expect(combobox().getAttribute('aria-controls')).toBeTruthy();
    expect(combobox().getAttribute('aria-activedescendant')).toBeNull();
    expect(listbox()).toBeNull();

    type('Hand typed name');

    // Free text: the Name the product is created with is whatever was typed, reported immediately.
    expect(component.productName).toBe('Hand typed name');
    expect(getMasterProductSuggestions).not.toHaveBeenCalled();
  });

  it('asks nothing below two characters, and once for a settled term rather than once per keystroke', async () => {
    const { type, settle, getMasterProductSuggestions, listbox } = await render();

    type('c');
    settle();
    expect(getMasterProductSuggestions).not.toHaveBeenCalled();
    expect(listbox()).toBeNull();

    type('ch');
    type('che');
    type('chee');
    expect(getMasterProductSuggestions).not.toHaveBeenCalled();

    settle();
    expect(getMasterProductSuggestions).toHaveBeenCalledTimes(1);
    expect(getMasterProductSuggestions).toHaveBeenCalledWith('chee');
  });

  it('lists each match with its brand, name and size, so two sizes of one product are told apart', async () => {
    const { search, optionLabels, options, combobox, listbox } = await render(
      jest.fn(() => of(page([suggestion(), partySize])))
    );

    search('cheese corn');

    expect(optionLabels()).toEqual([
      "CC's Tasty Cheese Corn Chips · 45 g",
      "CC's Tasty Cheese Corn Chips · 175 g"
    ]);
    // Each option carries that same text as its accessible name, which is what is announced when
    // the keyboard moves onto it.
    expect(options().map((option) => option.getAttribute('aria-label'))).toEqual(optionLabels());
    expect(combobox().getAttribute('aria-expanded')).toBe('true');
    expect(listbox()?.id).toBe(combobox().getAttribute('aria-controls'));
  });

  it('says it is searching, says when nothing matches, and says when there is more to narrow', async () => {
    const answers = new Subject<MasterProductSuggestionPage>();
    const { search, stateText, state, settle, type } = await render(jest.fn(() => answers));

    search('cheese');
    expect(stateText('loading')).toContain('Searching');
    expect(state('empty')).toBeNull();

    answers.next(page([]));
    settle();
    expect(state('loading')).toBeNull();
    expect(stateText('empty')).toContain('cheese');

    type('cheese corn');
    settle();
    answers.next(page([suggestion()], true));
    settle();
    expect(stateText('more')).toContain('keep typing');
  });

  it('shows a failed lookup as a message that blocks neither typing nor the rest of the form', async () => {
    const { search, state, stateText, combobox, type, component, getMasterProductSuggestions } = await render(
      jest.fn(() => throwError(() => new Error('lookup unavailable')))
    );

    search('cheese');

    expect(stateText('error')).toContain('Type the Name yourself');
    expect(state('error')?.getAttribute('role')).toBe('alert');
    expect(combobox().disabled).toBe(false);
    expect(combobox().readOnly).toBe(false);

    // The stream survives its own failure: the next settled term asks again.
    type('cheese corn');
    jest.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    expect(component.productName).toBe('cheese corn');
    expect(getMasterProductSuggestions).toHaveBeenCalledTimes(2);
  });

  it('shows only the latest term, so a late response changes neither the list nor anything reported', async () => {
    const first = new Subject<MasterProductSuggestionPage>();
    const second = new Subject<MasterProductSuggestionPage>();
    const getMasterProductSuggestions = jest.fn((term: string) =>
      term === 'cheese' ? (first as Observable<MasterProductSuggestionPage>) : second
    );
    const { search, optionLabels, component, fixture } = await render(getMasterProductSuggestions as jest.Mock);

    search('cheese');
    search('cheese corn');

    second.next(page([partySize]));
    fixture.detectChanges();
    expect(optionLabels()).toEqual(["CC's Tasty Cheese Corn Chips · 175 g"]);

    // The superseded request answers late. It is not the question that is on screen, so nothing of
    // it is shown and nothing is reported from it.
    first.next(page([suggestion({ id: 99, productName: 'Stale Answer', unitLabel: '1each' })]));
    fixture.detectChanges();

    expect(optionLabels()).toEqual(["CC's Tasty Cheese Corn Chips · 175 g"]);
    expect(component.productName).toBe('cheese corn');
    expect(component.selected).toEqual([]);
  });

  it('drops the question as the box changes, so its answer cannot land during the next debounce', async () => {
    const first = new Subject<MasterProductSuggestionPage>();
    const getMasterProductSuggestions = jest.fn((term: string) =>
      term === 'cheese'
        ? (first as Observable<MasterProductSuggestionPage>)
        : new Subject<MasterProductSuggestionPage>()
    );
    const { search, type, options, press, state, component, fixture } = await render(
      getMasterProductSuggestions as jest.Mock
    );

    search('cheese');
    // Typed inside the next term's debounce, so the request for 'cheese' is still the only one made.
    type('cheese corn');
    expect(getMasterProductSuggestions).toHaveBeenCalledTimes(1);

    // The answer to the question the operator has already moved on from.
    first.next(page([suggestion({ id: 99, productName: 'Stale Answer', unitLabel: '1each' })]));
    fixture.detectChanges();

    expect(options()).toEqual([]);
    expect(state('loading')).not.toBeNull();

    // There is nothing of the old term to move onto or take, either.
    press('ArrowDown');
    press('Enter');
    expect(component.selected).toEqual([]);
    expect(component.productName).toBe('cheese corn');
  });

  it('stays closed when the field is cleared below two characters before the answer arrives', async () => {
    const answers = new Subject<MasterProductSuggestionPage>();
    const { search, type, listbox, options, combobox, state, fixture } = await render(
      jest.fn(() => answers)
    );

    search('cheese');
    // Cleared below the minimum while the request for 'cheese' is still in flight.
    type('c');

    answers.next(page([suggestion()]));
    fixture.detectChanges();

    expect(listbox()).toBeNull();
    expect(options()).toEqual([]);
    expect(state('loading')).toBeNull();
    expect(combobox().getAttribute('aria-expanded')).toBe('false');

    // And the dropped wait asks nothing of its own when the debounce would have elapsed.
    jest.advanceTimersByTime(SEARCH_DEBOUNCE_MS * 2);
    fixture.detectChanges();
    expect(listbox()).toBeNull();
  });

  it('drops the previous term’s rows at once, so a stale row cannot be taken while the next search runs', async () => {
    const answers = new Subject<MasterProductSuggestionPage>();
    const { search, type, options, optionLabels, listbox, press, component, fixture } = await render(
      jest.fn(() => answers)
    );

    search('cheese');
    answers.next(page([suggestion(), partySize]));
    fixture.detectChanges();
    expect(optionLabels()).toHaveLength(2);
    const staleRow = options()[0];

    // The list is now about text that is no longer in the box, so it is gone before the next answer.
    type('cheese corn');

    expect(options()).toEqual([]);
    expect(listbox()).toBeNull();

    press('ArrowDown');
    press('Enter');
    staleRow.click();
    fixture.detectChanges();

    expect(component.selected).toEqual([]);
    expect(component.productName).toBe('cheese corn');
  });

  it('cannot be reopened by the lookup it dropped once a suggestion is taken', async () => {
    const answers = new Subject<MasterProductSuggestionPage>();
    const { search, options, listbox, combobox, component, fixture } = await render(
      jest.fn(() => answers)
    );

    search('cheese');
    answers.next(page([suggestion()]));
    fixture.detectChanges();

    options()[0].click();
    fixture.detectChanges();
    expect(component.selected).toEqual([suggestion()]);
    expect(listbox()).toBeNull();

    // Anything the dropped lookup still had to say arrives after the selection: the Name and unit
    // label it filled in are not overwritten, and no obsolete list opens over them.
    answers.next(page([suggestion({ id: 99, productName: 'Stale Answer', unitLabel: '1each' })]));
    jest.advanceTimersByTime(SEARCH_DEBOUNCE_MS * 2);
    fixture.detectChanges();

    expect(listbox()).toBeNull();
    expect(options()).toEqual([]);
    expect(combobox().getAttribute('aria-expanded')).toBe('false');
    expect(component.selected).toEqual([suggestion()]);
  });

  it('moves through the list with the arrow keys and reports the one Enter takes', async () => {
    const { search, press, combobox, options, component, listbox } = await render(
      jest.fn(() => of(page([suggestion(), partySize])))
    );

    search('cheese corn');
    expect(combobox().getAttribute('aria-activedescendant')).toBeNull();

    press('ArrowDown');
    expect(combobox().getAttribute('aria-activedescendant')).toBe(options()[0].id);
    expect(options()[0].getAttribute('aria-selected')).toBe('true');

    press('ArrowDown');
    expect(combobox().getAttribute('aria-activedescendant')).toBe(options()[1].id);

    press('ArrowUp');
    expect(combobox().getAttribute('aria-activedescendant')).toBe(options()[0].id);

    const enter = press('Enter');

    expect(component.selected).toEqual([suggestion()]);
    // Enter acted on the list, not on the form it sits in: a suggestion is taken, nothing submitted.
    expect(enter.defaultPrevented).toBe(true);
    expect(component.submitted).toBe(0);
    expect(listbox()).toBeNull();
    expect(combobox().getAttribute('aria-expanded')).toBe('false');
  });

  it('takes a pressed option, which is how the list is used by touch', async () => {
    const { search, options, fixture, component, listbox } = await render(
      jest.fn(() => of(page([suggestion(), partySize])))
    );

    search('cheese corn');
    options()[1].click();
    fixture.detectChanges();

    expect(component.selected).toEqual([partySize]);
    expect(listbox()).toBeNull();
  });

  it('closes on Escape and on a click elsewhere, keeping the typed text either time', async () => {
    const { search, press, listbox, combobox, component, fixture } = await render();

    search('cheese');
    expect(listbox()).not.toBeNull();

    const escape = press('Escape');
    expect(listbox()).toBeNull();
    expect(escape.defaultPrevented).toBe(true);
    expect(component.productName).toBe('cheese');
    expect(combobox().value).toBe('cheese');

    // Reopened by typing, then dismissed by a pointer somewhere else on the page.
    search('cheese corn');
    expect(listbox()).not.toBeNull();

    document.dispatchEvent(new MouseEvent('click'));
    fixture.detectChanges();
    expect(listbox()).toBeNull();
    expect(component.productName).toBe('cheese corn');
  });

  it('reports no suggestion by typing, by closing the list or by leaving the field', async () => {
    const { search, press, component, combobox, fixture } = await render();

    search('cheese');
    press('ArrowDown');
    press('Escape');
    combobox().dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(component.selected).toEqual([]);
    expect(component.productName).toBe('cheese');
  });
});
