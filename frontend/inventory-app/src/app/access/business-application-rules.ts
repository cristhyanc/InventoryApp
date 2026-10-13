/**
 * The business application form's inline validation (issue #543), mirroring the API's own rules
 * for `POST /api/onboarding/business-applications` so a person sees the same refusal before they
 * submit that the API would give after: `Inventory.Domain.Tenancy.BusinessSettingsPolicy`,
 * `AustralianBusinessNumber` and `BusinessContactEmail`.
 *
 * **The messages are the API's own sentences**, copied rather than reworded, so the form and the
 * `400` it guards against never describe one rule two ways. The API stays authoritative: anything
 * this file lets through is re-checked there, and a `400` it returns is shown as it is written.
 *
 * The one rule that is not a copy is the time zone's. The API requires a non-blank IANA id and
 * decides whether the host can resolve it on the way into the database; the form asks the browser
 * instead, through `Intl`, so a typo is caught here while anything the browser knows is left for
 * the API to accept or refuse.
 */

/** `BusinessSettingsPolicy.MaxNameLength`. */
export const MAX_BUSINESS_NAME_LENGTH = 200;

/** `BusinessContactEmail.MaxLength`. */
const MAX_CONTACT_EMAIL_LENGTH = 254;

export const NAME_REQUIRED_MESSAGE = 'A business name is required, and must be no longer than 200 characters.';
export const CONTACT_EMAIL_INVALID_MESSAGE =
  'A contact email address must be a single address of the form name@example.com.';
export const ABN_INVALID_MESSAGE = 'An ABN must be 11 digits and pass the ABN check-digit test.';
export const GST_REGISTRATION_REQUIRED_MESSAGE =
  'State whether the business is registered for GST. It is never inferred from an ABN.';
export const TIME_ZONE_REQUIRED_MESSAGE =
  "A business time zone is required, as an IANA identifier such as 'Australia/Sydney'.";
export const TIME_ZONE_UNKNOWN_MESSAGE = 'Choose a time zone from the list, such as Australia/Sydney.';

/** What the form holds while a person fills it in. */
export interface BusinessApplicationDraft {
  name: string;
  contactEmail: string;
  abn: string;
  gstRegistered: boolean | null;
  timeZoneId: string;
}

/** The body `POST /api/onboarding/business-applications` accepts (`BusinessApplicationRequest`). */
export interface BusinessApplicationRequest {
  readonly name: string;
  readonly contactEmail: string;
  readonly abn: string | null;
  readonly gstRegistered: boolean;
  readonly timeZoneId: string;
}

export type BusinessApplicationField = keyof BusinessApplicationDraft;

/** One message per field that breaks a rule; a valid draft has none. */
export type BusinessApplicationErrors = Partial<Record<BusinessApplicationField, string>>;

const ABN_DIGIT_COUNT = 11;
const ABN_WEIGHTS = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];
const MAILBOX_SYMBOLS = "!#$%&'*+-/=?^_`{|}~";

/**
 * The ABN's canonical 11 digits, or `null` when it is not a well-formed ABN: the same answer as
 * `AustralianBusinessNumber.TryNormalize`, spaces allowed and removed, the check digit enforced.
 */
export function normalizeAbn(submitted: string): string | null {
  if (submitted.trim().length === 0) {
    return null;
  }

  const digits = submitted.split(' ').join('');
  if (digits.length !== ABN_DIGIT_COUNT || !/^[0-9]+$/.test(digits)) {
    return null;
  }

  let weighted = 0;
  for (let position = 0; position < ABN_DIGIT_COUNT; position++) {
    // 1 is subtracted from the leading digit before weighting, as the ABN check-digit test does.
    const digit = Number(digits[position]) - (position === 0 ? 1 : 0);
    weighted += digit * ABN_WEIGHTS[position];
  }

  return weighted % 89 === 0 ? digits : null;
}

function isAsciiLetterOrDigit(character: string): boolean {
  return /^[A-Za-z0-9]$/.test(character);
}

function isMailbox(local: string): boolean {
  return (
    local.length > 0 &&
    local
      .split('.')
      .every(
        (part) =>
          part.length > 0 &&
          [...part].every((character) => isAsciiLetterOrDigit(character) || MAILBOX_SYMBOLS.includes(character))
      )
  );
}

