import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EndCommissionAgreementComponent } from './end-commission-agreement.component';
import { SiteCommissionAgreement } from '../../../services/reporting.service';

function agreement(overrides: Partial<SiteCommissionAgreement> = {}): SiteCommissionAgreement {
  return { id: 5, siteId: 1, effectiveFrom: '2026-01-01T00:00:00', commissionRate: 0.1, frequency: 1, basis: 0, ...overrides };
}

async function render(input: SiteCommissionAgreement = agreement(), timeZoneId: string | null = 'Australia/Sydney') {
  await TestBed.configureTestingModule({ imports: [EndCommissionAgreementComponent] }).compileComponents();

  const fixture: ComponentFixture<EndCommissionAgreementComponent> =
    TestBed.createComponent(EndCommissionAgreementComponent);
  fixture.componentInstance.agreement = input;
  fixture.componentInstance.timeZoneId = timeZoneId;
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function button(host: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find(
    (candidate) => candidate.textContent?.trim() === label
  ) as HTMLButtonElement | undefined;
}

/**
 * The End agreement action of issue #1254: a date picker behind an explicit two-step confirmation,
 * with the warning an end date in the past has to raise before it is confirmed.
 */
describe('EndCommissionAgreementComponent (issue #1254)', () => {
  it('offers End agreement and no date picker until it is pressed', async () => {
    const { host } = await render();

    expect(button(host, 'End agreement')).toBeDefined();
    expect(host.querySelector('input[type="date"]')).toBeNull();
  });

  it('opens a date picker bounded by the agreement\'s own start date', async () => {
    const { fixture, host } = await render();

    fixture.componentInstance.start();
    fixture.detectChanges();

    const input = host.querySelector('input[type="date"]') as HTMLInputElement;
    expect(input).not.toBeNull();
    expect(input.min).toBe('2026-01-01');
    expect(input.max).toBe('');
  });

  it('bounds the picker by an end date the agreement already carries, because ending never extends', async () => {
    const { fixture, host } = await render(agreement({ effectiveTo: '2026-06-30T00:00:00' }));

    fixture.componentInstance.start();
    fixture.detectChanges();

    expect((host.querySelector('input[type="date"]') as HTMLInputElement).max).toBe('2026-06-30');
  });

  it('emits the confirmed end date once and closes', async () => {
    const { fixture } = await render();
    const ended: string[] = [];
    fixture.componentInstance.agreementEnded.subscribe((date) => ended.push(date));

    fixture.componentInstance.start();
    fixture.componentInstance.endDate = '2026-06-30';
    fixture.componentInstance.confirm();

    expect(ended).toEqual(['2026-06-30']);
    expect(fixture.componentInstance.open).toBe(false);
  });

  it('emits nothing when no date has been chosen', async () => {
    const { fixture } = await render();
    const ended: string[] = [];
    fixture.componentInstance.agreementEnded.subscribe((date) => ended.push(date));

    fixture.componentInstance.start();
    fixture.componentInstance.confirm();

    expect(ended).toEqual([]);
  });

  it('emits nothing while the page is mid-write, even if the handler is invoked directly', async () => {
    const { fixture } = await render();
    const ended: string[] = [];
    fixture.componentInstance.agreementEnded.subscribe((date) => ended.push(date));

    fixture.componentInstance.start();
    fixture.componentInstance.endDate = '2026-06-30';
    fixture.componentInstance.saving = true;
    fixture.componentInstance.confirm();

    expect(ended).toEqual([]);
  });

  it('cancelling closes the picker and forgets the date without emitting', async () => {
    const { fixture, host } = await render();
    const ended: string[] = [];
    fixture.componentInstance.agreementEnded.subscribe((date) => ended.push(date));

    fixture.componentInstance.start();
    fixture.componentInstance.endDate = '2026-06-30';
    fixture.componentInstance.cancel();
    fixture.detectChanges();

    expect(ended).toEqual([]);
    expect(fixture.componentInstance.endDate).toBe('');
    expect(host.querySelector('input[type="date"]')).toBeNull();
  });

  /**
   * The criterion: commission is calculated on read, so an end date that has already passed changes
   * commission due from that date on - and the operator is told so before they confirm it.
   */
  it('warns before confirming an end date that has already passed', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-16T03:00:00Z'));
    try {
      const { fixture, host } = await render();

      fixture.componentInstance.start();
      fixture.componentInstance.endDate = '2026-06-30';
      fixture.detectChanges();

      expect(fixture.componentInstance.endsInThePast).toBe(true);
      const warning = host.querySelector('[role="alert"]');
      expect(warning?.textContent).toContain('commission due for dates after it will change');
      expect(button(host, 'Confirm end date')?.disabled).toBe(false);
    } finally {
      jest.useRealTimers();
    }
  });

  it('shows no warning for an end date that has not passed', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-16T03:00:00Z'));
    try {
      const { fixture, host } = await render();

      fixture.componentInstance.start();
      fixture.componentInstance.endDate = '2026-12-31';
      fixture.detectChanges();

      expect(fixture.componentInstance.endsInThePast).toBe(false);
      expect(host.querySelector('[role="alert"]')).toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  /**
   * Today is the business's own calendar day, not the host's: at 03:00 UTC on 16 November it is
   * already 17 November in Sydney, so 16 November has passed for that business and has not for UTC.
   */
  it('judges "already passed" in the business time zone', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-11-16T15:00:00Z'));
    try {
      const { fixture } = await render(agreement(), 'Australia/Sydney');
      fixture.componentInstance.start();
      fixture.componentInstance.endDate = '2026-11-16';

      expect(fixture.componentInstance.endsInThePast).toBe(true);
    } finally {
      jest.useRealTimers();
    }
  });
});
