import { HttpErrorResponse } from '@angular/common/http';
import { apiErrorMessage } from './api-error-message';

describe('apiErrorMessage', () => {
  it('returns the refusal sentence a 409 Conflict response carries as its raw string body', () => {
    const error = new HttpErrorResponse({
      status: 409,
      error: 'This product cannot be deleted because its history refers to it (1 stock movement). ' +
        'Deactivate it instead - a deactivated product keeps its history, stays out of the reorder ' +
        'alerts and can be reactivated. Nothing was deleted.'
    });

    expect(apiErrorMessage(error, 'Failed to delete product.')).toBe(error.error);
  });

  it('returns the refusal sentence a 400 Bad Request response carries', () => {
    const error = new HttpErrorResponse({ status: 400, error: 'Product name is required.' });

    expect(apiErrorMessage(error, 'Failed to save product.')).toBe('Product name is required.');
  });

  it('falls back when the response carries no string body', () => {
    const error = new HttpErrorResponse({ status: 500, error: { title: 'Internal Server Error' } });

    expect(apiErrorMessage(error, 'Failed to save product.')).toBe('Failed to save product.');
  });

  it('falls back when the response body is blank', () => {
    const error = new HttpErrorResponse({ status: 409, error: '   ' });

    expect(apiErrorMessage(error, 'Failed to delete product.')).toBe('Failed to delete product.');
  });

  it('falls back for a failure that is not an HTTP error at all', () => {
    expect(apiErrorMessage(new Error('boom'), 'Failed to save product.')).toBe('Failed to save product.');
  });
});
