import * as fs from 'fs';
import * as path from 'path';
import {
  SUPPLIER_SEARCH_WEEKDAYS,
  SupplierSearchFrequency
} from '../../../services/super-admin-supplier-presets.service';
import {
  SUPPLIER_SEARCH_FRESHNESS_MARGIN_HOURS,
  SupplierSearchSchedule,
  describeSearchSchedule,
  formatSearchDateOnly,
  isSearchDateOnly,
  longestSearchGapDays,
  orderedSearchWeekdays,
  searchFreshnessHours
} from './supplier-search-schedule';

/**
 * The shared test-case file issue #1109 asks for: the Super Admin page derives the freshness limit
 * the same way `Inventory.Domain.PriceCatalogue.SupplierSearchSchedulePolicy` does, so the two
 * cannot be made to disagree by anybody changing one of them. The cases live in
 * `docs/test-vectors/supplier-search-schedule.json` and `SupplierSearchSchedulePolicyTests` reads
 * the same file; a case belongs there rather than in either suite.
 */
interface ScheduleVectorCase {
  readonly name: string;
  readonly frequency: string;
  readonly daysOfWeek: readonly string[];
  readonly fortnightStartDate: string | null;
  readonly longestGapDays: number;
  readonly freshnessHours: number;
}

interface ScheduleVectorFile {
  readonly marginHours: number;
  readonly frequencies: readonly string[];
  readonly daysOfWeek: readonly string[];
  readonly cases: readonly ScheduleVectorCase[];
}

const repoRoot = path.resolve(__dirname, '../../../../../../..');
const vectors = JSON.parse(
  fs.readFileSync(path.resolve(repoRoot, 'docs/test-vectors/supplier-search-schedule.json'), 'utf8')
) as ScheduleVectorFile;

/** The declared frequency the file names, as this client's own number for it. */
function frequencyNamed(name: string): SupplierSearchFrequency {
  const match = Object.entries(SupplierSearchFrequency).find(([member]) => member === name);
  expect(match).toBeDefined();
  return match![1] as SupplierSearchFrequency;
}

function scheduleOf(testCase: ScheduleVectorCase): SupplierSearchSchedule {
  return {
    frequency: frequencyNamed(testCase.frequency),
    daysOfWeek: testCase.daysOfWeek,
    fortnightStartDate: testCase.fortnightStartDate
  };
}

