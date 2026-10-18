import { TestBed } from '@angular/core/testing';
import { ComponentFixture } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { MachineLatestSalesComponent } from './machine-latest-sales.component';
import { MachineService } from '../../../services/machine.service';
import { AccessService } from '../../../access/access.service';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';
import { MachineLatestSale, MachineLatestSales } from '../../../models/models';

/**
 * The machine-details Sales panel (issue #768).
 *
 * <p>What these cases are about is the difference between answers that look alike on screen and
 * mean opposite things: a window the provider reported as empty, a business with no sales provider
 * at all, a connection that needs reconnecting, a permission Nayax refused, and a request that
 * failed. Only the first of those means "this machine sold nothing", and none of them may be shown
 * as a sale amount of zero.</p>
 */
describe('MachineLatestSalesComponent (issue #768)', () => {
  const OneSaleAt = '2026-10-17T23:30:00Z';

  function sale(overrides: Partial<MachineLatestSale> = {}): MachineLatestSale {
    return {
      transactionId: 2,
      saleDateTimeUtc: OneSaleAt,
      productName: 'Bottled Water',
      paymentMethod: 'Credit Card',
      saleAmount: 9.5,
      currencyCode: 'AUD',
      ...overrides
    };
  }

  function available(sales: MachineLatestSale[], machineId = 7): MachineLatestSales {
    return { machineId, isAvailable: true, unavailableReason: null, unavailableDetail: null, sales };
  }

  function unavailable(
    reason: MachineLatestSales['unavailableReason'], detail: string, machineId = 7
  ): MachineLatestSales {
    return { machineId, isAvailable: false, unavailableReason: reason, unavailableDetail: detail, sales: [] };
  }

  async function render(
    getLatestSales: (id: number) => Observable<MachineLatestSales>,
    canViewFinancials = true
  ): Promise<{
    fixture: ComponentFixture<MachineLatestSalesComponent>;
    host: HTMLElement;
    calls: number[];
  }> {
    const calls: number[] = [];
    await TestBed.configureTestingModule({
      imports: [MachineLatestSalesComponent],
      providers: [
        {
          provide: MachineService,
          useValue: {
            getLatestSales: (id: number) => {
              calls.push(id);
              return getLatestSales(id);
            }
          }
        },
        { provide: AccessService, useValue: { allows: () => of(canViewFinancials) } }
      ]
    }).compileComponents();

    // The pipe renders nothing without the current business's zone, exactly as the application
    // shell guarantees one before a routed page renders (issue #499).
    TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

    const fixture = TestBed.createComponent(MachineLatestSalesComponent);
    fixture.componentRef.setInput('machineId', 7);
    fixture.detectChanges(false);
    return { fixture, host: fixture.nativeElement as HTMLElement, calls };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('reads the machine given to it and renders the window in the order the API sent', async () => {
    const newest = sale({ transactionId: 2 });
    const older = sale({ transactionId: 1, saleDateTimeUtc: '2026-10-16T09:28:18Z', productName: 'Chips' });
    const { host, calls } = await render(() => of(available([newest, older])));

    expect(calls).toEqual([7]);
    const rows = host.querySelectorAll('tbody tr');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Bottled Water');
    expect(rows[1].textContent).toContain('Chips');
  });

  it('labels the window as the provider rolling one rather than a complete sales history', async () => {
    const { host } = await render(() => of(available([sale()])));

    expect(host.textContent).toContain('rolling');
    expect(host.textContent).toContain('not this machine');
    expect(host.textContent).not.toContain('all sales');
  });

  /**
   * The amount carries the currency the provider reported, and no denomination is invented when it
   * reported none. The documented contract declares `CurrencyCode` nullable and carries no symbol,
   * so a `$` on these figures would be this application guessing at somebody else's money.
   */
  it('labels the amount with the reported currency code instead of a dollar sign', async () => {
    const { host } = await render(() => of(available([sale()])));

    const row = host.querySelector('tbody tr')!.textContent!;
    expect(row).toContain('9.50 AUD');
    expect(row).not.toContain('$');
  });

  it('labels a non-dollar currency with the code the provider reported', async () => {
    const { host } = await render(() => of(available([sale({ currencyCode: 'EUR' })])));

    const row = host.querySelector('tbody tr')!.textContent!;
    expect(row).toContain('9.50 EUR');
    expect(row).not.toContain('$');
    expect(row).not.toContain('AUD');
  });

  it('says the currency was not reported rather than showing the amount as dollars', async () => {
    const { host } = await render(() => of(available([sale({ currencyCode: null })])));

    const row = host.querySelector('tbody tr')!.textContent!;
    expect(row).toContain('9.50');
    expect(row).toContain('currency not reported');
    expect(row).not.toContain('$');
    expect(row).not.toContain('AUD');
  });

  /**
   * The financial boundary as the operator meets it. The API has already withheld the amount; this
   * panel says why rather than leaving an empty cell, and never renders `$0.00`.
   */
  it('replaces the amount with a role explanation for a role that may not see it', async () => {
    const { host } = await render(() => of(available([sale({ saleAmount: null, currencyCode: null })])), false);

    const row = host.querySelector('tbody tr')!.textContent!;
    expect(row).toContain('Not available for your role');
    expect(row).not.toContain('$0.00');
    // The rest of the row stays readable, which is what keeps the tab usable for restocking work.
    expect(row).toContain('Bottled Water');
    expect(row).toContain('Credit Card');
  });

  it('shows an absent amount as unavailable rather than as zero', async () => {
    const { host } = await render(() => of(available([sale({ saleAmount: null })])));

    const row = host.querySelector('tbody tr')!.textContent!;
    expect(row).toContain('Unavailable');
    expect(row).not.toContain('$0.00');
  });

  it('shows a value the provider did not report as not reported', async () => {
    const { host } = await render(() =>
      of(available([sale({ saleDateTimeUtc: null, productName: null, paymentMethod: null })])));

    const row = host.querySelector('tbody tr')!.textContent!;
    expect(row.match(/Not reported/g) ?? []).toHaveLength(3);
  });

  it('shows an honest empty state when the provider reported no sales', async () => {
    const { host } = await render(() => of(available([])));

    expect(host.querySelector('[data-testid="latest-sales-empty"]')).not.toBeNull();
    expect(host.querySelector('tbody tr')).toBeNull();
  });

  /**
   * A business with no sales provider (issue #759) is a supported way to run this application, so
   * it is information rather than a failure, and the empty list is never shown as "no sales".
   */
  it('shows the no-sales-provider state as information, not as a failure', async () => {
    const detail = 'This business has no sales provider connected, so there are no provider sales to show.';
    const { host } = await render(() => of(unavailable('NoSalesProvider', detail)));

    const banner = host.querySelector('[data-testid="latest-sales-unavailable-NoSalesProvider"]')!;
    expect(banner).not.toBeNull();
    expect(banner.classList).toContain('alert-info');
    expect(banner.textContent).toContain(detail);
    expect(host.querySelector('[data-testid="latest-sales-empty"]')).toBeNull();
    expect(host.querySelector('[data-testid="latest-sales-error"]')).toBeNull();
  });

  it.each(['NayaxNotConnected', 'PermissionNotGranted'] as const)(
    'shows the %s state as something an operator can act on',
    async (reason) => {
      const { host } = await render(() => of(unavailable(reason, `Latest sales are unavailable because of ${reason}.`)));

      const banner = host.querySelector(`[data-testid="latest-sales-unavailable-${reason}"]`)!;
      expect(banner).not.toBeNull();
      expect(banner.classList).toContain('alert-warning');
      expect(host.querySelector('[data-testid="latest-sales-empty"]')).toBeNull();
    }
  );

  /** A failed request says nothing about whether the machine sold anything. */
  it('shows a retryable failure rather than an empty window when the request fails', async () => {
    const { host } = await render(() => throwError(() => new Error('offline')));

    const error = host.querySelector('[data-testid="latest-sales-error"]')!;
    expect(error).not.toBeNull();
    expect(error.textContent).toContain('does not mean the machine has no sales');
    expect(host.querySelector('[data-testid="latest-sales-empty"]')).toBeNull();
    expect(host.querySelector('tbody tr')).toBeNull();
  });

  it('re-reads the window on Refresh', async () => {
    const { fixture, host, calls } = await render(() => of(available([sale()])));

    (host.querySelector('[data-testid="refresh-latest-sales"]') as HTMLButtonElement).click();
    fixture.detectChanges(false);

    expect(calls).toEqual([7, 7]);
  });

  it('disables Refresh and shows a loading state while a request is in flight', async () => {
    const answer = new Subject<MachineLatestSales>();
    const { fixture, host, calls } = await render(() => answer);

    const refresh = () => host.querySelector('[data-testid="refresh-latest-sales"]') as HTMLButtonElement;
    expect(refresh().disabled).toBe(true);
    expect(host.querySelector('[data-testid="latest-sales-loading"]')).not.toBeNull();

    // A click while the first request is still outstanding must not start a second one.
    refresh().click();
    fixture.detectChanges(false);
    expect(calls).toEqual([7]);

    answer.next(available([sale()]));
    fixture.detectChanges(false);

    expect(refresh().disabled).toBe(false);
    expect(host.querySelector('[data-testid="latest-sales-loading"]')).toBeNull();
  });

  /**
   * The obsolete-answer rule: navigating to a different machine clears the rows immediately and
   * unsubscribes the previous machine's request, so its late answer can never be read under the new
   * machine's heading.
   */
  it('discards the previous machine answer when the machine changes', async () => {
    const answers = new Map<number, Subject<MachineLatestSales>>([
      [7, new Subject<MachineLatestSales>()],
      [8, new Subject<MachineLatestSales>()]
    ]);
    const { fixture, host, calls } = await render((id) => answers.get(id)!);

    fixture.componentRef.setInput('machineId', 8);
    fixture.detectChanges(false);

    // The read for machine 7 answers late. Its subscription is already gone.
    answers.get(7)!.next(available([sale({ productName: 'Previous machine chocolate' })], 7));
    fixture.detectChanges(false);

    expect(calls).toEqual([7, 8]);
    expect(host.textContent).not.toContain('Previous machine chocolate');

    answers.get(8)!.next(available([sale({ productName: 'This machine water' })], 8));
    fixture.detectChanges(false);

    expect(host.textContent).toContain('This machine water');
  });

  it('makes no request and offers no Refresh until a machine has been resolved', async () => {
    const calls: number[] = [];
    await TestBed.configureTestingModule({
      imports: [MachineLatestSalesComponent],
      providers: [
        {
          provide: MachineService,
          useValue: {
            getLatestSales: (id: number) => {
              calls.push(id);
              return of(available([sale()]));
            }
          }
        },
        { provide: AccessService, useValue: { allows: () => of(true) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineLatestSalesComponent);
    fixture.componentRef.setInput('machineId', null);
    fixture.detectChanges(false);
    const host = fixture.nativeElement as HTMLElement;

    expect(calls).toEqual([]);
    expect((host.querySelector('[data-testid="refresh-latest-sales"]') as HTMLButtonElement).disabled).toBe(true);
  });
});
