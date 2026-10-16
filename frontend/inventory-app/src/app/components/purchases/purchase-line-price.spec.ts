import {
  DEFAULT_PRICE_ENTRY_MODE,
  PRICE_ENTRY_MODE_OPTIONS,
  PurchaseLinePrice,
  applyPriceEntryMode,
  isPurchaseLinePriceComplete,
  isValidLineAmount,
  isValidLineQuantity,
  lineTotalFromUnitCost,
  purchaseLinePriceProblem,
  roundToCents,
  savedLineAmount,
  syncPurchaseLinePrice,
  unitCostFromLineTotal
} from './purchase-line-price';

/** A purchase line as either form holds it while it is being filled in. */
function line(overrides: Partial<PurchaseLinePrice> = {}): PurchaseLinePrice {
  return {
    quantity: 1,
    priceEntryMode: DEFAULT_PRICE_ENTRY_MODE,
    lineTotal: null,
    unitCost: null,
    ...overrides
  };
}

describe('purchase line price entry modes (issue #707)', () => {
  it('offers both entry modes and defaults to the total paid for the line', () => {
    expect(DEFAULT_PRICE_ENTRY_MODE).toBe('total');
    expect(PRICE_ENTRY_MODE_OPTIONS.map((option) => option.value)).toEqual(['total', 'unit']);
    expect(PRICE_ENTRY_MODE_OPTIONS.map((option) => option.label))
      .toEqual(['Total purchase price', 'Unit price']);
  });
});

describe('purchase line total to unit cost (issue #707)', () => {
  it('divides the line total by the purchased units', () => {
    expect(unitCostFromLineTotal(32.5, 20)).toBe(1.625);
  });

  it('submits the unrounded quotient, so the saved line amount reproduces the entered total', () => {
    const unitCost = unitCostFromLineTotal(100, 300);

    expect(unitCost).toBe(100 / 300);
    expect(unitCost).not.toBe(0.3333);
    // The point of not rounding before saving: the server's own line amount,
    // round(quantity * unitCost, 2), has to come back to the amount that was entered.
    expect(roundToCents(300 * unitCost!)).toBe(100);
    expect(roundToCents(300 * 0.3333)).toBe(99.99);
  });

  it('has no unit cost for a quantity that is not a whole number of units above zero', () => {
    expect(unitCostFromLineTotal(32.5, 0)).toBeNull();
    expect(unitCostFromLineTotal(32.5, -4)).toBeNull();
    expect(unitCostFromLineTotal(32.5, 2.5)).toBeNull();
    expect(unitCostFromLineTotal(32.5, null)).toBeNull();
    expect(unitCostFromLineTotal(32.5, Number.NaN)).toBeNull();
  });

  it('has no unit cost for a blank, negative or non-finite line total', () => {
    expect(unitCostFromLineTotal(null, 20)).toBeNull();
    expect(unitCostFromLineTotal(undefined, 20)).toBeNull();
    expect(unitCostFromLineTotal(-1, 20)).toBeNull();
    expect(unitCostFromLineTotal(Number.NaN, 20)).toBeNull();
    expect(unitCostFromLineTotal(Number.POSITIVE_INFINITY, 20)).toBeNull();
  });

  it('derives a zero unit cost from a zero line total rather than refusing it', () => {
    expect(unitCostFromLineTotal(0, 20)).toBe(0);
  });
});

describe('purchase line unit cost to line total (issue #707)', () => {
  it('multiplies the unit price by the purchased units and rounds to cents', () => {
    expect(lineTotalFromUnitCost(1.6625, 24)).toBe(39.9);
    expect(lineTotalFromUnitCost(1.625, 20)).toBe(32.5);
  });

  it('has no line total for an unusable quantity or price', () => {
    expect(lineTotalFromUnitCost(1.5, 0)).toBeNull();
    expect(lineTotalFromUnitCost(1.5, 1.5)).toBeNull();
    expect(lineTotalFromUnitCost(null, 10)).toBeNull();
    expect(lineTotalFromUnitCost(-1, 10)).toBeNull();
    expect(lineTotalFromUnitCost(Number.NaN, 10)).toBeNull();
  });

  it('rounds cent halves away from zero, as every purchase amount is rounded', () => {
    expect(roundToCents(1.005)).toBe(1.01);
    expect(roundToCents(39.900000000000006)).toBe(39.9);
    expect(roundToCents(0)).toBe(0);
  });

  it('reports the amount a line will save, which is the rounded quantity times unit cost', () => {
    expect(savedLineAmount(line({ quantity: 300, unitCost: 100 / 300 }))).toBe(100);
    expect(savedLineAmount(line({ quantity: 3, unitCost: 2 }))).toBe(6);
    expect(savedLineAmount(line({ quantity: 3, unitCost: null }))).toBe(0);
  });
});

