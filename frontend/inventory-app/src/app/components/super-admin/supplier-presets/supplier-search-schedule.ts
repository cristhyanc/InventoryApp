import {
  SUPPLIER_SEARCH_WEEKDAYS,
  SupplierSearchFrequency,
  SupplierSearchWeekday
} from '../../../services/super-admin-supplier-presets.service';

/**
 * One supplier's search schedule as the Super Admin page reads it: the three fields the API
 * publishes and the three a write submits, in the shapes they arrive in.
 */
export interface SupplierSearchSchedule {
  readonly frequency: SupplierSearchFrequency;
  readonly daysOfWeek: readonly string[];
  /** `YYYY-MM-DD`, or `null`. Never a `Date` - see {@link formatSearchDateOnly}. */
  readonly fortnightStartDate: string | null;
}

/**
 * The margin the derived freshness limit carries above the longest gap between two searches
 * (backend `SupplierPriceFreshnessPolicy.MarginHours`, issue #925).
 *
 * It is mirrored rather than fetched because the page previews a limit for a schedule nobody has
 * saved yet, and there is no endpoint that answers "what would this schedule derive". The agreement
 * is pinned by `supplier-search-schedule.spec.ts`, which reads the same
 * `docs/test-vectors/supplier-search-schedule.json` the backend's own
 * `SupplierSearchSchedulePolicyTests` reads, so neither side can be changed alone.
 */
export const SUPPLIER_SEARCH_FRESHNESS_MARGIN_HOURS = 6;

/** How many days one whole cycle of each frequency spans (backend `CycleDaysOf`). */
const CYCLE_DAYS: Readonly<Record<SupplierSearchFrequency, number>> = {
  [SupplierSearchFrequency.Daily]: 1,
  [SupplierSearchFrequency.Weekly]: 7,
  [SupplierSearchFrequency.Fortnightly]: 14
};

/** What each frequency is called on screen. */
export const SUPPLIER_SEARCH_FREQUENCY_LABELS: Readonly<Record<SupplierSearchFrequency, string>> = {
  [SupplierSearchFrequency.Daily]: 'Daily',
  [SupplierSearchFrequency.Weekly]: 'Weekly',
  [SupplierSearchFrequency.Fortnightly]: 'Fortnightly'
};

/** The weekday names the dialog's checkboxes carry, so a label is a word rather than a code. */
export const SUPPLIER_SEARCH_WEEKDAY_NAMES: Readonly<Record<SupplierSearchWeekday, string>> = {
  MON: 'Monday',
  TUE: 'Tuesday',
  WED: 'Wednesday',
  THU: 'Thursday',
  FRI: 'Friday',
  SAT: 'Saturday',
  SUN: 'Sunday'
};

/** The short weekday names the table's schedule text uses, where a row has one line to say it in. */
export const SUPPLIER_SEARCH_WEEKDAY_SHORT_NAMES: Readonly<Record<SupplierSearchWeekday, string>> = {
  MON: 'Mon',
  TUE: 'Tue',
  WED: 'Wed',
  THU: 'Thu',
  FRI: 'Fri',
  SAT: 'Sat',
  SUN: 'Sun'
};

/** The month names a date-only value is shown with, so no `Date` and no locale is involved. */
const MONTH_SHORT_NAMES = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec'
];

const DATE_ONLY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

/** What the page says about a supplier whose schedule it cannot read. */
const UNKNOWN_SCHEDULE_TEXT = 'Not set';

/**
 * The chosen weekdays in the server's own Monday-to-Sunday order, without repeats and without a
 * code that is not one of the seven (backend `SupplierSearchSchedulePolicy.Normalise`).
 *
 * The order is what makes one set of days have one spelling on the wire as well as on screen, so
 * both the dialog's submission and the table's text go through this.
 */
export function orderedSearchWeekdays(daysOfWeek: readonly string[] | null | undefined): SupplierSearchWeekday[] {
  const chosen = daysOfWeek ?? [];
  return SUPPLIER_SEARCH_WEEKDAYS.filter((weekday) => chosen.includes(weekday));
}

/**
 * The longest gap, in days, between two consecutive searches over one whole cycle - seven days for
 * a weekly schedule, fourteen for a fortnightly one, wrapping round to the next cycle's first
 * search - or `null` when the schedule does not yet say.
 *
 * It mirrors backend `SupplierSearchSchedulePolicy.LongestGapDays`: the *longest* gap rather than
 * the average, because a freshness limit has to cover the longest wait a price can legitimately
 * have had. `null` is the state a schedule being edited is in while no weekday is chosen - the
 * backend refuses that schedule outright, and the dialog refuses to submit it, so here it is "no
 * answer yet" rather than a number. A fortnightly schedule's start date is deliberately not read:
 * it decides which weeks are on, never how far apart two searches are.
 */
