/**
 * The optional supplier website rule, in the browser (issue #848).
 *
 * It mirrors `Inventory.Domain.Suppliers.SupplierUrlPolicy` - absolute `http`/`https`, at most
 * {@link SUPPLIER_URL_MAX_LENGTH} characters once trimmed, blank meaning no address at all - so a
 * Super Admin is told what is wrong with the field they are typing in rather than after a round
 * trip. **The server is still the authority**: it applies the same rule and refuses anything this
 * misses, and its refusal sentence is what a page shows when one arrives.
 *
 * It lives here, once, for the reason the server-side rule does: the platform's supplier presets and
 * (issue #847) a business's own suppliers hold the same kind of value, and a value one form accepts
 * that the other refuses would be a copy that fails.
 */

/** The longest accepted address, measured on the trimmed value. */
export const SUPPLIER_URL_MAX_LENGTH = 2048;

/** What the server says about an address that is not an absolute http(s) one. */
export const SUPPLIER_URL_INVALID_MESSAGE = 'A website address must be a complete http:// or https:// address.';

/** What the server says about an address that is too long. */
export const SUPPLIER_URL_TOO_LONG_MESSAGE = 'A website address must be 2048 characters or fewer.';

/**
 * `null` when `url` is acceptable - including when it is blank, which means no address - and the
 * message to show otherwise.
 */
export function supplierUrlError(url: string | null | undefined): string | null {
  const trimmed = (url ?? '').trim();
  if (trimmed.length === 0) {
    return null;
  }

  if (trimmed.length > SUPPLIER_URL_MAX_LENGTH) {
    return SUPPLIER_URL_TOO_LONG_MESSAGE;
  }

  return isAbsoluteHttpUrl(trimmed) ? null : SUPPLIER_URL_INVALID_MESSAGE;
}

/** The trimmed address, or `null` when nothing was typed - what the API is sent. */
export function normaliseSupplierUrl(url: string | null | undefined): string | null {
  const trimmed = (url ?? '').trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Whether `url` is an absolute `http`/`https` address. Only those two schemes, because the value is
 * rendered as a link a person clicks: `javascript:` and `data:` are refused here as the server
 * refuses them, rather than relied on being escaped later.
 */
function isAbsoluteHttpUrl(url: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }

  return parsed.protocol === 'http:' || parsed.protocol === 'https:';
}
