/**
 * The InventoryApp business timezone used for operator-facing date/time display and local calendar
 * inputs. `Australia/Canberra` is an IANA identifier, so `Intl.DateTimeFormat` resolves AEST/AEDT
 * from the platform timezone database instead of a fixed UTC offset.
 */
export const BUSINESS_TIME_ZONE = 'Australia/Canberra';

/** The `timeZone`'s offset from UTC, in minutes, at the given instant. */
function timeZoneOffsetMinutes(instant: Date, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).formatToParts(instant);

  const value = (type: string) => Number(parts.find((part) => part.type === type)?.value);
  const asIfUtc = Date.UTC(
    value('year'), value('month') - 1, value('day'), value('hour'), value('minute'), value('second')
  );
  return (asIfUtc - instant.getTime()) / 60_000;
}

/**
 * The UTC instant that corresponds to the given `year`/`month`(1-12)/`day`/`hour`/`minute`/`second`
 * wall-clock time in `timeZone`, resolved from the IANA timezone database so AEST/AEDT
 * daylight-saving transitions are applied automatically rather than a fixed UTC offset (issue
 * #218, generalised to hour/minute/second for issue #361).
 */
export function zonedDateTimeToUtc(
  year: number,
  month: number,
  day: number,
  hour: number,
  minute: number,
  second: number,
  timeZone: string
): Date {
  const utcGuessMillis = Date.UTC(year, month - 1, day, hour, minute, second);
  const offsetMinutes = timeZoneOffsetMinutes(new Date(utcGuessMillis), timeZone);
  const candidateMillis = utcGuessMillis - offsetMinutes * 60_000;

  // A DST transition can change the offset between the initial UTC guess and the candidate
  // instant it produced; re-resolving the offset at the candidate corrects that case.
  const refinedOffsetMinutes = timeZoneOffsetMinutes(new Date(candidateMillis), timeZone);
  return refinedOffsetMinutes === offsetMinutes
    ? new Date(candidateMillis)
    : new Date(utcGuessMillis - refinedOffsetMinutes * 60_000);
}

/**
 * The UTC instant of midnight/start-of-day for the given `year`/`month`(1-12)/`day` calendar date
 * in `timeZone`. A thin specialisation of {@link zonedDateTimeToUtc} at midnight (issue #218).
 */
export function startOfDayUtc(year: number, month: number, day: number, timeZone: string): Date {
  return zonedDateTimeToUtc(year, month, day, 0, 0, 0, timeZone);
}

/**
 * Formats `instant` as the `timeZone` wall-clock date and time in the `yyyy-MM-ddTHH:mm` shape an
 * HTML `datetime-local` input's value expects (issue #361), so a form can default to, and display,
 * the business timezone's current date/time rather than the browser's own local timezone.
 */
export function formatForDateTimeLocalInput(instant: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).formatToParts(instant);

  const value = (type: string) => parts.find((part) => part.type === type)?.value ?? '';
  return `${value('year')}-${value('month')}-${value('day')}T${value('hour')}:${value('minute')}`;
}

/** A calendar date, independent of any instant or timezone once resolved. */
export interface CalendarDate {
  year: number;
  /** 1-12. */
  month: number;
  day: number;
}

/** The `timeZone`'s current calendar date at the given `instant`, per the IANA timezone database. */
export function currentDateInTimeZone(instant: Date, timeZone: string): CalendarDate {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit'
  }).formatToParts(instant);

  const value = (type: string) => Number(parts.find((part) => part.type === type)?.value);
  return { year: value('year'), month: value('month'), day: value('day') };
}

/** `date` shifted by `deltaDays` calendar days, independent of any timezone. */
export function shiftCalendarDate(date: CalendarDate, deltaDays: number): CalendarDate {
  const utc = new Date(Date.UTC(date.year, date.month - 1, date.day));
  utc.setUTCDate(utc.getUTCDate() + deltaDays);
  return { year: utc.getUTCFullYear(), month: utc.getUTCMonth() + 1, day: utc.getUTCDate() };
}
