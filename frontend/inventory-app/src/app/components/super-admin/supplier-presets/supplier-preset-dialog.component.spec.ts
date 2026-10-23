import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
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
    createdAtUtc: '2026-10-23T00:00:00Z',
    updatedAtUtc: '2026-10-23T00:00:00Z',
    ...overrides
  };
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

    expect(create).toHaveBeenCalledWith({ name: 'Aldi', url: 'https://www.aldi.com.au' });
    expect(saved).toHaveBeenCalledWith(preset({ id: 7, name: 'Aldi' }));
  });

  it('adds a preset with no website at all', async () => {
    const create = jest.fn(() => of(preset({ url: null })));
    const { fixture, host } = await render({ create });

    type(fixture, host, 'preset-name', 'Aldi');
    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledWith({ name: 'Aldi', url: null });
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
      isEnabled: false
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
