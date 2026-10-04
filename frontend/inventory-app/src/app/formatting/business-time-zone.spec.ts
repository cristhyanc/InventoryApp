import {
  currentDateInTimeZone,
  currentDateTimeInTimeZone,
  fromDateTimeLocalValue,
  resolveZonedDateTime,
  shiftCalendarDate,
  startOfDayUtc,
  toDateTimeLocalValue,
  zonedDateTimeToUtc
} from './business-time-zone';

describe('startOfDayUtc', () => {
  it('resolves an Australia/Canberra midnight during AEST (UTC+10, Australian winter) to the correct UTC instant', () => {
    // 2026-06-15 is deep in the Australian winter, well outside any daylight-saving transition.
    expect(startOfDayUtc(2026, 6, 15, 'Australia/Canberra').toISOString()).toBe('2026-06-14T14:00:00.000Z');
  });

  it('resolves an Australia/Canberra midnight during AEDT (UTC+11, Australian summer) to the correct UTC instant', () => {
    // 2026-01-15 is deep in the Australian summer, inside daylight saving.
    expect(startOfDayUtc(2026, 1, 15, 'Australia/Canberra').toISOString()).toBe('2026-01-14T13:00:00.000Z');
  });

  it('resolves midnight on the day AEDT begins (first Sunday of October) using the still-AEST offset that applies at midnight', () => {
    // Clocks jump from 02:00 to 03:00 local time on 2026-10-04, so 00:00 that day is still AEST (+10).
    expect(startOfDayUtc(2026, 10, 4, 'Australia/Canberra').toISOString()).toBe('2026-10-03T14:00:00.000Z');
  });

  it('resolves midnight the day after AEDT begins using the new AEDT offset', () => {
    expect(startOfDayUtc(2026, 10, 5, 'Australia/Canberra').toISOString()).toBe('2026-10-04T13:00:00.000Z');
  });

  it('resolves midnight on the day AEDT ends (first Sunday of April) using the still-AEDT offset that applies at midnight', () => {
    // Clocks fall back from 03:00 to 02:00 local time on 2026-04-05, so 00:00 that day is still AEDT (+11).
    expect(startOfDayUtc(2026, 4, 5, 'Australia/Canberra').toISOString()).toBe('2026-04-04T13:00:00.000Z');
  });

  it('resolves midnight the day after AEDT ends using the new AEST offset', () => {
    expect(startOfDayUtc(2026, 4, 6, 'Australia/Canberra').toISOString()).toBe('2026-04-05T14:00:00.000Z');
  });

  it('lands on the previous UTC calendar day, proving the conversion is not a same-UTC-day shortcut', () => {
    const utcInstant = startOfDayUtc(2026, 6, 15, 'Australia/Canberra');
    expect(utcInstant.getUTCDate()).toBe(14);
    expect(utcInstant.getUTCMonth()).toBe(5); // June, 0-indexed.
  });

  it('never uses a fixed offset: AEST and AEDT midnights for consecutive days are exactly one hour further apart than 24 hours', () => {
    const beforeDst = startOfDayUtc(2026, 10, 3, 'Australia/Canberra').getTime();
    const onDstStartDay = startOfDayUtc(2026, 10, 4, 'Australia/Canberra').getTime();
    const afterDstStartDay = startOfDayUtc(2026, 10, 5, 'Australia/Canberra').getTime();

    expect(onDstStartDay - beforeDst).toBe(24 * 60 * 60 * 1000);
    expect(afterDstStartDay - onDstStartDay).toBe(23 * 60 * 60 * 1000);
  });
});

describe('currentDateInTimeZone', () => {
  it("resolves the Canberra calendar date that is one day ahead of UTC's, when UTC has not yet crossed into it", () => {
    // 2026-06-14T23:00:00Z is 2026-06-15 09:00 AEST (+10) in Canberra.
    expect(currentDateInTimeZone(new Date('2026-06-14T23:00:00Z'), 'Australia/Canberra')).toEqual({
      year: 2026,
      month: 6,
      day: 15
    });
  });
});

describe('shiftCalendarDate', () => {
  it('shifts across a month/year boundary without any timezone involved', () => {
    expect(shiftCalendarDate({ year: 2026, month: 1, day: 3 }, -7)).toEqual({ year: 2025, month: 12, day: 27 });
  });
});

describe('zonedDateTimeToUtc (issue #361)', () => {
  it('resolves an Australia/Canberra wall-clock time during AEST (UTC+10) to the correct UTC instant', () => {
    expect(zonedDateTimeToUtc(2026, 6, 15, 14, 30, 'Australia/Canberra').toISOString()).toBe('2026-06-15T04:30:00.000Z');
  });

  it('resolves an Australia/Canberra wall-clock time during AEDT (UTC+11) to the correct UTC instant', () => {
    expect(zonedDateTimeToUtc(2026, 1, 15, 14, 30, 'Australia/Canberra').toISOString()).toBe('2026-01-15T03:30:00.000Z');
  });

  it('matches startOfDayUtc when the time-of-day is midnight', () => {
    expect(zonedDateTimeToUtc(2026, 10, 4, 0, 0, 'Australia/Canberra').toISOString()).toBe(
      startOfDayUtc(2026, 10, 4, 'Australia/Canberra').toISOString()
    );
  });
});

