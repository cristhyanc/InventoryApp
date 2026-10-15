import { AccountStateAnswer } from './account-state.service';

/**
 * Whether the application shell offers the ordinary business navigation at all (issue #676).
 *
 * It is the one question `AccessService`'s capability set cannot answer. That read has three
 * answers and its `unavailable` one covers two different situations: a confirmed member whose
 * capability probe failed, for whom offering every destination is the documented fail-open rule
 * (`AccessService`), and somebody the API already told us has no usable membership at all, for
 * whom offering every destination is a full business menu nothing behind it would ever answer.
 * The account state (`GET /api/me/account-state`, issue #523) is what tells the two apart, and
 * this is where it does.
 */
export type BusinessNavigationVisibility = 'offered' | 'withheld';

/** What the shell knows about who is signed in when it decides that. */
export interface BusinessNavigationContext {
  /** Whether MSAL holds an account at all - a signed-out visitor is nobody's member. */
  readonly isSignedIn: boolean;

  /** The cached `GET /api/me/account-state` answer for the identity signed in **now**. */
  readonly accountState: AccountStateAnswer;

  /**
   * Whether this tab is in an **effective** support view (issue #553). The business its requests
   * resolve is then the session's own rather than this administrator's membership, which they may
   * not have at all.
   */
  readonly isSupportViewEffective: boolean;
}

/**
 * Whether `context` may be offered the ordinary business navigation.
 *
 * - **signed out** - withheld. There is no account to be a member of, and every destination's own
 *   endpoint would answer `401`;
 * - **an effective support view** - offered, whatever the administrator's own account state says:
 *   the session names the business this tab's requests resolve, and `GET /api/me/access` answers
 *   for that business (issue #671's same exception on the shell's business lookup);
 * - **`unread`** - withheld. Nothing has been answered yet, and a menu that offered every
 *   destination and then took them away would have shown entries that were never this person's -
 *   the same rule the sidebar already follows for an unread capability set;
 * - **a known `Member`** - offered. What of it each role may reach is still the capability set's
 *   decision, including its fail-open one;
 * - **any other known state** - withheld. `NoMembership`, `ApplicationPending`,
 *   `ApplicationRejected`, `MembershipRevoked`, `BusinessDeactivated` and `MembershipAmbiguous`
 *   are all accounts `BusinessScopeMiddleware` refuses every business call for, and
 *   `accountStateGuard` routes each of them to the one page that explains it (issue #529);
 * - **`unavailable`** - offered. An account state that could not be read stays the documented
 *   fail-open case: `accountStateGuard` lets the navigation through for it, and withholding the
 *   menu over a failed probe would strand a legitimate member instead.
 *
 * **It hides a menu rather than protecting a page**, exactly as the capability set does. Every
 * destination's endpoint authorizes its own request - `BusinessScopeMiddleware` for the membership
 * and the capability policy for the role - so this changes what a person is offered, never what
 * they may read. The separately authorized `Super Admin` navigation is deliberately not this
 * decision: platform-admin authority is independent of business membership (issue #546), so an
 * administrator with no membership of their own keeps that group while this withholds the rest.
 */
export function businessNavigationFor(context: BusinessNavigationContext): BusinessNavigationVisibility {
  if (!context.isSignedIn) {
    return 'withheld';
  }

  if (context.isSupportViewEffective) {
    return 'offered';
  }

  switch (context.accountState.status) {
    case 'unread':
      return 'withheld';
    case 'known':
      return context.accountState.accountState.state === 'Member' ? 'offered' : 'withheld';
    default:
      return 'offered';
  }
}
