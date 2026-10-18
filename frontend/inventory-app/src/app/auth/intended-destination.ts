/**
 * The page a visitor asked for before they were sent to the sign-in page, held across the hosted
 * sign-in round trip (issue #711).
 *
 * **Why it exists.** `MsalGuard` used to be the first thing a signed-out visitor met, and it hands
 * MSAL the requested URL as the redirect start page itself. With a second authority the visitor
 * meets the sign-in page first, so the destination has to survive that one extra step and then the
 * redirect - including a cancelled one, which comes back to `/auth` and then to the sign-in page to
 * be tried again.
 *
 * **It is never caller-supplied.** The design's open-redirect protection is "there is no
 * caller-supplied return URL" (§ 9.1), so the destination is taken from the router state the guard
 * refused and is never read from a query parameter, a fragment or anything the authorize response
 * carries. {@link isInternalDestination} is defence in depth on top of that: an absolute URL, a
 * protocol-relative `//host` (with or without a backslash, which browsers normalise), and anything
 * carrying whitespace or control characters are all refused, so nothing stored here can send a
 * returning visitor off this origin.
 *
 * **The two authentication addresses are refused too**, which is what stops a callback loop: a
 * destination of `/auth` would return the visitor to the callback page they just came from, and one
 * of `/sign-in` would return them to the chooser for ever. Neither is a page anybody asked for.
 *
 * **Where it is held, and until when.** `sessionStorage`, one tab only, never sent to a server,
 * with the same 15-minute window as the pending invite token (`access/pending-invite-token.ts`).
 * It is held until a sign-in **completes**, not until it is read: a hosted journey that is
 * cancelled, refused or could not start at all comes back carrying nothing, and the retry the
 * sign-in page offers has to return the visitor to the page they asked for rather than quietly to
 * the application's home page. {@link peekIntendedDestination} therefore reads without consuming,
 * {@link clearIntendedDestination} is what the completed callback calls, and an abandoned sign-in
 * still leaves nothing usable in the tab because the window is enforced on every read.
 *
 * **A retry never extends that window.** Reusing a destination only reads it, so `storedAt` stays
 * the instant the guard recorded it: fifteen minutes is fifteen minutes from the page the visitor
 * asked for, however many attempts happen inside it. Only a fresh refusal by the guard records a
 * new destination with a new instant.
 *
 * An invite is unaffected by any of this: its token travels in its own store and the destination
 * recorded for it is the tokenless `/invite`, exactly as before.
 */
export const INTENDED_DESTINATION_KEY = 'inventoryapp.intendedDestination';

/** How long a destination may wait for its sign-in; the pending invite token's window. */
export const INTENDED_DESTINATION_LIFETIME_MS = 15 * 60 * 1000;

/** The sign-in page's path, which is also one of the two destinations this module refuses. */
export const SIGN_IN_ROUTE = 'sign-in';

/** The MSAL redirect callback's path, the other refused destination. */
export const AUTH_CALLBACK_ROUTE = 'auth';

const REFUSED_ROUTES = [SIGN_IN_ROUTE, AUTH_CALLBACK_ROUTE];

interface StoredDestination {
  url: string;
  storedAt: number;
}

/**
 * Whether `url` is a path inside this application that it makes sense to return somebody to.
 */
export function isInternalDestination(url: string): boolean {
  if (typeof url !== 'string' || url.length === 0 || url.length > 2048) {
    return false;
  }

  // Exactly one leading slash: `//host` and `/\host` are protocol-relative URLs to another origin.
  if (!url.startsWith('/') || url.startsWith('//') || url.startsWith('/\\')) {
    return false;
  }

  // A backslash, whitespace or a control character is never part of a path this router produced,
  // and each of them is a way browsers have been persuaded to read a path as a host. The control
  // characters are checked by code point rather than by a regular expression, which is what
  // `no-control-regex` is for.
  if (url.includes('\\') || /\s/.test(url) || [...url].some(isControlCharacter)) {
    return false;
  }

  let path = url.split(/[?#]/, 1)[0];
  while (path.length > 1 && path.endsWith('/')) {
    path = path.slice(0, -1);
  }

  return !REFUSED_ROUTES.some((route) => path === `/${route}`);
}

function isControlCharacter(character: string): boolean {
  const code = character.codePointAt(0) ?? 0;
  return code < 0x20 || code === 0x7f;
}

/** Forgets the held destination. Called when a sign-in completes, and on an unusable one. */
export function clearIntendedDestination(): void {
  try {
    sessionStorage.removeItem(INTENDED_DESTINATION_KEY);
  } catch {
    // Storage refused: there is nothing held to forget.
  }
}

/** Holds the page `url` for the sign-in about to start, replacing any destination before it. */
export function rememberIntendedDestination(url: string): void {
  if (!isInternalDestination(url)) {
    // Not somewhere to return to. Drop whatever was held rather than keep an older destination
    // that this navigation has superseded.
    clearIntendedDestination();
    return;
  }

  try {
    sessionStorage.setItem(INTENDED_DESTINATION_KEY, JSON.stringify({ url, storedAt: Date.now() }));
  } catch {
    // Storage refused: the sign-in still works and lands on the application's home page.
  }
}

/**
 * Reads the held destination, leaving it in place for a sign-in that has to be tried again.
 *
 * The store is readable by anything running in this tab, so what comes out of it is checked again
 * rather than trusted because it went in through this module, and a destination that is expired,
 * tampered with or unreadable is dropped here rather than left to be re-read on every attempt.
 */
export function peekIntendedDestination(): string | null {
  let raw: string | null = null;
  try {
    raw = sessionStorage.getItem(INTENDED_DESTINATION_KEY);
  } catch {
    return null;
  }

  if (raw === null) {
    return null;
  }

  const usable = readUsable(raw);
  if (usable === null) {
    clearIntendedDestination();
  }

  return usable;
}

function readUsable(raw: string): string | null {
  try {
    const parsed = JSON.parse(raw) as Partial<StoredDestination> | null;
    const usable =
      typeof parsed?.url === 'string' &&
      isInternalDestination(parsed.url) &&
      typeof parsed.storedAt === 'number' &&
      Date.now() - parsed.storedAt <= INTENDED_DESTINATION_LIFETIME_MS;

    return usable ? (parsed.url as string) : null;
  } catch {
    return null;
  }
}
