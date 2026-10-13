/**
 * Pulls the invite token out of whatever a person pasted into the welcome or rejected page's "I
 * have an invite link" field (issue #542): the full `/invite/<token>` link #535's acceptance page
 * reads, or just the bare token on its own. Returns `null` only for blank input - every other
 * value is handed to `/invite/<token>`, whose own page and redemption endpoint are the actual
 * authority on whether it is usable; this function does not validate a token's shape.
 */
export function extractInviteToken(pastedValue: string): string | null {
  const trimmed = pastedValue.trim();
  if (trimmed.length === 0) {
    return null;
  }

  const match = /\/invite\/([^/?#]+)/.exec(trimmed);
  return match ? match[1] : trimmed;
}
