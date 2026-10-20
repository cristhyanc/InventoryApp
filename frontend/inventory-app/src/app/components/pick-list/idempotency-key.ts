/**
 * A fresh opaque idempotency key for one batch refill submission (issue #744): a new pick gets a
 * new key, and `ManualPickListComponent` is the one place that decides when a retry must reuse the
 * previous one instead of calling this again.
 *
 * `crypto.randomUUID()` is unavailable in this project's Jest/jsdom test environment (though every
 * supported browser has it), so this falls back to assembling a v4-shaped UUID from
 * `crypto.getRandomValues()`, which both environments provide. Either way the result is only ever
 * compared for equality by the server; nothing about its shape is a contract.
 */
export function generateIdempotencyKey(): string {
  if (typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  // RFC 4122 version 4: fix the version and variant bits so the result still looks like a v4 UUID.
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0'));
  return `${hex.slice(0, 4).join('')}-${hex.slice(4, 6).join('')}-${hex.slice(6, 8).join('')}-${hex.slice(8, 10).join('')}-${hex.slice(10, 16).join('')}`;
}
