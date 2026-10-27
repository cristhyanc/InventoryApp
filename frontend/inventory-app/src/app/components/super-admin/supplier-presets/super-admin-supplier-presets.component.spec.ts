import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
  SupplierPriceConnector,
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

/** The supplier-master half of the row a toggle re-sends unchanged. */
const nationwideGstInclusive = {
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: null,
  priceConnectorKey: null
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
  it('re-sends the row’s own served states, GST basis and connector when toggling', async () => {
    const jbMetro = preset({
      id: 7,
      name: 'JB Metro SA & NT',
      url: 'https://www.jbmetro-sa-nt.com.au',
      servesNationwide: false,
      servedStates: ['SA', 'NT'],
      pricesIncludeGst: false,
      priceConnector: SupplierPriceConnector.JbMetro,
      priceConnectorKey: null
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
      servesNationwide: false,
      servedStates: ['SA', 'NT'],
      pricesIncludeGst: false,
      priceConnector: SupplierPriceConnector.JbMetro,
      priceConnectorKey: null
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

  it('offers no delete, because the API has none', async () => {
    const { host } = await render({ list: jest.fn(() => of([preset()])) });

    const labels = Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim());
    expect(labels).not.toContain('Delete');
    expect(labels).not.toContain('Remove');
  });
});
