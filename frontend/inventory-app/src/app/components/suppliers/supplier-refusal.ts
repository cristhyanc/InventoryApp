import { HttpErrorResponse } from '@angular/common/http';

/**
 * The server's own refusal sentence when it sent one, and a fixed fallback otherwise (issue #934).
 *
 * The refusals a supplier write produces - a linked supplier's name, its website, a delete, a "Not
 * used" switch it does not allow - are sentences written for the operator, so they are shown rather
 * than replaced with a message of the page's own. Both shapes the API uses are read: a field-level
 * `errors` map (the website and name refusals) and a `ProblemDetails` `message`/`detail` (the delete
 * and "Not used" refusals).
 *
 * It lives beside the two callers rather than inside either of them because issue #1174 moved the
 * supplier form into its own dialog: the form reports what a create or update was refused with, and
 * the list reports what a delete, a "Not used" switch or an add from the platform's list was
 * refused with, and both have to read the same sentence out of the same two shapes.
 */
export function supplierRefusal(response: HttpErrorResponse, fallback: string): string {
  const body = response.error as
    | { message?: string; detail?: string; errors?: Record<string, string[]> }
    | null
    | undefined;

  const fieldMessage = body?.errors
    ? Object.values(body.errors)
        .flat()
        .find((message) => !!message)
    : undefined;

  return fieldMessage ?? body?.message ?? body?.detail ?? fallback;
}
