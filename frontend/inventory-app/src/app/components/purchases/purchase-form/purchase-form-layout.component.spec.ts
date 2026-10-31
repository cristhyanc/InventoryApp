import { ComponentFixture, TestBed } from '@angular/core/testing';
import { GstClassification, Product, PurchaseGstSummary } from '../../../models/models';
import { PurchaseFormFields, PurchaseFormLayoutComponent, PurchaseFormLine } from './purchase-form-layout.component';

function product(id: number, name: string): Product {
  return { id, name, unit: null } as Product;
}

function emptyForm(overrides: Partial<PurchaseFormFields> = {}): PurchaseFormFields {
  return {
    title: 'Acme — 24 Oct 2026',
    notes: '',
    totalAmount: null,
    deliveryCost: null,
    deliveryGstClassification: GstClassification.Unknown,
    packageCost: null,
    packageGstClassification: GstClassification.Unknown,
    purchaseDate: '2026-10-24',
    supplierId: '',
    ...overrides
  };
}

function newLine(overrides: Partial<PurchaseFormLine> = {}): PurchaseFormLine {
  return {
    productId: 0,
    quantity: 1,
    priceEntryMode: 'total',
    lineTotal: null,
    unitCost: null,
    gstClassification: GstClassification.Unknown,
    ...overrides
  };
}

/** A complete line: 72 units for $108.00, which is $1.50 a unit. */
function pricedLine(overrides: Partial<PurchaseFormLine> = {}): PurchaseFormLine {
  return newLine({ productId: 1, quantity: 72, lineTotal: 108, unitCost: 1.5, ...overrides });
}

