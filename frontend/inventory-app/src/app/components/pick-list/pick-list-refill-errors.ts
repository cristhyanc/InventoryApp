import { HttpErrorResponse } from '@angular/common/http';
import { PickListRefillRowRequest } from '../../models/models';

/**
 * Parsed refusals from a `400` `POST /api/pick-list/refills` response (issue #743's
 * `ValidationProblemDetails`, `InventoryApi.Adapters.Mapping.PickListRefillResponseMapper`):
 * `rowErrors` keyed by the **product id** of the row the submitted index named, so a caller can put
 * each message back on the line the operator typed regardless of how the visible list is sorted or
 * filtered, and `requestErrors` for anything refused about the request as a whole (`idempotencyKey`,
 * `machineId`, `notes`, or the bare `rows` key, none of which this manual-entry screen offers a
 * field for, but every one of which must still surface rather than be silently dropped).
 */
export interface PickListRefillRejectionDetails {
  readonly rowErrors: Map<number, readonly string[]>;
  readonly requestErrors: readonly string[];
}

const ROW_KEY_PATTERN = /^rows\[(\d+)\]$/;

/**
 * Parses a refused save into per-row and request-level messages, or `null` when the response is
 * not this endpoint's `400` `ValidationProblemDetails` shape - a network failure, a `409` key
 * conflict, or anything else, which the caller handles itself rather than reading a refusal nobody
 * wrote.
 *
 * `submittedRows` must be exactly the rows the failed request sent, in the order it sent them: the
 * server's `rows[i]` refers to that index, not to any later-filtered or re-sorted view of the
 * catalogue.
 */
export function parsePickListRefillRejection(
  error: unknown,
  submittedRows: readonly PickListRefillRowRequest[]
): PickListRefillRejectionDetails | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) {
    return null;
  }

  const body = error.error as { errors?: unknown } | null;
  const errors = body && typeof body === 'object' ? body.errors : null;
  if (errors === null || typeof errors !== 'object') {
    return null;
  }

  const rowErrors = new Map<number, string[]>();
  const requestErrors: string[] = [];

  for (const [key, value] of Object.entries(errors as Record<string, unknown>)) {
    const messages = Array.isArray(value) ? value.filter((message): message is string => typeof message === 'string') : [];
    const rowMatch = ROW_KEY_PATTERN.exec(key);
    if (rowMatch !== null) {
      const productId = submittedRows[Number(rowMatch[1])]?.productId;
      if (productId !== undefined) {
        rowErrors.set(productId, messages);
      }
      continue;
    }
    requestErrors.push(...messages);
  }

  return { rowErrors, requestErrors };
}

/** Whether two row sets are the same submission: same products, same quantities, same order. */
export function pickListRefillRowsEqual(
  a: readonly PickListRefillRowRequest[],
  b: readonly PickListRefillRowRequest[]
): boolean {
  return (
    a.length === b.length &&
    a.every((row, index) => row.productId === b[index].productId && row.quantityTaken === b[index].quantityTaken)
  );
}
