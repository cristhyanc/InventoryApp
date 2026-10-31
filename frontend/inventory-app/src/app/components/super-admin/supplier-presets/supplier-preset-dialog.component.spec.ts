import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
  SupplierPriceConnector,
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
    servesNationwide: true,
    servedStates: [],
    pricesIncludeGst: true,
    priceConnector: null,
    priceConnectorKey: null,
    createdAtUtc: '2026-10-23T00:00:00Z',
    updatedAtUtc: '2026-10-23T00:00:00Z',
    ...overrides
  };
}

/** The supplier-master half of every create the dialog sends when nothing was changed. */
const nationwideGstInclusive = {
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: null,
  priceConnectorKey: null
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
        servesNationwide: false,
        // In the server's own order, whatever order the boxes were ticked in, so one market has one
        // spelling on the wire as well as in the database.
        servedStates: ['SA', 'NT'],
        pricesIncludeGst: true,
        priceConnector: null,
        priceConnectorKey: null
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
        servesNationwide: true,
        servedStates: [],
        pricesIncludeGst: true,
        priceConnector: null,
        priceConnectorKey: null
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
        servesNationwide: false,
        servedStates: ['SA', 'NT'],
        pricesIncludeGst: false,
        priceConnector: SupplierPriceConnector.Varnmill,
        priceConnectorKey: 'aldi'
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
