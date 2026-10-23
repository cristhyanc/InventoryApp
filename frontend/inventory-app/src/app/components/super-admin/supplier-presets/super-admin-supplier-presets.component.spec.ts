import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
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
    createdAtUtc: '2026-10-23T00:00:00Z',
    updatedAtUtc: '2026-10-23T00:00:00Z',
    ...overrides
  };
}

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

    expect(update).toHaveBeenCalledWith(5, { name: 'Big W', url: null, isEnabled: false });
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

    expect(update).toHaveBeenCalledWith(6, { name: 'Costco', url: null, isEnabled: true });
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

  it('offers no delete, because the API has none', async () => {
    const { host } = await render({ list: jest.fn(() => of([preset()])) });

    const labels = Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim());
    expect(labels).not.toContain('Delete');
    expect(labels).not.toContain('Remove');
  });
});
