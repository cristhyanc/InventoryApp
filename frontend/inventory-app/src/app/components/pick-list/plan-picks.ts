/**
 * The Plan picks view's own planning arithmetic (issue #996). Everything here is local, in-memory
 * planning state: an override is the quantity the operator intends to pick for one product at one
 * machine, it never changes stock, a target or anything the server holds, and the original
 * suggestion it replaces is always kept beside it.
 */

/**
 * The largest pick quantity an override may hold: the same 32-bit integer the pick list's own
 * quantities and the refill endpoints use, so a planned quantity can never be one no other screen
 * could represent.
 */
export const MAXIMUM_PICK_QUANTITY = 2147483647;

export const PICK_QUANTITY_INVALID_MESSAGE = 'Enter a whole number of 0 or more.';

export const PICK_QUANTITY_TOO_LARGE_MESSAGE = `Enter a quantity of ${MAXIMUM_PICK_QUANTITY} or less.`;

/** A parsed pick quantity: a whole number of `0` or more, or the message it is refused with. */
export type ParsedPickQuantity = { readonly quantity: number } | { readonly error: string };

/**
 * What one cell's always-visible quantity box holds while it is not a quantity the plan can use
 * (issue #1008): the operator's text exactly as typed, never truncated or coerced, and the message
 * refusing it. The cell's last valid effective quantity goes on carrying every total meanwhile.
 */
export interface InvalidPickQuantity {
  readonly text: string;
  readonly error: string;
}

/**
 * Accepts only a nonnegative whole number the pick list can represent. Unlike a refill entry, a
 * blank box is not "nothing": the operator is committing a planned quantity, and `0` is the explicit
 * way to plan a deliberate skip.
 */
export function parsePickQuantity(raw: string): ParsedPickQuantity {
  const trimmed = raw.trim();
  if (!/^\d+$/.test(trimmed)) {
    return { error: PICK_QUANTITY_INVALID_MESSAGE };
  }
  const quantity = Number(trimmed);
  return quantity > MAXIMUM_PICK_QUANTITY ? { error: PICK_QUANTITY_TOO_LARGE_MESSAGE } : { quantity };
}

/** The key one product-machine cell's planning state is kept under. */
export function planCellKey(productId: number, machineId: number): string {
  return `${productId}:${machineId}`;
}

/** The machine id a {@link planCellKey} names. */
export function planCellMachineId(key: string): number {
  return Number(key.split(':')[1]);
}

/**
 * Whether an unknown value is a {@link planCellKey} - two nonnegative whole numbers either side of
 * one colon. Nothing read back out of browser storage is a key until this says so (issue #1021):
 * `planCellMachineId` answers `NaN` for anything else, which would silently survive every
 * machine-scoped comparison the page makes.
 */
export function isPlanCellKey(value: unknown): value is string {
  return typeof value === 'string' && /^\d+:\d+$/.test(value);
}

/**
 * Units the planned run needs from storage beyond what storage holds: the same `max(0, total -
 * storage)` the backend projection reports as `storageShortageQuantity`, applied to the planned
 * total so a committed override is reflected immediately.
 */
export function storageShortage(plannedTotal: number, storageQuantity: number): number {
  return Math.max(0, plannedTotal - storageQuantity);
}

/** "12 of 20 picks completed", in plain language, singular where it reads that way. */
export function picksCompletedLabel(completed: number, total: number): string {
  return `${completed} of ${total} ${total === 1 ? 'pick' : 'picks'} completed`;
}