describe('the supplier search schedule the Super Admin page reads and writes (issue #1109)', () => {
  describe('the shared test cases of issue #1108', () => {
    it('reads cases out of the shared file, so an empty or moved file fails rather than passing', () => {
      expect(vectors.cases.length).toBeGreaterThan(0);
    });

    /**
     * The vocabularies are asserted against the same file, because they are half of the agreement:
     * a renumbered frequency or a renamed weekday code would make every stored schedule read as a
     * different one, and the derivation below would keep agreeing while the meaning drifted.
     */
    it('mirrors the frequencies and weekday codes the shared file declares', () => {
      // The declared member names only: a numeric TypeScript enum also carries the reverse mapping,
      // which is a fact about the language rather than about the contract.
      const declared = Object.keys(SupplierSearchFrequency).filter((key) => Number.isNaN(Number(key)));

      expect(declared).toEqual([...vectors.frequencies]);
      expect([...SUPPLIER_SEARCH_WEEKDAYS]).toEqual([...vectors.daysOfWeek]);
      expect(SUPPLIER_SEARCH_FRESHNESS_MARGIN_HOURS).toBe(vectors.marginHours);
    });

    it.each(vectors.cases.map((testCase) => [testCase.name, testCase] as const))(
      'derives the shared case: %s',
      (_name, testCase) => {
        const schedule = scheduleOf(testCase);

        expect(longestSearchGapDays(schedule)).toBe(testCase.longestGapDays);
        expect(searchFreshnessHours(schedule)).toBe(testCase.freshnessHours);
      }
    );
  });

  describe('a schedule that is not yet derivable', () => {
    // A weekly or fortnightly schedule with no day chosen has no gap between occurrences to measure,
    // which is the state the dialog is in while somebody is still choosing. It reports "no answer
    // yet" rather than a number that would be wrong.
    it.each([SupplierSearchFrequency.Weekly, SupplierSearchFrequency.Fortnightly])(
      'has no freshness limit while no weekday is chosen (%s)',
      (frequency) => {
        const schedule: SupplierSearchSchedule = {
          frequency,
          daysOfWeek: [],
          fortnightStartDate: frequency === SupplierSearchFrequency.Fortnightly ? '2026-11-09' : null
        };

        expect(longestSearchGapDays(schedule)).toBeNull();
        expect(searchFreshnessHours(schedule)).toBeNull();
      }
    );

    it('has no freshness limit for a frequency this client does not know', () => {
      const schedule = { frequency: 99 as SupplierSearchFrequency, daysOfWeek: [], fortnightStartDate: null };

      expect(searchFreshnessHours(schedule)).toBeNull();
    });

    /**
     * A fortnightly schedule's start date says which weeks are on, never how far apart two
     * occurrences are - exactly as the backend's own `LongestGapDays` ignores it - so the preview is
     * derivable before a date is chosen.
     */
    it('derives the limit of a fortnightly schedule before its start date is chosen', () => {
      const schedule: SupplierSearchSchedule = {
        frequency: SupplierSearchFrequency.Fortnightly,
        daysOfWeek: ['THU'],
        fortnightStartDate: null
      };

      expect(searchFreshnessHours(schedule)).toBe(342);
    });
  });

  describe('the weekday order', () => {
    it('returns the chosen days in Monday-to-Sunday order whatever order they arrived in', () => {
      expect(orderedSearchWeekdays(['SUN', 'WED', 'MON'])).toEqual(['MON', 'WED', 'SUN']);
    });

    it('drops a repeat and a code that is not one of the seven', () => {
      expect(orderedSearchWeekdays(['THU', 'THU', 'FUNDAY'])).toEqual(['THU']);
    });

    it('derives the same limit however the days were ordered', () => {
      const monday: SupplierSearchSchedule = {
        frequency: SupplierSearchFrequency.Weekly,
        daysOfWeek: ['SAT', 'WED'],
        fortnightStartDate: null
      };

      expect(searchFreshnessHours(monday)).toBe(102);
    });
  });

  describe('what the page shows', () => {
    it('describes a daily schedule in one word', () => {
      expect(
        describeSearchSchedule({
          frequency: SupplierSearchFrequency.Daily,
          daysOfWeek: [],
          fortnightStartDate: null
        })
      ).toBe('Daily');
    });

    it('describes a weekly schedule with its days in Monday-to-Sunday order', () => {
      expect(
        describeSearchSchedule({
          frequency: SupplierSearchFrequency.Weekly,
          daysOfWeek: ['SAT', 'WED'],
          fortnightStartDate: null
        })
      ).toBe('Weekly: Wed, Sat');
    });

    it('describes a fortnightly schedule with the week its start date is in', () => {
      expect(
        describeSearchSchedule({
          frequency: SupplierSearchFrequency.Fortnightly,
          daysOfWeek: ['THU'],
          fortnightStartDate: '2026-11-09'
        })
      ).toBe('Fortnightly: Thu (on weeks from 9 Nov 2026)');
    });

    it('describes a stored schedule with no day as the gap it is rather than inventing one', () => {
      expect(
        describeSearchSchedule({
          frequency: SupplierSearchFrequency.Weekly,
          daysOfWeek: [],
          fortnightStartDate: null
        })
      ).toBe('Weekly: no days set');
    });

    it('describes a frequency this client does not know as not set', () => {
      expect(
        describeSearchSchedule({
          frequency: 99 as SupplierSearchFrequency,
          daysOfWeek: [],
          fortnightStartDate: null
        })
      ).toBe('Not set');
    });
  });

  /**
   * The date is read and written as the characters the API sent, never through a `Date`: a Super
   * Admin in another time zone must not be able to shift the on week by a day. These are the pure
   * string rules; `supplier-preset-dialog.timezone.spec.ts` proves the whole round trip under a
   * mocked zone in which a `Date`-based conversion does shift it.
   */
  describe('the date-only start date', () => {
    it('formats the characters it was given, with no Date and no zone in between', () => {
      expect(formatSearchDateOnly('2026-11-09')).toBe('9 Nov 2026');
      expect(formatSearchDateOnly('2027-01-01')).toBe('1 Jan 2027');
      expect(formatSearchDateOnly('2026-12-31')).toBe('31 Dec 2026');
    });

    it('reports a value that is not a plain YYYY-MM-DD date as no date at all', () => {
      expect(formatSearchDateOnly(null)).toBeNull();
      expect(formatSearchDateOnly('')).toBeNull();
      expect(formatSearchDateOnly('9/11/2026')).toBeNull();
      expect(formatSearchDateOnly('2026-11-09T00:00:00Z')).toBeNull();
      expect(formatSearchDateOnly('2026-13-09')).toBeNull();

      expect(isSearchDateOnly('2026-11-09')).toBe(true);
      expect(isSearchDateOnly('2026-11-09T00:00:00Z')).toBe(false);
      expect(isSearchDateOnly(null)).toBe(false);
    });
  });
});
