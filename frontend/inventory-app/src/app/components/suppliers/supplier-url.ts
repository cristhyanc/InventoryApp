/**
 * The browser-side half of the supplier website rule (issue #847).
 *
 * It mirrors `Inventory.Application.Suppliers.SupplierUrlPolicy`, which stays the authority: the
 * server validates every write whatever a form does. This copy exists for two reasons. An operator
 * sees the refusal on the field they typed in rather than as a round trip, and - the reason that
 * matters for safety - nothing is ever rendered as a link without being checked here first, so a
 * value that somehow reached the database without passing the server rule still shows as plain
 * text. Angular's own URL sanitisation stays in place behind this; no `bypassSecurityTrust*` call
 * belongs anywhere near a supplier's website.
 */

/** The longest URL that may be stored, matching the `Suppliers.Url` column and the server rule. */
export const SUPPLIER_URL_MAX_LENGTH = 2048;

export const SUPPLIER_URL_INVALID_MESSAGE =
  'Website URL must be a complete web address starting with http:// or https://.';

export const SUPPLIER_URL_TOO_LONG_MESSAGE = 'Website URL must be 2048 characters or fewer.';

export interface SupplierUrlResult {
  /** The value to send, or `null` when none was given or the value was refused. */
  url: string | null;
  /** The field-level message to show, or `null` when the value was accepted. */
  error: string | null;
}

/**
 * Normalizes a typed website URL. A blank value is "no website" and becomes `null`; anything else
 * is trimmed and must be an absolute `http`/`https` address with a host, within
 * {@link SUPPLIER_URL_MAX_LENGTH} characters.
 */
export function normalizeSupplierUrl(value: string | null | undefined): SupplierUrlResult {
  const trimmed = (value ?? '').trim();
  if (trimmed === '') {
    return { url: null, error: null };
  }

  // Length first, so an over-long value is reported against the limit the operator can act on
  // rather than against whatever the parser made of it.
  if (trimmed.length > SUPPLIER_URL_MAX_LENGTH) {
    return { url: null, error: SUPPLIER_URL_TOO_LONG_MESSAGE };
  }

  return isAbsoluteWebUrl(trimmed)
    ? { url: trimmed, error: null }
    : { url: null, error: SUPPLIER_URL_INVALID_MESSAGE };
}

/**
 * Whether a stored value may be rendered as a link. Defence in depth: the server already refuses
 * anything else, and a value that predates or bypassed that rule renders as plain text instead.
 */
export function isSafeSupplierUrl(value: string | null | undefined): boolean {
  const trimmed = (value ?? '').trim();
  return trimmed !== '' && trimmed.length <= SUPPLIER_URL_MAX_LENGTH && isAbsoluteWebUrl(trimmed);
}

/**
 * The authority pattern is checked alongside the parser because the WHATWG parser is forgiving
 * about repeated slashes for a special scheme - it reads `https:///x` as the host `x`, where the
 * server's `Uri` reads an empty host and refuses it. Requiring a host character straight after
 * `://` keeps the two rules answering the same thing.
 */
const ABSOLUTE_WEB_URL = /^https?:\/\/[^\s/?#]/i;

function isAbsoluteWebUrl(trimmed: string): boolean {
  if (!ABSOLUTE_WEB_URL.test(trimmed)) {
    return false;
  }

  try {
    const parsed = new URL(trimmed);
    return (parsed.protocol === 'http:' || parsed.protocol === 'https:') && parsed.hostname !== '';
  } catch {
    return false;
  }
}
