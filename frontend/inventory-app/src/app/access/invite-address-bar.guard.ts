import { inject } from '@angular/core';
import { CanMatchFn, RedirectCommand, Router } from '@angular/router';
import { hasPendingInviteToken, storePendingInviteToken } from './pending-invite-token';

/**
 * `/invite`, the only address an invite is ever accepted at (issue #681). An invite link's
 * `/invite/<token>` is turned into it by {@link inviteTokenAddressBarGuard} before anything else
 * sees the token. Two routes share it: the acceptance page while this tab holds a token
 * ({@link pendingInviteTokenGuard}), and `InviteLinkReopenComponent` otherwise, so a reload or a
 * Back into it explains what happened instead of falling through to the wildcard.
 */
export const INVITE_ADDRESS_BAR_ROUTE = 'invite';

/** A browser path still showing an `/invite/<token>` link, as opened from the address bar. */
const TOKEN_BEARING_INVITE_PATH = /\/invite\/[^/]+\/?$/;

/**
 * Takes the token out of an `/invite/<token>` navigation before any `canActivate` guard - `MsalGuard`
 * included - can see it (issue #681: finding 3 of the #328 implementation review, then the review
 * of PR #696).
 *
 * It runs as the route's `canMatch` guard, during route recognition, so it always runs first. It
 * hands the token to `pending-invite-token.ts`, rewrites the browser's current history entry to
 * `/invite` when the address bar shows the link, and redirects the navigation to `/invite`. It
 * never lets `/invite/<token>` itself activate. So `MsalGuard` only ever runs on `/invite`, and a
 * signed-out visitor's sign-in redirect carries, stores and returns to `/invite`: the token is not
 * in MSAL's redirect start page, its cache, the history entry Back returns to, or the address a
 * `Referer` could be built from.
 *
 * The history rewrite happens here, not by the router, because the router only updates the address
 * bar once a navigation's guards have passed, and a signed-out visitor's `MsalGuard` leaves the page
 * before that. An in-app navigation to `/invite/<token>` (the welcome page's "I have an invite link")
 * never put the token in the address bar, so it leaves the previous page's history entry alone and
 * the redirect adds `/invite` after it as usual.
 */
export const inviteTokenAddressBarGuard: CanMatchFn = (_route, segments) => {
  const router = inject(Router);
  const token = segments[1]?.path ?? '';
  if (token.length > 0) {
    storePendingInviteToken(token);
  }

  const openedFromAddressBar = TOKEN_BEARING_INVITE_PATH.test(window.location.pathname);
  if (openedFromAddressBar) {
    history.replaceState(history.state, '', `/${INVITE_ADDRESS_BAR_ROUTE}`);
  }

  return new RedirectCommand(router.parseUrl(`/${INVITE_ADDRESS_BAR_ROUTE}`), { replaceUrl: openedFromAddressBar });
};

/**
 * Matches the acceptance page at `/invite` only while this tab holds an invite token it has not
 * read yet; otherwise `/invite` falls through to `InviteLinkReopenComponent`.
 */
export const pendingInviteTokenGuard: CanMatchFn = () => hasPendingInviteToken();
