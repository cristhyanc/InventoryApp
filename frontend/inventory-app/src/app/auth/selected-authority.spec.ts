import { configureExternalId, unconfigureExternalId } from './external-id.spec-helpers';
import {
  SELECTED_AUTHORITY_KEY,
  TAB_AUTHORITY_KEY,
  forgetSelectedAuthority,
  hasSelectedAuthority,
  needsAuthorityChoice,
  readSelectedAuthority,
  rememberSelectedAuthority
} from './selected-authority';

/**
 * Which authority this browser signs in against (issue #711).
 *
 * The two things these tests exist for: an unconfigured deployment must behave exactly as it did
 * before the second authority existed, and clearing the configuration - the design's rollback
 * lever (§ 15.1) - must take every browser back to workforce sign-in rather than leave one
 * pointed at an authority that is no longer there.
 */
describe('the selected authority (issue #711)', () => {
  beforeEach(() => {
    localStorage.clear();
    // A fresh tab: nothing pinned yet, so the first read takes its snapshot from the shared record.
    sessionStorage.clear();
    unconfigureExternalId();
  });

  afterEach(() => unconfigureExternalId());

  describe('with no customer identity tenant configured (the shipped state)', () => {
    it('signs in against the workforce authority and asks nobody anything', () => {
      expect(readSelectedAuthority()).toBe('workforce');
      expect(needsAuthorityChoice(false)).toBe(false);
      expect(needsAuthorityChoice(true)).toBe(false);
    });

    it('ignores a remembered customer identity authority, which is what makes rollback work', () => {
      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');

      expect(readSelectedAuthority()).toBe('workforce');
      expect(hasSelectedAuthority()).toBe(false);
    });
  });

  describe('with a customer identity tenant configured', () => {
    beforeEach(() => configureExternalId());

    it('asks a signed-out visitor who has not chosen yet', () => {
      expect(needsAuthorityChoice(false)).toBe(true);
    });

    it('does not ask somebody who is already signed in', () => {
      expect(needsAuthorityChoice(true)).toBe(false);
    });

    it('does not ask again once a choice is on record, and goes back to that authority', () => {
      rememberSelectedAuthority('externalId');

      expect(hasSelectedAuthority()).toBe(true);
      expect(readSelectedAuthority()).toBe('externalId');
      expect(needsAuthorityChoice(false)).toBe(false);
    });

    it('remembers a workforce choice just as deliberately as a customer identity one', () => {
      rememberSelectedAuthority('workforce');

      expect(hasSelectedAuthority()).toBe(true);
      expect(readSelectedAuthority()).toBe('workforce');
      expect(needsAuthorityChoice(false)).toBe(false);
    });

    it('asks again after the choice is forgotten, which is what sign-out does', () => {
      rememberSelectedAuthority('externalId');
      forgetSelectedAuthority();

      expect(hasSelectedAuthority()).toBe(false);
      expect(readSelectedAuthority()).toBe('workforce');
      expect(needsAuthorityChoice(false)).toBe(true);
    });

    it('treats a value nobody wrote as no choice at all', () => {
      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'google');

      expect(hasSelectedAuthority()).toBe(false);
      expect(readSelectedAuthority()).toBe('workforce');
    });

    it('treats a half-filled configuration as no second authority, exactly as the API does', () => {
      configureExternalId({ apiClientId: '   ' });
      rememberSelectedAuthority('externalId');

      expect(readSelectedAuthority()).toBe('workforce');
      expect(needsAuthorityChoice(false)).toBe(false);
    });

    /**
     * The shared record belongs to the browser; what a tab builds its MSAL instance, guard request
     * and interceptor scopes from is this tab's snapshot of it. The three are built by separate
     * factories at separate moments - the interceptor's not until the first HTTP request - so a
     * second tab writing a different choice in between must not reach any of them.
     */
    describe('the per-tab snapshot (issue #711)', () => {
      it('pins the first answer, and ignores another tab changing the shared choice afterwards', () => {
        expect(readSelectedAuthority()).toBe('workforce');
        expect(sessionStorage.getItem(TAB_AUTHORITY_KEY)).toBe('workforce');

        // Another tab of this origin chooses the customer identity authority.
        localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');

        expect(readSelectedAuthority()).toBe('workforce');
      });

      /**
       * Including while a redirect this tab started is in flight: `sessionStorage` survives the
       * round trip, so the callback bootstraps the authority that began the journey.
       */
      it('keeps this tab on the authority its own redirect started, across the callback', () => {
        rememberSelectedAuthority('externalId');
        expect(sessionStorage.getItem(TAB_AUTHORITY_KEY)).toBe('externalId');

        localStorage.setItem(SELECTED_AUTHORITY_KEY, 'workforce');

        expect(readSelectedAuthority()).toBe('externalId');
      });

      it('takes its snapshot from the shared choice when a new tab opens with one on record', () => {
        localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');

        expect(readSelectedAuthority()).toBe('externalId');
        expect(sessionStorage.getItem(TAB_AUTHORITY_KEY)).toBe('externalId');
      });

      it('drops a pin for an authority the configuration no longer has, which is the rollback lever', () => {
        rememberSelectedAuthority('externalId');
        unconfigureExternalId();

        expect(readSelectedAuthority()).toBe('workforce');
        expect(sessionStorage.getItem(TAB_AUTHORITY_KEY)).toBe('workforce');
      });

      it('clears the pin with the choice on sign-out', () => {
        rememberSelectedAuthority('externalId');
        forgetSelectedAuthority();

        expect(sessionStorage.getItem(TAB_AUTHORITY_KEY)).toBeNull();
      });
    });
  });
});
