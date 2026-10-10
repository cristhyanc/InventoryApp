import { BusinessDateTimePipe } from './business-date-time.pipe';
import { BusinessTimeZoneService } from './business-time-zone.service';

/**
 * The pipe formats an instant in the current business's own time zone (issue #499), so each test
 * says which zone the business is in. `Australia/Sydney` is the zone the application's existing
 * business uses and the cases over it are the ones this pipe already had; `America/New_York`
 * proves the zone is read rather than assumed; and the unresolved case - a business that could not
 * be read at all - must render nothing rather than a confidently wrong local time.
 */
function pipeFor(timeZoneId?: string): BusinessDateTimePipe {
  const service = new BusinessTimeZoneService();
  if (timeZoneId !== undefined) {
    service.publish(timeZoneId);
  }
  return new BusinessDateTimePipe(service);
}

describe('BusinessDateTimePipe', () => {
  it('renders a UTC instant during Australian Eastern Standard Time (AEST, UTC+10) as business local time', () => {
    // 2024-06-15T00:00:00Z falls in the Australian winter, outside daylight saving (AEST, UTC+10).
    expect(pipeFor('Australia/Sydney').transform('2024-06-15T00:00:00Z')).toBe('15/06/2024, 10:00 am');
  });

  it('renders a UTC instant during Australian Eastern Daylight Time (AEDT, UTC+11) as business local time', () => {
    // 2024-01-15T00:00:00Z falls in the Australian summer, inside daylight saving (AEDT, UTC+11).
    expect(pipeFor('Australia/Sydney').transform('2024-01-15T00:00:00Z')).toBe('15/01/2024, 11:00 am');
  });

  it('accepts a Date instance in addition to an ISO string', () => {
    expect(pipeFor('Australia/Sydney').transform(new Date('2024-06-15T00:00:00Z'))).toBe('15/06/2024, 10:00 am');
  });

  it('returns an empty string for a null, undefined, or empty value instead of a raw UTC fallback', () => {
    const pipe = pipeFor('Australia/Sydney');

    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform(undefined)).toBe('');
    expect(pipe.transform('')).toBe('');
  });

  it('renders the same instant differently for a business in another time zone', () => {
    // One instant, two businesses: 14:00 UTC is the morning of 15 June in New York and midnight
    // of the same date in Sydney. A timestamp is only meaningful once the zone it is shown in is
    // the business's own.
    expect(pipeFor('America/New_York').transform('2024-06-15T14:00:00Z')).toBe('15/06/2024, 10:00 am');
    expect(pipeFor('Australia/Sydney').transform('2024-06-15T14:00:00Z')).toBe('16/06/2024, 12:00 am');
  });

  it('renders nothing when the business time zone is unknown, rather than guessing one', () => {
    expect(pipeFor().transform('2024-06-15T00:00:00Z')).toBe('');
  });

  it('renders the instant once the business time zone is known', () => {
    const service = new BusinessTimeZoneService();
    const pipe = new BusinessDateTimePipe(service);

    expect(pipe.transform('2024-06-15T00:00:00Z')).toBe('');

    service.publish('Australia/Sydney');

    expect(pipe.transform('2024-06-15T00:00:00Z')).toBe('15/06/2024, 10:00 am');
  });
});