function isHostName(domain: string): boolean {
  const labels = domain.split('.');
  if (labels.length < 2) {
    return false;
  }

  const labelsAreValid = labels.every(
    (label) =>
      label.length > 0 &&
      !label.startsWith('-') &&
      !label.endsWith('-') &&
      [...label].every((character) => isAsciiLetterOrDigit(character) || character === '-')
  );

  const topLevel = labels[labels.length - 1];
  return labelsAreValid && topLevel.length >= 2 && /^[A-Za-z]+$/.test(topLevel);
}

/** Whether the address is one `BusinessContactEmail.TryNormalize` accepts, once trimmed. */
export function isValidContactEmail(submitted: string): boolean {
  const candidate = submitted.trim();
  if (candidate.length === 0 || candidate.length > MAX_CONTACT_EMAIL_LENGTH || /\s/.test(candidate)) {
    return false;
  }

  const parts = candidate.split('@');
  return parts.length === 2 && isMailbox(parts[0]) && isHostName(parts[1]);
}

/** Whether the browser's own time zone database knows this IANA id. */
export function isKnownTimeZone(timeZoneId: string): boolean {
  try {
    new Intl.DateTimeFormat('en-US', { timeZone: timeZoneId });
    return true;
  } catch {
    return false;
  }
}

/**
 * Every IANA time zone the browser knows, for the searchable list. A browser that cannot list them
 * offers its own zone alone; the field still accepts any id the browser can resolve.
 */
export function availableTimeZones(): string[] {
  const supportedValuesOf = (Intl as { supportedValuesOf?: (key: 'timeZone') => string[] }).supportedValuesOf;
  const zones = typeof supportedValuesOf === 'function' ? supportedValuesOf('timeZone') : [];
  const own = browserTimeZone();
  return own === null || zones.includes(own) ? zones : [own, ...zones];
}

/** The browser's own IANA time zone, which the form starts with, or `null` when it reports none. */
export function browserTimeZone(): string | null {
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  return typeof zone === 'string' && zone.length > 0 ? zone : null;
}

/** Every rule the draft breaks, keyed by field, in the order the API checks them. */
export function validateBusinessApplication(draft: BusinessApplicationDraft): BusinessApplicationErrors {
  const errors: BusinessApplicationErrors = {};
  const name = draft.name.trim();

  if (name.length === 0 || name.length > MAX_BUSINESS_NAME_LENGTH) {
    errors.name = NAME_REQUIRED_MESSAGE;
  }

  if (!isValidContactEmail(draft.contactEmail)) {
    errors.contactEmail = CONTACT_EMAIL_INVALID_MESSAGE;
  }

  if (draft.gstRegistered === null) {
    errors.gstRegistered = GST_REGISTRATION_REQUIRED_MESSAGE;
  }

  // An empty field is "no ABN", which is the API's null; anything typed must be a valid one.
  if (draft.abn.trim().length > 0 && normalizeAbn(draft.abn) === null) {
    errors.abn = ABN_INVALID_MESSAGE;
  }

  const timeZoneId = draft.timeZoneId.trim();
  if (timeZoneId.length === 0) {
    errors.timeZoneId = TIME_ZONE_REQUIRED_MESSAGE;
  } else if (!isKnownTimeZone(timeZoneId)) {
    errors.timeZoneId = TIME_ZONE_UNKNOWN_MESSAGE;
  }

  return errors;
}

/**
 * The request body for a draft {@link validateBusinessApplication} found nothing wrong with. The
 * name and address are trimmed and the ABN sent as its digits; a blank ABN is sent as `null`,
 * the API's one representation of "not recorded", never as an empty string it would refuse.
 */
export function toBusinessApplicationRequest(draft: BusinessApplicationDraft): BusinessApplicationRequest {
  if (draft.gstRegistered === null) {
    throw new Error('A business application cannot be sent without a stated GST registration.');
  }

  return {
    name: draft.name.trim(),
    contactEmail: draft.contactEmail.trim(),
    abn: draft.abn.trim().length === 0 ? null : normalizeAbn(draft.abn),
    gstRegistered: draft.gstRegistered,
    timeZoneId: draft.timeZoneId.trim()
  };
}
