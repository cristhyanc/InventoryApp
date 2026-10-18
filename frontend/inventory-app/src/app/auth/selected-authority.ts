import { AuthorityId, isExternalIdConfigured } from '../auth-config';

/**
 * Which trusted authority this browser signs in against, and the fact that somebody chose it
 * (issue #711, design `docs/identity-provider-decision.md` § 9.2).
 *
 * **Why it is remembered.** The person chooses the authority once, explicitly, before any redirect;
 * everything afterwards - the token refresh, the silent acquisition, the sign-out - has to go back
 * to the authority that issued the session. MSAL partitions its cache by client id and authority,
 * so the authority is decided *before* the MSAL instance exists rather than switched on a live one:
 * `browser-auth-providers.ts` reads this at bootstrap and builds the instance for it. Switching a
 * live instance's authority is the shape that serves a token issued by authority X out of
 * authority Y's cache, and the design forbids it.
 *
 * **Where the choice is held.** `localStorage`, the same place and lifetime as MSAL's own cache, so
 * a new tab and a reload continue the same session rather than asking again. It is not a credential
 * and decides nothing about authorization: an attacker who writes `externalId` here only gets the
 * sign-in page of an authority the API may refuse. A browser that refuses storage simply asks which
 * authority to use on every page load.
 *
 * **What a tab actually uses is a snapshot of it, taken once and then immutable for that tab.**
 * `localStorage` is shared by every tab of this origin, and the three MSAL configurations - the
 * instance, the guard's `authRequest` and the interceptor's protected-resource map - are built by
 * three separate Angular factories that run at different moments: the interceptor's is not
 * constructed until the first HTTP request. A second tab writing a different choice between any two
 * of them would otherwise hand one authority's `PublicClientApplication` the *other* authority's
 * scopes, and the token acquisition that follows would fail. The same shared write landing while a
 * redirect is in flight would bring the visitor back into a tab bootstrapped for an authority that
 * did not start their journey.
 *
 * So the first read in a tab pins the answer into `sessionStorage` - which is per tab and survives
 * the redirect round trip - and every later read in that tab returns the pinned value. Choosing an
 * authority ({@link rememberSelectedAuthority}) re-pins it, because that *is* this tab's new
 * decision and the redirect about to start is made with it. A tab whose `sessionStorage` is refused
 * falls back to reading the shared record, exactly as before.
 *
 * **It is forgotten on sign-out** (design § 9.3): a sign-out ends one authority's session, and the
 * next person at this browser chooses again rather than being sent silently to the previous one.
 * That clears the shared record and this tab's pin together.
 */
export const SELECTED_AUTHORITY_KEY = 'inventoryapp.selectedAuthority';

/** Where this tab's own immutable snapshot of that choice is pinned, for this page load and the
 * redirect round trip after it. */
export const TAB_AUTHORITY_KEY = 'inventoryapp.tabAuthority';

/** A stored value read back as an authority, or `null` when it is not one this build honours. */
function asAuthority(stored: string | null): AuthorityId | null {
  if (stored === 'workforce') {
    return 'workforce';
  }

  // A remembered customer identity authority is honoured only while this build still has one
  // configured. Clearing the configuration is the rollback lever (design § 15.1), and it must take
  // every browser back to workforce sign-in rather than leave them pointed at an authority that is
  // no longer there. A pin is checked against the same rule, so a rollback reaches an open tab too.
  return stored === 'externalId' && isExternalIdConfigured() ? 'externalId' : null;
}

/** The browser-wide choice, or `null` when nobody has chosen one this browser still honours. */
function readShared(): AuthorityId | null {
  try {
    return asAuthority(localStorage.getItem(SELECTED_AUTHORITY_KEY));
  } catch {
    return null;
  }
}

/** This tab's pinned snapshot, or `null` when nothing is pinned yet or storage refuses. */
function readPinned(): AuthorityId | null {
  try {
    return asAuthority(sessionStorage.getItem(TAB_AUTHORITY_KEY));
  } catch {
    return null;
  }
}

function pin(authority: AuthorityId): void {
  try {
    sessionStorage.setItem(TAB_AUTHORITY_KEY, authority);
  } catch {
    // Storage refused: this tab reads the shared record every time instead, which is what the
    // application did before the snapshot existed.
  }
}

/** Whether a deliberate choice is on record, which is what stops the sign-in page asking again. */
export function hasSelectedAuthority(): boolean {
  return readShared() !== null;
}

/**
 * The authority every MSAL configuration in this tab is built for: the tab's snapshot, taken from
 * the browser-wide choice on first use and unchanged by anything another tab does afterwards.
 */
export function readSelectedAuthority(): AuthorityId {
  const pinned = readPinned();
  if (pinned !== null) {
    return pinned;
  }

  const authority = readShared() ?? 'workforce';
  pin(authority);
  return authority;
}

/** Records the authority the next redirect is made against, before that redirect starts. */
export function rememberSelectedAuthority(authority: AuthorityId): void {
  // This tab's snapshot first: the redirect about to start is made against this authority, and the
  // page it returns to has to bootstrap the matching instance even if another tab has since chosen
  // differently.
  pin(authority);

  try {
    localStorage.setItem(SELECTED_AUTHORITY_KEY, authority);
  } catch {
    // Storage refused: the choice still applies to this page load, because the caller hands the
    // chosen authority straight to its own MSAL instance. Only the memory of it is lost.
  }
}

/** Forgets the choice, so the next sign-in asks again. Called on sign-out. */
export function forgetSelectedAuthority(): void {
  try {
    sessionStorage.removeItem(TAB_AUTHORITY_KEY);
  } catch {
    // Storage refused: there is nothing pinned to forget.
  }

  try {
    localStorage.removeItem(SELECTED_AUTHORITY_KEY);
  } catch {
    // Storage refused: there is nothing recorded to forget.
  }
}

/**
 * Whether a visitor must be asked which authority to sign in against before any redirect starts.
 *
 * Only three things make the question real, and all three must hold: this build has a second
 * authority configured at all, nobody is signed in here yet, and no choice is on record. A
 * deployment with no customer identity tenant therefore behaves exactly as it did before issue
 * #711 - `MsalGuard` redirects straight to the workforce authority, with no extra page and no extra
 * click.
 */
export function needsAuthorityChoice(isSignedIn: boolean): boolean {
  return !isSignedIn && isExternalIdConfigured() && !hasSelectedAuthority();
}
