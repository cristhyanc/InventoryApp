import { GstClassification } from '../../../models/models';
import { PurchaseScanDraft, PurchaseScanLine, PurchaseScanValue } from '../../../models/purchase-scan';
import {
  UNRESOLVED_LEVY_MESSAGE,
  purchaseScanFill,
  scannedAmountOutsideLines
} from './purchase-scan-draft';

function read<T>(value: T): PurchaseScanValue<T> {
  return { value, provenance: 'read' };
}

function suggested<T>(value: T): PurchaseScanValue<T> {
  return { value, provenance: 'suggested' };
}

function stockLine(overrides: Partial<PurchaseScanLine> = {}): PurchaseScanLine {
  return {
    kind: 'stock',
    text: read('COKE 375ML 24PK'),
    packs: read(2),
    unitsPerPack: read(24),
    units: suggested(48),
    lineTotal: read(60),
    unitCost: suggested(1.25),
    productId: suggested(5),
    gst: { priceIncludesGst: 'yes', taxable: 'yes', marker: '*' },
    levyUnresolved: false,
    missing: [],
    ...overrides
  };
}

function draft(overrides: Partial<PurchaseScanDraft> = {}): PurchaseScanDraft {
  return {
    supplierId: suggested(7),
    supplier: read('Acme Supplies'),
    purchaseDate: read('2026-11-02'),
    invoiceNumber: read('INV-1234'),
    receiptTotal: read(60),
    gstTotal: read(5.45),
    deliveryCost: null,
    packageCost: null,
    lines: [stockLine()],
    usage: { month: '2026-11', used: 3, remaining: 97, limit: 100 },
    ...overrides
  };
}

const CATALOGUE = {
  products: [{ id: 5 }, { id: 6 }, { id: 7 }],
  suppliers: [{ id: 7 }, { id: 8 }]
};

describe('purchaseScanFill purchase-level fields (issue #1166)', () => {
  it('establishes the supplier, date, invoice number and receipt total the draft read', () => {
    const fill = purchaseScanFill(draft(), CATALOGUE);

    expect(fill.fields.supplierId).toBe(7);
    expect(fill.fields.purchaseDate).toBe('2026-11-02');
    expect(fill.fields.invoiceNumber).toBe('INV-1234');
    expect(fill.fields.receiptTotal).toBe(60);
  });

  it('establishes the delivery and package costs the draft carries', () => {
    const fill = purchaseScanFill(draft({ deliveryCost: read(9.5), packageCost: suggested(0.25) }), CATALOGUE);

    expect(fill.fields.deliveryCost).toBe(9.5);
    expect(fill.fields.packageCost).toBe(0.25);
  });

  it('establishes nothing for a value the receipt did not carry, so the form keeps what it had', () => {
    const fill = purchaseScanFill(
      draft({ supplierId: null, purchaseDate: null, invoiceNumber: null, receiptTotal: null }),
      CATALOGUE
    );

    expect(fill.fields.supplierId).toBeNull();
    expect(fill.fields.purchaseDate).toBeNull();
    expect(fill.fields.invoiceNumber).toBeNull();
    expect(fill.fields.receiptTotal).toBeNull();
    expect(fill.fields.deliveryCost).toBeNull();
    expect(fill.fields.packageCost).toBeNull();
  });

  /**
   * The backend only ever suggests an id of this business, so this is belt and braces rather than a
   * tenancy rule: a supplier the form cannot name would show as a blank picker, and a product it
   * cannot name would be saved blind.
   */
  it('drops a supplier the business does not have in its list', () => {
    const fill = purchaseScanFill(draft({ supplierId: suggested(99) }), CATALOGUE);

    expect(fill.fields.supplierId).toBeNull();
  });
});