describe('PurchaseFormLayoutComponent (issue #1029)', () => {
  let fixture: ComponentFixture<PurchaseFormLayoutComponent>;
  let component: PurchaseFormLayoutComponent;
  let host: HTMLElement;

  async function render(options: {
    form?: PurchaseFormFields;
    items?: PurchaseFormLine[];
    mode?: 'add' | 'edit';
    gstSummary?: PurchaseGstSummary | null;
  } = {}): Promise<void> {
    await TestBed.configureTestingModule({ imports: [PurchaseFormLayoutComponent] }).compileComponents();
    fixture = TestBed.createComponent(PurchaseFormLayoutComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('form', options.form ?? emptyForm());
    fixture.componentRef.setInput('items', options.items ?? []);
    fixture.componentRef.setInput('mode', options.mode ?? 'add');
    fixture.componentRef.setInput('products', [product(1, 'Red Bull Energy Drink'), product(2, 'Coke')]);
    fixture.componentRef.setInput('suppliers', [{ id: 5, name: 'Acme' }]);
    fixture.componentRef.setInput('gstSummary', options.gstSummary ?? null);
    fixture.detectChanges();
    await fixture.whenStable();
    host = fixture.nativeElement as HTMLElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  const byTestId = (id: string) => host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const text = () => host.textContent?.replace(/\s+/g, ' ') ?? '';

  function refresh(): void {
    fixture.detectChanges();
  }

  it('lays out the details, items, optional extras and summary, with no explanatory paragraphs', async () => {
    await render({ items: [newLine()] });

    const headings = Array.from(host.querySelectorAll('h2')).map((h) => h.textContent?.replace(/\s+/g, ' ').trim());
    expect(headings).toEqual(['Purchase details', 'Purchased items', 'Additional costs Show', 'Notes Show', 'Purchase summary']);
    expect(text()).not.toContain('For each line, type either');
    expect(text()).not.toContain('calculated and shown by the API');
    expect(host.querySelector('.page-subtitle')).toBeNull();
  });

  it('ends with Cancel on the left and Save purchase on the right in the sticky action bar', async () => {
    await render();

    const cancel = byTestId('purchase-cancel') as HTMLButtonElement;
    const save = byTestId('purchase-save') as HTMLButtonElement;
    expect(cancel.parentElement).toBe(save.parentElement);
    expect(cancel.parentElement?.className).toContain('sticky');
    expect(cancel.nextElementSibling).toBe(save);
    expect(save.type).toBe('submit');
    expect(save.textContent?.trim()).toBe('Save purchase');
  });

  it('shows no red errors on an untouched new line, and every problem once Save is pressed', async () => {
    await render({ items: [newLine()] });
    const submitted = jest.fn();
    component.submitted.subscribe(submitted);

    expect(host.querySelectorAll('.field-error')).toHaveLength(0);

    (byTestId('purchase-save') as HTMLButtonElement).click();
    refresh();

    expect(submitted).toHaveBeenCalledTimes(1);
    expect(text()).toContain('Choose a product.');
    expect(text()).toContain('Enter the total price paid for this line.');
  });

  it('points out a line problem once that line has been touched', async () => {
    await render({ items: [newLine(), newLine()] });

    const quantities = host.querySelectorAll<HTMLInputElement>('[data-testid="purchase-item-quantity"]');
    quantities[0].dispatchEvent(new Event('blur'));
    refresh();

    expect(host.querySelectorAll('.field-error')).toHaveLength(2); // product and price, first line only
    expect(text()).toContain('Choose a product.');
  });

  it('points out a missing title once the field was left empty', async () => {
    await render({ form: emptyForm({ title: '' }) });
    expect(text()).not.toContain('Enter a purchase title.');

    host.querySelector<HTMLInputElement>('#title')!.dispatchEvent(new Event('blur'));
    refresh();

    expect(text()).toContain('Enter a purchase title.');
  });

  it('reports a typed title, and a supplier or date change, for the page to act on', async () => {
    await render();
    const edited = jest.fn();
    const details = jest.fn();
    component.titleEdited.subscribe(edited);
    component.detailsChange.subscribe(details);

    const title = host.querySelector<HTMLInputElement>('#title')!;
    title.value = 'My own title';
    title.dispatchEvent(new Event('input'));
    const supplier = host.querySelector<HTMLSelectElement>('#supplierId')!;
    supplier.value = supplier.options[1].value;
    supplier.dispatchEvent(new Event('change'));

    expect(edited).toHaveBeenCalledTimes(1);
    expect(component.form.title).toBe('My own title');
    expect(details).toHaveBeenCalledTimes(1);
    expect(component.form.supplierId).toBe(5);
  });

  it('chooses a line product through the searchable selector', async () => {
    const line = newLine();
    await render({ items: [line] });

    const box = byTestId('purchase-item-product') as HTMLInputElement;
    box.value = 'coke';
    box.dispatchEvent(new Event('input'));
    refresh();
    host.querySelector<HTMLElement>('[data-testid="purchase-product-option"]')!.click();
    refresh();

    expect(line.productId).toBe(2);
  });

  it('shows a saved line product as fixed on Edit, and offers the selector for a new line', async () => {
    await render({ mode: 'edit', items: [pricedLine({ id: 11 }), newLine()] });

    expect(host.querySelectorAll('[data-testid="purchase-item-product-fixed"]')).toHaveLength(1);
    expect(host.querySelectorAll('[data-testid="purchase-item-product"]')).toHaveLength(1);
  });

  it('offers Taxable, GST-free and Not classified exactly once in each GST selector', async () => {
    await render({ items: [pricedLine()], form: emptyForm({ deliveryCost: 5 }) });

    const selects = host.querySelectorAll<HTMLSelectElement>('select[data-testid="purchase-item-gst"], select[data-testid="delivery-gst"]');
    expect(selects).toHaveLength(2);
    selects.forEach((select) => {
      const labels = Array.from(select.options).map((o) => o.textContent?.trim()).sort();
      expect(labels).toEqual(['GST-free', 'Not classified', 'Taxable']);
    });
  });

  it('keeps the price and GST explanations in help buttons beside the column headings', async () => {
    await render({ items: [pricedLine()] });

    const helpLabels = Array.from(host.querySelectorAll('app-help-toggletip button[aria-controls]')).map((b) => b.getAttribute('aria-label'));
    expect(helpLabels).toEqual(expect.arrayContaining(['About unit price', 'About line total', 'About GST', 'About receipt total']));
  });

  it('reports Add product and a line removal for the page to act on', async () => {
    await render({ items: [pricedLine(), pricedLine()] });
    const added = jest.fn();
    const removed: number[] = [];
    component.addLine.subscribe(added);
    component.removeLine.subscribe((index) => removed.push(index));

    (byTestId('purchase-add-item') as HTMLButtonElement).click();
    host.querySelectorAll<HTMLButtonElement>('[data-testid="purchase-item-remove"]')[1].click();

    expect(added).toHaveBeenCalledTimes(1);
    expect(removed).toEqual([1]);
  });

  describe('optional sections', () => {
    it('start collapsed while empty and expand to show delivery, packaging and notes', async () => {
      await render();

      expect(host.querySelector('#deliveryCost')).toBeNull();
      expect(host.querySelector('#notes')).toBeNull();

      (byTestId('purchase-additional-costs-toggle') as HTMLButtonElement).click();
      (byTestId('purchase-notes-toggle') as HTMLButtonElement).click();
      refresh();

      expect(host.querySelector('#deliveryCost')).not.toBeNull();
      expect(host.querySelector('#packageCost')).not.toBeNull();
      expect(host.querySelector('#notes')).not.toBeNull();
      expect(byTestId('purchase-additional-costs-toggle')?.getAttribute('aria-expanded')).toBe('true');
    });

    it('start expanded when they hold a value, as an Edit of such a purchase does', async () => {
      await render({ mode: 'edit', form: emptyForm({ deliveryCost: 15, packageCost: 5, notes: 'Weekly restock' }) });

      expect(host.querySelector('#deliveryCost')).not.toBeNull();
      expect(byTestId('delivery-gst')).not.toBeNull();
      expect(byTestId('package-gst')).not.toBeNull();
      expect(host.querySelector<HTMLTextAreaElement>('#notes')).not.toBeNull();
    });

    it('never discard a value when collapsed', async () => {
      const form = emptyForm({ deliveryCost: 15, notes: 'Weekly restock' });
      await render({ form });

      (byTestId('purchase-additional-costs-toggle') as HTMLButtonElement).click();
      (byTestId('purchase-notes-toggle') as HTMLButtonElement).click();
      refresh();

      expect(host.querySelector('#deliveryCost')).toBeNull();
      expect(form.deliveryCost).toBe(15);
      expect(form.notes).toBe('Weekly restock');
      // The charge still counts in the summary while its section is collapsed.
      expect(text()).toContain('Delivery$15.00');
    });
  });

  describe('summary', () => {
    it('shows the subtotal, the charges that exist and the purchase total', async () => {
      await render({ items: [pricedLine(), pricedLine({ quantity: 84, lineTotal: 84, unitCost: 1 })], form: emptyForm({ deliveryCost: 15 }) });

      expect(byTestId('purchase-summary-subtotal')?.textContent).toContain('$192.00');
      expect(text()).toContain('Delivery$15.00');
      expect(text()).not.toContain('Packaging$');
      expect(byTestId('purchase-summary-total')?.textContent).toContain('$207.00');
    });

    it('says GST is calculated when saved rather than inventing a figure on Add', async () => {
      await render({ items: [pricedLine({ gstClassification: GstClassification.Taxable })] });

      expect(byTestId('purchase-summary-gst')?.textContent).toContain('Calculated when saved');
      expect(byTestId('purchase-summary-gst')?.textContent).not.toContain('$');
    });

    it('shows the last saved GST on Edit, with anything still not classified', async () => {
      await render({ mode: 'edit', gstSummary: { inputGst: 28.85, unresolvedComponentCount: 1, unresolvedAmount: 25 } });

      const gst = byTestId('purchase-summary-gst')?.textContent?.replace(/\s+/g, ' ') ?? '';
      expect(gst).toContain('$28.85');
      expect(gst).toContain('as last saved');
      expect(gst).toContain('1 not classified ($25.00)');
    });
  });

  describe('receipt total', () => {
    async function withReceipt(totalAmount: number | null): Promise<string> {
      await render({ items: [pricedLine()], form: emptyForm({ totalAmount }) });
      return byTestId('purchase-receipt-comparison')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
    }

    it('says nothing while blank', async () => {
      expect(await withReceipt(null)).toBe('');
    });

    it('says the receipt matches', async () => {
      expect(await withReceipt(108)).toBe('Matches receipt');
    });

    it('shows the difference, including against an entered zero', async () => {
      expect(await withReceipt(110)).toBe('Difference: $2.00');
      TestBed.resetTestingModule();
      expect(await withReceipt(0)).toBe('Difference: $108.00');
    });

    it('is labelled Receipt total and stays the purchase total amount field', async () => {
      const form = emptyForm();
      await render({ items: [pricedLine()], form });

      const input = host.querySelector<HTMLInputElement>('#totalAmount')!;
      expect(host.querySelector('label[for="totalAmount"]')?.textContent).toContain('Receipt total (optional)');
      input.value = '108';
      input.dispatchEvent(new Event('input'));
      refresh();

      expect(form.totalAmount).toBe(108);
      expect(byTestId('purchase-summary-total')?.textContent).toContain('$108.00');
    });
  });
});
