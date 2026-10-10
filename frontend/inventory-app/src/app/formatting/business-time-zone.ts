/**
 * Timezone-aware conversions for operator-facing date/time display and local calendar inputs.
 *
 * Every function here takes the IANA `timeZone` it works in rather than reading a constant: since
 * issue #499 the business timezone is a property of the signed-in operator's business, supplied by
 * `BusinessTimeZoneService` from `GET /api/business/current`. Because the ids are IANA ones,
 * `Intl.DateTimeFormat` resolves each zone's daylight-saving rules from the platform timezone
 * database instead of a fixed UTC offset.
 */

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
 * The UTC instant of the given `year`/`month`(1-12)/`day`/`hour`/`minute` wall-clock time in
 * `timeZone`, resolved from the IANA timezone database so that zone's daylight-saving transitions
 * are applied automatically rather than a fixed UTC offset (issue #218; generalised from
 * whole-day resolution to also carry a time-of-day component by issue #361's costing-repair
 * effective-time input). It always returns some instant: a time inside a daylight-saving gap is
 * moved forward and a repeated-hour time resolves to one of its two instants. Operator-entered
 * times must go through `resolveZonedDateTime` instead, which reports those cases.
 */
export function zonedDateTimeToUtc(
  year: number, month: number, day: number, hour: number, minute: number, timeZone: string
): Date {
  const utcGuessMillis = Date.UTC(year, month - 1, day, hour, minute);
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
 * How a wall-clock time in a timezone maps onto real instants:
 * - `valid`: exactly one instant shows that wall-clock time.
 * - `nonexistent`: no instant does, because the clocks skipped over it (the hour lost when
 *   daylight saving starts, e.g. Sydney 02:00-02:59 on the first Sunday of October).
 * - `ambiguous`: two instants do, because the clocks went back over it (the hour repeated when
 *   daylight saving ends, e.g. Sydney 02:00-02:59 on the first Sunday of April); `earlier` and
 *   `later` are the daylight-saving and standard-time instants respectively.
 */
export type ZonedDateTimeResolution =
  | { kind: 'valid'; utc: Date }
  | { kind: 'nonexistent' }
  | { kind: 'ambiguous'; earlier: Date; later: Date };

/**
 * Resolves a `year`/`month`(1-12)/`day`/`hour`/`minute` wall-clock time in `timeZone` without
 * silently normalising it (issue #361 review): unlike `zonedDateTimeToUtc`, a time inside a
 * daylight-saving gap is reported as `nonexistent` rather than shifted, and a time inside the
 * repeated hour is reported as `ambiguous` rather than picking one of its two instants, so an
 * operator-entered time is only ever used exactly as entered.
 */
export function resolveZonedDateTime(
  year: number, month: number, day: number, hour: number, minute: number, timeZone: string
): ZonedDateTimeResolution {
  const wallClockAsUtcMillis = Date.UTC(year, month - 1, day, hour, minute);
  const dayMillis = 24 * 60 * 60_000;
  // A transition shifts the offset at most once around a given day in any real timezone, so the
  // offsets a day either side are the only ones that can apply to this wall-clock time.
  const offsets = new Set([
    timeZoneOffsetMinutes(new Date(wallClockAsUtcMillis - dayMillis), timeZone),
    timeZoneOffsetMinutes(new Date(wallClockAsUtcMillis), timeZone),
    timeZoneOffsetMinutes(new Date(wallClockAsUtcMillis + dayMillis), timeZone)
  ]);

  const matches = [...offsets]
    .map(offset => wallClockAsUtcMillis - offset * 60_000)
    .filter(candidate => {
      const shown = currentDateTimeInTimeZone(new Date(candidate), timeZone);
      return shown.year === year && shown.month === month && shown.day === day &&
        shown.hour === hour && shown.minute === minute;
    })
    .sort((a, b) => a - b);

  if (matches.length === 0) return { kind: 'nonexistent' };
  if (matches.length === 1) return { kind: 'valid', utc: new Date(matches[0]) };
  return { kind: 'ambiguous', earlier: new Date(matches[0]), later: new Date(matches[matches.length - 1]) };
}

/**
 * The UTC instant of midnight/start-of-day for the given `year`/`month`(1-12)/`day` calendar date
 * in `timeZone` (issue #218). A whole-day instant is the same resolution with the time-of-day
 * component held at midnight.
 */
export function startOfDayUtc(year: number, month: number, day: number, timeZone: string): Date {
  return zonedDateTimeToUtc(year, month, day, 0, 0, timeZone);
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

/** A wall-clock date and time, independent of any instant or timezone once resolved. */
export interface CalendarDateTime extends CalendarDate {
  hour: number;
  minute: number;
}

/** The `timeZone`'s current wall-clock date and time at the given `instant`. */
export function currentDateTimeInTimeZone(instant: Date, timeZone: string): CalendarDateTime {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).formatToParts(instant);

  const value = (type: string) => Number(parts.find((part) => part.type === type)?.value);
  return { year: value('year'), month: value('month'), day: value('day'), hour: value('hour'), minute: value('minute') };
}

/**
 * Formats `dateTime` as an `<input type="datetime-local">` value (`yyyy-MM-ddTHH:mm`), with no
 * timezone designator - the caller supplies the wall-clock values already resolved in whichever
 * timezone the input represents (issue #361's costing-repair effective date/time, entered in the
 * business's own timezone).
 */
export function toDateTimeLocalValue(dateTime: CalendarDateTime): string {
  const pad = (value: number) => value.toString().padStart(2, '0');
  return `${dateTime.year}-${pad(dateTime.month)}-${pad(dateTime.day)}T${pad(dateTime.hour)}:${pad(dateTime.minute)}`;
}

/**
 * Parses an `<input type="datetime-local">` value (`yyyy-MM-ddTHH:mm`) into its wall-clock parts,
 * or `null` if it is empty or malformed.
 */
export function fromDateTimeLocalValue(value: string): CalendarDateTime | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value);
  if (!match) return null;
  return {
    year: Number(match[1]),
    month: Number(match[2]),
    day: Number(match[3]),
    hour: Number(match[4]),
    minute: Number(match[5])
  };
}
