/**
 * @jest-environment <rootDir>/src/app/components/super-admin/supplier-presets/timezone-test-environment.cjs
 *
 * The fortnight start date round-trips as the `YYYY-MM-DD` characters the API sent, in a time zone
 * where a `Date`-based conversion would move it by a day (issue #1109).
 *
 * The zone is fixed by `timezone-test-environment.cjs` - a spec cannot set it for itself, because
 * Jest gives the sandbox a copy of `process.env` - and the first test below checks that it is
 * actually in force, so this suite cannot pass by having no zone mocked at all.
 */
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import {
  SupplierSearchFrequency,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from '../../../services/super-admin-supplier-presets.service';
import { SupplierPresetDialogComponent } from './supplier-preset-dialog.component';

const FORTNIGHT_START_DATE = '2026-11-09';

/** A `YYYY-MM-DD` value built from a `Date`'s **local** parts - the conversion this feature avoids. */
function localDateParts(date: Date): string {
  const month = `${date.getMonth() + 1}`.padStart(2, '0');
  const day = `${date.getDate()}`.padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

function fortnightlyPreset(): SuperAdminSupplierPreset {
  return {
    id: 4,
    name: 'Coles',
    url: 'https://www.coles.com.au',
    isEnabled: true,
    searchPrices: true,
    servesNationwide: true,
    servedStates: [],
    pricesIncludeGst: true,
    priceConnector: null,
    priceConnectorKey: null,
    searchFrequency: SupplierSearchFrequency.Fortnightly,
    searchDaysOfWeek: ['THU'],
    fortnightStartDate: FORTNIGHT_START_DATE,
    scheduleEffectiveFromUtc: '2026-11-05T00:00:00Z',
    priceCheckFreshnessHours: 342,
    createdAtUtc: '2026-10-23T00:00:00Z',
    updatedAtUtc: '2026-10-23T00:00:00Z'
  };
}

async function render(service: Partial<SuperAdminSupplierPresetsService>, editing: SuperAdminSupplierPreset) {
  await TestBed.configureTestingModule({
    imports: [SupplierPresetDialogComponent],
    providers: [{ provide: SuperAdminSupplierPresetsService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierPresetDialogComponent);
  fixture.componentRef.setInput('preset', editing);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('the fortnight start date in a time zone behind UTC (issue #1109)', () => {
  /**
   * The hazard this file exists for, asserted rather than assumed: if the mocked zone ever stopped
   * taking effect, every test below would pass for the wrong reason.
   */
  it('is a zone in which a Date-based conversion does move the day', () => {
    const throughADate = new Date(FORTNIGHT_START_DATE);

    expect(throughADate.getDate()).toBe(8);
    expect(localDateParts(throughADate)).toBe('2026-11-08');
    expect(localDateParts(throughADate)).not.toBe(FORTNIGHT_START_DATE);
  });

  it('shows the stored date unchanged in the date field', async () => {
    const { host } = await render({ update: jest.fn() }, fortnightlyPreset());

    expect((host.querySelector('#preset-fortnight-start') as HTMLInputElement).value).toBe(
      FORTNIGHT_START_DATE
    );
  });

  it('sends the stored date back unchanged when nothing about it was edited', async () => {
    const update = jest.fn(() => of(fortnightlyPreset()));
    const { fixture, host } = await render({ update }, fortnightlyPreset());

    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(
      4,
      expect.objectContaining({
        searchFrequency: SupplierSearchFrequency.Fortnightly,
        searchDaysOfWeek: ['THU'],
        fortnightStartDate: FORTNIGHT_START_DATE
      })
    );
  });

  it('sends a newly chosen date as the characters that were chosen', async () => {
    const update = jest.fn(() => of(fortnightlyPreset()));
    const { fixture, host } = await render({ update }, fortnightlyPreset());

    const input = host.querySelector('#preset-fortnight-start') as HTMLInputElement;
    input.value = '2027-01-01';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    buttonWithText(host, 'Save').click();
    fixture.detectChanges();

    // Not 2026-12-31, which is what this zone makes of a `Date` built from the chosen day.
    expect(localDateParts(new Date('2027-01-01'))).toBe('2026-12-31');
    expect(update).toHaveBeenCalledWith(
      4,
      expect.objectContaining({ fortnightStartDate: '2027-01-01' })
    );
  });
});
