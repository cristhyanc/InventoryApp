import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { Observable, filter, map, merge } from 'rxjs';

/** The MSAL events that mean the signed-in identity may have changed. */
const ACCOUNT_EVENTS: ReadonlySet<string> = new Set<string>([
  EventType.LOGIN_SUCCESS,
  EventType.LOGOUT_SUCCESS,
  EventType.ACTIVE_ACCOUNT_CHANGED
]);

/**
 * Emits whenever the signed-in identity may have changed - sign-in, sign-out and an active-account
 * change - from the two signals that report them: MSAL's own account events, and `inProgress$`
 * settling, which is what the shell already uses to decide that an interaction has finished.
 *
 * It only says "look again": callers compare `activeAccountKey` with the identity their cached
 * answer belongs to and decide for themselves. `AccessService` and
 * `PlatformDiagnosticsAccessService` share this trigger and nothing else.
 */
export function signedInAccountMayHaveChanged(msalBroadcastService: MsalBroadcastService): Observable<void> {
  return merge(
    msalBroadcastService.inProgress$.pipe(
      filter((status: InteractionStatus) => status === InteractionStatus.None)
    ),
    msalBroadcastService.msalSubject$.pipe(
      filter((event: EventMessage) => ACCOUNT_EVENTS.has(event.eventType))
    )
  ).pipe(map(() => undefined));
}

/**
 * The identity a cached answer belongs to: the active account, or the first signed-in account,
 * keyed by `homeAccountId` (falling back to `localAccountId` and `username`). `null` when nobody is
 * signed in.
 */
export function activeAccountKey(authService: MsalService): string | null {
  const account = authService.instance.getActiveAccount() ?? authService.instance.getAllAccounts()[0] ?? null;
  if (account === null) {
    return null;
  }

  return account.homeAccountId || account.localAccountId || account.username || '';
}
