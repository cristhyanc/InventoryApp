import { BusinessDateTimePipe } from './business-date-time.pipe';

describe('BusinessDateTimePipe', () => {
  const pipe = new BusinessDateTimePipe();

  it('renders a UTC instant during Australian Eastern Standard Time (AEST, UTC+10) as Canberra local time', () => {
    // 2024-06-15T00:00:00Z falls in the Australian winter, outside daylight saving (AEST, UTC+10).
    expect(pipe.transform('2024-06-15T00:00:00Z')).toBe('15/06/2024, 10:00 am');
  });

  it('renders a UTC instant during Australian Eastern Daylight Time (AEDT, UTC+11) as Canberra local time', () => {
    // 2024-01-15T00:00:00Z falls in the Australian summer, inside daylight saving (AEDT, UTC+11).
    expect(pipe.transform('2024-01-15T00:00:00Z')).toBe('15/01/2024, 11:00 am');
  });

  it('accepts a Date instance in addition to an ISO string', () => {
    expect(pipe.transform(new Date('2024-06-15T00:00:00Z'))).toBe('15/06/2024, 10:00 am');
  });

  it('returns an empty string for a null, undefined, or empty value instead of a raw UTC fallback', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform(undefined)).toBe('');
    expect(pipe.transform('')).toBe('');
  });
});
