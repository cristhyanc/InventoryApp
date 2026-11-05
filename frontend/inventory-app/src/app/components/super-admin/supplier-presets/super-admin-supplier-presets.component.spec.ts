import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
  SupplierPriceConnector,
  SupplierSearchFrequency,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from '../../../services/super-admin-supplier-presets.service';
import { ToastService } from '../../../services/toast.service';
import { SuperAdminSupplierPresetsComponent } from './super-admin-supplier-presets.component';

function preset(overrides: Partial<SuperAdminSupplierPreset> = {}): SuperAdminSupplierPreset {
  return {
    id: 1,
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

/** The supplier-master half of the row a toggle re-sends unchanged. */
const nationwideGstInclusive = {
  searchPrices: true,
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: null,
  priceConnectorKey: null,
  // The schedule travels on a toggle's `PUT` too (issue #1109), stated exactly as the row holds it,
  // so neither button moves the supplier's timetable.
  searchFrequency: SupplierSearchFrequency.Daily,
  searchDaysOfWeek: [],
  fortnightStartDate: null
};

async function render(service: Partial<SuperAdminSupplierPresetsService>, toast?: Partial<ToastService>) {
  await TestBed.configureTestingModule({
    imports: [SuperAdminSupplierPresetsComponent],
    providers: [
      { provide: SuperAdminSupplierPresetsService, useValue: service },
      {
        provide: ToastService,
        useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast }
      }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SuperAdminSupplierPresetsComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

function rowFor(host: HTMLElement, name: string): HTMLTableRowElement {
  const match = Array.from(host.querySelectorAll('tbody tr')).find((row) => row.textContent?.includes(name));
  expect(match).toBeDefined();
  return match as HTMLTableRowElement;
}

function buttonInRow(row: HTMLTableRowElement, text: string): HTMLButtonElement {
  const match = Array.from(row.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('SuperAdminSupplierPresetsComponent (issue #848)', () => {
  it('lists every preset, disabled ones included, with its website as a safe external link', async () => {
    const list = jest.fn(() =>
      of([
        preset({ id: 1, name: 'Coles' }),
        preset({ id: 2, name: 'Retired Wholesaler', url: null, isEnabled: false })
      ])
    );
    const { host } = await render({ list });

    expect(list).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('Coles');
    expect(host.textContent).toContain('Retired Wholesaler');

    const link = rowFor(host, 'Coles').querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://www.coles.com.au');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.getAttribute('target')).toBe('_blank');

    // A preset with no website shows no link at all rather than an empty one.
    expect(rowFor(host, 'Retired Wholesaler').querySelector('a')).toBeNull();
  });

  /**
   * The API refuses anything but an absolute http(s) address, so this is about a value that
   * reached the table another way - a direct edit of the database, or a row older than the rule.
   * The one website helper (issue #847) decides, exactly as it does on the Suppliers list: the text
   * is still shown, and it is not clickable.
   */
  it('shows a stored website that the website rule refuses as plain text, not as a link', async () => {
    const list = jest.fn(() => of([preset({ id: 3, name: 'Smuggled', url: 'javascript:alert(1)' })]));
    const { host } = await render({ list });

    const row = rowFor(host, 'Smuggled');
    expect(row.querySelector('a')).toBeNull();
    expect(row.textContent).toContain('javascript:alert(1)');
  });

  it('offers Disable for an enabled preset and Enable for a disabled one', async () => {
    const list = jest.fn(() =>
      of([preset({ id: 1, name: 'Coles' }), preset({ id: 2, name: 'Retired Wholesaler', isEnabled: false })])
    );
    const { host } = await render({ list });

    expect(buttonInRow(rowFor(host, 'Coles'), 'Disable')).toBeDefined();
    expect(buttonInRow(rowFor(host, 'Retired Wholesaler'), 'Enable')).toBeDefined();
  });

  it('shows an empty state when there are no presets', async () => {
    const { host } = await render({ list: jest.fn(() => of([])) });

    expect(host.textContent).toContain('No supplier presets yet.');
  });

  it('reports a failed load instead of an empty list', async () => {
    const { host } = await render({ list: jest.fn(() => throwError(() => ({ status: 500 }))) });

    expect(host.textContent).toContain('Unable to load supplier presets.');
    expect(host.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('opens the Add dialog with no preset to edit', async () => {
    const { fixture, host, component } = await render({ list: jest.fn(() => of([preset()])) });

    buttonWithText(host, 'Add preset').click();
    fixture.detectChanges();

    expect(component.dialogOpen).toBe(true);
    expect(component.editTarget).toBeNull();
    expect(host.textContent).toContain('Add supplier preset');
  });

  it('opens the Edit dialog for the row it was pressed on, and re-reads the list once saved', async () => {
    const list = jest.fn(() => of([preset({ id: 3, name: 'Woolworths' })]));
    const success = jest.fn();
    const { fixture, host, component } = await render({ list, update: jest.fn(() => of(preset())) }, { success });

    buttonInRow(rowFor(host, 'Woolworths'), 'Edit').click();
    fixture.detectChanges();
    expect(component.editTarget?.id).toBe(3);
    expect(host.textContent).toContain('Edit supplier preset');

    component.onSaved(preset({ id: 3, name: 'Woolworths Group' }));
    fixture.detectChanges();

    expect(component.dialogOpen).toBe(false);
    expect(success).toHaveBeenCalledWith('Woolworths Group was updated.');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('closes the dialog without a toast when it is canceled', async () => {
    const success = jest.fn();
    const { fixture, host, component } = await render({ list: jest.fn(() => of([preset()])) }, { success });

    buttonWithText(host, 'Add preset').click();
    fixture.detectChanges();
    component.onDialogCanceled();
    fixture.detectChanges();

    expect(component.dialogOpen).toBe(false);
    expect(host.textContent).not.toContain('Add supplier preset');
    expect(success).not.toHaveBeenCalled();
  });

  it('confirms before disabling, then puts the preset with isEnabled false', async () => {
    const list = jest.fn(() => of([preset({ id: 5, name: 'Big W', url: null })]));
    const update = jest.fn(() => of(preset({ id: 5, name: 'Big W', url: null, isEnabled: false })));
    const success = jest.fn();
    const { fixture, host } = await render({ list, update }, { success });

    buttonInRow(rowFor(host, 'Big W'), 'Disable').click();
    fixture.detectChanges();

    // A confirmation is asked for first: nothing has been sent yet.
    expect(host.querySelector('app-confirmation-dialog')).not.toBeNull();
    expect(host.textContent).toContain('Stop suggesting Big W?');
    expect(update).not.toHaveBeenCalled();

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(5, {
      name: 'Big W',
      url: null,
      isEnabled: false,
      ...nationwideGstInclusive
    });
    expect(success).toHaveBeenCalledWith('Big W is no longer suggested.');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('enables a disabled preset through the same call', async () => {
    const list = jest.fn(() => of([preset({ id: 6, name: 'Costco', url: null, isEnabled: false })]));
    const update = jest.fn(() => of(preset({ id: 6, name: 'Costco', url: null, isEnabled: true })));
    const { fixture, host } = await render({ list, update });

    buttonInRow(rowFor(host, 'Costco'), 'Enable').click();
    fixture.detectChanges();
    expect(host.textContent).toContain('Suggest Costco again');

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(6, {
      name: 'Costco',
      url: null,
      isEnabled: true,
      ...nationwideGstInclusive
    });
  });

  /**
   * The toggle is a `PUT` of the whole resource, so it has to carry the supplier-master fields the
   * row already holds. If it sent the defaults instead, disabling a JB Metro record would also move
   * its market to nationwide and its prices to GST-inclusive - a financial field changed by a button
   * that says "Disable".
   */
  it('re-sends the row’s own served states, GST basis, connector and schedule when toggling', async () => {
    const jbMetro = preset({
      id: 7,
      name: 'JB Metro SA & NT',
      url: 'https://www.jbmetro-sa-nt.com.au',
      servesNationwide: false,
      servedStates: ['SA', 'NT'],
      pricesIncludeGst: false,
      priceConnector: SupplierPriceConnector.JbMetro,
      priceConnectorKey: null,
      searchFrequency: SupplierSearchFrequency.Weekly,
      searchDaysOfWeek: ['WED', 'SAT'],
      priceCheckFreshnessHours: 102
    });
    const list = jest.fn(() => of([jbMetro]));
    const update = jest.fn(() => of({ ...jbMetro, isEnabled: false }));
    const { fixture, host } = await render({ list, update });

    buttonInRow(rowFor(host, 'JB Metro SA & NT'), 'Disable').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(7, {
      name: 'JB Metro SA & NT',
      url: 'https://www.jbmetro-sa-nt.com.au',
      isEnabled: false,
      searchPrices: true,
      servesNationwide: false,
      servedStates: ['SA', 'NT'],
      pricesIncludeGst: false,
      priceConnector: SupplierPriceConnector.JbMetro,
      priceConnectorKey: null,
      // The stored schedule, not the daily default: pressing Disable must not reset a weekly
      // supplier's timetable to every day, which on the paid routes is seven times the searches.
      searchFrequency: SupplierSearchFrequency.Weekly,
      searchDaysOfWeek: ['WED', 'SAT'],
      fortnightStartDate: null
    });
  });

  it('sends nothing when the confirmation is canceled', async () => {
    const update = jest.fn();
    const { fixture, host } = await render({ list: jest.fn(() => of([preset()])), update });

    buttonInRow(rowFor(host, 'Coles'), 'Disable').click();
    fixture.detectChanges();
    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();

    expect(update).not.toHaveBeenCalled();
    expect(host.querySelector('app-confirmation-dialog')).toBeNull();
  });

  it('shows the API refusal and re-reads a stale list when a toggle conflicts', async () => {
    const list = jest.fn(() => of([preset()]));
    const message = 'A supplier preset with this name already exists.';
    const update = jest.fn(() => throwError(() => ({ status: 409, error: { message } })));
    const error = jest.fn();
    const { fixture, host } = await render({ list, update }, { error });

    buttonInRow(rowFor(host, 'Coles'), 'Disable').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith(message);
    expect(list).toHaveBeenCalledTimes(2);
  });

  describe('the supplier-master columns (issue #931)', () => {
    it('shows the market, the GST basis and the price connector for each preset', async () => {
      const list = jest.fn(() =>
        of([
          preset({ id: 1, name: 'Coles', priceConnector: SupplierPriceConnector.Varnmill, priceConnectorKey: 'coles' }),
          preset({
            id: 2,
            name: 'JB Metro SA & NT',
            url: null,
            servesNationwide: false,
            servedStates: ['SA', 'NT'],
            pricesIncludeGst: false,
            priceConnector: SupplierPriceConnector.JbMetro
          }),
          preset({ id: 3, name: 'Costco', url: null })
        ])
      );
      const { host } = await render({ list });

      const coles = rowFor(host, 'Coles');
      expect(coles.textContent).toContain('Nationwide');
      expect(coles.textContent).toContain('Includes GST');
      // The route and the retailer it is for, because one Varnmill row is not the same as another.
      expect(coles.textContent).toContain('Varnmill');
      expect(coles.textContent).toContain('coles');

      const jbMetro = rowFor(host, 'JB Metro SA & NT');
      expect(jbMetro.textContent).toContain('SA, NT');
      expect(jbMetro.textContent).not.toContain('Nationwide');
      expect(jbMetro.textContent).toContain('Excludes GST');
      expect(jbMetro.textContent).toContain('JB Metro');

      // A supplier nobody collects prices for says so, rather than showing an empty cell that could
      // be read as a missing value.
      expect(rowFor(host, 'Costco').textContent).toContain('Not collected');
    });

    /**
     * A row the issue #931 migration could not recognise - one that was renamed before it ran - has
     * no market recorded. That is a gap a Super Admin has to fill, so it is reported as one rather
     * than drawn as nationwide or left blank.
     */
    it('reports a preset with no recorded market as not set', async () => {
      const list = jest.fn(() =>
        of([preset({ id: 4, name: 'Coles Group', url: null, servesNationwide: false, servedStates: [] })])
      );
      const { host } = await render({ list });

      const row = rowFor(host, 'Coles Group');
      expect(row.textContent).toContain('Not set');
      expect(row.textContent).not.toContain('Nationwide');
    });
  });

  describe('the Search prices column (issue #1077)', () => {
    const kmart = (overrides: Partial<SuperAdminSupplierPreset> = {}) =>
      preset({
        id: 10,
        name: 'Kmart',
        url: null,
        priceConnector: SupplierPriceConnector.Kmart,
        ...overrides
      });

    it('shows On or Off per preset, beside the Suggested column', async () => {
      const list = jest.fn(() =>
        of([kmart(), kmart({ id: 11, name: 'Big W', searchPrices: false, isEnabled: false })])
      );
      const { host } = await render({ list });

      const headers = Array.from(host.querySelectorAll('thead th')).map((cell) => cell.textContent?.trim());
      expect(headers).toContain('Search prices');

      expect(rowFor(host, 'Kmart').textContent).toContain('On');
      expect(rowFor(host, 'Big W').textContent).toContain('Off');
    });

    /**
     * The two switches are independent, which is the whole point of this issue: a preset that is no
     * longer suggested can still be searched, and a suggested one can have its searches paused.
     */
    it('offers both toggles independently of each other', async () => {
      const list = jest.fn(() =>
        of([
          kmart({ id: 10, name: 'Paused Kmart', isEnabled: true, searchPrices: false }),
          kmart({ id: 11, name: 'Retired Big W', isEnabled: false, searchPrices: true })
        ])
      );
      const { host } = await render({ list });

      const paused = rowFor(host, 'Paused Kmart');
      expect(buttonInRow(paused, 'Disable')).toBeDefined();
      expect(buttonInRow(paused, 'Start searching')).toBeDefined();

      const retired = rowFor(host, 'Retired Big W');
      expect(buttonInRow(retired, 'Enable')).toBeDefined();
      expect(buttonInRow(retired, 'Stop searching')).toBeDefined();
    });

    /**
     * A preset nothing collects has no route to search, so the switch would decide nothing: the
     * column says so in words and offers no button, rather than showing an Off badge that reads as
     * "somebody paused this".
     */
    it('shows no price route and no toggle for a preset with no connector', async () => {
      const list = jest.fn(() => of([preset({ id: 12, name: 'Costco', url: null, priceConnector: null })]));
      const { host } = await render({ list });

      const row = rowFor(host, 'Costco');
      expect(row.textContent).toContain('No price route');

      const labels = Array.from(row.querySelectorAll('button')).map((button) => button.textContent?.trim());
      expect(labels).not.toContain('Stop searching');
      expect(labels).not.toContain('Start searching');
      // The Suggested column's own button is still there: the two are separate.
      expect(labels).toContain('Disable');
    });

    it('confirms before stopping a supplier’s searches, then puts searchPrices false', async () => {
      const list = jest.fn(() => of([kmart()]));
      const update = jest.fn(() => of(kmart({ searchPrices: false })));
      const success = jest.fn();
      const { fixture, host } = await render({ list, update }, { success });

      buttonInRow(rowFor(host, 'Kmart'), 'Stop searching').click();
      fixture.detectChanges();

      expect(host.querySelector('app-confirmation-dialog')).not.toBeNull();
      expect(host.textContent).toContain('Stop searching prices at Kmart?');
      expect(update).not.toHaveBeenCalled();

      buttonWithText(host, 'Confirm').click();
      fixture.detectChanges();

      // The whole resource, with the suggestion flag re-sent exactly as the row holds it: pausing
      // searches must not withdraw the supplier from businesses as a side effect.
      expect(update).toHaveBeenCalledWith(10, {
        name: 'Kmart',
        url: null,
        isEnabled: true,
        searchPrices: false,
        servesNationwide: true,
        servedStates: [],
        pricesIncludeGst: true,
        priceConnector: SupplierPriceConnector.Kmart,
        priceConnectorKey: null,
        searchFrequency: SupplierSearchFrequency.Daily,
        searchDaysOfWeek: [],
        fortnightStartDate: null
      });
      expect(success).toHaveBeenCalledWith('Kmart prices are no longer searched.');
      expect(list).toHaveBeenCalledTimes(2);
    });

    it('starts a paused supplier’s searches again through the same call', async () => {
      const paused = kmart({ searchPrices: false, isEnabled: false });
      const list = jest.fn(() => of([paused]));
      const update = jest.fn(() => of({ ...paused, searchPrices: true }));
      const { fixture, host } = await render({ list, update });

      buttonInRow(rowFor(host, 'Kmart'), 'Start searching').click();
      fixture.detectChanges();
      expect(host.textContent).toContain('Search prices at Kmart again');

      buttonWithText(host, 'Confirm').click();
      fixture.detectChanges();

      // isEnabled stays false: starting a search does not start suggesting the supplier either.
      expect(update).toHaveBeenCalledWith(10, {
        name: 'Kmart',
        url: null,
        isEnabled: false,
        searchPrices: true,
        servesNationwide: true,
        servedStates: [],
        pricesIncludeGst: true,
        priceConnector: SupplierPriceConnector.Kmart,
        priceConnectorKey: null,
        searchFrequency: SupplierSearchFrequency.Daily,
        searchDaysOfWeek: [],
        fortnightStartDate: null
      });
    });

    it('sends nothing when the searching confirmation is canceled', async () => {
      const update = jest.fn();
      const { fixture, host } = await render({ list: jest.fn(() => of([kmart()])), update });

      buttonInRow(rowFor(host, 'Kmart'), 'Stop searching').click();
      fixture.detectChanges();
      buttonWithText(host, 'Cancel').click();
      fixture.detectChanges();

      expect(update).not.toHaveBeenCalled();
      expect(host.querySelector('app-confirmation-dialog')).toBeNull();
    });

    it('shows the API refusal and re-reads a stale list when a searching toggle conflicts', async () => {
      const list = jest.fn(() => of([kmart()]));
      const update = jest.fn(() => throwError(() => ({ status: 404 })));
      const error = jest.fn();
      const { fixture, host } = await render({ list, update }, { error });

      buttonInRow(rowFor(host, 'Kmart'), 'Stop searching').click();
      fixture.detectChanges();
      buttonWithText(host, 'Confirm').click();
      fixture.detectChanges();

      expect(error).toHaveBeenCalledWith('Unable to change this supplier preset.');
      expect(list).toHaveBeenCalledTimes(2);
    });
  });

  describe('the Search schedule column (issue #1109)', () => {
    /**
     * The cell under the named column header, found by the header's own position rather than by a
     * fixed index, so a column added beside it moves the assertion with it.
     */
    function cellUnder(host: HTMLElement, row: HTMLTableRowElement, header: string): HTMLElement {
      const headers = Array.from(host.querySelectorAll('thead th')).map((cell) => cell.textContent?.trim());
      const index = headers.indexOf(header);
      expect(index).toBeGreaterThanOrEqual(0);

      const cell = row.querySelectorAll('td')[index] as HTMLElement;
      expect(cell).toBeDefined();
      return cell;
    }

    /** The lines the Search schedule cell shows, in order and without its blank nodes. */
    function scheduleLines(host: HTMLElement, name: string): string[] {
      const cell = cellUnder(host, rowFor(host, name), 'Search schedule');

      return Array.from(cell.childNodes)
        .filter((node) => node.nodeType === Node.TEXT_NODE || node.nodeType === Node.ELEMENT_NODE)
        .map((node) => node.textContent?.replace(/\s+/g, ' ').trim() ?? '')
        .filter((text) => text.length > 0);
    }

    it('shows each preset’s schedule and the freshness hours the API reported', async () => {
      const list = jest.fn(() =>
        of([
          // Each row's connector is one no other row's name mentions, so a row is found by its own
          // name rather than by another row's Price source cell.
          preset({ id: 1, name: 'Coles', priceConnector: SupplierPriceConnector.Varnmill }),
          preset({
            id: 2,
            name: 'Kmart',
            priceConnector: SupplierPriceConnector.Kmart,
            searchFrequency: SupplierSearchFrequency.Weekly,
            searchDaysOfWeek: ['WED', 'SAT'],
            priceCheckFreshnessHours: 102
          }),
          preset({
            id: 3,
            name: 'Big W',
            priceConnector: SupplierPriceConnector.BigW,
            searchFrequency: SupplierSearchFrequency.Fortnightly,
            searchDaysOfWeek: ['THU'],
            fortnightStartDate: '2026-11-09',
            priceCheckFreshnessHours: 342
          })
        ])
      );
      const { host } = await render({ list });

      expect(scheduleLines(host, 'Coles')).toEqual(['Daily', 'Prices fresh for 30 h']);
      expect(scheduleLines(host, 'Kmart')).toEqual(['Weekly: Wed, Sat', 'Prices fresh for 102 h']);
      // The start date is shown as the day it names, with no browser zone able to move it.
      expect(scheduleLines(host, 'Big W')).toEqual([
        'Fortnightly: Thu (on weeks from 9 Nov 2026)',
        'Prices fresh for 342 h'
      ]);
    });

    /**
     * The hours are the API's own reported value, never a second calculation of them: the server
     * derives the stored limit from the schedule, and a row whose two values disagree is something
     * to see rather than something for the page to quietly correct.
     */
    it('reports the hours the API gave even where they do not match the schedule', async () => {
      const list = jest.fn(() =>
        of([
          preset({
            name: 'Legacy Row',
            priceConnector: SupplierPriceConnector.Kmart,
            searchFrequency: SupplierSearchFrequency.Weekly,
            searchDaysOfWeek: ['WED', 'SAT'],
            priceCheckFreshnessHours: 999
          })
        ])
      );
      const { host } = await render({ list });

      expect(scheduleLines(host, 'Legacy Row')).toEqual([
        'Weekly: Wed, Sat',
        'Prices fresh for 999 h'
      ]);
    });

    /**
     * The schedule stays on the row for a supplier nothing searches - it is still the timetable the
     * searches would follow, and the dialog still edits it - but the row says plainly that nothing
     * is searched meanwhile, so a timetable is never read as "searched on these days".
     */
    it('says nothing is searched while Search prices is off, and still shows the schedule', async () => {
      const list = jest.fn(() =>
        of([
          preset({
            name: 'Paused Kmart',
            priceConnector: SupplierPriceConnector.Kmart,
            searchPrices: false,
            searchFrequency: SupplierSearchFrequency.Weekly,
            searchDaysOfWeek: ['THU'],
            priceCheckFreshnessHours: 174
          })
        ])
      );
      const { host } = await render({ list });

      expect(scheduleLines(host, 'Paused Kmart')).toEqual([
        'Weekly: Thu',
        'Prices fresh for 174 h',
        'Search prices is off, so nothing is searched.'
      ]);
    });

    it('says a preset with no price route has nothing searched at all', async () => {
      const list = jest.fn(() => of([preset({ name: 'Costco', priceConnector: null })]));
      const { host } = await render({ list });

      expect(scheduleLines(host, 'Costco')).toEqual([
        'Daily',
        'Prices fresh for 30 h',
        'No price route, so nothing is searched.'
      ]);
    });

    it('says nothing about searching for a supplier whose schedule is in force', async () => {
      const list = jest.fn(() =>
        of([preset({ name: 'Coles', priceConnector: SupplierPriceConnector.Kmart })])
      );
      const { host } = await render({ list });

      expect(scheduleLines(host, 'Coles')).toEqual(['Daily', 'Prices fresh for 30 h']);
    });
  });

  it('offers no delete, because the API has none', async () => {
    const { host } = await render({ list: jest.fn(() => of([preset()])) });

    const labels = Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim());
    expect(labels).not.toContain('Delete');
    expect(labels).not.toContain('Remove');
  });
});
