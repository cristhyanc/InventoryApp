import { ACCOUNT_STATES, AccountStateAnswer, AccountStateName } from './account-state.service';
import { BusinessNavigationContext, businessNavigationFor } from './business-navigation';

const MEMBER: AccountStateAnswer = {
  status: 'known',
  accountState: { state: 'Member', onboardingEnabled: false, rejectionReason: null }
};

/** Every published state that is not `Member`, so a state added later is covered here too. */
const NONMEMBER_STATES = ACCOUNT_STATES.filter((state) => state !== 'Member');

function known(state: AccountStateName): AccountStateAnswer {
  return { status: 'known', accountState: { state, onboardingEnabled: false, rejectionReason: null } };
}

/** A signed-in account in ordinary context, which is what each test varies one thing from. */
function context(overrides: Partial<BusinessNavigationContext> = {}): BusinessNavigationContext {
  return { isSignedIn: true, accountState: MEMBER, isSupportViewEffective: false, ...overrides };
}

describe('businessNavigationFor (issue #676)', () => {
  it('offers the business navigation to a confirmed Member', () => {
    expect(businessNavigationFor(context())).toBe('offered');
  });

  it('withholds it from a signed-out visitor, whatever a previous answer said', () => {
    expect(businessNavigationFor(context({ isSignedIn: false }))).toBe('withheld');
    expect(businessNavigationFor(context({ isSignedIn: false, accountState: { status: 'unavailable' } }))).toBe(
      'withheld'
    );
    expect(businessNavigationFor(context({ isSignedIn: false, isSupportViewEffective: true }))).toBe('withheld');
  });

  /**
   * "Nothing has been answered yet" is deliberately not "answered, and could not be answered": a
   * menu that offered every destination and then took entries away would have shown entries that
   * were never this person's, which is the rule the sidebar already follows for an unread
   * capability set.
   */
  it('withholds it while the account state has not been answered yet', () => {
    expect(businessNavigationFor(context({ accountState: { status: 'unread' } }))).toBe('withheld');
  });

  it.each(NONMEMBER_STATES)('withholds it from a known %s account', (state) => {
    expect(businessNavigationFor(context({ accountState: known(state) }))).toBe('withheld');
  });

  /**
   * The fail-open case issue #529 documents for `accountStateGuard`, kept here: the membership
   * check `BusinessScopeMiddleware` applies to every business call remains the real boundary, so a
   * failed probe must not strand a legitimate member with no menu.
   */
  it('offers it when the account state could not be read', () => {
    expect(businessNavigationFor(context({ accountState: { status: 'unavailable' } }))).toBe('offered');
  });

  /**
   * An effective support view resolves the session's business rather than this administrator's own
   * membership (issue #671's same exception on the shell's business lookup), so the administrator's
   * own account state does not decide the menu in it - including the platform administrator who
   * holds no membership at all.
   */
  it.each([...NONMEMBER_STATES.map(known), { status: 'unread' } as AccountStateAnswer])(
    'offers it in an effective support view whatever the administrator’s own state is (%p)',
    (accountState) => {
      expect(businessNavigationFor(context({ accountState, isSupportViewEffective: true }))).toBe('offered');
    }
  );
});
