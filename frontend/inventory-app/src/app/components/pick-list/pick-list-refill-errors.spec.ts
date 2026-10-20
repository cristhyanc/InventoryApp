import { HttpErrorResponse } from '@angular/common/http';
import { PickListRefillRowRequest } from '../../models/models';
import { parsePickListRefillRejection, pickListRefillRowsEqual } from './pick-list-refill-errors';

function rows(...entries: [number, number][]): PickListRefillRowRequest[] {
  return entries.map(([productId, quantityTaken]) => ({ productId, quantityTaken }));
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
