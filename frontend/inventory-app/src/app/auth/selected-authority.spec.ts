import { configureExternalId, unconfigureExternalId } from './external-id.spec-helpers';
import {
  SELECTED_AUTHORITY_KEY,
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
  });
});
