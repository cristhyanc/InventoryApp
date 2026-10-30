// Unit tests for scripts/price-collector/output-schema.mjs and output.schema.json: what a collector
// output file must hold before anything is posted to Restockroo (issue #926, parent #817).
//
// The schema is the collector's half of the contracts in #937 and #941. It is deliberately stricter
// than the API in the places where a wrong value would be accepted as a price - a missing barcode
// scope, two rows claiming one supplier product id, a search the page cap stopped reported as
// complete - and it never invents a value a file left out.
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { SCHEMA, validateMatchFile, validateResultFile } from './output-schema.mjs';

const row = (overrides = {}) => ({
  supplierProductId: 'coles:5216969',
  packPrice: 24.5,
  currency: 'AUD',
  priceBasis: 'inclusive_of_gst',
  stockStatus: 'in_stock',
  observedAtUtc: '2026-10-30T02:15:00Z',
  ...overrides,
});

const resultFile = (overrides = {}) => ({
  runId: 41,
  workItemId: 7001,
  supplierPresetId: 3,
  outcome: 'complete',
  rows: [row()],
  ...overrides,
});

const matchFile = (overrides = {}) => ({
  runId: 41,
  waitingKey: 'coca-cola classic 375ml|can',
  masterProductId: 12,
  confidence: 'high',
  reason: 'Brand, variant and size all agree and only one master product fits.',
  ...overrides,
});

const problemsOf = (answer) => answer.problems.join(' | ');

describe('the schema document', () => {
  it('declares both file shapes and refuses properties neither names', () => {
    assert.equal(SCHEMA.resultFile.additionalProperties, false);
    assert.equal(SCHEMA.matchFile.additionalProperties, false);
    assert.equal(SCHEMA.resultFile.properties.rows.items.additionalProperties, false);
  });
});

describe('a result file', () => {
  it('accepts a complete search with one row', () => {
    const answer = validateResultFile(resultFile());

    assert.equal(answer.ok, true, problemsOf(answer));
  });

  it('refuses a file that is not an object, and never throws on one', () => {
    assert.equal(validateResultFile(null).ok, false);
    assert.equal(validateResultFile([]).ok, false);
    assert.equal(validateResultFile('{}').ok, false);
  });

  it('requires the run, the work item and the supplier it answers for', () => {
    for (const field of ['runId', 'workItemId', 'supplierPresetId', 'outcome', 'rows']) {
      const file = resultFile();
      delete file[field];
      const answer = validateResultFile(file);

      assert.equal(answer.ok, false, `${field} should be required`);
      assert.match(problemsOf(answer), new RegExp(field));
    }
  });

  it('refuses a property the schema does not name', () => {
    const answer = validateResultFile(resultFile({ listingId: 9 }));

    assert.equal(answer.ok, false);
    assert.match(problemsOf(answer), /listingId/);
  });

  it('refuses an outcome outside the four a collector may report', () => {
    assert.equal(validateResultFile(resultFile({ outcome: 'not_searched', rows: [] })).ok, false);
    assert.equal(validateResultFile(resultFile({ outcome: 'partly', rows: [] })).ok, false);
  });

  it('requires a failure reason with a failed search and refuses one anywhere else', () => {
    assert.equal(validateResultFile(resultFile({ outcome: 'failed', rows: [] })).ok, false);
    assert.equal(
      validateResultFile(resultFile({ outcome: 'failed', failureReason: 'timeout', rows: [] })).ok,
      true,
    );
    assert.equal(validateResultFile(resultFile({ failureReason: 'timeout' })).ok, false);
    assert.equal(
      validateResultFile(resultFile({ outcome: 'failed', failureReason: 'the site was slow', rows: [] })).ok,
      false,
    );
  });

  it('refuses rows on a search that reported finding nothing', () => {
    assert.equal(validateResultFile(resultFile({ outcome: 'no_matches' })).ok, false);
    assert.equal(validateResultFile(resultFile({ outcome: 'no_matches', rows: [] })).ok, true);
  });

  it('accepts a truncated search with the rows it did read', () => {
    assert.equal(validateResultFile(resultFile({ outcome: 'truncated' })).ok, true);
  });

  it('refuses a search the page cap stopped that calls itself complete', () => {
    const stopped = { pagesRead: 4, pageCap: 4, moreAvailable: true };
    const answer = validateResultFile(resultFile({ paging: stopped }));

    assert.equal(answer.ok, false);
    assert.match(problemsOf(answer), /truncated/);
    assert.equal(validateResultFile(resultFile({ outcome: 'truncated', paging: stopped })).ok, true);
    assert.equal(
      validateResultFile(resultFile({ paging: { pagesRead: 2, pageCap: 4, moreAvailable: false } })).ok,
      true,
    );
  });

  it('requires a price above zero, in AUD, with a declared basis and stock answer', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ packPrice: 0 })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ packPrice: -1 })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ packPrice: '24.50' })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ currency: 'NZD' })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ priceBasis: 'with_gst' })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ stockStatus: 'maybe' })] })).ok, false);
  });

  it('requires a supplier product id a listing can be recognised by', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ supplierProductId: '' })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ supplierProductId: 'x'.repeat(201) })] })).ok, false);
  });

  it('refuses two rows claiming one supplier product id', () => {
    const answer = validateResultFile(resultFile({ rows: [row(), row({ packPrice: 30 })] }));

    assert.equal(answer.ok, false);
    assert.match(problemsOf(answer), /coles:5216969/);
  });

  it('accepts two packs of one product as two rows', () => {
    const answer = validateResultFile(
      resultFile({ rows: [row({ packQuantity: 24 }), row({ supplierProductId: 'coles:5216970', packQuantity: 30 })] }),
    );

    assert.equal(answer.ok, true, problemsOf(answer));
  });

  it('refuses a row whose own work item is not the file it is in', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ workItemId: 7001 })] })).ok, true);
    assert.equal(validateResultFile(resultFile({ rows: [row({ workItemId: 7002 })] })).ok, false);
  });

  it('refuses a barcode with no scope, because the two mean different packs', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ barcode: '09300675024235' })] })).ok, false);
    assert.equal(
      validateResultFile(resultFile({ rows: [row({ barcode: '09300675024235', barcodeScope: 'unit' })] })).ok,
      true,
    );
  });

  it('keeps a barcode a string, so a leading zero survives', () => {
    assert.equal(
      validateResultFile(resultFile({ rows: [row({ barcode: 9300675024235, barcodeScope: 'unit' })] })).ok,
      false,
    );
  });

  it('refuses an unknown value reported as a zero rather than left out', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ packQuantity: 0 })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ sizeValue: 0, sizeUnit: 'ml' })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ packQuantity: 24 })] })).ok, true);
  });

  it('requires a size unit beside a size, and a declared one', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ sizeValue: 375 })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ sizeValue: 375, sizeUnit: 'millilitre' })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ sizeValue: 375, sizeUnit: 'ml' })] })).ok, true);
  });

  it('requires a GST rate to be a proportion', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ supplierGstRate: 10 })] })).ok, false);
    assert.equal(validateResultFile(resultFile({ rows: [row({ supplierGstRate: 0.1 })] })).ok, true);
    assert.equal(validateResultFile(resultFile({ rows: [row({ supplierGstRate: 0 })] })).ok, true);
  });

  it('requires an instant a date can be read from', () => {
    assert.equal(validateResultFile(resultFile({ rows: [row({ observedAtUtc: 'this morning' })] })).ok, false);
    assert.equal(
      validateResultFile(resultFile({ rows: [row({ promotionEndsAtUtc: 'next week', isPromotion: true })] })).ok,
      false,
    );
    assert.equal(
      validateResultFile(resultFile({ rows: [row({ promotionEndsAtUtc: '2026-11-05T13:00:00Z', isPromotion: true })] }))
        .ok,
      true,
    );
  });
});

