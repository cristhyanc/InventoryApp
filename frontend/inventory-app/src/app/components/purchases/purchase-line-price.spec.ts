import {
  DEFAULT_PRICE_ENTRY_MODE,
  PurchaseLinePrice,
  STORED_LINE_PRICE_ENTRY_MODE,
  enterLineTotal,
  enterUnitCost,
  isPurchaseLinePriceComplete,
  isValidLineAmount,
  isValidLineQuantity,
  lineTotalFromUnitCost,
  purchaseLinePriceProblem,
  roundToCents,
  savedLineAmount,
  syncPurchaseLinePrice,
  totalPriceFieldValue,
  unitCostFromLineTotal,
  unitPriceFieldValue
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

describe('purchase line price entry defaults (issues #707, #878)', () => {
  it('starts a new line on the total paid for it and a stored line on its unit price', () => {
    expect(DEFAULT_PRICE_ENTRY_MODE).toBe('total');
    expect(STORED_LINE_PRICE_ENTRY_MODE).toBe('unit');
  });
});

describe('purchase line last-typed-wins price entry (issue #878)', () => {
  it('makes the total the entered figure and derives the unit cost from it', () => {
    const subject = line({ quantity: 300, priceEntryMode: 'unit', unitCost: 7, lineTotal: 2100 });

    enterLineTotal(subject, 100);

    expect(subject.priceEntryMode).toBe('total');
    expect(subject.lineTotal).toBe(100);
    expect(subject.unitCost).toBe(100 / 300);
  });

  it('makes the unit price the entered figure and derives the total from it', () => {
    const subject = line({ quantity: 28, lineTotal: 100, unitCost: 100 / 28 });

    enterUnitCost(subject, 1.5);

    expect(subject.priceEntryMode).toBe('unit');
    expect(subject.unitCost).toBe(1.5);
    expect(subject.lineTotal).toBe(42);
  });

  it('hands the win to whichever field was typed into last, in either direction', () => {
    const subject = line({ quantity: 10 });

    enterLineTotal(subject, 20);
    expect([subject.priceEntryMode, subject.unitCost, subject.lineTotal]).toEqual(['total', 2, 20]);

    enterUnitCost(subject, 3);
    expect([subject.priceEntryMode, subject.unitCost, subject.lineTotal]).toEqual(['unit', 3, 30]);

    enterLineTotal(subject, 50);
    expect([subject.priceEntryMode, subject.unitCost, subject.lineTotal]).toEqual(['total', 5, 50]);
  });

  it('clears the calculated figure when the entered one is cleared, in either direction', () => {
    const typedTotal = line({ quantity: 20, lineTotal: 32.5, unitCost: 1.625 });

    enterLineTotal(typedTotal, null);

    expect(typedTotal.lineTotal).toBeNull();
    expect(typedTotal.unitCost).toBeNull();

    const typedUnit = line({ quantity: 20, priceEntryMode: 'unit', lineTotal: 32.5, unitCost: 1.625 });

    enterUnitCost(typedUnit, null);

    expect(typedUnit.unitCost).toBeNull();
    expect(typedUnit.lineTotal).toBeNull();
  });

  it('clears the calculated figure rather than dividing by or multiplying an unusable quantity', () => {
    const typedTotal = line({ quantity: 0 });

    enterLineTotal(typedTotal, 32.5);

    expect(typedTotal.unitCost).toBeNull();

    const typedUnit = line({ quantity: 2.5 });

    enterUnitCost(typedUnit, 1.625);

    expect(typedUnit.lineTotal).toBeNull();
  });
});

describe('purchase line price field values (issue #878)', () => {
  it('shows a calculated unit cost rounded to four decimals while the total is the entered figure', () => {
    const subject = line({ quantity: 300 });

    enterLineTotal(subject, 100);

    expect(unitPriceFieldValue(subject)).toBe('0.3333');
    // The displayed figure is a display value only: the submitted unit cost stays unrounded, so the
    // line still saves at the $100.00 that was entered.
    expect(subject.unitCost).toBe(100 / 300);
    expect(roundToCents(300 * subject.unitCost!)).toBe(100);
  });

  it('shows an entered unit price exactly as it was typed, unrounded and unpadded', () => {
    const subject = line({ quantity: 28 });

    enterUnitCost(subject, 1.5);

    expect(unitPriceFieldValue(subject)).toBe(1.5);
  });

  it('shows a calculated line total to the cent it will save at', () => {
    const subject = line({ quantity: 28 });

    enterUnitCost(subject, 1.5);

    expect(totalPriceFieldValue(subject)).toBe('42.00');
  });

  it('shows an entered line total exactly as it was typed', () => {
    const subject = line({ quantity: 300 });

    enterLineTotal(subject, 100);

    expect(totalPriceFieldValue(subject)).toBe(100);
  });

  it('shows a stored line its recorded unit cost and the total that cost implies', () => {
    const subject = line({
      quantity: 300, priceEntryMode: STORED_LINE_PRICE_ENTRY_MODE, unitCost: 100 / 300,
      lineTotal: lineTotalFromUnitCost(100 / 300, 300)
    });

    expect(unitPriceFieldValue(subject)).toBe(100 / 300);
    expect(totalPriceFieldValue(subject)).toBe('100.00');
  });

  it('shows nothing for a figure that could not be calculated', () => {
    const noTotal = line({ quantity: 0, priceEntryMode: 'unit', unitCost: 1.5, lineTotal: null });
    const noUnitCost = line({ quantity: 0, lineTotal: 32.5, unitCost: null });

    expect(totalPriceFieldValue(noTotal)).toBeNull();
    expect(unitPriceFieldValue(noUnitCost)).toBeNull();
  });

  it('leaves an entered figure on screen even when it is not a usable amount', () => {
    const blank = line({ quantity: 20, lineTotal: null, unitCost: null });

    expect(totalPriceFieldValue(blank)).toBeNull();
    expect(unitPriceFieldValue(line({ quantity: 20, priceEntryMode: 'unit', unitCost: Number.NaN })))
      .toBe(Number.NaN);
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

  it('spreads an entered total over a new quantity and keeps the total itself', () => {
    const subject = line({ quantity: 300, lineTotal: 100, unitCost: 100 / 300 });

    subject.quantity = 200;
    syncPurchaseLinePrice(subject);

    expect(subject.lineTotal).toBe(100);
    expect(subject.unitCost).toBe(0.5);
  });

  it('multiplies an entered unit price by a new quantity and keeps the unit price itself', () => {
    const subject = line({ quantity: 28, priceEntryMode: 'unit', unitCost: 1.5, lineTotal: 42 });

    subject.quantity = 30;
    syncPurchaseLinePrice(subject);

    expect(subject.unitCost).toBe(1.5);
    expect(subject.lineTotal).toBe(45);
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

  it('names the amount the entered figure actually asks for', () => {
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
