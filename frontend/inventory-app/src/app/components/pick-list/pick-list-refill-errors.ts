import { HttpErrorResponse } from '@angular/common/http';
import { MultiMachineRefillAllocationRequest, PickListRefillRowRequest } from '../../models/models';

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

/**
 * Parsed refusals from a `400` `POST /api/pick-list/multi-machine-refills` response (issue #897's
 * `ValidationProblemDetails`, `InventoryApi.Adapters.Mapping.MultiMachinePickListRefillResponseMapper`):
 * `cellErrors` keyed by {@link refillCellKey} of the **machine and product** the submitted allocation
 * index named, so a caller can put each message back on the cell the operator typed however the
 * matrix is currently filtered or its columns ordered, and `requestErrors` for anything refused about
 * the request as a whole (`idempotencyKey`, `notes`, or the bare `allocations` key - for instance the
 * server's own limit on how many allocations one request may record, which the matrix offers no field
 * for but must still surface rather than drop).
 */
export interface MultiMachineRefillRejectionDetails {
  readonly cellErrors: Map<string, readonly string[]>;
  readonly requestErrors: readonly string[];
}

const ALLOCATION_KEY_PATTERN = /^allocations\[(\d+)\]$/;

/** The one key a matrix cell's drafts and refusals are held under: one product on one machine. */
export function refillCellKey(productId: number, machineId: number): string {
  return `${productId}:${machineId}`;
}

/**
 * Parses a refused batch save into per-cell and request-level messages, or `null` when the response
 * is not this endpoint's `400` `ValidationProblemDetails` shape - a network failure, a `409` key
 * conflict, or anything else, which the caller handles itself rather than reading a refusal nobody
 * wrote.
 *
 * `submittedAllocations` must be exactly the allocations the failed request sent, in the order it
 * sent them: the server's `allocations[i]` refers to that index, not to any later reordering of the
 * machine columns or filtering of the rows.
 */
export function parseMultiMachineRefillRejection(
  error: unknown,
  submittedAllocations: readonly MultiMachineRefillAllocationRequest[]
): MultiMachineRefillRejectionDetails | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) {
    return null;
  }

  const body = error.error as { errors?: unknown } | null;
  const errors = body && typeof body === 'object' ? body.errors : null;
  if (errors === null || typeof errors !== 'object') {
    return null;
  }

  const cellErrors = new Map<string, readonly string[]>();
  const requestErrors: string[] = [];

  for (const [key, value] of Object.entries(errors as Record<string, unknown>)) {
    const messages = Array.isArray(value) ? value.filter((message): message is string => typeof message === 'string') : [];
    const allocationMatch = ALLOCATION_KEY_PATTERN.exec(key);
    if (allocationMatch !== null) {
      const allocation = submittedAllocations[Number(allocationMatch[1])];
      if (allocation !== undefined) {
        cellErrors.set(refillCellKey(allocation.productId, allocation.machineId), messages);
      }
      continue;
    }
    requestErrors.push(...messages);
  }

  return { cellErrors, requestErrors };
}

/**
 * Whether two allocation sets are the same submission: the same machine/product pairs with the same
 * quantities, **in any order**. Order is deliberately not part of the comparison, because the server
 * fingerprints a batch in its own machine-then-product order (issue #897's
 * `MultiMachinePickListRefillFingerprint`): reordering the matrix's machine columns re-renders the
 * same physical pick, so it must not mint a new idempotency key.
 */
export function multiMachineAllocationsEqual(
  a: readonly MultiMachineRefillAllocationRequest[],
  b: readonly MultiMachineRefillAllocationRequest[]
): boolean {
  if (a.length !== b.length) {
    return false;
  }
  const render = (allocations: readonly MultiMachineRefillAllocationRequest[]) =>
    allocations
      .map((allocation) => `${allocation.machineId}:${allocation.productId}:${allocation.quantityTaken}`)
      .sort((left, right) => left.localeCompare(right));
  const left = render(a);
  const right = render(b);
  return left.every((entry, index) => entry === right[index]);
}
