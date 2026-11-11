import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { BusinessReceiptScanUsageCardComponent } from './business-receipt-scan-usage-card.component';
import {
  SuperAdminReceiptScanUsage,
  SuperAdminReceiptScanUsageService
} from '../../../services/super-admin-receipt-scan-usage.service';

const partlyUsed: SuperAdminReceiptScanUsage = {
  businessId: 9,
  month: '2026-11',
  used: 37,
  remaining: 63,
  limit: 100
};

const exhausted: SuperAdminReceiptScanUsage = {
  ...partlyUsed,
  used: 100,
  remaining: 0
};

async function render(service: Partial<SuperAdminReceiptScanUsageService>) {
  await TestBed.configureTestingModule({
    imports: [BusinessReceiptScanUsageCardComponent],
    providers: [{ provide: SuperAdminReceiptScanUsageService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(BusinessReceiptScanUsageCardComponent);
  fixture.componentRef.setInput('businessId', 9);
  fixture.componentRef.setInput('businessName', 'Snack Co');
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function text(host: HTMLElement, testId: string): string {
  return (host.querySelector(`[data-testid="${testId}"]`)?.textContent ?? '').trim();
}

describe('BusinessReceiptScanUsageCardComponent (issue #1164)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the count, the cap, what is left and the month it is all about', async () => {
    const { host } = await render({ get: () => of(partlyUsed) });

    expect(text(host, 'scan-usage-month')).toBe('2026-11');
    expect(text(host, 'scan-usage-used')).toBe('37 of 100');
    expect(text(host, 'scan-usage-remaining')).toBe('63');
    // And it says whose month it is, because the administrator's own month may be a different one.
    expect(text(host, 'scan-usage-month-note')).toContain("business's own calendar month");
  });

  it('asks the server for the business it was given', async () => {
    const asked: number[] = [];
    await render({
      get: (businessId: number) => {
        asked.push(businessId);
        return of(partlyUsed);
      }
    });

    expect(asked).toEqual([9]);
  });

  it('says so when the allowance is gone, and says what scanning answers', async () => {
    const { host } = await render({ get: () => of(exhausted) });

    expect(text(host, 'scan-usage-remaining')).toBe('0');
    expect(text(host, 'scan-usage-full')).toContain('used all 100 receipt scans for this month');
  });

  it('does not show the exhausted note while scans are left', async () => {
    const { host } = await render({ get: () => of(partlyUsed) });

    expect(host.querySelector('[data-testid="scan-usage-full"]')).toBeNull();
  });

  it('offers no action that could change the count', async () => {
    const { host } = await render({ get: () => of(partlyUsed) });

    // One button, and it closes the card. Buying extra scans and resetting a counter are out of
    // scope on issue #1164, and the server has no route that would accept either.
    const buttons = Array.from(host.querySelectorAll('button')).map((button) =>
      (button.textContent ?? '').trim()
    );
    expect(buttons).toEqual(['Close']);
  });

  it('emits closed when the card is dismissed', async () => {
    const { host, component } = await render({ get: () => of(partlyUsed) });
    let closed = 0;
    component.closed.subscribe(() => closed++);

    (host.querySelector('button') as HTMLButtonElement).click();

    expect(closed).toBe(1);
  });

  it('shows the server’s own message when the read fails, and no figures', async () => {
    const { host } = await render({
      get: () =>
        throwError(
          () =>
            new HttpErrorResponse({
              status: 500,
              statusText: 'Server Error',
              error: { message: 'Something went wrong reading that.' }
            })
        )
    });

    expect(host.textContent).toContain('Something went wrong reading that.');
    expect(host.querySelector('[data-testid="scan-usage-used"]')).toBeNull();
  });

  it('falls back to its own sentence when the failure carries none', async () => {
    const { host } = await render({
      get: () => throwError(() => new HttpErrorResponse({ status: 503, statusText: 'Unavailable' }))
    });

    expect(host.textContent).toContain('Unable to load this business’s receipt scan usage.');
  });
});
