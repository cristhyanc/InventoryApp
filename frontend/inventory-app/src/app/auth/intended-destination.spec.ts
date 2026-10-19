import {
  INTENDED_DESTINATION_KEY,
  INTENDED_DESTINATION_LIFETIME_MS,
  clearIntendedDestination,
  isInternalDestination,
  peekIntendedDestination,
  rememberIntendedDestination
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

  /**
   * A sign-in that is cancelled or cannot start at all comes back carrying nothing, and the retry
   * the sign-in page offers has to return the visitor to the page they asked for. The destination
   * is therefore held until the callback that *completes* releases it, not until it is first read.
   */
  it('holds a destination across repeated reads, until a completed sign-in clears it', () => {
    rememberIntendedDestination('/reports/daily');

    expect(peekIntendedDestination()).toBe('/reports/daily');
    expect(peekIntendedDestination()).toBe('/reports/daily');

    clearIntendedDestination();

    expect(peekIntendedDestination()).toBeNull();
    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
  });

  /** Reusing a destination only reads it, so fifteen minutes stays fifteen minutes from the page
   * the guard refused, however many attempts happen inside that window. */
  it('never extends its own window by being read again', () => {
    const storedAt = Date.now() - INTENDED_DESTINATION_LIFETIME_MS + 1_000;
    sessionStorage.setItem(INTENDED_DESTINATION_KEY, JSON.stringify({ url: '/reports/daily', storedAt }));

    expect(peekIntendedDestination()).toBe('/reports/daily');

    const held = JSON.parse(sessionStorage.getItem(INTENDED_DESTINATION_KEY) ?? 'null') as { storedAt: number };
    expect(held.storedAt).toBe(storedAt);
  });

  it('stores nothing for a destination it refuses, and drops any destination held before it', () => {
    rememberIntendedDestination('/reports/daily');
    rememberIntendedDestination('//evil.example/steal');

    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
    expect(peekIntendedDestination()).toBeNull();
  });

  it('treats an abandoned sign-in older than its window as no destination at all, and drops it', () => {
    const storedAt = Date.now() - INTENDED_DESTINATION_LIFETIME_MS - 1;
    sessionStorage.setItem(INTENDED_DESTINATION_KEY, JSON.stringify({ url: '/reports/daily', storedAt }));

    expect(peekIntendedDestination()).toBeNull();
    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
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
    expect(peekIntendedDestination()).toBeNull();
    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();

    sessionStorage.setItem(INTENDED_DESTINATION_KEY, 'not json');
    expect(peekIntendedDestination()).toBeNull();
    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
  });
});
