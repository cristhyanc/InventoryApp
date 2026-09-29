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
 * The UTC instant of midnight/start-of-day for the given `year`/`month`(1-12)/`day` calendar date
 * in `timeZone`, resolved from the IANA timezone database so AEST/AEDT daylight-saving transitions
 * are applied automatically rather than a fixed UTC offset (issue #218).
 */
export function startOfDayUtc(year: number, month: number, day: number, timeZone: string): Date {
  const utcMidnightMillis = Date.UTC(year, month - 1, day);
  const offsetMinutes = timeZoneOffsetMinutes(new Date(utcMidnightMillis), timeZone);
  const candidateMillis = utcMidnightMillis - offsetMinutes * 60_000;

  // A DST transition can change the offset between the initial UTC-midnight guess and the
  // candidate instant it produced; re-resolving the offset at the candidate corrects that case.
  const refinedOffsetMinutes = timeZoneOffsetMinutes(new Date(candidateMillis), timeZone);
  return refinedOffsetMinutes === offsetMinutes
    ? new Date(candidateMillis)
    : new Date(utcMidnightMillis - refinedOffsetMinutes * 60_000);
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
