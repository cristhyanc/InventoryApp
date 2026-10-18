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
 * **Where it is held.** `localStorage`, the same place and lifetime as MSAL's own cache, so a new
 * tab and a reload continue the same session rather than asking again. It is not a credential and
 * decides nothing about authorization: an attacker who writes `externalId` here only gets the
 * sign-in page of an authority the API may refuse. A browser that refuses storage simply asks which
 * authority to use on every page load.
 *
 * **It is forgotten on sign-out** (design § 9.3): a sign-out ends one authority's session, and the
 * next person at this browser chooses again rather than being sent silently to the previous one.
 */
export const SELECTED_AUTHORITY_KEY = 'inventoryapp.selectedAuthority';

/** The authority recorded here, or `null` when nobody has chosen one this browser still honours. */
function read(): AuthorityId | null {
  let stored: string | null = null;
  try {
    stored = localStorage.getItem(SELECTED_AUTHORITY_KEY);
  } catch {
    return null;
  }

  if (stored === 'workforce') {
    return 'workforce';
  }

  // A remembered customer identity authority is honoured only while this build still has one
  // configured. Clearing the configuration is the rollback lever (design § 15.1), and it must take
  // every browser back to workforce sign-in rather than leave them pointed at an authority that is
  // no longer there.
  return stored === 'externalId' && isExternalIdConfigured() ? 'externalId' : null;
}

/** Whether a deliberate choice is on record, which is what stops the sign-in page asking again. */
export function hasSelectedAuthority(): boolean {
  return read() !== null;
}

/** The authority to build this page load's MSAL instance for; workforce unless one was chosen. */
export function readSelectedAuthority(): AuthorityId {
  return read() ?? 'workforce';
}

/** Records the authority the next redirect is made against, before that redirect starts. */
export function rememberSelectedAuthority(authority: AuthorityId): void {
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
