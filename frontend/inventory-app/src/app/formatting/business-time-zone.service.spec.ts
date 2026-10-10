import { BusinessTimeZoneService } from './business-time-zone.service';

/**
 * The holder every operator-facing date/time reads the business's zone from (issue #499). Its one
 * rule is that "not known yet" is not a usable value: it never answers a zone nobody confirmed.
 */
describe('BusinessTimeZoneService (issue #499)', () => {
  it('starts unresolved, with no fallback zone at all', () => {
    const service = new BusinessTimeZoneService();

    expect(service.timeZoneId).toBeNull();
    expect(service.isResolved).toBe(false);
  });

  it('publishes the zone to everyone reading it, including a late subscriber', () => {
    const service = new BusinessTimeZoneService();
    const seen: (string | null)[] = [];
    service.timeZoneId$.subscribe((zone) => seen.push(zone));

    service.publish('America/New_York');
    const late: (string | null)[] = [];
    service.timeZoneId$.subscribe((zone) => late.push(zone));

    expect(seen).toEqual([null, 'America/New_York']);
    expect(late).toEqual(['America/New_York']);
    expect(service.timeZoneId).toBe('America/New_York');
  });

  it('trims a published zone', () => {
    const service = new BusinessTimeZoneService();

    service.publish('  Australia/Sydney  ');

    expect(service.timeZoneId).toBe('Australia/Sydney');
  });

  it('cannot be un-resolved by a blank or missing value', () => {
    const service = new BusinessTimeZoneService();
    service.publish('Australia/Sydney');

    service.publish('');
    service.publish('   ');
    service.publish(null);
    service.publish(undefined);

    expect(service.timeZoneId).toBe('Australia/Sydney');
  });

  it('returns to unknown when cleared, for a change of signed-in account', () => {
    const service = new BusinessTimeZoneService();
    const seen: Array<string | null> = [];
    service.timeZoneId$.subscribe((zone) => seen.push(zone));
    service.publish('Australia/Sydney');

    service.clear();

    expect(service.timeZoneId).toBeNull();
    expect(service.isResolved).toBe(false);
    expect(seen).toEqual([null, 'Australia/Sydney', null]);
  });
});
