import { of } from 'rxjs';
import { ActivatedRoute } from '@angular/router';
import { DailyReportComponent } from './daily-report.component';
import { ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { providerAvailabilityDouble } from '../../provider/provider-availability.spec-helpers';

function createComponent(timeZoneId: string | null): DailyReportComponent {
  const route = {} as unknown as ActivatedRoute;
  const reports = {} as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;
  const businessTimeZone = { timeZoneId } as unknown as BusinessTimeZoneService;
  return new DailyReportComponent(route, reports, machines, providerAvailabilityDouble(), businessTimeZone);
}

/**
 * `ReportPageBase.selectPeriod` and its initial "This month" default (issue #1273): every period
 * preset reads "today" from the business's own time zone, never the UTC date - tested here through
 * `DailyReportComponent`, one of the six reports that extend the base directly, since the base
 * itself is abstract.
 */
describe('ReportPageBase period presets read the business time zone (issue #1273)', () => {
  afterEach(() => jest.useRealTimers());

  it('defaults This month to the Sydney date at construction, when UTC has not yet crossed into it', () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-12T21:30:00Z')); // 08:30 AEDT on 13 Nov
    const component = createComponent('Australia/Sydney');

    expect(component.period).toBe('thisMonth');
    expect(component.from).toBe('2026-11-01');
    expect(component.to).toBe('2026-11-13');
  });

  it('gives the Sydney month for This month and Last month on the last day of the month, while UTC is still the day before', () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-29T21:30:00Z')); // 08:30 AEDT on 30 Nov
    const component = createComponent('Australia/Sydney');

    component.selectPeriod('thisMonth');
    expect(component.from).toBe('2026-11-01');
    expect(component.to).toBe('2026-11-30');

    component.selectPeriod('lastMonth');
    expect(component.from).toBe('2026-10-01');
    expect(component.to).toBe('2026-10-31');
  });

  it('starts Current FY on 1 July of the new year and ends Previous FY on 30 June, at 08:30 Sydney on 1 July while UTC is still 30 June', () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-06-30T22:30:00Z')); // 08:30 AEST on 1 July
    const component = createComponent('Australia/Sydney');

    component.selectPeriod('currentFy');
    expect(component.from).toBe('2026-07-01');
    expect(component.to).toBe('2026-07-01');

    component.selectPeriod('previousFy');
    expect(component.from).toBe('2025-07-01');
    expect(component.to).toBe('2026-06-30');
  });

  it('keeps Current FY in the old financial year at 23:30 Sydney on 30 June', () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-06-30T13:30:00Z')); // 23:30 AEST on 30 June
    const component = createComponent('Australia/Sydney');

    component.selectPeriod('currentFy');
    expect(component.from).toBe('2025-07-01');
    expect(component.to).toBe('2026-06-30');
  });

  it('resolves a non-Sydney business time zone on its own calendar date', () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-12T21:30:00Z')); // 13:30 PST, still 12 Nov
    const component = createComponent('America/Los_Angeles');

    expect(component.from).toBe('2026-11-01');
    expect(component.to).toBe('2026-11-12');
  });

  it('falls back to the device local date, never the UTC date, while the time zone is not known yet', () => {
    jest.useFakeTimers().setSystemTime(new Date(2026, 10, 13, 8, 30)); // local 13 Nov 2026 08:30
    const component = createComponent(null);

    expect(component.from).toBe('2026-11-01');
    expect(component.to).toBe('2026-11-13');
  });
});