export function longestSearchGapDays(schedule: SupplierSearchSchedule): number | null {
  const cycleDays = CYCLE_DAYS[schedule.frequency];
  if (cycleDays === undefined) {
    return null;
  }

  if (schedule.frequency === SupplierSearchFrequency.Daily) {
    return 1;
  }

  const offsets = orderedSearchWeekdays(schedule.daysOfWeek).map((weekday) =>
    SUPPLIER_SEARCH_WEEKDAYS.indexOf(weekday)
  );
  if (offsets.length === 0) {
    return null;
  }

  let longest = 0;
  for (let index = 0; index < offsets.length; index++) {
    // The last search's gap wraps to the first search of the next cycle, which is what makes a
    // single chosen day come out as the whole cycle instead of zero.
    const next = index + 1 < offsets.length ? offsets[index + 1] : offsets[0] + cycleDays;
    longest = Math.max(longest, next - offsets[index]);
  }

  return longest;
}

/**
 * The freshness limit a supplier on this schedule carries, in hours: the longest gap in days times
 * 24, plus {@link SUPPLIER_SEARCH_FRESHNESS_MARGIN_HOURS} - backend
 * `SupplierSearchSchedulePolicy.FreshnessHoursFor`, mirrored so the dialog can preview it.
 *
 * `null` when the schedule does not yet derive one. The server stays the authority: it derives the
 * stored value itself and ignores anything a request says about it, so this is a preview of what it
 * will decide and never a value that is sent.
 */
export function searchFreshnessHours(schedule: SupplierSearchSchedule): number | null {
  const gapDays = longestSearchGapDays(schedule);
  return gapDays === null ? null : gapDays * 24 + SUPPLIER_SEARCH_FRESHNESS_MARGIN_HOURS;
}

/**
 * The schedule in a sentence a person reads: `Daily`, `Weekly: Wed, Sat`, or
 * `Fortnightly: Thu (on weeks from 9 Nov 2026)`.
 *
 * A weekly or fortnightly schedule with no day stored says so rather than reading as never, and a
 * frequency this client does not know reads as not set rather than as the first declared one - the
 * reason the enum starts at 1 on both sides.
 */
export function describeSearchSchedule(schedule: SupplierSearchSchedule): string {
  const label = SUPPLIER_SEARCH_FREQUENCY_LABELS[schedule.frequency];
  if (label === undefined) {
    return UNKNOWN_SCHEDULE_TEXT;
  }

  if (schedule.frequency === SupplierSearchFrequency.Daily) {
    return label;
  }

  const days = orderedSearchWeekdays(schedule.daysOfWeek);
  if (days.length === 0) {
    return `${label}: no days set`;
  }

  const named = `${label}: ${days.map((day) => SUPPLIER_SEARCH_WEEKDAY_SHORT_NAMES[day]).join(', ')}`;
  if (schedule.frequency !== SupplierSearchFrequency.Fortnightly) {
    return named;
  }

  const startDate = formatSearchDateOnly(schedule.fortnightStartDate);
  return startDate === null ? named : `${named} (on weeks from ${startDate})`;
}

/** Whether a value is a plain `YYYY-MM-DD` date naming a real month and day-of-month range. */
export function isSearchDateOnly(value: string | null | undefined): boolean {
  return parseSearchDateOnly(value) !== null;
}

/**
 * A date-only value as `9 Nov 2026`, or `null` when it is not a plain `YYYY-MM-DD` date.
 *
 * **No `Date` is constructed and no zone is consulted.** `new Date('2026-11-09')` is midnight UTC,
 * so reading its day in a zone behind UTC gives the 8th and writing one back from a zone ahead of
 * UTC gives the 10th - which would move a fortnightly supplier's on week by a week for a Super
 * Admin outside Sydney. The characters the API sent are the value, and this formats those
 * characters.
 */
export function formatSearchDateOnly(value: string | null | undefined): string | null {
  const parts = parseSearchDateOnly(value);
  if (parts === null) {
    return null;
  }

  const [year, month, day] = parts;
  return `${day} ${MONTH_SHORT_NAMES[month - 1]} ${year}`;
}

/** The year, month and day a `YYYY-MM-DD` value names, or `null` when it names none. */
function parseSearchDateOnly(value: string | null | undefined): [number, number, number] | null {
  const matched = DATE_ONLY_PATTERN.exec(value ?? '');
  if (matched === null) {
    return null;
  }

  const year = Number(matched[1]);
  const month = Number(matched[2]);
  const day = Number(matched[3]);

  // The month and the day are checked against the calendar's own limits rather than against a
  // `Date`, which would silently roll 31 February over into March and call it valid.
  return month >= 1 && month <= 12 && day >= 1 && day <= daysInMonth(year, month)
    ? [year, month, day]
    : null;
}

function daysInMonth(year: number, month: number): number {
  if (month === 2) {
    return isLeapYear(year) ? 29 : 28;
  }

  return month === 4 || month === 6 || month === 9 || month === 11 ? 30 : 31;
}

function isLeapYear(year: number): boolean {
  return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
}
