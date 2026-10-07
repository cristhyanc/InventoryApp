import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HistoricalGstClassificationWorkflowComponent } from './historical-gst-classification-workflow.component';
import {
  HistoricalGstClassificationApplied,
  HistoricalGstClassificationPreview,
  HistoricalGstClassificationService
} from '../../../services/historical-gst-classification.service';
import { ToastService } from '../../../services/toast.service';

const FINGERPRINT = 'a'.repeat(64);

function summary(overrides: Partial<HistoricalGstClassificationPreview['summary']> = {}) {
  return {
    productLines: { examined: 3, becomingTaxable: 2, becomingGstFree: 1, stayingUnknown: 0 },
    deliveryCharges: { examined: 1, becomingTaxable: 1, becomingGstFree: 0, stayingUnknown: 0 },
    packageCharges: { examined: 1, becomingTaxable: 0, becomingGstFree: 0, stayingUnknown: 1 },
    purchasesExamined: 2,
    lineGst: 1.74,
    chargeGst: 0.5,
    stayingUnknownAmount: 3,
    componentsExamined: 5,
    becomingTaxable: 3,
    becomingGstFree: 1,
    stayingUnknown: 1,
    inputGst: 2.24,
    classifiesAnything: true,
    ...overrides
  };
}

function preview(
  overrides: Partial<HistoricalGstClassificationPreview['summary']> = {}
): HistoricalGstClassificationPreview {
  return { summary: summary(overrides), fingerprint: FINGERPRINT };
}

function applied(componentsClassified = 4): HistoricalGstClassificationApplied {
  return { summary: summary(), componentsClassified };
}

