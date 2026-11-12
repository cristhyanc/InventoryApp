import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { PurchaseScanDraft, PurchaseScanLine } from '../../../models/purchase-scan';
import { PurchaseScanService } from '../../../services/purchase-scan.service';
import { PurchaseReceiptScanComponent, PurchaseReceiptScanned } from './purchase-receipt-scan.component';

function stockLine(): PurchaseScanLine {
  return {
    kind: 'stock',
    text: { value: 'COKE 375ML 24PK', provenance: 'read' },
    packs: { value: 2, provenance: 'read' },
    unitsPerPack: { value: 24, provenance: 'read' },
    units: { value: 48, provenance: 'suggested' },
    lineTotal: { value: 60, provenance: 'read' },
    unitCost: { value: 1.25, provenance: 'suggested' },
    productId: { value: 5, provenance: 'suggested' },
    gst: { priceIncludesGst: 'yes', taxable: 'yes', marker: null },
    levyUnresolved: false,
    missing: []
  };
}

function draft(): PurchaseScanDraft {
  return {
    lines: [stockLine()],
    usage: { month: '2026-11', used: 4, remaining: 96, limit: 100 }
  };
}

describe('PurchaseReceiptScanComponent (issue #1166)', () => {
  let fixture: ComponentFixture<PurchaseReceiptScanComponent>;
  let component: PurchaseReceiptScanComponent;
  let host: HTMLElement;
  let scan: jest.Mock;
  let scanned: PurchaseReceiptScanned[];

  async function render(options: {
    scanResult?: Observable<PurchaseScanDraft>;
    planAllowed?: Observable<boolean>;
    hasExistingLines?: boolean;
    disabled?: boolean;
  } = {}): Promise<void> {
    scan = jest.fn(() => options.scanResult ?? of(draft()));

    await TestBed.configureTestingModule({
      imports: [PurchaseReceiptScanComponent],
      providers: [
        provideRouter([]),
        { provide: PurchaseScanService, useValue: { scan } },
        { provide: AccessService, useValue: { allowsPlanFeature: () => options.planAllowed ?? of(true) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(PurchaseReceiptScanComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('hasExistingLines', options.hasExistingLines ?? false);
    fixture.componentRef.setInput('disabled', options.disabled ?? false);
    scanned = [];
    component.scanned.subscribe((result) => scanned.push(result));
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  const byTestId = (id: string) => host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const text = () => host.textContent?.replace(/\s+/g, ' ') ?? '';

  /** Picks a receipt exactly as the hidden file input reports one, and lets the preparation settle. */
  async function pick(name = 'receipt.pdf', type = 'application/pdf'): Promise<File> {
    const file = new File(['receipt bytes'], name, { type });
    const input = byTestId('purchase-receipt-scan-input') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    fixture.detectChanges();
    return file;
  }

  it('offers a Scan receipt button on a form with nothing in it', async () => {
    await render();

    const button = byTestId('purchase-receipt-scan-start') as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    expect(button.textContent?.trim()).toBe('Scan receipt');
  });

  it('scans the chosen receipt and reports the draft and the file that was read', async () => {
    await render();

    const file = await pick('receipt.pdf');

    expect(scan).toHaveBeenCalledTimes(1);
    expect(scan.mock.calls[0][0]).toBe(file);
    expect(scanned).toHaveLength(1);
    expect(scanned[0].draft.lines).toHaveLength(1);
    expect(scanned[0].file).toBe(file);
    expect(component.state).toBe('idle');
  });

  it('shows the reading progress with a Cancel while the request is out', async () => {
    const pending = new Subject<PurchaseScanDraft>();
    await render({ scanResult: pending });

    await pick();

    expect(byTestId('purchase-receipt-scan-progress')?.textContent).toContain('Reading the receipt');
    expect((byTestId('purchase-receipt-scan-start') as HTMLButtonElement).disabled).toBe(true);
    expect(byTestId('purchase-receipt-scan-cancel')).not.toBeNull();
  });

  describe('estimated progress bar', () => {
    afterEach(() => jest.useRealTimers());

    const bar = () => byTestId('purchase-receipt-scan-progress-bar');
    const valueNow = () => Number(bar()?.getAttribute('aria-valuenow'));

    it('rises while the receipt is read, never shows finished, and says when a read runs long', async () => {
      const pending = new Subject<PurchaseScanDraft>();
      await render({ scanResult: pending });
      jest.useFakeTimers({ doNotFake: ['nextTick', 'queueMicrotask'] });
      await pick();

      expect(valueNow()).toBe(0);
      expect(byTestId('purchase-receipt-scan-progress-hint')?.textContent).toContain('20 to 30 seconds');

      jest.advanceTimersByTime(24_000);
      fixture.detectChanges();
      const atTypicalRead = valueNow();
      expect(atTypicalRead).toBeGreaterThan(70);
      expect(atTypicalRead).toBeLessThan(95);

      jest.advanceTimersByTime(36_000);
      fixture.detectChanges();
      expect(valueNow()).toBeGreaterThan(atTypicalRead);
      expect(valueNow()).toBeLessThanOrEqual(95);
      expect(byTestId('purchase-receipt-scan-progress-hint')?.textContent).toContain('up to a minute');
    });

    it('goes away when the draft arrives', async () => {
      const pending = new Subject<PurchaseScanDraft>();
      await render({ scanResult: pending });
      jest.useFakeTimers({ doNotFake: ['nextTick', 'queueMicrotask'] });
      await pick();
      jest.advanceTimersByTime(10_000);

      pending.next(draft());
      fixture.detectChanges();

      expect(bar()).toBeNull();
      expect(scanned).toHaveLength(1);
    });
  });

  it('stops waiting when Cancel is pressed, and ignores an answer that arrives afterwards', async () => {
    const pending = new Subject<PurchaseScanDraft>();
    await render({ scanResult: pending });
    await pick();

    (byTestId('purchase-receipt-scan-cancel') as HTMLButtonElement).click();
    fixture.detectChanges();
    pending.next(draft());

    expect(component.state).toBe('idle');
    expect(scanned).toHaveLength(0);
    expect(byTestId('purchase-receipt-scan-progress')).toBeNull();
  });

  describe('refusals', () => {
    async function failWith(status: number, body: unknown = null): Promise<void> {
      await render({ scanResult: throwError(() => new HttpErrorResponse({ status, error: body })) });
      await pick();
    }

    it('shows the switched-off answer and reports no draft at all (503)', async () => {
      await failWith(503, 'Receipt scanning is not available.');

      expect(byTestId('purchase-receipt-scan-error')?.textContent).toContain('Receipt scanning is not available.');
      expect(scanned).toHaveLength(0);
    });

    it('falls back to its own sentence for a refusal that carried no body', async () => {
      await failWith(503);

      expect(text()).toContain('Receipt scanning is not available.');
    });

    it('shows the unreadable, timeout and monthly-limit answers', async () => {
      await failWith(422, 'Could not read this receipt.');
      expect(text()).toContain('Could not read this receipt.');

      TestBed.resetTestingModule();
      await failWith(504, 'Reading the receipt took too long. Try again, or enter the purchase manually.');
      expect(text()).toContain('took too long');

      TestBed.resetTestingModule();
      await failWith(429, 'You have used all 100 receipt scans for this month.');
      expect(text()).toContain('You have used all 100 receipt scans for this month.');
    });

    it('shows its own sentence for a transport failure with no usable body', async () => {
      await failWith(0, new ProgressEvent('error'));

      expect(text()).toContain('Could not scan the receipt.');
    });

    it('offers the button again, and drops the message, once the failure is dismissed', async () => {
      await failWith(422, 'Could not read this receipt.');

      (byTestId('purchase-receipt-scan-dismiss') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(byTestId('purchase-receipt-scan-error')).toBeNull();
      expect((byTestId('purchase-receipt-scan-start') as HTMLButtonElement).disabled).toBe(false);
    });

    /** A plan refusal is the business's own answer, so it locks rather than offering a retry. */
    it('locks the button when the scan is refused for the plan', async () => {
      await failWith(403, { code: 'plan_required', requiredPlan: 'Pro' });

      expect(byTestId('purchase-receipt-scan-plan-lock')?.textContent).toContain('needs the Pro plan');
      expect(byTestId('purchase-receipt-scan-start')).toBeNull();
      expect(byTestId('purchase-receipt-scan-error')).toBeNull();
    });
  });

  describe('plan lock', () => {
    it('locks the button for a business whose plan does not include scanning', async () => {
      await render({ planAllowed: of(false) });

      expect(byTestId('purchase-receipt-scan-start')).toBeNull();
      expect(byTestId('purchase-receipt-scan-plan-lock')?.textContent).toContain(
        'Scanning a receipt needs the Pro plan.'
      );
      expect(host.querySelector('a[href="/plan"]')).not.toBeNull();
    });

    it('offers the button while the entitlement answers that the plan includes it', async () => {
      await render({ planAllowed: of(true) });

      expect(byTestId('purchase-receipt-scan-start')).not.toBeNull();
      expect(byTestId('purchase-receipt-scan-plan-lock')).toBeNull();
    });
  });

  describe('replacing items already in the form', () => {
    it('asks before anything is sent, and sends nothing while the question is open', async () => {
      await render({ hasExistingLines: true });

      await pick();

      expect(byTestId('purchase-receipt-scan-replace-dialog')).not.toBeNull();
      expect(text()).toContain('Replace the items in this purchase?');
      expect(scan).not.toHaveBeenCalled();
    });

    it('scans once Replace items is chosen', async () => {
      await render({ hasExistingLines: true });
      const file = await pick();

      (byTestId('purchase-receipt-scan-replace') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(scan).toHaveBeenCalledTimes(1);
      expect(scan.mock.calls[0][0]).toBe(file);
      expect(scanned).toHaveLength(1);
    });

    it('sends nothing and reports nothing when the typed items are kept', async () => {
      await render({ hasExistingLines: true });
      await pick();

      (byTestId('purchase-receipt-scan-keep') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(scan).not.toHaveBeenCalled();
      expect(scanned).toHaveLength(0);
      expect(byTestId('purchase-receipt-scan-replace-dialog')).toBeNull();
      expect(component.state).toBe('idle');
    });

    it('labels the button Scan again once the form already holds items', async () => {
      await render({ hasExistingLines: true });

      expect((byTestId('purchase-receipt-scan-start') as HTMLButtonElement).textContent?.trim()).toBe('Scan again');
    });
  });

  it('cannot be started while the page is busy or the write is unavailable', async () => {
    await render({ disabled: true });

    expect((byTestId('purchase-receipt-scan-start') as HTMLButtonElement).disabled).toBe(true);
    component.chooseFile();
    await pick();

    expect(scan).not.toHaveBeenCalled();
  });
});
