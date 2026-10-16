import {
  PENDING_INVITE_TOKEN_KEY,
  PENDING_INVITE_TOKEN_LIFETIME_MS,
  hasPendingInviteToken,
  storePendingInviteToken,
  takePendingInviteToken
} from './pending-invite-token';

/**
 * The tab's pending invite (issue #681, review of PR #696): the one place an invite token waits
 * through a sign-in redirect, instead of MSAL's redirect start page.
 */
describe('pending invite token (issue #681)', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    sessionStorage.clear();
    localStorage.clear();
  });

  it('is held in this tab only, and never in localStorage', () => {
    storePendingInviteToken('secret');

    expect(hasPendingInviteToken()).toBe(true);
    expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toContain('secret');
    expect(localStorage.length).toBe(0);
  });

  it('can be taken exactly once', () => {
    storePendingInviteToken('secret');

    expect(takePendingInviteToken()).toBe('secret');
    expect(takePendingInviteToken()).toBeNull();
    expect(hasPendingInviteToken()).toBe(false);
    expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toBeNull();
  });

  it('is replaced by a newer invite opened in the same tab', () => {
    storePendingInviteToken('first');
    storePendingInviteToken('second');

    expect(takePendingInviteToken()).toBe('second');
  });

  it('expires, and is removed, once a sign-in has been abandoned for longer than its lifetime', () => {
    const now = jest.spyOn(Date, 'now').mockReturnValue(1_000_000);
    storePendingInviteToken('secret');
    now.mockReturnValue(1_000_000 + PENDING_INVITE_TOKEN_LIFETIME_MS + 1);

    expect(hasPendingInviteToken()).toBe(false);
    expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toBeNull();
    expect(takePendingInviteToken()).toBeNull();
  });

  it('treats an unreadable entry as absent', () => {
    sessionStorage.setItem(PENDING_INVITE_TOKEN_KEY, 'not json');

    expect(takePendingInviteToken()).toBeNull();
    expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toBeNull();
  });

  it('loses the invite rather than failing when the browser refuses storage', () => {
    jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    jest.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    expect(() => storePendingInviteToken('secret')).not.toThrow();
    expect(hasPendingInviteToken()).toBe(false);
    expect(takePendingInviteToken()).toBeNull();
  });
});
