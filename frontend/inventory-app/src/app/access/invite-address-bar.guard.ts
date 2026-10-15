import { CanMatchFn } from '@angular/router';

/**
 * `/invite`, the tokenless address an invite link is left at once its token has been stripped out
 * of the address bar (issue #681). It is also a route of its own, `InviteLinkReopenComponent`, so a
 * reload of that address explains what happened instead of falling through to the wildcard.
 */
export const INVITE_ADDRESS_BAR_ROUTE = 'invite';

/** A browser path still showing an `/invite/<token>` link, as opened from the address bar. */
const TOKEN_BEARING_INVITE_PATH = /\/invite\/[^/]+\/?$/;

/**
 * Strips the invite token out of the browser's address bar before `MsalGuard` can run (issue
 * #681, finding 3 of the #328 implementation review).
 *
 * `InviteAcceptanceComponent` already removes the token in its own `ngOnInit`, but a signed-out
 * visitor never reaches it: `MsalGuard` sends them to the Entra sign-in page first, and that
 * navigation would leave the token-bearing URL in the address bar and in the session history entry
 * Back returns to. This runs as the route's `canMatch` guard, during route recognition and so
 * before any `canActivate` guard - `MsalGuard` included - is even called, and rewrites the current
 * history entry to `/invite` with `history.replaceState`.
 *
 * It holds nothing and stores nothing. The token stays only where the router already has it - the
 * URL tree of the navigation in progress, untouched by `replaceState` - so `MsalGuard` still hands
 * that exact URL to MSAL as its redirect start page (the one accepted residual the invite
 * acceptance page documents) and `InviteAcceptanceComponent` still reads it from the matched route.
 * It rewrites the address bar only when the browser actually shows an invite link; an in-app
 * navigation to `/invite/<token>` (the welcome page's "I have an invite link") leaves the previous
 * page's own history entry alone. It always matches, so it decides nothing about access.
 */
export const inviteTokenAddressBarGuard: CanMatchFn = () => {
  if (TOKEN_BEARING_INVITE_PATH.test(window.location.pathname)) {
    history.replaceState(history.state, '', `/${INVITE_ADDRESS_BAR_ROUTE}`);
  }

  return true;
};
