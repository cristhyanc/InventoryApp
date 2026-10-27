import { HttpErrorResponse } from '@angular/common/http';
import { MultiMachineRefillAllocationRequest, PickListRefillRowRequest } from '../../models/models';
import {
  multiMachineAllocationsEqual,
  parseMultiMachineRefillRejection,
  parsePickListRefillRejection,
  pickListRefillRowsEqual,
  refillCellKey
} from './pick-list-refill-errors';

function rows(...entries: [number, number][]): PickListRefillRowRequest[] {
  return entries.map(([productId, quantityTaken]) => ({ productId, quantityTaken }));
}

function allocations(...entries: [number, number, number][]): MultiMachineRefillAllocationRequest[] {
  return entries.map(([machineId, productId, quantityTaken]) => ({ machineId, productId, quantityTaken }));
}

function validationProblem(errors: Record<string, string[]>): HttpErrorResponse {
  return new HttpErrorResponse({ status: 400, error: { title: 'Request validation failed', status: 400, errors } });
}

describe('parsePickListRefillRejection', () => {
  it('maps a rows[i] refusal back onto the product id the submitted row at that index named', () => {
    const submitted = rows([1, 3], [2, 5]);
    const error = validationProblem({ 'rows[1]': ['Quantity taken must be a whole number greater than 0.'] });

    const parsed = parsePickListRefillRejection(error, submitted);

    expect(parsed).not.toBeNull();
    expect(parsed!.rowErrors.get(2)).toEqual(['Quantity taken must be a whole number greater than 0.']);
    expect(parsed!.rowErrors.has(1)).toBe(false);
    expect(parsed!.requestErrors).toEqual([]);
  });

  it('collects a request-level refusal (idempotencyKey, machineId, notes, bare rows) separately from any row', () => {
    const error = validationProblem({ idempotencyKey: ['An idempotency key is required.'] });

    const parsed = parsePickListRefillRejection(error, rows([1, 2]));

    expect(parsed!.requestErrors).toEqual(['An idempotency key is required.']);
    expect(parsed!.rowErrors.size).toBe(0);
  });

  it('reports every refused row, not only the first', () => {
    const submitted = rows([1, 1], [2, -1]);
    const error = validationProblem({
      'rows[0]': ['This product appears more than once in the request.'],
      'rows[1]': ['This product appears more than once in the request.']
    });

    const parsed = parsePickListRefillRejection(error, submitted);

    expect(parsed!.rowErrors.get(1)).toEqual(['This product appears more than once in the request.']);
    expect(parsed!.rowErrors.get(2)).toEqual(['This product appears more than once in the request.']);
  });

  it('returns null for a conflict, since that is not this validation shape', () => {
    const error = new HttpErrorResponse({ status: 409, error: { detail: 'conflict' } });

    expect(parsePickListRefillRejection(error, rows([1, 1]))).toBeNull();
  });

  it('returns null for a network failure', () => {
    const error = new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') });

    expect(parsePickListRefillRejection(error, rows([1, 1]))).toBeNull();
  });

  it('returns null when the body carries no errors dictionary at all', () => {
    const error = new HttpErrorResponse({ status: 400, error: 'Plain text refusal' });

    expect(parsePickListRefillRejection(error, rows([1, 1]))).toBeNull();
  });
});

describe('pickListRefillRowsEqual', () => {
  it('is true for the same products, quantities and order', () => {
    expect(pickListRefillRowsEqual(rows([1, 2], [3, 4]), rows([1, 2], [3, 4]))).toBe(true);
  });

  it('is false when a quantity differs', () => {
    expect(pickListRefillRowsEqual(rows([1, 2]), rows([1, 3]))).toBe(false);
  });

  it('is false when the order differs', () => {
    expect(pickListRefillRowsEqual(rows([1, 2], [3, 4]), rows([3, 4], [1, 2]))).toBe(false);
  });

  it('is false when a row was added or removed', () => {
    expect(pickListRefillRowsEqual(rows([1, 2]), rows([1, 2], [3, 4]))).toBe(false);
  });
});