describe('a match file', () => {
  it('accepts one existing master product with a confidence and a reason', () => {
    const answer = validateMatchFile(matchFile());

    assert.equal(answer.ok, true, problemsOf(answer));
  });

  it('accepts a complete suggested new master product', () => {
    const answer = validateMatchFile(
      matchFile({
        masterProductId: undefined,
        suggestedBrand: 'Coca-Cola',
        suggestedName: 'Classic',
        suggestedSizeValue: 375,
        suggestedSizeUnit: 'ml',
        confidence: 'medium',
      }),
    );

    assert.equal(answer.ok, true, problemsOf(answer));
  });

  it('refuses a proposal that names both an existing and a new master product', () => {
    const answer = validateMatchFile(
      matchFile({
        suggestedBrand: 'Coca-Cola',
        suggestedName: 'Classic',
        suggestedSizeValue: 375,
        suggestedSizeUnit: 'ml',
      }),
    );

    assert.equal(answer.ok, false);
    assert.match(problemsOf(answer), /exactly one/i);
  });

  it('refuses a proposal that names neither', () => {
    const answer = validateMatchFile(matchFile({ masterProductId: undefined }));

    assert.equal(answer.ok, false);
    assert.match(problemsOf(answer), /exactly one/i);
  });

  it('refuses a half-described new master product', () => {
    const answer = validateMatchFile(
      matchFile({ masterProductId: undefined, suggestedBrand: 'Coca-Cola', suggestedName: 'Classic' }),
    );

    assert.equal(answer.ok, false);
  });

  it('requires the waiting key, a declared confidence and a reason', () => {
    assert.equal(validateMatchFile(matchFile({ waitingKey: '' })).ok, false);
    assert.equal(validateMatchFile(matchFile({ confidence: 'certain' })).ok, false);
    assert.equal(validateMatchFile(matchFile({ confidence: 1 })).ok, false);
    assert.equal(validateMatchFile(matchFile({ reason: '' })).ok, false);
    assert.equal(validateMatchFile(matchFile({ reason: 'x'.repeat(501) })).ok, false);
  });

  it('refuses a property the schema does not name', () => {
    assert.equal(validateMatchFile(matchFile({ workItemId: 7001 })).ok, false);
  });
});
