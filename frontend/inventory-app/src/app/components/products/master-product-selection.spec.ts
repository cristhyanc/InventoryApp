import { HttpErrorResponse } from '@angular/common/http';
import {
  MASTER_PRODUCT_CHANGED_CODE,
  MASTER_PRODUCT_REFUSED_CODE,
  MASTER_PRODUCT_UNAVAILABLE_CODE,
  isSamePrefillValue,
  masterProductConflictOf
} from './master-product-selection';

/** A `409` of the shape `ProductsController.MasterProductSelectionConflict` writes (issue #1051). */
function conflict(code: string, message: string): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: {
      title: 'The selected master product is no longer available',
      detail: message,
      status: 409,
      traceId: '00-trace-01',
      message,
      code
    }
  });
}

describe('isSamePrefillValue (issue #1052)', () => {
  it('reads the three differences the server does not call an edit as the same value', () => {
    expect(isSamePrefillValue("CC's Tasty Cheese Corn Chips", "CC's Tasty Cheese Corn Chips")).toBe(true);
    expect(isSamePrefillValue("  CC's Tasty Cheese Corn Chips  ", "CC's Tasty Cheese Corn Chips")).toBe(true);
    expect(isSamePrefillValue("CC's   Tasty  Cheese Corn Chips", "CC's Tasty Cheese Corn Chips")).toBe(true);
    expect(isSamePrefillValue("cc's tasty cheese corn chips", "CC's Tasty Cheese Corn Chips")).toBe(true);
  });

  it('reads every other difference as an edit, including a size spaced differently', () => {
    // `45 g` for `45g` is the example the server's own rule calls a change: a size written
    // differently is how a product gets matched to the wrong shelf.
    expect(isSamePrefillValue('45 g', '45g')).toBe(false);
    expect(isSamePrefillValue('45g', '450g')).toBe(false);
    expect(isSamePrefillValue('', '45g')).toBe(false);
    expect(isSamePrefillValue("CC's Tasty Cheese Corn Chips (party)", "CC's Tasty Cheese Corn Chips")).toBe(false);
  });
});

describe('masterProductConflictOf (issue #1052)', () => {
  it('reads each of the three codes with the sentence the server worded', () => {
    for (const code of [MASTER_PRODUCT_UNAVAILABLE_CODE, MASTER_PRODUCT_CHANGED_CODE, MASTER_PRODUCT_REFUSED_CODE]) {
      expect(masterProductConflictOf(conflict(code, `Refused: ${code}.`))).toEqual({
        code,
        message: `Refused: ${code}.`
      });
    }
  });

  it('falls back to the problem detail, then to its own sentence, when no message is published', () => {
    const detailOnly = new HttpErrorResponse({
      status: 409,
      error: { detail: 'The product you chose is no longer in the shared catalogue.', code: MASTER_PRODUCT_UNAVAILABLE_CODE }
    });
    expect(masterProductConflictOf(detailOnly)?.message).toBe(
      'The product you chose is no longer in the shared catalogue.'
    );

    const codeOnly = new HttpErrorResponse({ status: 409, error: { code: MASTER_PRODUCT_REFUSED_CODE } });
    expect(masterProductConflictOf(codeOnly)?.message).toContain('without the link');
  });

  it('is not a conflict for another refusal, another status or a transport failure', () => {
    expect(masterProductConflictOf(conflict('something_else', 'Another conflict.'))).toBeNull();
    expect(
      masterProductConflictOf(
        new HttpErrorResponse({ status: 400, error: { code: MASTER_PRODUCT_CHANGED_CODE, message: 'Not a 409.' } })
      )
    ).toBeNull();
    expect(masterProductConflictOf(new HttpErrorResponse({ status: 409, error: 'Unit label is required.' }))).toBeNull();
    expect(masterProductConflictOf(new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') }))).toBeNull();
    expect(masterProductConflictOf(new Error('not an HTTP failure'))).toBeNull();
  });
});