describe('parseMultiMachineRefillRejection (issue #904)', () => {
  it('maps an allocations[i] refusal back onto the cell the submitted allocation named', () => {
    const submitted = allocations([5, 1, 6], [6, 1, 6]);
    const error = validationProblem({
      'allocations[1]': ['The allocations for this product take 12 units, but only 10 are in stock.']
    });

    const parsed = parseMultiMachineRefillRejection(error, submitted);

    expect(parsed).not.toBeNull();
    expect(parsed!.cellErrors.get(refillCellKey(1, 6))).toEqual([
      'The allocations for this product take 12 units, but only 10 are in stock.'
    ]);
    expect(parsed!.cellErrors.has(refillCellKey(1, 5))).toBe(false);
    expect(parsed!.requestErrors).toEqual([]);
  });

  it('reports every allocation a product’s combined total refused, not only one of them', () => {
    const submitted = allocations([5, 1, 6], [6, 1, 6]);
    const error = validationProblem({
      'allocations[0]': ['The allocations for this product take 12 units, but only 10 are in stock.'],
      'allocations[1]': ['The allocations for this product take 12 units, but only 10 are in stock.']
    });

    const parsed = parseMultiMachineRefillRejection(error, submitted);

    expect(parsed!.cellErrors.get(refillCellKey(1, 5))).toHaveLength(1);
    expect(parsed!.cellErrors.get(refillCellKey(1, 6))).toHaveLength(1);
  });

  it('collects a request-level refusal (idempotencyKey, notes, the bare allocations key) separately', () => {
    const error = validationProblem({
      allocations: ['A refill may record at most 500 allocations in one request.'],
      idempotencyKey: ['An idempotency key is required.']
    });

    const parsed = parseMultiMachineRefillRejection(error, allocations([5, 1, 1]));

    expect(parsed!.requestErrors).toEqual([
      'A refill may record at most 500 allocations in one request.',
      'An idempotency key is required.'
    ]);
    expect(parsed!.cellErrors.size).toBe(0);
  });

  it('ignores an index the request never sent rather than inventing a cell for it', () => {
    const error = validationProblem({ 'allocations[7]': ['This allocation is missing.'] });

    const parsed = parseMultiMachineRefillRejection(error, allocations([5, 1, 1]));

    expect(parsed!.cellErrors.size).toBe(0);
  });

  it('returns null for a conflict, a network failure and a body with no errors dictionary', () => {
    expect(parseMultiMachineRefillRejection(new HttpErrorResponse({ status: 409, error: { detail: 'c' } }), [])).toBeNull();
    expect(parseMultiMachineRefillRejection(new HttpErrorResponse({ status: 0 }), [])).toBeNull();
    expect(parseMultiMachineRefillRejection(new HttpErrorResponse({ status: 400, error: 'Plain text refusal' }), [])).toBeNull();
  });
});

describe('multiMachineAllocationsEqual (issue #904)', () => {
  it('is true for the same machine/product pairs and quantities in the same order', () => {
    expect(multiMachineAllocationsEqual(allocations([5, 1, 2], [6, 1, 3]), allocations([5, 1, 2], [6, 1, 3]))).toBe(true);
  });

  it('is true when only the order differs, because the server fingerprints a batch in its own order', () => {
    expect(multiMachineAllocationsEqual(allocations([5, 1, 2], [6, 1, 3]), allocations([6, 1, 3], [5, 1, 2]))).toBe(true);
  });

  it('is false when a quantity, a machine or a product differs', () => {
    expect(multiMachineAllocationsEqual(allocations([5, 1, 2]), allocations([5, 1, 3]))).toBe(false);
    expect(multiMachineAllocationsEqual(allocations([5, 1, 2]), allocations([6, 1, 2]))).toBe(false);
    expect(multiMachineAllocationsEqual(allocations([5, 1, 2]), allocations([5, 2, 2]))).toBe(false);
  });

  it('is false when an allocation was added or removed', () => {
    expect(multiMachineAllocationsEqual(allocations([5, 1, 2]), allocations([5, 1, 2], [6, 1, 2]))).toBe(false);
  });
});
