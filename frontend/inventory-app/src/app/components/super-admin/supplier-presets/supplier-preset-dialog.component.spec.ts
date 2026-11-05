import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
  SupplierPriceConnector,
  SupplierSearchFrequency,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from '../../../services/super-admin-supplier-presets.service';
import { SUPPLIER_PRESET_NAME_MAX_LENGTH, SupplierPresetDialogComponent } from './supplier-preset-dialog.component';
import { SUPPLIER_URL_INVALID_MESSAGE } from '../../suppliers/supplier-url';

function preset(overrides: Partial<SuperAdminSupplierPreset> = {}): SuperAdminSupplierPreset {
  return {
    id: 4,
    name: 'Coles',
    url: 'https://www.coles.com.au',
    isEnabled: true,
    searchPrices: true,
    servesNationwide: true,
    servedStates: [],
    pricesIncludeGst: true,
    priceConnector: null,
    priceConnectorKey: null,
    searchFrequency: SupplierSearchFrequency.Daily,
    searchDaysOfWeek: [],
    fortnightStartDate: null,
    scheduleEffectiveFromUtc: '2026-10-23T00:00:00Z',
    priceCheckFreshnessHours: 30,
    createdAtUtc: '2026-10-23T00:00:00Z',
    updatedAtUtc: '2026-10-23T00:00:00Z',
    ...overrides
  };
}

/**
 * The schedule half of every write the dialog sends when nothing about it was changed (issue
 * #1109). A new preset starts on the daily schedule, which is what the server stores for a create
 * that states none and what the collector did before schedules existed.
 */
const dailySchedule = {
  searchFrequency: SupplierSearchFrequency.Daily,
  searchDaysOfWeek: [],
  fortnightStartDate: null
};

/** The supplier-master half of every create the dialog sends when nothing was changed. */
const nationwideGstInclusive = {
  searchPrices: true,
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: null,
  priceConnectorKey: null,
  ...dailySchedule
};

function check(fixture: { detectChanges: () => void }, host: HTMLElement, id: string, checked: boolean): void {
  const input = host.querySelector(`#${id}`) as HTMLInputElement;
  expect(input).not.toBeNull();
  if (input.checked !== checked) {
    input.click();
  }
  fixture.detectChanges();
}

function select(fixture: { detectChanges: () => void }, host: HTMLElement, id: string, value: string): void {
  const element = host.querySelector(`#${id}`) as HTMLSelectElement;
  expect(element).not.toBeNull();
  element.value = value;
  element.dispatchEvent(new Event('change'));
  fixture.detectChanges();
}