async function render(previewFn: jest.Mock, applyFn: jest.Mock = jest.fn()) {
  const toast = { success: jest.fn(), error: jest.fn(), info: jest.fn() };
  await TestBed.configureTestingModule({
    imports: [HistoricalGstClassificationWorkflowComponent],
    providers: [
      { provide: HistoricalGstClassificationService, useValue: { preview: previewFn, apply: applyFn } },
      { provide: ToastService, useValue: toast }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(HistoricalGstClassificationWorkflowComponent);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, toast };
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find(candidate => candidate.textContent?.includes(text));
}

// The `window.confirm` spies are restored after each test's assertions: restoring them inside the
// test would also clear the recorded calls those assertions check.
afterEach(() => jest.restoreAllMocks());

describe('HistoricalGstClassificationWorkflowComponent preview (issue #433)', () => {
  it('requests nothing until the operator previews', async () => {
    const previewFn = jest.fn(() => of(preview()));
    const { host } = await render(previewFn);

    expect(previewFn).not.toHaveBeenCalled();
    expect(button(host, 'Apply classification')).toBeUndefined();
  });

  it('shows the API counts, the charge counts by type and the resulting input GST', async () => {
    const previewFn = jest.fn(() => of(preview()));
    const { fixture, host } = await render(previewFn);

    button(host, 'Preview classification')?.click();
    fixture.detectChanges();

    expect(previewFn).toHaveBeenCalledTimes(1);
    const text = host.textContent ?? '';
    expect(text).toContain('Components examined:');
    expect(text).toContain('Resulting input GST:');
    expect(text).toContain('$2.24');
    expect(text).toContain('$1.74');
    expect(text).toContain('$0.50');
    const rows = Array.from(host.querySelectorAll('tbody tr')).map(row =>
      Array.from(row.querySelectorAll('td')).map(cell => cell.textContent?.trim())
    );
    expect(rows).toEqual([
      ['Purchased items', '3', '2', '1', '0'],
      ['Delivery charges', '1', '1', '0', '0'],
      ['Package charges', '1', '0', '0', '1']
    ]);
  });

  /**
   * Components no rule covers must stay visible as unresolved rather than looking handled: the
   * report says how many and how much, and what to do about them.
   */
  it('warns about the components no configured rule covers', async () => {
    const { fixture, host } = await render(jest.fn(() => of(preview())));

    button(host, 'Preview classification')?.click();
    fixture.detectChanges();

    const warning = host.querySelector('.alert-warning');
    // The currency symbol depends on the configured locale, so the amount alone is asserted here.
    expect(warning?.textContent).toContain('1 component(s) worth');
    expect(warning?.textContent).toContain('3.00 are not covered by any configured rule');
    expect(warning?.textContent).toContain('stay Not classified');
  });

  it('offers no Apply action when no configured rule classifies anything', async () => {
    const { fixture, host } = await render(
      jest.fn(() => of(preview({ classifiesAnything: false, becomingTaxable: 0, becomingGstFree: 0 })))
    );

    button(host, 'Preview classification')?.click();
    fixture.detectChanges();

    expect(button(host, 'Apply classification')).toBeUndefined();
    expect(host.textContent).toContain('Nothing to apply');
  });

  it('reports a failed preview through the toast and stays applicable to nothing', async () => {
    const { fixture, component, toast } = await render(
      jest.fn(() => throwError(() => ({ error: { message: 'Preview unavailable.' } })))
    );

    component.runPreview();
    fixture.detectChanges();

    expect(toast.error).toHaveBeenCalledWith('Preview unavailable.');
    expect(component.preview).toBeNull();
    expect(component.loading).toBe(false);
  });
});

describe('HistoricalGstClassificationWorkflowComponent apply (issue #433)', () => {
  it('applies the preview it was shown, echoing back its fingerprint, after confirmation', async () => {
    const applyFn = jest.fn(() => of(applied()));
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, host, toast } = await render(jest.fn(() => of(preview())), applyFn);

    button(host, 'Preview classification')?.click();
    fixture.detectChanges();
    button(host, 'Apply classification')?.click();
    fixture.detectChanges();

    expect(confirm).toHaveBeenCalledTimes(1);
    expect(applyFn).toHaveBeenCalledWith(preview());
    expect(toast.success).toHaveBeenCalledWith('Classified 4 purchase component(s).');
    expect(host.textContent).toContain('Classified 4 component(s)');
  });

  it('does not call the API when the confirmation is dismissed', async () => {
    const applyFn = jest.fn(() => of(applied()));
    jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { fixture, component, host } = await render(jest.fn(() => of(preview())), applyFn);

    button(host, 'Preview classification')?.click();
    fixture.detectChanges();
    button(host, 'Apply classification')?.click();

    expect(applyFn).not.toHaveBeenCalled();
    expect(component.preview).not.toBeNull();
  });

  /**
   * After an apply the preview describes work that is already done, so it is discarded: the page
   * cannot offer to apply it a second time, and the operator is told to preview again.
   */
  it('discards the applied preview so it can never be applied twice', async () => {
    const applyFn = jest.fn(() => of(applied()));
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, component, host } = await render(jest.fn(() => of(preview())), applyFn);

    component.runPreview();
    fixture.detectChanges();
    component.applyPreview();
    fixture.detectChanges();

    expect(component.preview).toBeNull();
    expect(button(host, 'Apply classification')).toBeUndefined();
    expect(host.textContent).toContain('Preview again to see what is left.');
  });

  /**
   * The stale-preview refusal, as the operator meets it: the server's own message is shown, and the
   * preview is cleared so a fresh one has to be taken before another apply is possible.
   */
  it('shows the stale-preview refusal and clears the preview so a fresh one is required', async () => {
    const stale = 'The purchase data or the configured GST rules changed after this preview.';
    const applyFn = jest.fn(() => throwError(() => ({ error: { message: stale } })));
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, component, host, toast } = await render(jest.fn(() => of(preview())), applyFn);

    component.runPreview();
    fixture.detectChanges();
    component.applyPreview();
    fixture.detectChanges();

    expect(toast.error).toHaveBeenCalledWith(stale);
    expect(component.preview).toBeNull();
    expect(component.applied).toBeNull();
    expect(button(host, 'Apply classification')).toBeUndefined();
    expect(component.loading).toBe(false);
  });

  it('falls back to a generic message when the API returns no message', async () => {
    const applyFn = jest.fn(() => throwError(() => ({})));
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, component, toast } = await render(jest.fn(() => of(preview())), applyFn);

    component.runPreview();
    fixture.detectChanges();
    component.applyPreview();

    expect(toast.error).toHaveBeenCalledWith('Unable to apply the historical GST classification.');
  });

  it('does nothing without a preview on screen', async () => {
    const applyFn = jest.fn(() => of(applied()));
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = await render(jest.fn(() => of(preview())), applyFn);

    component.applyPreview();

    expect(confirm).not.toHaveBeenCalled();
    expect(applyFn).not.toHaveBeenCalled();
  });

  it('disables the actions while a request is in flight', async () => {
    const { fixture, component, host } = await render(jest.fn(() => of(preview())));

    component.runPreview();
    component.applying = true;
    fixture.detectChanges();

    expect(Array.from(host.querySelectorAll('button')).every(candidate => candidate.disabled)).toBe(true);
  });

  /**
   * Leaving the page discards an in-flight preview: a response arriving afterwards must not land on
   * a destroyed view, and must never leave an applicable preview behind.
   */
  it('discards a preview response that arrives after the component is destroyed', async () => {
    const responses = new Subject<HistoricalGstClassificationPreview>();
    const { fixture, component } = await render(jest.fn(() => responses.asObservable()));

    component.runPreview();
    fixture.destroy();
    responses.next(preview());

    expect(component.preview).toBeNull();
    expect(component.loading).toBe(false);
  });
});
