/**
 * The one place an invite token is held between opening an invite link and the acceptance page
 * reading it (issue #681, review of PR #696).
 *
 * **Why it exists.** A signed-out visitor who opens `/invite/<token>` has to leave the application
 * for the Entra sign-in page and come back. Before this, the token made that trip inside the URL
 * `MsalGuard` hands MSAL as its redirect start page, and MSAL kept that URL in its own cache
 * (`auth-config.ts` puts it in `localStorage`, shared by every tab and kept after the tab closes)
 * and navigated back to it afterwards. Now `inviteTokenAddressBarGuard` puts the token here and
 * sends the router to the tokenless `/invite` before `MsalGuard` runs, so the only URL MSAL ever sees,
 * stores or returns to is `/invite`.
 *
 * **Where it is held, and for how long.** `sessionStorage`, because it is the narrowest storage that
 * survives the sign-in round trip: one tab only, never sent to any server, gone when the tab closes.
 * The entry is removed the moment the acceptance page reads it, and an entry older than
 * {@link PENDING_INVITE_TOKEN_LIFETIME_MS} is treated as absent and removed, so a sign-in abandoned
 * half-way does not leave an invite waiting in the tab. Every access is guarded: a browser that
 * refuses storage (private mode, blocked site data) just loses the hand-off, and the visitor is told
 * to open the link again by the `/invite` page, exactly as after a reload.
 */
export const PENDING_INVITE_TOKEN_KEY = 'inventoryapp.pendingInviteToken';

/** How long a stored token may wait for its acceptance page: long enough for a sign-in, no longer. */
export const PENDING_INVITE_TOKEN_LIFETIME_MS = 15 * 60 * 1000;

interface PendingInviteToken {
  token: string;
  storedAt: number;
}

function read(): PendingInviteToken | null {
  try {
    const raw = sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY);
    if (raw === null) {
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<PendingInviteToken> | null;
    const fresh =
      typeof parsed?.token === 'string' &&
      parsed.token.length > 0 &&
      typeof parsed.storedAt === 'number' &&
      Date.now() - parsed.storedAt <= PENDING_INVITE_TOKEN_LIFETIME_MS;
    if (!fresh) {
      forget();
      return null;
    }

    return { token: parsed.token as string, storedAt: parsed.storedAt as number };
  } catch {
    forget();
    return null;
  }
}

function forget(): void {
  try {
    sessionStorage.removeItem(PENDING_INVITE_TOKEN_KEY);
  } catch {
    // Storage refused: there is nothing held to forget.
  }
}

/** Holds `token` for this tab's acceptance page, replacing any invite opened before it. */
export function storePendingInviteToken(token: string): void {
  try {
    sessionStorage.setItem(PENDING_INVITE_TOKEN_KEY, JSON.stringify({ token, storedAt: Date.now() }));
  } catch {
    // Storage refused: the `/invite` page will ask for the link to be opened again.
  }
}

/** Whether this tab holds an invite token the acceptance page has not read yet. */
export function hasPendingInviteToken(): boolean {
  return read() !== null;
}

/** Reads the held token and removes it in the same step, so it is only ever read once. */
export function takePendingInviteToken(): string | null {
  const pending = read();
  forget();
  return pending?.token ?? null;
}