async function render(
  service: Partial<SuperAdminSupplierPresetsService>,
  editing: SuperAdminSupplierPreset | null = null
) {
  await TestBed.configureTestingModule({
    imports: [SupplierPresetDialogComponent],
    providers: [{ provide: SuperAdminSupplierPresetsService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierPresetDialogComponent);
  fixture.componentRef.setInput('preset', editing);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

function type(fixture: { detectChanges: () => void }, host: HTMLElement, id: string, value: string): void {
  const input = host.querySelector(`#${id}`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

describe('SupplierPresetDialogComponent (issue #848)', () => {
  it('adds a preset with the trimmed name and website', async () => {
    const create = jest.fn(() => of(preset({ id: 7, name: 'Aldi' })));
    const { fixture, host, component } = await render({ create });
    const saved = jest.fn();
    component.saved.subscribe(saved);

    expect(host.textContent).toContain('Add supplier preset');
    type(fixture, host, 'preset-name', '  Aldi  ');
    type(fixture, host, 'preset-url', '  https://www.aldi.com.au  ');

    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledWith({
      name: 'Aldi',
      url: 'https://www.aldi.com.au',
      ...nationwideGstInclusive
    });
    expect(saved).toHaveBeenCalledWith(preset({ id: 7, name: 'Aldi' }));
  });

  it('adds a preset with no website at all', async () => {
    const create = jest.fn(() => of(preset({ url: null })));
    const { fixture, host } = await render({ create });

    type(fixture, host, 'preset-name', 'Aldi');
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledWith({ name: 'Aldi', url: null, ...nationwideGstInclusive });
  });

  it('offers no enabled toggle when adding, because a new preset is created enabled', async () => {
    const { host } = await render({ create: jest.fn() });

    expect(host.querySelector('#preset-enabled')).toBeNull();
  });

  /**
   * The Search prices switch (issue #1077) is offered on an Add as well as an Edit, unlike the
   * enabled one: a route that charges per search is one somebody may want to record before they
   * start paying for it. It starts on, which is what the server defaults an omitted value to.
   */
  it('offers the search prices switch when adding, starting on', async () => {
    const create = jest.fn(() => of(preset({ id: 7, name: 'Aldi' })));
    const { fixture, host } = await render({ create });

    const searching = host.querySelector('#preset-search-prices') as HTMLInputElement;
    expect(searching).not.toBeNull();
    expect(searching.checked).toBe(true);

    type(fixture, host, 'preset-name', 'Aldi');
    check(fixture, host, 'preset-search-prices', false);
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledWith({
      name: 'Aldi',
      url: null,
      ...nationwideGstInclusive,
      searchPrices: false
    });
  });

  /**
   * And on an edit it is pre-filled from the row and submitted independently of the enabled switch,
   * which is the whole of this issue: one `PUT` carries both, and neither moves the other.
   */
  it('pre-fills the search prices switch when editing and submits it independently', async () => {
    const stored = preset({ isEnabled: false, searchPrices: true });
    const update = jest.fn(() => of(stored));
    const { fixture, host } = await render({ update }, stored);

    expect((host.querySelector('#preset-enabled') as HTMLInputElement).checked).toBe(false);
    expect((host.querySelector('#preset-search-prices') as HTMLInputElement).checked).toBe(true);

    check(fixture, host, 'preset-search-prices', false);
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(4, {
      name: 'Coles',
      url: 'https://www.coles.com.au',
      isEnabled: false,
      ...nationwideGstInclusive,
      searchPrices: false
    });
  });

  it('pre-fills the form when editing and puts the whole resource', async () => {
    const update = jest.fn(() => of(preset({ name: 'Coles Supermarkets', isEnabled: false })));
    const { fixture, host, component } = await render({ update }, preset());
    const saved = jest.fn();
    component.saved.subscribe(saved);

    expect(host.textContent).toContain('Edit supplier preset');
    expect((host.querySelector('#preset-name') as HTMLInputElement).value).toBe('Coles');
    expect((host.querySelector('#preset-url') as HTMLInputElement).value).toBe('https://www.coles.com.au');

    const enabled = host.querySelector('#preset-enabled') as HTMLInputElement;
    expect(enabled.checked).toBe(true);
    enabled.click();
    fixture.detectChanges();

    type(fixture, host, 'preset-name', 'Coles Supermarkets');
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(4, {
      name: 'Coles Supermarkets',
      url: 'https://www.coles.com.au',
      isEnabled: false,
      ...nationwideGstInclusive
    });
    expect(saved).toHaveBeenCalled();
  });

  it('refuses a blank name without calling the API', async () => {
    const create = jest.fn();
    const { fixture, host } = await render({ create });

    type(fixture, host, 'preset-name', '   ');
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(host.textContent).toContain('A supplier preset needs a name.');
    expect(create).not.toHaveBeenCalled();
  });

  it('refuses a name longer than the limit without calling the API', async () => {
    const create = jest.fn();
    const { fixture, host, component } = await render({ create });

    // Set the value on the model directly: the input carries a maxlength, so a browser would not
    // let an over-long value be typed - and the rule still has to hold for a value that arrives
    // another way.
    component.name = 'n'.repeat(SUPPLIER_PRESET_NAME_MAX_LENGTH + 1);
    fixture.detectChanges();
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(host.textContent).toContain('A supplier preset name must be 200 characters or fewer.');
    expect(create).not.toHaveBeenCalled();
  });

  // The sentence is the one supplier website helper's own (issue #847), not a copy of it: this
  // dialog and the Suppliers form refuse the same addresses with the same words, which is what the
  // server does too.
  it.each(['www.aldi.com.au', 'javascript:alert(1)', 'ftp://files.aldi.com.au'])(
    'refuses a website that is not an absolute http(s) address (%s)',
    async (url) => {
      const create = jest.fn();
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Aldi');
      type(fixture, host, 'preset-url', url);
      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain(SUPPLIER_URL_INVALID_MESSAGE);
      expect(create).not.toHaveBeenCalled();
    }
  );

  it('shows the API refusal verbatim and does not emit saved', async () => {
    const message = 'A supplier preset with this name already exists.';
    const create = jest.fn(() => throwError(() => ({ status: 409, error: { message } })));
    const { fixture, host, component } = await render({ create });
    const saved = jest.fn();
    component.saved.subscribe(saved);

    type(fixture, host, 'preset-name', 'Coles');
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(host.textContent).toContain(message);
    expect(saved).not.toHaveBeenCalled();
  });

  it('clears a field message as soon as the value changes', async () => {
    const create = jest.fn();
    const { fixture, host } = await render({ create });

    buttonWithText(host, 'Save').click();
    fixture.detectChanges();
    expect(host.textContent).toContain('A supplier preset needs a name.');

    type(fixture, host, 'preset-name', 'Aldi');
    expect(host.textContent).not.toContain('A supplier preset needs a name.');
  });

  describe('the supplier-master fields (issue #931)', () => {
    it('submits a state list instead of nationwide when nationwide is switched off', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'JB Metro SA & NT');
      check(fixture, host, 'preset-nationwide', false);
      check(fixture, host, 'preset-state-NT', true);
      check(fixture, host, 'preset-state-SA', true);

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith({
        name: 'JB Metro SA & NT',
        url: null,
        searchPrices: true,
        servesNationwide: false,
        // In the server's own order, whatever order the boxes were ticked in, so one market has one
        // spelling on the wire as well as in the database.
        servedStates: ['SA', 'NT'],
        pricesIncludeGst: true,
        priceConnector: null,
        priceConnectorKey: null,
        ...dailySchedule
      });
    });

    it('offers no state choices while nationwide is on, and sends no states', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      expect(host.querySelector('#preset-state-NSW')).toBeNull();

      // Ticking a state and then switching nationwide back on clears the list: "nationwide and NSW"
      // is a combination the server refuses, so the form cannot produce it in the first place.
      check(fixture, host, 'preset-nationwide', false);
      check(fixture, host, 'preset-state-NSW', true);
      check(fixture, host, 'preset-nationwide', true);

      type(fixture, host, 'preset-name', 'Kmart');
      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith({
        name: 'Kmart',
        url: null,
        searchPrices: true,
        servesNationwide: true,
        servedStates: [],
        pricesIncludeGst: true,
        priceConnector: null,
        priceConnectorKey: null,
        ...dailySchedule
      });
    });

    it('refuses a preset that serves nowhere without calling the API', async () => {
      const create = jest.fn();
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Nowhere Supplier');
      check(fixture, host, 'preset-nationwide', false);

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain(
        'A supplier preset must serve Australia nationwide, or at least one state or territory.'
      );
      expect(create).not.toHaveBeenCalled();
    });

    it('submits ex-GST prices when the GST switch is turned off', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'JB Metro NSW');
      check(fixture, host, 'preset-prices-include-gst', false);

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'JB Metro NSW', pricesIncludeGst: false })
      );
    });

    it('submits the chosen connector and its key, and offers the key only for Varnmill', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      expect(host.querySelector('#preset-connector-key')).toBeNull();

      type(fixture, host, 'preset-name', 'Aldi');
      select(fixture, host, 'preset-connector', String(SupplierPriceConnector.Varnmill));
      type(fixture, host, 'preset-connector-key', '  aldi  ');

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({
          priceConnector: SupplierPriceConnector.Varnmill,
          priceConnectorKey: 'aldi'
        })
      );
    });

    it('offers no key for a route that takes none, and sends none', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Kmart');
      select(fixture, host, 'preset-connector', String(SupplierPriceConnector.Kmart));

      expect(host.querySelector('#preset-connector-key')).toBeNull();

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({
          priceConnector: SupplierPriceConnector.Kmart,
          priceConnectorKey: null
        })
      );
    });

    it('refuses Varnmill with no key without calling the API', async () => {
      const create = jest.fn();
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Aldi');
      select(fixture, host, 'preset-connector', String(SupplierPriceConnector.Varnmill));

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain('The Varnmill price connector needs a connector key.');
      expect(create).not.toHaveBeenCalled();
    });

    it('drops a key that was typed before a keyless route was chosen', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Kmart');
      select(fixture, host, 'preset-connector', String(SupplierPriceConnector.Varnmill));
      type(fixture, host, 'preset-connector-key', 'aldi');
      select(fixture, host, 'preset-connector', String(SupplierPriceConnector.Kmart));

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith(expect.objectContaining({ priceConnectorKey: null }));
    });

    it('pre-fills the supplier-master fields when editing', async () => {
      const update = jest.fn(() => of(preset()));
      const { fixture, host } = await render(
        { update },
        preset({
          servesNationwide: false,
          servedStates: ['SA', 'NT'],
          pricesIncludeGst: false,
          priceConnector: SupplierPriceConnector.Varnmill,
          priceConnectorKey: 'aldi'
        })
      );

      expect((host.querySelector('#preset-nationwide') as HTMLInputElement).checked).toBe(false);
      expect((host.querySelector('#preset-state-SA') as HTMLInputElement).checked).toBe(true);
      expect((host.querySelector('#preset-state-NT') as HTMLInputElement).checked).toBe(true);
      expect((host.querySelector('#preset-state-NSW') as HTMLInputElement).checked).toBe(false);
      expect((host.querySelector('#preset-prices-include-gst') as HTMLInputElement).checked).toBe(false);
      expect((host.querySelector('#preset-connector') as HTMLSelectElement).value).toBe(
        String(SupplierPriceConnector.Varnmill)
      );
      expect((host.querySelector('#preset-connector-key') as HTMLInputElement).value).toBe('aldi');

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(update).toHaveBeenCalledWith(4, {
        name: 'Coles',
        url: 'https://www.coles.com.au',
        isEnabled: true,
        searchPrices: true,
        servesNationwide: false,
        servedStates: ['SA', 'NT'],
        pricesIncludeGst: false,
        priceConnector: SupplierPriceConnector.Varnmill,
        priceConnectorKey: 'aldi',
        ...dailySchedule
      });
    });

    it('starts a preset with no recorded market on the state list rather than on nationwide', async () => {
      // A row the issue #931 migration could not recognise reads as "no market recorded". The edit
      // has to make that visible and refusable rather than quietly claiming the whole country.
      const update = jest.fn();
      const { fixture, host } = await render(
        { update },
        preset({ servesNationwide: false, servedStates: [] })
      );

      expect((host.querySelector('#preset-nationwide') as HTMLInputElement).checked).toBe(false);

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain(
        'A supplier preset must serve Australia nationwide, or at least one state or territory.'
      );
      expect(update).not.toHaveBeenCalled();
    });
  });

  describe('the search schedule (issue #1109)', () => {
    const SEARCH_WEEKDAY_REQUIRED =
      'A weekly or fortnightly search schedule names at least one day of the week.';
    const FORTNIGHT_START_DATE_REQUIRED =
      'A fortnightly search schedule needs a start date, which decides which weeks are searched.';

    function freshnessText(host: HTMLElement): string {
      const preview = host.querySelector('#preset-search-freshness') as HTMLElement;
      expect(preview).not.toBeNull();
      return preview.textContent?.replace(/\s+/g, ' ').trim() ?? '';
    }

    it('starts a new preset on Daily, offering no weekday and no start date', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      expect((host.querySelector('#preset-search-frequency') as HTMLSelectElement).value).toBe(
        String(SupplierSearchFrequency.Daily)
      );
      // Every day is a search day, so there is nothing to tick and no off week to place.
      expect(host.querySelector('#preset-search-day-MON')).toBeNull();
      expect(host.querySelector('#preset-fortnight-start')).toBeNull();

      type(fixture, host, 'preset-name', 'Aldi');
      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith({ name: 'Aldi', url: null, ...nationwideGstInclusive });
    });

    it.each([
      ['Weekly', SupplierSearchFrequency.Weekly],
      ['Fortnightly', SupplierSearchFrequency.Fortnightly]
    ])('offers the seven weekdays in Monday-to-Sunday order for %s', async (_name, frequency) => {
      const { fixture, host } = await render({ create: jest.fn() });

      select(fixture, host, 'preset-search-frequency', String(frequency));

      const offered = Array.from(host.querySelectorAll('input[id^="preset-search-day-"]')).map(
        (input) => input.id
      );
      expect(offered).toEqual([
        'preset-search-day-MON',
        'preset-search-day-TUE',
        'preset-search-day-WED',
        'preset-search-day-THU',
        'preset-search-day-FRI',
        'preset-search-day-SAT',
        'preset-search-day-SUN'
      ]);
    });

    it('sends a weekly schedule with its days in Monday-to-Sunday order and no start date', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Kmart');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Weekly));
      // Ticked Saturday first: the order on the wire is the server's own, not the order of clicks.
      check(fixture, host, 'preset-search-day-SAT', true);
      check(fixture, host, 'preset-search-day-WED', true);

      expect(host.querySelector('#preset-fortnight-start')).toBeNull();

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith({
        name: 'Kmart',
        url: null,
        ...nationwideGstInclusive,
        searchFrequency: SupplierSearchFrequency.Weekly,
        searchDaysOfWeek: ['WED', 'SAT'],
        fortnightStartDate: null
      });
    });

    it('sends a fortnightly schedule with its start date as the characters it was given', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'BIG W');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Fortnightly));
      check(fixture, host, 'preset-search-day-THU', true);
      type(fixture, host, 'preset-fortnight-start', '2026-11-09');

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith({
        name: 'BIG W',
        url: null,
        ...nationwideGstInclusive,
        searchFrequency: SupplierSearchFrequency.Fortnightly,
        searchDaysOfWeek: ['THU'],
        fortnightStartDate: '2026-11-09'
      });
    });

    it('explains which weeks a fortnight start date chooses', async () => {
      const { fixture, host } = await render({ create: jest.fn() });

      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Fortnightly));

      expect(host.textContent).toContain(
        'The week this date falls in, Monday to Sunday, is an on week.'
      );
      expect(host.textContent).toContain('The weeks alternate on and off from there.');
    });

    it('says that the days are Sydney time and that a missed day is caught up', async () => {
      const { host } = await render({ create: jest.fn() });

      expect(host.textContent).toContain(
        'Search days are Sydney time. A day a run misses is caught up on the next run.'
      );
    });

    it.each([
      ['Weekly', SupplierSearchFrequency.Weekly],
      ['Fortnightly', SupplierSearchFrequency.Fortnightly]
    ])('refuses %s with no day chosen without calling the API', async (_name, frequency) => {
      const create = jest.fn();
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Kmart');
      select(fixture, host, 'preset-search-frequency', String(frequency));
      if (frequency === SupplierSearchFrequency.Fortnightly) {
        // So the missing weekday is the only thing left to refuse.
        type(fixture, host, 'preset-fortnight-start', '2026-11-09');
      }

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain(SEARCH_WEEKDAY_REQUIRED);
      expect(create).not.toHaveBeenCalled();
    });

    it('refuses a fortnightly schedule with no start date without calling the API', async () => {
      const create = jest.fn();
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'BIG W');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Fortnightly));
      check(fixture, host, 'preset-search-day-THU', true);

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain(FORTNIGHT_START_DATE_REQUIRED);
      expect(create).not.toHaveBeenCalled();
    });

    /**
     * A value that is not a plain `YYYY-MM-DD` date counts as no date, which is the one answer the
     * server's vocabulary has here. A browser's date input cannot produce one; a value that reaches
     * the model another way must still not be submitted.
     */
    it('treats a start date that is not a plain date as none', async () => {
      const create = jest.fn();
      const { fixture, host, component } = await render({ create });

      type(fixture, host, 'preset-name', 'BIG W');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Fortnightly));
      check(fixture, host, 'preset-search-day-THU', true);
      component.fortnightStartDate = '9/11/2026';
      fixture.detectChanges();

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain(FORTNIGHT_START_DATE_REQUIRED);
      expect(create).not.toHaveBeenCalled();
    });

    it('clears a schedule message as soon as the schedule changes', async () => {
      const create = jest.fn();
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Kmart');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Weekly));
      buttonWithText(host, 'Save').click();
      fixture.detectChanges();
      expect(host.textContent).toContain(SEARCH_WEEKDAY_REQUIRED);

      check(fixture, host, 'preset-search-day-THU', true);

      expect(host.textContent).not.toContain(SEARCH_WEEKDAY_REQUIRED);
    });

    /**
     * The server refuses a daily schedule that names weekdays and a non-fortnightly one that
     * carries a start date, so the form never produces either: what the new frequency does not use
     * is dropped when it is chosen, exactly as a ticked state is dropped by switching nationwide on.
     */
    it('does not send weekdays that were ticked before Daily was chosen', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Aldi');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Weekly));
      check(fixture, host, 'preset-search-day-THU', true);
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Daily));

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({
          searchFrequency: SupplierSearchFrequency.Daily,
          searchDaysOfWeek: [],
          fortnightStartDate: null
        })
      );
    });

    it('does not send a start date that was typed before a weekly schedule was chosen', async () => {
      const create = jest.fn(() => of(preset()));
      const { fixture, host } = await render({ create });

      type(fixture, host, 'preset-name', 'Kmart');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Fortnightly));
      check(fixture, host, 'preset-search-day-THU', true);
      type(fixture, host, 'preset-fortnight-start', '2026-11-09');
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Weekly));

      expect(host.querySelector('#preset-fortnight-start')).toBeNull();

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({
          searchFrequency: SupplierSearchFrequency.Weekly,
          searchDaysOfWeek: ['THU'],
          fortnightStartDate: null
        })
      );
    });

    /**
     * The limit is shown, never offered: the server derives the stored value from the schedule and
     * ignores anything a request says about it, so there is no control to submit one and the values
     * are the shared test cases' own (`docs/test-vectors/supplier-search-schedule.json`, read by
     * `supplier-search-schedule.spec.ts` and by the backend's `SupplierSearchSchedulePolicyTests`).
     */
    it('previews the freshness limit read-only and updates it as the schedule changes', async () => {
      const { fixture, host } = await render({ create: jest.fn() });

      expect((host.querySelector('#preset-search-freshness') as HTMLElement).tagName).toBe('P');
      expect(freshnessText(host)).toBe('Prices fresh for 30 h');

      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Weekly));
      // No day chosen yet: there is no gap between two searches to measure, so it says so rather
      // than showing a number that would be wrong.
      expect(freshnessText(host)).toBe('Choose at least one search day to see how long prices stay fresh.');

      check(fixture, host, 'preset-search-day-THU', true);
      expect(freshnessText(host)).toBe('Prices fresh for 174 h');

      check(fixture, host, 'preset-search-day-SUN', true);
      expect(freshnessText(host)).toBe('Prices fresh for 102 h');

      // The same days a fortnight apart wait longer, and one day a fortnight waits longest of all.
      select(fixture, host, 'preset-search-frequency', String(SupplierSearchFrequency.Fortnightly));
      expect(freshnessText(host)).toBe('Prices fresh for 270 h');

      check(fixture, host, 'preset-search-day-SUN', false);
      expect(freshnessText(host)).toBe('Prices fresh for 342 h');
    });

    it('pre-fills the stored schedule when editing and sends it back unchanged', async () => {
      const update = jest.fn(() => of(preset()));
      const stored = preset({
        searchFrequency: SupplierSearchFrequency.Fortnightly,
        searchDaysOfWeek: ['MON', 'FRI'],
        fortnightStartDate: '2026-11-09',
        priceCheckFreshnessHours: 246
      });
      const { fixture, host } = await render({ update }, stored);

      expect((host.querySelector('#preset-search-frequency') as HTMLSelectElement).value).toBe(
        String(SupplierSearchFrequency.Fortnightly)
      );
      expect((host.querySelector('#preset-search-day-MON') as HTMLInputElement).checked).toBe(true);
      expect((host.querySelector('#preset-search-day-FRI') as HTMLInputElement).checked).toBe(true);
      expect((host.querySelector('#preset-search-day-THU') as HTMLInputElement).checked).toBe(false);
      expect((host.querySelector('#preset-fortnight-start') as HTMLInputElement).value).toBe('2026-11-09');
      expect(freshnessText(host)).toBe('Prices fresh for 246 h');

      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(update).toHaveBeenCalledWith(4, {
        name: 'Coles',
        url: 'https://www.coles.com.au',
        isEnabled: true,
        ...nationwideGstInclusive,
        searchFrequency: SupplierSearchFrequency.Fortnightly,
        searchDaysOfWeek: ['MON', 'FRI'],
        fortnightStartDate: '2026-11-09'
      });
    });

    /**
     * Turning the searches off is not the same as having no timetable: the schedule stays on screen
     * and editable - setting one before the searches start is the point of the two switches being
     * separate - and the dialog says plainly that nothing is searched meanwhile.
     */
    it('keeps the schedule visible and editable while Search prices is off, and says nothing is searched', async () => {
      const update = jest.fn(() => of(preset()));
      const stored = preset({
        searchPrices: false,
        searchFrequency: SupplierSearchFrequency.Weekly,
        searchDaysOfWeek: ['WED', 'SAT'],
        priceCheckFreshnessHours: 102
      });
      const { fixture, host } = await render({ update }, stored);

      expect((host.querySelector('#preset-search-prices') as HTMLInputElement).checked).toBe(false);
      expect((host.querySelector('#preset-search-frequency') as HTMLSelectElement).disabled).toBe(false);
      expect((host.querySelector('#preset-search-day-WED') as HTMLInputElement).checked).toBe(true);
      expect(host.textContent).toContain(
        'Search prices is off, so nothing is searched on these days until it is switched back on.'
      );

      check(fixture, host, 'preset-search-day-MON', true);
      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(update).toHaveBeenCalledWith(4, {
        name: 'Coles',
        url: 'https://www.coles.com.au',
        isEnabled: true,
        ...nationwideGstInclusive,
        searchPrices: false,
        searchFrequency: SupplierSearchFrequency.Weekly,
        searchDaysOfWeek: ['MON', 'WED', 'SAT'],
        fortnightStartDate: null
      });
    });

    it('shows the API’s own refusal of a schedule it still will not store', async () => {
      // The server stays the authority on the schedule, as it is on the name: a refusal that gets
      // through the mirrored rules is shown in its own words.
      const message = 'A daily search schedule names no day of the week, because every day is one.';
      const create = jest.fn(() => throwError(() => ({ status: 400, error: { message } })));
      const { fixture, host, component } = await render({ create });
      const saved = jest.fn();
      component.saved.subscribe(saved);

      type(fixture, host, 'preset-name', 'Aldi');
      buttonWithText(host, 'Save').click();
      fixture.detectChanges();

      expect(host.textContent).toContain(message);
      expect(saved).not.toHaveBeenCalled();
    });
  });

  it('emits canceled without calling the API', async () => {
    const create = jest.fn();
    const { fixture, host, component } = await render({ create });
    const canceled = jest.fn();
    component.canceled.subscribe(canceled);

    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();

    expect(create).not.toHaveBeenCalled();
    expect(canceled).toHaveBeenCalledTimes(1);
  });
});