describe('purchase line price staleness (issue #707)', () => {
  it('re-derives the unit cost when the quantity changes under an entered line total', () => {
    const subject = line({ quantity: 20, lineTotal: 32.5, unitCost: 1.625 });

    subject.quantity = 10;
    syncPurchaseLinePrice(subject);

    expect(subject.lineTotal).toBe(32.5);
    expect(subject.unitCost).toBe(3.25);
  });

  it('re-derives the line total when the quantity changes under an entered unit price', () => {
    const subject = line({ quantity: 24, priceEntryMode: 'unit', unitCost: 1.6625, lineTotal: 39.9 });

    subject.quantity = 12;
    syncPurchaseLinePrice(subject);

    expect(subject.unitCost).toBe(1.6625);
    expect(subject.lineTotal).toBe(19.95);
  });

  it('clears the derived value rather than leaving a stale one when the entry is emptied', () => {
    const subject = line({ quantity: 20, lineTotal: 32.5, unitCost: 1.625 });

    subject.lineTotal = null;
    syncPurchaseLinePrice(subject);

    expect(subject.unitCost).toBeNull();
  });

  it('clears the derived unit cost rather than dividing by a zero quantity', () => {
    const subject = line({ quantity: 20, lineTotal: 32.5, unitCost: 1.625 });

    subject.quantity = 0;
    syncPurchaseLinePrice(subject);

    expect(subject.unitCost).toBeNull();
  });

  it('keeps the unit cost it would submit when the entry mode changes, and shows the matching total', () => {
    const subject = line({ quantity: 300, lineTotal: 100, unitCost: 100 / 300 });

    applyPriceEntryMode(subject, 'unit');

    expect(subject.priceEntryMode).toBe('unit');
    expect(subject.unitCost).toBe(100 / 300);
    expect(subject.lineTotal).toBe(100);

    applyPriceEntryMode(subject, 'total');

    expect(subject.priceEntryMode).toBe('total');
    expect(subject.unitCost).toBe(100 / 300);
    expect(subject.lineTotal).toBe(100);
  });

  it('fills the line total from the unit price when a unit-priced line switches to total entry', () => {
    const subject = line({ quantity: 24, priceEntryMode: 'unit', unitCost: 1.6625, lineTotal: null });

    applyPriceEntryMode(subject, 'total');

    expect(subject.lineTotal).toBe(39.9);
    expect(subject.unitCost).toBe(1.6625);
  });
});

describe('purchase line price validation (issue #707)', () => {
  it('accepts only a whole number of units above zero', () => {
    expect(isValidLineQuantity(1)).toBe(true);
    expect(isValidLineQuantity(24)).toBe(true);
    expect(isValidLineQuantity(0)).toBe(false);
    expect(isValidLineQuantity(-1)).toBe(false);
    expect(isValidLineQuantity(1.5)).toBe(false);
    expect(isValidLineQuantity(null)).toBe(false);
    expect(isValidLineQuantity(Number.NaN)).toBe(false);
    expect(isValidLineQuantity(Number.POSITIVE_INFINITY)).toBe(false);
  });

  it('accepts only a finite, non-negative amount of money', () => {
    expect(isValidLineAmount(0)).toBe(true);
    expect(isValidLineAmount(32.5)).toBe(true);
    expect(isValidLineAmount(-0.01)).toBe(false);
    expect(isValidLineAmount(null)).toBe(false);
    expect(isValidLineAmount(undefined)).toBe(false);
    expect(isValidLineAmount(Number.NaN)).toBe(false);
    expect(isValidLineAmount(Number.POSITIVE_INFINITY)).toBe(false);
    expect(isValidLineAmount(Number.NEGATIVE_INFINITY)).toBe(false);
  });

  it('names the quantity first, because an unusable quantity is also why no unit cost exists', () => {
    expect(purchaseLinePriceProblem(line({ quantity: 0, lineTotal: 32.5 })))
      .toBe('Enter a whole number of units above zero.');
    expect(purchaseLinePriceProblem(line({ quantity: 2.5, lineTotal: 32.5 })))
      .toBe('Enter a whole number of units above zero.');
  });

  it('names the amount the chosen entry mode actually asks for', () => {
    expect(purchaseLinePriceProblem(line({ quantity: 20, lineTotal: null })))
      .toBe('Enter the total price paid for this line.');
    expect(purchaseLinePriceProblem(line({ quantity: 20, priceEntryMode: 'unit', unitCost: null })))
      .toBe('Enter a unit price.');
  });

  it('reports nothing for a line that is ready to save', () => {
    const subject = line({ quantity: 20, lineTotal: 32.5, unitCost: 1.625 });

    expect(purchaseLinePriceProblem(subject)).toBeNull();
    expect(isPurchaseLinePriceComplete(subject)).toBe(true);
  });

  it('refuses a line whose unit cost is not a finite amount, whatever its entry says', () => {
    expect(isPurchaseLinePriceComplete(line({ quantity: 20, lineTotal: 32.5, unitCost: Number.NaN })))
      .toBe(false);
    expect(isPurchaseLinePriceComplete(line({ quantity: 20, lineTotal: 32.5, unitCost: null })))
      .toBe(false);
    expect(isPurchaseLinePriceComplete(line({ quantity: 0, lineTotal: 32.5, unitCost: 1.625 })))
      .toBe(false);
  });
});
