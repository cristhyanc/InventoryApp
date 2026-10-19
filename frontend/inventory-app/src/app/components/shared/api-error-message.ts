import { HttpErrorResponse } from '@angular/common/http';

/**
 * Reads the refusal sentence a `400`/`409` response carries as its raw string body - the shape
 * `BadRequest(string)`/`Conflict(string)` serialize for a validation or deletion refusal across the
 * products and categories endpoints (issue #737) - falling back to a component-owned message when
 * the response carries anything else (a network failure, an unexpected `500`, or no body at all).
 */
export function apiErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse && typeof error.error === 'string' && error.error.trim().length > 0) {
    return error.error;
  }

  return fallback;
}
