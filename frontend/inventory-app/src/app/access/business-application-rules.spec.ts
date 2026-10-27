import {
  ABN_INVALID_MESSAGE,
  AUSTRALIAN_STATES,
  BusinessApplicationDraft,
  CONTACT_EMAIL_INVALID_MESSAGE,
  GST_REGISTRATION_REQUIRED_MESSAGE,
  NAME_REQUIRED_MESSAGE,
  STATE_REQUIRED_MESSAGE,
  TIME_ZONE_REQUIRED_MESSAGE,
  TIME_ZONE_UNKNOWN_MESSAGE,
  availableTimeZones,
  isKnownTimeZone,
  isValidContactEmail,
  normalizeAbn,
  toBusinessApplicationRequest,
  validateBusinessApplication
} from './business-application-rules';

function draft(overrides: Partial<BusinessApplicationDraft> = {}): BusinessApplicationDraft {
  return {
    name: 'Corner Store',
    contactEmail: 'owner@example.com',
    abn: '',
    gstRegistered: true,
    timeZoneId: 'Australia/Sydney',
    state: 'NSW',
    ...overrides
  };
}

/**
 * The business application form's inline rules (issue #543), which mirror the API's
 * `BusinessSettingsPolicy`, `AustralianBusinessNumber` and `BusinessContactEmail`.
 */
describe('business application rules (issue #543)', () => {
  describe('normalizeAbn', () => {
    it('accepts a valid ABN with or without spaces and returns its 11 digits', () => {
      expect(normalizeAbn('51 824 753 556')).toBe('51824753556');
      expect(normalizeAbn('53004085616')).toBe('53004085616');
    });

    it('refuses a wrong check digit, the wrong length and non-digits', () => {
      expect(normalizeAbn('51 824 753 557')).toBeNull();
      expect(normalizeAbn('5182475355')).toBeNull();
      expect(normalizeAbn('518247535561')).toBeNull();
      expect(normalizeAbn('5182475355a')).toBeNull();
      expect(normalizeAbn('51-824-753-556')).toBeNull();
      expect(normalizeAbn('   ')).toBeNull();
    });
  });

  describe('isValidContactEmail', () => {
    it.each(['owner@example.com', ' owner@example.com ', 'first.last+tag@mail.example.com.au'])(
      'accepts %p',
      (address) => expect(isValidContactEmail(address)).toBe(true)
    );

    it.each([
      '',
      'owner',
      'owner@localhost',
      'owner@example.c',
      'owner@example.123',
      'a@b@example.com',
      'own er@example.com',
      '.owner@example.com',
      'owner@-example.com',
      'owner@example-.com',
      `${'a'.repeat(250)}@example.com`
    ])('refuses %p', (address) => expect(isValidContactEmail(address)).toBe(false));
  });

  describe('time zones', () => {
    it('knows a real IANA id and refuses a made-up one', () => {
      expect(isKnownTimeZone('Australia/Sydney')).toBe(true);
      expect(isKnownTimeZone('Australia/Atlantis')).toBe(false);
    });

    it('lists the zones the browser knows', () => {
      expect(availableTimeZones()).toContain('Australia/Sydney');
    });
  });

  describe('validateBusinessApplication', () => {
    it('finds nothing wrong with a complete draft, with or without an ABN', () => {
      expect(validateBusinessApplication(draft())).toEqual({});
      expect(validateBusinessApplication(draft({ abn: '51 824 753 556' }))).toEqual({});
    });

    it("reports each broken rule with the API's own sentence", () => {
      const errors = validateBusinessApplication({
        name: '   ',
        contactEmail: 'not-an-address',
        abn: '12345678901',
        gstRegistered: null,
        timeZoneId: '',
        state: ''
      });

      expect(errors).toEqual({
        name: NAME_REQUIRED_MESSAGE,
        contactEmail: CONTACT_EMAIL_INVALID_MESSAGE,
        abn: ABN_INVALID_MESSAGE,
        gstRegistered: GST_REGISTRATION_REQUIRED_MESSAGE,
        timeZoneId: TIME_ZONE_REQUIRED_MESSAGE,
        state: STATE_REQUIRED_MESSAGE
      });
    });

    it.each(AUSTRALIAN_STATES)('accepts %p as a state or territory', (state) => {
      expect(validateBusinessApplication(draft({ state })).state).toBeUndefined();
    });

    it.each(['', '   ', 'New South Wales', 'NSWW', 'NZ'])('refuses %p as a state or territory', (state) => {
      expect(validateBusinessApplication(draft({ state })).state).toBe(STATE_REQUIRED_MESSAGE);
    });

    it('accepts a state typed in lower case or with spaces, as the API normalizes it', () => {
      expect(validateBusinessApplication(draft({ state: ' qld ' })).state).toBeUndefined();
    });

    it('refuses a name longer than 200 characters once trimmed', () => {
      expect(validateBusinessApplication(draft({ name: `  ${'a'.repeat(200)}  ` })).name).toBeUndefined();
      expect(validateBusinessApplication(draft({ name: 'a'.repeat(201) })).name).toBe(NAME_REQUIRED_MESSAGE);
    });

    it('treats GST not registered as an answer, not a missing one', () => {
      expect(validateBusinessApplication(draft({ gstRegistered: false })).gstRegistered).toBeUndefined();
    });

    it('refuses a time zone the browser does not know', () => {
      expect(validateBusinessApplication(draft({ timeZoneId: 'Australia/Atlantis' })).timeZoneId).toBe(
        TIME_ZONE_UNKNOWN_MESSAGE
      );
    });
  });

  describe('toBusinessApplicationRequest', () => {
    it('trims the text, sends the ABN as its digits and a blank ABN as null', () => {
      expect(
        toBusinessApplicationRequest(
          draft({
            name: ' Corner Store ',
            contactEmail: ' owner@example.com ',
            abn: '51 824 753 556',
            gstRegistered: false,
            state: ' nsw '
          })
        )
      ).toEqual({
        name: 'Corner Store',
        contactEmail: 'owner@example.com',
        abn: '51824753556',
        gstRegistered: false,
        timeZoneId: 'Australia/Sydney',
        state: 'NSW'
      });

      expect(toBusinessApplicationRequest(draft({ abn: '   ' })).abn).toBeNull();
    });

    it('never sends a request without a stated GST registration', () => {
      expect(() => toBusinessApplicationRequest(draft({ gstRegistered: null }))).toThrow();
    });
  });
});
