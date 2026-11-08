import { TestBed } from '@angular/core/testing';
import { SupplierSearchTerms } from '../../../services/super-admin-master-products.service';
import {
  SuperAdminSupplierPreset,
  SupplierPriceConnector
} from '../../../services/super-admin-supplier-presets.service';
import { MasterProductSearchTermsComponent } from './master-product-search-terms.component';

const bankstown = { id: 12, name: 'JB Metro Bankstown', priceConnector: SupplierPriceConnector.JbMetro } as SuperAdminSupplierPreset;
const handPriced = { id: 13, name: 'Corner Store', priceConnector: null } as SuperAdminSupplierPreset;

const jbMetroEntry: SupplierSearchTerms = {
  supplierPresetId: null,
  priceConnector: SupplierPriceConnector.JbMetro,
  searchPhrases: ['Smiths Plain', 'Smiths'],
  flavourAliases: ['Plain']
};

async function render(entries: readonly SupplierSearchTerms[] = [], presets = [bankstown, handPriced]) {
  await TestBed.configureTestingModule({ imports: [MasterProductSearchTermsComponent] }).compileComponents();

  const fixture = TestBed.createComponent(MasterProductSearchTermsComponent);
  fixture.componentRef.setInput('entries', entries);
  fixture.componentRef.setInput('presets', presets);
  fixture.detectChanges();
  await fixture.whenStable();

  const emitted: SupplierSearchTerms[][] = [];
  fixture.componentInstance.entriesChange.subscribe((value) => emitted.push(value));

  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance, emitted };
}

function all(host: HTMLElement, id: string): HTMLElement[] {
  return Array.from(host.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
}

/**
 * The supplier search terms editor of the master product form (issue #1138): it saves, displays,
 * reorders and removes phrases and aliases, and reports the whole list upwards. Every rule is checked
 * again by the server, so what is asserted is what it shows and what it reports.
 */
describe('MasterProductSearchTermsComponent (issue #1138)', () => {
  it('offers every connector and every collected supplier, but not a hand-priced one', async () => {
    const { component } = await render();

    const labels = component.targetOptions.map((option) => option.label);
    expect(labels).toContain('All JB Metro suppliers');
    expect(labels).toContain('All Varnmill suppliers');
    expect(labels).toContain('JB Metro Bankstown');
    expect(labels).not.toContain('Corner Store');
  });

  it('shows a stored entry and reports a reorder in the new order', async () => {
    const { component, fixture, host, emitted } = await render([jbMetroEntry]);

    const phrases = all(host, 'search-terms-phrase') as HTMLInputElement[];
    expect(phrases.map((input) => input.value)).toEqual(['Smiths Plain', 'Smiths']);
    expect((all(host, 'search-terms-target')[0] as HTMLSelectElement).value).toBe(`connector:${SupplierPriceConnector.JbMetro}`);

    all(host, 'search-terms-phrase-up')[1].click();
    fixture.detectChanges();

    expect(component.rows[0].phrases).toEqual(['Smiths', 'Smiths Plain']);
    expect(emitted.at(-1)).toEqual([{ ...jbMetroEntry, searchPhrases: ['Smiths', 'Smiths Plain'] }]);
  });

  it('never offers a third phrase field', async () => {
    const { host } = await render([jbMetroEntry]);

    expect((all(host, 'search-terms-add-phrase')[0] as HTMLButtonElement).disabled).toBe(true);
  });

  it('adds an entry for one supplier and reports its preset id', async () => {
    const { component, emitted } = await render();

    component.addEntry();
    component.setTarget(0, 'preset:12');
    component.setPhrase(0, 0, 'Smiths Plain');
    component.addAlias(0);
    component.setAlias(0, 0, 'Plain');

    expect(emitted.at(-1)).toEqual([
      { supplierPresetId: 12, priceConnector: null, searchPhrases: ['Smiths Plain'], flavourAliases: ['Plain'] }
    ]);
  });

  it('does not offer a target another entry already uses', async () => {
    const { component } = await render([jbMetroEntry]);

    component.addEntry();

    expect(component.isTaken(`connector:${SupplierPriceConnector.JbMetro}`, 1)).toBe(true);
    expect(component.isTaken(`connector:${SupplierPriceConnector.JbMetro}`, 0)).toBe(false);
  });

  it('removes a phrase, an alias and a whole entry', async () => {
    const { component, emitted } = await render([jbMetroEntry]);

    component.removePhrase(0, 0);
    component.removeAlias(0, 0);
    expect(emitted.at(-1)).toEqual([{ ...jbMetroEntry, searchPhrases: ['Smiths'], flavourAliases: [] }]);

    component.removeEntry(0);
    expect(emitted.at(-1)).toEqual([]);
  });

  it('still shows an entry for a supplier the list does not hold, by its number', async () => {
    const { component } = await render([{ ...jbMetroEntry, supplierPresetId: 99, priceConnector: null }], []);

    expect(component.targetOptions.map((option) => option.label)).toContain('Supplier #99');
  });
});
