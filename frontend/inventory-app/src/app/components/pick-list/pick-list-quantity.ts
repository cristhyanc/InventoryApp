/**
 * The quantity representation both Pick List views share (issue #996).
 *
 * A quantity is recorded by the server as a 32-bit integer, so this is the one ceiling either view
 * accepts: a bigger number is refused here, naming the cell it was typed into, rather than by model
 * binding with a message about a JSON field nobody typed. Planning quantities are never sent
 * anywhere at all, but they are edited into the same representation a recorded refill uses, so they
 * are held to the same ceiling - a plan nothing could ever record is not a plan.
 */
export const MAXIMUM_PICK_QUANTITY = 2147483647;

/** What a malformed planned quantity is refused with: fractional, negative, blank or non-numeric. */
export const PLANNED_QUANTITY_INVALID_MESSAGE = 'Enter a whole number of 0 or more.';

export const PLANNED_QUANTITY_TOO_LARGE_MESSAGE = `Enter a quantity of ${MAXIMUM_PICK_QUANTITY} or less.`;

/** A parsed entry: the quantity it holds, or the message it is refused with. */
export type ParsedQuantity = { readonly quantity: number } | { readonly error: string };

/**
 * Parses a planned pick quantity. Zero is a real answer here - a deliberate skip - so, unlike a
 * recorded "Quantity taken", it is accepted as a quantity rather than treated as nothing typed.
 * Everything that is not a nonnegative whole number inside {@link MAXIMUM_PICK_QUANTITY} is
 * refused: a blank box, a sign, a decimal point, spaces inside the digits, or an overflowing value.
 */
export function parsePlannedQuantity(raw: string): ParsedQuantity {
  const trimmed = raw.trim();
  if (!/^\d+$/.test(trimmed)) {
    return { error: PLANNED_QUANTITY_INVALID_MESSAGE };
  }
  const quantity = Number(trimmed);
  return quantity > MAXIMUM_PICK_QUANTITY ? { error: PLANNED_QUANTITY_TOO_LARGE_MESSAGE } : { quantity };
}
