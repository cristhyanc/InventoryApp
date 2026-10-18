import {
  INTENDED_DESTINATION_KEY,
  INTENDED_DESTINATION_LIFETIME_MS,
  isInternalDestination,
  rememberIntendedDestination,
  takeIntendedDestination
} from './intended-destination';

/**
 * The page a visitor is returned to after a hosted sign-in (issue #711).
 *
 * The destination never comes from the authorize response or from anything a caller supplies - the
 * guard takes it from the router state it refused - and these tests pin the second line of
 * defence: nothing stored here can send a returning visitor off this origin, back to the callback
 * page they just came from, or back to the sign-in chooser for ever.
 */
describe('the intended destination (issue #711)', () => {
  beforeEach(() => sessionStorage.clear());

  it('accepts a page inside this application, with its query and fragment', () => {
    for (const destination of ['/', '/reports/daily', '/stock-history?productId=12', '/invite', '/admin/users#members']) {
      expect(isInternalDestination(destination)).toBe(true);
    }
  });

  it('refuses anything that could leave this origin', () => {
    for (const external of [
      'https://evil.example/steal',
      'http://evil.example',
      '//evil.example/steal',
      '/\\evil.example/steal',
      '\\\\evil.example',
      'reports/daily',
      '/reports\\../../evil',
      '/reports/daily ',
      '/reports/\tdaily',
      '',
      `/${'a'.repeat(4096)}`
    ]) {
      expect(isInternalDestination(external)).toBe(false);
    }
  });

  /**
   * Returning somebody to `/auth` sends them to the callback page they have just come back from,
   * and returning them to `/sign-in` sends them to the chooser they have just used. Both are
   * loops, and neither is a page anybody asked for.
   */
  it('refuses the two authentication addresses, so a callback cannot loop', () => {
    for (const loop of ['/auth', '/auth/', '/auth?code=abc', '/sign-in', '/sign-in/', '/sign-in?x=1']) {
      expect(isInternalDestination(loop)).toBe(false);
    }
  });

  it('holds a destination for one read and then forgets it', () => {
    rememberIntendedDestination('/reports/daily');

    expect(takeIntendedDestination()).toBe('/reports/daily');
    expect(takeIntendedDestination()).toBeNull();
    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
  });

  it('stores nothing for a destination it refuses, and drops any destination held before it', () => {
    rememberIntendedDestination('/reports/daily');
    rememberIntendedDestination('//evil.example/steal');

    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
    expect(takeIntendedDestination()).toBeNull();
  });

  it('treats an abandoned sign-in older than its window as no destination at all', () => {
    const storedAt = Date.now() - INTENDED_DESTINATION_LIFETIME_MS - 1;
    sessionStorage.setItem(INTENDED_DESTINATION_KEY, JSON.stringify({ url: '/reports/daily', storedAt }));

    expect(takeIntendedDestination()).toBeNull();
  });

  /**
   * The store is readable by anything running in this tab, so what comes out of it is checked
   * again rather than trusted because it went in through this module.
   */
  it('refuses a stored destination that was tampered with, and unreadable contents', () => {
    sessionStorage.setItem(
      INTENDED_DESTINATION_KEY,
      JSON.stringify({ url: 'https://evil.example/steal', storedAt: Date.now() })
    );
    expect(takeIntendedDestination()).toBeNull();

    sessionStorage.setItem(INTENDED_DESTINATION_KEY, 'not json');
    expect(takeIntendedDestination()).toBeNull();
  });
});