describe('currentDateTimeInTimeZone (issue #361)', () => {
  it('resolves the Canberra wall-clock date and time that is ahead of UTC during AEST', () => {
    expect(currentDateTimeInTimeZone(new Date('2026-06-14T23:15:00Z'), 'Australia/Canberra')).toEqual({
      year: 2026,
      month: 6,
      day: 15,
      hour: 9,
      minute: 15
    });
  });
});

describe('toDateTimeLocalValue / fromDateTimeLocalValue (issue #361)', () => {
  it('formats a wall-clock date/time as a zero-padded datetime-local value', () => {
    expect(toDateTimeLocalValue({ year: 2026, month: 1, day: 2, hour: 9, minute: 5 })).toBe('2026-01-02T09:05');
  });

  it('parses a datetime-local value back into its wall-clock parts', () => {
    expect(fromDateTimeLocalValue('2026-01-02T09:05')).toEqual({ year: 2026, month: 1, day: 2, hour: 9, minute: 5 });
  });

  it('returns null for an empty or malformed value', () => {
    expect(fromDateTimeLocalValue('')).toBeNull();
    expect(fromDateTimeLocalValue('not-a-date')).toBeNull();
  });

  it('round-trips through zonedDateTimeToUtc and back to the same Sydney wall-clock value', () => {
    const wallClock = fromDateTimeLocalValue('2026-01-02T09:05')!;
    const utcInstant = zonedDateTimeToUtc(wallClock.year, wallClock.month, wallClock.day, wallClock.hour, wallClock.minute, 'Australia/Canberra');
    expect(toDateTimeLocalValue(currentDateTimeInTimeZone(utcInstant, 'Australia/Canberra'))).toBe('2026-01-02T09:05');
  });
});

describe('resolveZonedDateTime (issue #361 review: DST gap and repeated hour)', () => {
  const zone = 'Australia/Canberra';

  it('reports a time inside the October daylight-saving gap as nonexistent instead of moving it', () => {
    // 2026-10-04 02:00-02:59 never happens in Sydney: the clocks jump from 02:00 AEST to 03:00 AEDT.
    expect(resolveZonedDateTime(2026, 10, 4, 2, 30, zone)).toEqual({ kind: 'nonexistent' });
    expect(resolveZonedDateTime(2026, 10, 4, 2, 0, zone)).toEqual({ kind: 'nonexistent' });
    expect(resolveZonedDateTime(2026, 10, 4, 2, 59, zone)).toEqual({ kind: 'nonexistent' });
  });

  it('resolves valid times on both sides of the October gap with the offset each side uses', () => {
    expect(resolveZonedDateTime(2026, 10, 4, 1, 59, zone)).toEqual({ kind: 'valid', utc: new Date('2026-10-03T15:59:00Z') });
    expect(resolveZonedDateTime(2026, 10, 4, 3, 0, zone)).toEqual({ kind: 'valid', utc: new Date('2026-10-03T16:00:00Z') });
  });

  it('reports a time inside the April repeated hour as ambiguous, with both of its instants', () => {
    // 2026-04-05 02:00-02:59 happens twice: first in AEDT (UTC+11), then again in AEST (UTC+10).
    expect(resolveZonedDateTime(2026, 4, 5, 2, 30, zone)).toEqual({
      kind: 'ambiguous',
      earlier: new Date('2026-04-04T15:30:00Z'),
      later: new Date('2026-04-04T16:30:00Z')
    });
  });

  it('resolves valid times on both sides of the April repeated hour with the offset each side uses', () => {
    expect(resolveZonedDateTime(2026, 4, 5, 1, 59, zone)).toEqual({ kind: 'valid', utc: new Date('2026-04-04T14:59:00Z') });
    expect(resolveZonedDateTime(2026, 4, 5, 3, 0, zone)).toEqual({ kind: 'valid', utc: new Date('2026-04-04T17:00:00Z') });
  });

  it('agrees with zonedDateTimeToUtc for ordinary AEST and AEDT times', () => {
    expect(resolveZonedDateTime(2026, 6, 15, 14, 30, zone)).toEqual({ kind: 'valid', utc: zonedDateTimeToUtc(2026, 6, 15, 14, 30, zone) });
    expect(resolveZonedDateTime(2026, 1, 15, 14, 30, zone)).toEqual({ kind: 'valid', utc: zonedDateTimeToUtc(2026, 1, 15, 14, 30, zone) });
  });

  it('keeps start-of-day resolution unchanged on both transition days', () => {
    expect(resolveZonedDateTime(2026, 10, 4, 0, 0, zone)).toEqual({ kind: 'valid', utc: startOfDayUtc(2026, 10, 4, zone) });
    expect(resolveZonedDateTime(2026, 4, 5, 0, 0, zone)).toEqual({ kind: 'valid', utc: startOfDayUtc(2026, 4, 5, zone) });
  });
});
