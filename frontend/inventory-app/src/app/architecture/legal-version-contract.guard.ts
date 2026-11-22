const CURRENT_VERSION_PATTERN = /public\s+const\s+string\s+CurrentVersion\s*=\s*"([^"]+)"/;

const EFFECTIVE_DATE_PATTERN = /LEGAL_EFFECTIVE_DATE\s*=\s*'([^']+)'/;

const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December'
];

/**
 * The version the backend publishes, read out of `Inventory.Domain.Legal.LegalTerms`'s own source
 * rather than retyped, so a republished version is picked up here instead of silently falling out
 * of the comparison.
 *
 * Throws if the constant is not found at all, rather than returning nothing: a `LegalTerms`
 * rewritten in a shape this pattern no longer recognises must fail loudly, not be read as "there is
 * no version" and pass vacuously.
 */
export function parseBackendLegalVersion(legalTermsSource: string): string {
  const match = CURRENT_VERSION_PATTERN.exec(legalTermsSource);
  if (match === null) {
    throw new Error('Found no "public const string CurrentVersion = \\"...\\"" in LegalTerms.cs.');
  }

  return match[1];
}

/** The same for the effective date the published pages show, read out of `legal-effective-date.ts`. */
export function parsePublishedEffectiveDate(effectiveDateSource: string): string {
  const match = EFFECTIVE_DATE_PATTERN.exec(effectiveDateSource);
  if (match === null) {
    throw new Error("Found no \"LEGAL_EFFECTIVE_DATE = '...'\" in legal-effective-date.ts.");
  }

  return match[1];
}

/**
 * The ISO-8601 calendar date a published effective date such as `13 November 2026` names, which is
 * the form the backend's version constant takes.
 *
 * Deliberately not `new Date(...)`: parsing a human date string with the platform parser is
 * implementation-defined, and a value this comparison silently misread would make the whole check
 * meaningless. Anything that is not exactly "<day> <Month> <year>" with a month this list names
 * throws instead.
 */
export function toIsoDate(publishedEffectiveDate: string): string {
  const parts = publishedEffectiveDate.trim().split(/\s+/);
  if (parts.length !== 3) {
    throw new Error(`"${publishedEffectiveDate}" is not a "<day> <Month> <year>" effective date.`);
  }

  const [day, month, year] = parts;
  const monthIndex = MONTHS.indexOf(month);
  if (monthIndex < 0 || !/^\d{1,2}$/.test(day) || !/^\d{4}$/.test(year)) {
    throw new Error(`"${publishedEffectiveDate}" is not a "<day> <Month> <year>" effective date.`);
  }

  return `${year}-${String(monthIndex + 1).padStart(2, '0')}-${day.padStart(2, '0')}`;
}
