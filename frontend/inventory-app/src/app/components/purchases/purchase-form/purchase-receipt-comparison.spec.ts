import { PurchaseLinePrice } from '../purchase-line-price';
import { RECEIPT_TOTAL_TOLERANCE, compareReceiptTotal, purchaseTotal } from './purchase-receipt-comparison';

function line(quantity: number | null, unitCost: number | null): PurchaseLinePrice {
  return { quantity, unitCost, lineTotal: null, priceEntryMode: 'unit' };
}

describe('purchaseTotal (issue #1029)', () => {
  it('adds the delivery and packaging charges to the items subtotal the forms already sum', () => {
    expect(purchaseTotal([line(72, 1.5), line(84, 1)], 15, 5)).toBe(212);
  });

  it('counts an absent or unusable charge as nothing, and a line without a price as nothing', () => {
    expect(purchaseTotal([line(10, 2), line(3, null)], null, Number.NaN)).toBe(20);
  });

  it('sums each line at the amount it saves, to the cent', () => {
    // 150 x 0.81666... saves as 122.50; three of them are 367.50, not 367.4999...
    const unit = 122.5 / 150;
    expect(purchaseTotal([line(150, unit), line(150, unit), line(150, unit)], null, null)).toBe(367.5);
  });
});

describe('compareReceiptTotal (issue #1029)', () => {
  const items = [line(72, 1.5), line(84, 1), line(40, 1.45)]; // 108 + 84 + 58 = 250

  it('treats a blank receipt total as absent, never as zero', () => {
    expect(compareReceiptTotal(null, items, null, null)).toEqual({ kind: 'absent' });
    expect(compareReceiptTotal(undefined, items, null, null)).toEqual({ kind: 'absent' });
    expect(compareReceiptTotal(Number.NaN, items, null, null)).toEqual({ kind: 'absent' });
  });

  it('compares an entered zero like any other amount', () => {
    expect(compareReceiptTotal(0, items, null, null)).toEqual({ kind: 'difference', difference: 250, beyondTolerance: true });
    expect(compareReceiptTotal(0, [], null, null)).toEqual({ kind: 'match' });
  });

  it('says the receipt matches when it equals the purchase to the cent', () => {
    expect(compareReceiptTotal(250, items, null, null)).toEqual({ kind: 'match' });
    expect(compareReceiptTotal(270, items, 15, 5)).toEqual({ kind: 'match' });
  });

  it('reports the difference either way, and whether the list will flag it as a mismatch', () => {
    expect(compareReceiptTotal(249.5, items, null, null)).toEqual({ kind: 'difference', difference: 0.5, beyondTolerance: false });
    expect(compareReceiptTotal(251, items, null, null)).toEqual({ kind: 'difference', difference: 1, beyondTolerance: false });
    expect(compareReceiptTotal(251.01, items, null, null)).toEqual({ kind: 'difference', difference: 1.01, beyondTolerance: true });
    expect(RECEIPT_TOTAL_TOLERANCE).toBe(1);
  });

  it('compares the unrounded quantity x unit cost, rounding only the difference, as the server does', () => {
    // 3 x 0.3333 = 0.9999, which a receipt of $1.00 matches once the difference is taken to cents.
    expect(compareReceiptTotal(1, [line(3, 0.3333)], null, null)).toEqual({ kind: 'match' });
    // A sub-cent difference of exactly half a cent rounds away from zero.
    expect(compareReceiptTotal(1, [line(1, 1.005)], null, null)).toEqual({ kind: 'difference', difference: 0.01, beyondTolerance: false });
  });

  it('says nothing while a line still has no usable quantity or price', () => {
    expect(compareReceiptTotal(250, [...items, line(2, null)], null, null)).toEqual({ kind: 'incomplete' });
    expect(compareReceiptTotal(250, [line(0, 1)], null, null)).toEqual({ kind: 'incomplete' });
  });

  it('changes nothing it is given', () => {
    const lines = [line(3, 2)];
    const before = JSON.stringify(lines);
    compareReceiptTotal(5, lines, 1, 2);
    expect(JSON.stringify(lines)).toBe(before);
  });
});
