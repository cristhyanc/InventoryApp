import * as fs from 'fs';
import * as path from 'path';
import { LEGAL_EFFECTIVE_DATE } from '../public/legal/legal-effective-date';
import {
  parseBackendLegalVersion,
  parsePublishedEffectiveDate,
  toIsoDate
} from './legal-version-contract.guard';

/**
 * The cross-boundary check issue #1259's acceptance criteria ask for: "the published pages'
 * effective date matches it (test)".
 *
 * The version somebody agrees to is defined **once**, on the backend, in
 * `Inventory.Domain.Legal.LegalTerms.CurrentVersion`, and the frontend reads it at runtime from
 * `GET /api/me/account-state` rather than holding a copy. What this file guards is the one thing
 * that *is* duplicated by necessity: the effective date the published Terms of Use and Privacy
 * Policy pages show their readers. A republished document whose pages said one date while the
 * recorded acceptances named another would make every stored acceptance ambiguous, and nothing
 * else in either build would notice.
 *
 * It reads the real `LegalTerms.cs` so the comparison's "backend" side is the backend's own
 * constant, not a second hand-kept copy of it - the same approach
 * `role-capabilities-contract.guard.spec.ts` takes for the capability table (issue #677).
 */
describe('legal version contract between the published pages and the backend authority (issue #1259)', () => {
  describe('parsing', () => {
    it('reads the version out of a LegalTerms constant', () => {
      expect(
        parseBackendLegalVersion('    public const string CurrentVersion = "2026-11-13";')
      ).toBe('2026-11-13');
    });

    it('reads the effective date out of the published constant', () => {
      expect(parsePublishedEffectiveDate("export const LEGAL_EFFECTIVE_DATE = '13 November 2026';")).toBe(
        '13 November 2026'
      );
    });

    it('fails loudly rather than silently passing when the backend constant is not found', () => {
      expect(() => parseBackendLegalVersion('// LegalTerms rewritten with no CurrentVersion')).toThrow();
    });

    it('fails loudly rather than silently passing when the published constant is not found', () => {
      expect(() => parsePublishedEffectiveDate('// no effective date here')).toThrow();
    });

    it.each([
      ['13 November 2026', '2026-11-13'],
      ['1 January 2027', '2027-01-01'],
      ['09 September 2026', '2026-09-09']
    ])('converts %s to %s', (published, iso) => {
      expect(toIsoDate(published)).toBe(iso);
    });

    it.each(['13 Nov 2026', '2026-11-13', 'November 13 2026', '13 Brumaire 2026', ''])(
      'refuses to guess at %s rather than misreading it',
      (published) => {
        expect(() => toIsoDate(published)).toThrow();
      }
    );
  });

  describe('the actual backend authority, read from Inventory.Domain.Legal', () => {
    const repoRoot = path.resolve(__dirname, '../../../../..');
    const legalTermsSource = fs.readFileSync(
      path.resolve(repoRoot, 'backend/Inventory.Domain/Legal/LegalTerms.cs'),
      'utf8'
    );
    const backendVersion = parseBackendLegalVersion(legalTermsSource);

    it('publishes the effective date the Terms of Use and Privacy Policy pages show', () => {
      expect(toIsoDate(LEGAL_EFFECTIVE_DATE)).toBe(backendVersion);
    });

    it('states that version as an ISO-8601 calendar date, which is what a request carries', () => {
      expect(backendVersion).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    });
  });
});
