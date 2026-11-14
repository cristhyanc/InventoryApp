import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';
import { GstClassification, Product, PurchaseGstSummary } from '../../../models/models';
import { PurchaseFormFields, PurchaseFormLayoutComponent, PurchaseFormLine } from './purchase-form-layout.component';
import {
  UNRESOLVED_LEVY_MESSAGE,
  PurchaseScannedLevy,
  PurchaseScannedLine
} from './purchase-scan-draft';

function product(id: number, name: string): Product {
  return { id, name, unit: null } as Product;
}

function emptyForm(overrides: Partial<PurchaseFormFields> = {}): PurchaseFormFields {
  return {
    title: 'Acme — 24 Oct 2026',
    notes: '',
    invoiceNumber: '',
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
    scannedReceipt?: File | null;
    scannedLevies?: readonly PurchaseScannedLevy[];
    offerSaveWithoutReceipt?: boolean;
    error?: string;
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
    fixture.componentRef.setInput('scannedReceipt', options.scannedReceipt ?? null);
    fixture.componentRef.setInput('scannedLevies', options.scannedLevies ?? []);
    fixture.componentRef.setInput('offerSaveWithoutReceipt', options.offerSaveWithoutReceipt ?? false);
    fixture.componentRef.setInput('error', options.error ?? '');
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

  /**
   * Issue #1161: the receipt field shrinks a chosen photo before it reports it, so saving in the
   * meantime would create the purchase with no receipt at all. Save waits for it.
   */
  it('holds Save while the receipt field is still preparing a chosen photo', async () => {
    await render({ items: [pricedLine()] });
    const submitted = jest.fn();
    component.submitted.subscribe(submitted);
    const receipt = host.querySelector<HTMLElement>('[data-testid="purchase-receipt"]');
    expect(receipt).not.toBeNull();

    component.onReceiptPreparing(true);
    refresh();

    expect((byTestId('purchase-save') as HTMLButtonElement).disabled).toBe(true);
    component.submit();
    expect(submitted).not.toHaveBeenCalled();

    component.onReceiptPreparing(false);
    refresh();

    expect((byTestId('purchase-save') as HTMLButtonElement).disabled).toBe(false);
    component.submit();
    expect(submitted).toHaveBeenCalledTimes(1);
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

  describe('invoice number (issue #1162)', () => {
    it('offers an optional Invoice number field in the purchase details section', async () => {
      await render();

      const field = host.querySelector<HTMLInputElement>('#invoiceNumber');
      expect(field).not.toBeNull();
      expect(field!.required).toBe(false);
      expect(field!.maxLength).toBe(100);
      const label = host.querySelector<HTMLLabelElement>('label[for="invoiceNumber"]');
      expect(label?.textContent?.trim()).toBe('Invoice number (optional)');
      // In the header section beside the supplier, date and title - not in an optional panel.
      expect(field!.closest('section')?.querySelector('h2')?.textContent?.replace(/\s+/g, ' ').trim())
        .toBe('Purchase details');
    });

    it('shows the number the form holds and writes what is typed back to it', async () => {
      const form = emptyForm({ invoiceNumber: 'INV-123' });
      await render({ form });

      const field = host.querySelector<HTMLInputElement>('#invoiceNumber')!;
      expect(field.value).toBe('INV-123');

      field.value = 'INV-456';
      field.dispatchEvent(new Event('input'));

      expect(form.invoiceNumber).toBe('INV-456');
    });

    it('never reports a problem for an empty invoice number, even after Save was pressed', async () => {
      await render({ form: emptyForm({ invoiceNumber: '' }) });

      (byTestId('purchase-save') as HTMLButtonElement).click();
      refresh();

      expect(host.querySelectorAll('.field-error')).toHaveLength(0);
      expect(host.querySelector<HTMLInputElement>('#invoiceNumber')!.getAttribute('aria-invalid')).toBeNull();
    });
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

  /** Issue #1166: what a scanned receipt adds to this layout, and what it must leave alone. */
  describe('a scanned receipt', () => {
    function scan(overrides: Partial<PurchaseScannedLine> = {}): PurchaseScannedLine {
      return {
        text: 'COKE 375ML 24PK',
        kind: 'stock',
        packs: 2,
        unitsPerPack: 24,
        units: 48,
        packMaths: '2 x 24-pack = 48',
        packSizeSuggested: false,
        productSuggested: true,
        receiptAmount: 60,
        missing: [],
        skipped: false,
        ...overrides
      };
    }

    function scannedLine(overrides: Partial<PurchaseFormLine> = {}, scanOverrides: Partial<PurchaseScannedLine> = {}): PurchaseFormLine {
      return newLine({
        productId: 1,
        quantity: 48,
        priceEntryMode: 'unit',
        unitCost: 1.25,
        lineTotal: 60,
        scan: scan(scanOverrides),
        ...overrides
      });
    }

    const levy: PurchaseScannedLevy = {
      text: 'CONTAINER DEPOSIT',
      amount: 1.1,
      message: UNRESOLVED_LEVY_MESSAGE
    };

    it('shows the receipt text and the pack maths on the line, so the pack maths can be checked', async () => {
      await render({ items: [scannedLine()] });

      expect(byTestId('purchase-item-scan-text')?.textContent?.trim()).toBe('COKE 375ML 24PK');
      expect(byTestId('purchase-item-scan-pack-maths')?.textContent?.trim()).toBe('2 x 24-pack = 48');
      expect(byTestId('purchase-item-scan-product-source')?.textContent).toContain('Suggested product');
    });

    it('marks a pack size that came from the last purchase rather than the receipt', async () => {
      await render({ items: [scannedLine({}, { packSizeSuggested: true })] });

      expect(byTestId('purchase-item-scan-pack-source')?.textContent).toContain('Pack size from your last purchase');
    });

    /**
     * A scanned line is one the application filled in and is asking to have checked, so what it
     * still needs is shown at once - not after a blur or a Save, which would hide exactly the
     * figures that need attention.
     */
    it('highlights a scanned line with no product and no pack size straight away', async () => {
      await render({
        items: [
          scannedLine(
            { productId: 0, quantity: null, unitCost: null, lineTotal: 60, priceEntryMode: 'total' },
            { units: null, unitsPerPack: null, packMaths: null, productSuggested: false, missing: ['The pack size is not printed on the receipt. Enter the units.'] }
          )
        ]
      });

      expect(text()).toContain('Choose a product.');
      expect(text()).toContain('Enter a whole number of units above zero.');
      expect(byTestId('purchase-item-scan-missing')?.textContent).toContain('The pack size is not printed');
      expect(byTestId('purchase-item-scan-pack-maths')).toBeNull();
    });

    /**
     * The scan's list of what it could not complete never changes, so it has to stop being shown
     * once the person has completed the line: an instruction nobody can act on beside a finished
     * line is worse than no instruction.
     */
    it('stops explaining what the receipt did not print once the line is complete', async () => {
      const line = scannedLine(
        { productId: 1, quantity: null, unitCost: null, lineTotal: 60, priceEntryMode: 'total' },
        { units: null, unitsPerPack: null, packMaths: null, missing: ['The pack size is not printed on the receipt. Enter the units.'] }
      );
      await render({ items: [line] });
      expect(byTestId('purchase-item-scan-missing')).not.toBeNull();

      component.onQuantityChange(line, 80);
      refresh();

      expect(byTestId('purchase-item-scan-missing')).toBeNull();
      // The receipt's own text and the pack maths that could be shown are still there.
      expect(byTestId('purchase-item-scan-text')).not.toBeNull();
    });

    it('offers Not stock, skip on a line the receipt said is not stock, and counts it in neither total', async () => {
      const skipped = scannedLine(
        { productId: 0, quantity: 1, unitCost: 8.5, lineTotal: 8.5 },
        { kind: 'other', text: 'A4 COPY PAPER', productSuggested: false, receiptAmount: 8.5, skipped: true }
      );
      await render({ items: [scannedLine(), skipped], form: emptyForm({ totalAmount: 68.5 }) });

      expect((byTestId('purchase-item-skip') as HTMLInputElement).checked).toBe(true);
      expect(byTestId('purchase-item-count')?.textContent?.trim()).toBe('1 item');
      expect(byTestId('purchase-summary-subtotal')?.textContent).toContain('$60.00');
      expect(byTestId('purchase-summary-total')?.textContent).toContain('$60.00');
      // The skipped amount is what makes the receipt reconcile rather than show a difference.
      expect(byTestId('purchase-receipt-comparison')?.textContent).toContain('Matches receipt');
      expect(byTestId('purchase-receipt-outside-lines')?.textContent).toContain('$8.50');
    });

    it('shows no problems on a skipped line, because none of it is saved', async () => {
      await render({
        items: [
          scannedLine(
            { productId: 0, quantity: null, unitCost: null },
            { kind: 'other', productSuggested: false, skipped: true, missing: ['Enter the price.'] }
          )
        ]
      });

      expect(host.querySelectorAll('.field-error')).toHaveLength(0);
      expect(byTestId('purchase-item-scan-missing')).toBeNull();
    });

    it('puts a skipped line into the purchase when a product is chosen for it', async () => {
      const line = scannedLine({ productId: 0 }, { kind: 'other', productSuggested: false, skipped: true });
      await render({ items: [line] });

      component.onProductChange(line, 2);
      refresh();

      expect(line.scan?.skipped).toBe(false);
      expect((byTestId('purchase-item-skip') as HTMLInputElement).checked).toBe(false);
      expect(byTestId('purchase-item-count')?.textContent?.trim()).toBe('1 item');
    });

    it('takes a line back out of the purchase when Not stock, skip is turned on again', async () => {
      const line = scannedLine({}, { kind: 'other' });
      await render({ items: [line] });

      const checkbox = byTestId('purchase-item-skip') as HTMLInputElement;
      checkbox.checked = true;
      checkbox.dispatchEvent(new Event('change'));
      refresh();

      expect(line.scan?.skipped).toBe(true);
      expect(byTestId('purchase-item-count')?.textContent?.trim()).toBe('0 items');
    });

    it('shows an unresolved levy in its own row with no product picker, and counts it in the comparison', async () => {
      await render({ items: [scannedLine()], scannedLevies: [levy], form: emptyForm({ totalAmount: 61.1 }) });

      const row = byTestId('purchase-scan-levy')!;
      expect(row.textContent).toContain('CONTAINER DEPOSIT');
      expect(row.textContent).toContain(UNRESOLVED_LEVY_MESSAGE);
      expect(byTestId('purchase-scan-levy-amount')?.textContent).toContain('$1.10');
      expect(row.querySelector('app-purchase-product-picker')).toBeNull();
      expect(row.querySelector('input')).toBeNull();
      // One line only: a levy is never one of them.
      expect(host.querySelectorAll('[data-testid="purchase-item"]')).toHaveLength(1);
      expect(byTestId('purchase-summary-total')?.textContent).toContain('$60.00');
      expect(byTestId('purchase-receipt-comparison')?.textContent).toContain('Matches receipt');
    });

    it('shows the scanned receipt before the lines in the document, so it sits above them on a phone', async () => {
      const createObjectURL = jest.fn(() => 'blob:receipt');
      Object.defineProperty(URL, 'createObjectURL', { value: createObjectURL, configurable: true });
      Object.defineProperty(URL, 'revokeObjectURL', { value: jest.fn(), configurable: true });

      await render({
        items: [scannedLine()],
        scannedReceipt: new File(['bytes'], 'receipt.jpg', { type: 'image/jpeg' })
      });

      const preview = byTestId('purchase-scanned-receipt')!;
      const firstLine = byTestId('purchase-item')!;
      expect(preview.compareDocumentPosition(firstLine) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(byTestId('purchase-scanned-receipt-image')?.getAttribute('src')).toBe('blob:receipt');
      // Second column from `xl` up, which is what puts it beside the lines on a wide screen.
      expect(preview.closest('app-purchase-receipt-preview')?.parentElement?.className).toContain('xl:col-start-2');
    });

    it('renders no preview and no levy row on a form that has scanned nothing', async () => {
      await render({ items: [pricedLine()] });

      expect(byTestId('purchase-scanned-receipt')).toBeNull();
      expect(byTestId('purchase-scan-levy')).toBeNull();
      expect(byTestId('purchase-item-scan')).toBeNull();
      expect(byTestId('purchase-receipt-outside-lines')).toBeNull();
    });
  });

  /** Issue #1166 / #968: the storage allowance refused the receipt, and the purchase is still recordable. */
  describe('saving without the receipt', () => {
    it('offers the choice beside the refusal, and reports it only when it is chosen', async () => {
      await render({
        items: [pricedLine()],
        error: 'This upload would exceed the business’s attachment storage allowance.',
        offerSaveWithoutReceipt: true
      });
      const chosen = jest.fn();
      component.saveWithoutReceipt.subscribe(chosen);

      expect(byTestId('purchase-error')?.textContent).toContain('attachment storage allowance');
      expect(chosen).not.toHaveBeenCalled();

      (byTestId('purchase-save-without-receipt') as HTMLButtonElement).click();

      expect(chosen).toHaveBeenCalledTimes(1);
    });

    it('offers nothing of the kind for any other refusal', async () => {
      await render({ items: [pricedLine()], error: 'One or more purchase products do not exist.' });

      expect(byTestId('purchase-error')).not.toBeNull();
      expect(byTestId('purchase-save-without-receipt')).toBeNull();
    });
  });

  /**
   * Issue #1246: a purchase is never dated later than today in the business's own time zone,
   * because the stock such a purchase adds cannot be refilled or written off until that date
   * arrives. The date input stops offering it, the form explains it, and Save does not send it -
   * including when a receipt scan is what filled the date in.
   *
   * The clock is held at 13:30 UTC on 14 November 2026, which is 00:30 on the 15th in Sydney: the
   * business day is already the 15th while the UTC date is still the 14th, which is the boundary the
   * issue asks about.
   */
  describe('the purchase date limit', () => {
    const justAfterSydneyMidnight = new Date('2026-11-14T13:30:00Z');

    afterEach(() => jest.useRealTimers());

    async function renderAtSydneyMidnight(purchaseDate: string, options: Parameters<typeof render>[0] = {}): Promise<void> {
      await render({ ...options, form: emptyForm({ purchaseDate }) });
      TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');
      jest.useFakeTimers().setSystemTime(justAfterSydneyMidnight);
      refresh();
    }

    const dateInput = () => host.querySelector<HTMLInputElement>('#purchaseDate')!;

    it('offers no later date than the business date, even while the UTC date is the day before', async () => {
      await renderAtSydneyMidnight('2026-11-15');

      expect(dateInput().getAttribute('max')).toBe('2026-11-15');
      expect(justAfterSydneyMidnight.toISOString().slice(0, 10)).toBe('2026-11-14');
    });

    it('accepts the business date itself and saves it', async () => {
      await renderAtSydneyMidnight('2026-11-15');
      const submitted = jest.fn();
      component.submitted.subscribe(submitted);

      expect(component.purchaseDateProblem).toBeNull();
      expect(byTestId('purchase-date-error')).toBeNull();

      component.submit();

      expect(submitted).toHaveBeenCalledTimes(1);
    });

    it('explains a later date and does not save it', async () => {
      await renderAtSydneyMidnight('2026-11-16');
      const submitted = jest.fn();
      component.submitted.subscribe(submitted);

      expect(byTestId('purchase-date-error')?.textContent).toContain(
        'Purchase date cannot be later than today'
      );
      expect(dateInput().getAttribute('aria-invalid')).toBe('true');

      component.submit();

      expect(submitted).not.toHaveBeenCalled();
    });

    /**
     * The scanned-receipt case the issue names: the date was filled in by the scan rather than
     * typed, so the explanation is there before anything is touched, and Save sends nothing.
     */
    it('explains a future date a receipt scan filled in, instead of saving it', async () => {
      await renderAtSydneyMidnight('2026-11-21', { items: [pricedLine()] });
      const submitted = jest.fn();
      component.submitted.subscribe(submitted);

      expect(byTestId('purchase-date-error')?.textContent).toContain(
        'Purchase date cannot be later than today'
      );

      component.submit();

      expect(submitted).not.toHaveBeenCalled();
    });

    it('leaves the date alone, and still saves, while the business time zone is unknown', async () => {
      await render({ form: emptyForm({ purchaseDate: '2026-10-24' }) });
      const submitted = jest.fn();
      component.submitted.subscribe(submitted);

      expect(component.purchaseDateProblem).toBeNull();
      expect(dateInput().getAttribute('max')).not.toBe('');

      component.submit();

      expect(submitted).toHaveBeenCalledTimes(1);
    });
  });
});