describe('purchaseScanFill lines (issue #1166)', () => {
  it('fills one line per stock line with the product, units and unit cost of the draft', () => {
    const fill = purchaseScanFill(
      draft({ lines: [stockLine(), stockLine({ productId: suggested(6), units: suggested(6), unitCost: suggested(2) })] }),
      CATALOGUE
    );

    expect(fill.lines).toHaveLength(2);
    expect(fill.lines[0].productId).toBe(5);
    expect(fill.lines[0].quantity).toBe(48);
    expect(fill.lines[0].unitCost).toBe(1.25);
    expect(fill.lines[1].productId).toBe(6);
    expect(fill.lines[1].quantity).toBe(6);
    expect(fill.lines[1].unitCost).toBe(2);
  });

  it('keeps the unit cost the draft calculated as the entered figure, with the line total derived', () => {
    const fill = purchaseScanFill(draft({ lines: [stockLine({ unitCost: suggested(1.25) })] }), CATALOGUE);

    expect(fill.lines[0].priceEntryMode).toBe('unit');
    expect(fill.lines[0].unitCost).toBe(1.25);
    expect(fill.lines[0].lineTotal).toBe(60);
  });

  /**
   * A printed amount with no usable unit count: the amount is the entered figure, so typing the
   * units is all it takes to get a unit cost - rather than asking for the price again.
   */
  it('enters a printed amount as the line total when the draft could not work out a unit cost', () => {
    const fill = purchaseScanFill(
      draft({
        lines: [stockLine({ unitsPerPack: null, units: null, unitCost: null, lineTotal: read(60), missing: ['unitsPerPack'] })]
      }),
      CATALOGUE
    );

    expect(fill.lines[0].priceEntryMode).toBe('total');
    expect(fill.lines[0].lineTotal).toBe(60);
    expect(fill.lines[0].quantity).toBeNull();
    expect(fill.lines[0].unitCost).toBeNull();
  });

  it('leaves a line with no product when nothing was matched, or when the product is not in the list', () => {
    const fill = purchaseScanFill(
      draft({ lines: [stockLine({ productId: null }), stockLine({ productId: suggested(404) })] }),
      CATALOGUE
    );

    expect(fill.lines[0].productId).toBe(0);
    expect(fill.lines[1].productId).toBe(0);
  });

  it('classifies no line for GST, which only a person or a rule decides', () => {
    const fill = purchaseScanFill(draft(), CATALOGUE);

    expect(fill.lines[0].gstClassification).toBe(GstClassification.Unknown);
  });

  it('shows the receipt text and the pack maths on every filled line', () => {
    const fill = purchaseScanFill(draft(), CATALOGUE);

    expect(fill.lines[0].scan.text).toBe('COKE 375ML 24PK');
    expect(fill.lines[0].scan.packMaths).toBe('2 x 24-pack = 48');
  });

  it('shows no pack maths when the receipt did not print a pack size', () => {
    const fill = purchaseScanFill(
      draft({ lines: [stockLine({ unitsPerPack: null, units: null, unitCost: null, missing: ['unitsPerPack'] })] }),
      CATALOGUE
    );

    expect(fill.lines[0].scan.packMaths).toBeNull();
    expect(fill.lines[0].scan.missing).toEqual(['The pack size is not printed on the receipt. Enter the units.']);
  });

  it('names an unrecognised missing marker as something to check rather than dropping it', () => {
    const fill = purchaseScanFill(draft({ lines: [stockLine({ missing: ['somethingNewer'] })] }), CATALOGUE);

    expect(fill.lines[0].scan.missing).toEqual(['Check this line against the receipt.']);
  });

  /** Issue #1165's pack-size suggestion arrives as a `suggested` pack size and nothing else. */
  it('marks a pack size that was suggested rather than printed as coming from the last purchase', () => {
    const printed = purchaseScanFill(draft(), CATALOGUE);
    const fromHistory = purchaseScanFill(draft({ lines: [stockLine({ unitsPerPack: suggested(24) })] }), CATALOGUE);

    expect(printed.lines[0].scan.packSizeSuggested).toBe(false);
    expect(fromHistory.lines[0].scan.packSizeSuggested).toBe(true);
  });
});

describe('purchaseScanFill other and levy entries (issue #1166)', () => {
  const otherLine = stockLine({
    kind: 'other',
    text: read('A4 COPY PAPER'),
    productId: null,
    packs: read(1),
    unitsPerPack: read(1),
    units: suggested(1),
    lineTotal: read(8.5),
    unitCost: suggested(8.5)
  });

  it('starts a line that is not stock as skipped, so it is not saved as a purchase line', () => {
    const fill = purchaseScanFill(draft({ lines: [stockLine(), otherLine] }), CATALOGUE);

    expect(fill.lines[0].scan.skipped).toBe(false);
    expect(fill.lines[1].scan.kind).toBe('other');
    expect(fill.lines[1].scan.skipped).toBe(true);
  });

  it('returns an unresolved levy as its own entry and never as a line', () => {
    const levy = stockLine({
      kind: 'levy',
      text: read('CONTAINER DEPOSIT'),
      packs: null,
      unitsPerPack: null,
      units: null,
      lineTotal: read(1.1),
      unitCost: null,
      productId: null,
      levyUnresolved: true
    });

    const fill = purchaseScanFill(draft({ lines: [stockLine(), levy] }), CATALOGUE);

    expect(fill.lines).toHaveLength(1);
    expect(fill.levies).toEqual([{ text: 'CONTAINER DEPOSIT', amount: 1.1, message: UNRESOLVED_LEVY_MESSAGE }]);
  });

  it('ignores a kind the lines never carry, rather than guessing what to do with it', () => {
    const fill = purchaseScanFill(draft({ lines: [stockLine({ kind: 'delivery' })] }), CATALOGUE);

    expect(fill.lines).toHaveLength(0);
    expect(fill.levies).toHaveLength(0);
  });
});

describe('scannedAmountOutsideLines (issue #1166)', () => {
  it('counts a skipped line and an unresolved levy, so the receipt difference is explained', () => {
    const fill = purchaseScanFill(
      draft({
        lines: [
          stockLine(),
          stockLine({ kind: 'other', productId: null, lineTotal: read(8.5), unitCost: suggested(8.5), units: suggested(1) }),
          stockLine({ kind: 'levy', lineTotal: read(1.1), levyUnresolved: true, productId: null })
        ]
      }),
      CATALOGUE
    );

    expect(scannedAmountOutsideLines(fill.lines, fill.levies)).toBe(9.6);
  });

  it('stops counting a line the person turned into a stock line', () => {
    const fill = purchaseScanFill(
      draft({ lines: [stockLine({ kind: 'other', productId: null, lineTotal: read(8.5) })] }),
      CATALOGUE
    );
    fill.lines[0].scan.skipped = false;

    expect(scannedAmountOutsideLines(fill.lines, fill.levies)).toBe(0);
  });

  it('counts nothing for a form with no scanned entries at all', () => {
    expect(scannedAmountOutsideLines([], [])).toBe(0);
  });
});
