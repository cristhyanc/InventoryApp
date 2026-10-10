import { HttpClient } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { BusinessService, CurrentBusiness } from './business.service';
import { ConfigService } from './config.service';
import { BusinessTimeZoneService } from '../formatting/business-time-zone.service';

interface HttpStub {
  get: jest.Mock;
}

const sydneyBusiness: CurrentBusiness = { name: 'Vending Co', timeZoneId: 'Australia/Sydney' };

function createService(
  response: () => Observable<unknown> = () => of(sydneyBusiness),
  apiBaseUrl = '/api'
): { service: BusinessService; http: HttpStub; timeZone: BusinessTimeZoneService } {
  const http: HttpStub = { get: jest.fn(response) };
  const timeZone = new BusinessTimeZoneService();
  const service = new BusinessService(
    http as unknown as HttpClient,
    { apiBaseUrl } as ConfigService,
    timeZone
  );
  return { service, http, timeZone };
}

/**
 * The frontend's only source of the business time zone (issue #499). There is deliberately no
 * constant, configuration value or browser zone it can fall back to: an unanswered or failed
 * lookup leaves the zone unknown, so every display shows its loading state instead of a
 * confidently wrong business day.
 */
describe('BusinessService (issue #499)', () => {
  it('reads the current business from the business endpoint', () => {
    const { service, http } = createService();

    service.load().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/business/current');
  });

  it('publishes the business time zone the API reported', () => {
    const { service, timeZone } = createService();

    service.load().subscribe();

    expect(timeZone.timeZoneId).toBe('Australia/Sydney');
    expect(timeZone.isResolved).toBe(true);
  });

  it('publishes whatever zone the business is actually in, not an Australian default', () => {
    const { service, timeZone } = createService(() =>
      of({ name: 'Vending NY', timeZoneId: 'America/New_York' })
    );

    service.load().subscribe();

    expect(timeZone.timeZoneId).toBe('America/New_York');
  });

  it('asks the endpoint once however often the business is needed', () => {
    const { service, http } = createService();

    service.load().subscribe();
    service.load().subscribe();
    service.load().subscribe();

    expect(http.get).toHaveBeenCalledTimes(1);
  });

  it('leaves the zone unknown when the lookup fails, rather than assuming one', () => {
    jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { service, timeZone } = createService(() => throwError(() => new Error('network down')));
    const settled: (CurrentBusiness | null)[] = [];

    service.load().subscribe((business) => settled.push(business));

    // Settled, so the shell renders the application rather than waiting forever - with no zone, so
    // nothing renders a business date in a zone the API never confirmed.
    expect(settled).toEqual([null]);
    expect(timeZone.timeZoneId).toBeNull();
    expect(timeZone.isResolved).toBe(false);
    jest.restoreAllMocks();
  });

  it('retries after a failed lookup so a transient failure is not permanent', () => {
    jest.spyOn(console, 'error').mockImplementation(() => undefined);
    let attempt = 0;
    const { service, http, timeZone } = createService(() =>
      ++attempt === 1 ? throwError(() => new Error('network down')) : of(sydneyBusiness)
    );

    service.load().subscribe();
    service.load().subscribe();

    expect(http.get).toHaveBeenCalledTimes(2);
    expect(timeZone.timeZoneId).toBe('Australia/Sydney');
    jest.restoreAllMocks();
  });

  it('ignores a response with no usable time zone instead of publishing a blank one', () => {
    const { service, timeZone } = createService(() => of({ name: 'Vending Co', timeZoneId: '' }));

    service.load().subscribe();

    expect(timeZone.timeZoneId).toBeNull();
  });
});
