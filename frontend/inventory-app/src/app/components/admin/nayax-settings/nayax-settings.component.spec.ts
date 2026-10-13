import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { NayaxSettingsComponent } from './nayax-settings.component';
import { NayaxConnectionWizardComponent } from './nayax-connection-wizard.component';
import { AccessService } from '../../../access/access.service';
import { NayaxConnectionService } from '../../../services/nayax-connection.service';
import { NayaxProcessingFeeRate, NayaxSettingsService } from '../../../services/nayax-settings.service';
import { ToastService } from '../../../services/toast.service';

function rate(overrides: Partial<NayaxProcessingFeeRate> = {}): NayaxProcessingFeeRate {
  return { id: 1, effectiveFrom: '2026-01-01', feeExGst: 0.17, ...overrides };
}

async function render(nayaxSettings: Partial<NayaxSettingsService>, toast?: Partial<ToastService>) {
  // The connection wizard (issue #538) is composed by this page and tested in its own spec; the
  // page only has to supply it, so its collaborators are stubbed at their narrowest here.
  const connections = {
    test: jest.fn(),
    save: jest.fn(),
    retest: jest.fn(),
    status: jest.fn(() => of({ status: 'NotConfigured' as const, machineChecksPending: false }))
  };

  await TestBed.configureTestingModule({
    imports: [NayaxSettingsComponent],
    providers: [
      provideRouter([]),
      { provide: NayaxSettingsService, useValue: nayaxSettings },
      { provide: NayaxConnectionService, useValue: connections },
      { provide: AccessService, useValue: { allows: () => of(true) } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(NayaxSettingsComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, connections };
}

describe('NayaxSettingsComponent (issue #388)', () => {
  it('loads the configured rate history on initialization and pre-fills the current rate', async () => {
    const getRates = jest.fn(() => of([rate({ feeExGst: 0.21 })]));
    const { fixture, host } = await render({ getRates, saveRate: jest.fn() });

    expect(getRates).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.feeExGst).toBe(0.21);
    expect(host.textContent).toContain('Current');
  });

  it('shows an empty state and reports a toast error when loading fails', async () => {
    const error = jest.fn();
    const { host } = await render({ getRates: () => throwError(() => new Error('boom')), saveRate: jest.fn() }, { error });

    expect(error).toHaveBeenCalledWith('Failed to load Nayax settings.');
    expect(host.textContent).toContain('No Nayax processing-fee settings configured.');
  });

  it('rejects saving a negative fee without calling the API', async () => {
    const saveRate = jest.fn();
    const error = jest.fn();
    const { fixture } = await render({ getRates: jest.fn(() => of([])), saveRate }, { error });

    fixture.componentInstance.feeExGst = -1;
    fixture.componentInstance.saveFeeRate();

    expect(saveRate).not.toHaveBeenCalled();
    expect(error).toHaveBeenCalledWith('Enter a non-negative fee and effective date.');
  });

  it('saves the rate, shows a success toast, and reloads the configured rates', async () => {
    const saveRate = jest.fn(() => of(rate()));
    const getRates = jest.fn(() => of([]));
    const success = jest.fn();
    const { fixture } = await render({ getRates, saveRate }, { success });

    fixture.componentInstance.feeExGst = 0.19;
    fixture.componentInstance.effectiveFrom = '2026-02-01';
    fixture.componentInstance.saveFeeRate();

    expect(saveRate).toHaveBeenCalledWith({ effectiveFrom: '2026-02-01', feeExGst: 0.19 });
    expect(success).toHaveBeenCalledWith('Nayax processing-fee rate saved.');
    expect(getRates).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.loading).toBe(false);
  });

  /**
   * The page is a composition boundary (docs/architecture.md § Page composition boundary, issue
   * #191): the connection wizard of issue #538 owns the test, save and re-test workflow, and the
   * fee-rate editor it was added beside stays exactly where it was.
   */
  it('composes the connection wizard beside the fee-rate editor it did not replace', async () => {
    const { fixture, host } = await render({ getRates: jest.fn(() => of([rate()])), saveRate: jest.fn() });

    const wizard = fixture.debugElement.query(By.directive(NayaxConnectionWizardComponent));
    expect(wizard).toBeTruthy();
    expect(host.querySelector('#nayax-fee-ex-gst')).toBeTruthy();
    expect(host.textContent).toContain('Estimated processing fee rate');
  });

  it('reads the stored connection state through the wizard, and tests nothing on arrival', async () => {
    const { connections } = await render({ getRates: jest.fn(() => of([])), saveRate: jest.fn() });

    expect(connections.status).toHaveBeenCalledTimes(1);
    expect(connections.test).not.toHaveBeenCalled();
    expect(connections.save).not.toHaveBeenCalled();
    expect(connections.retest).not.toHaveBeenCalled();
  });

  it('reports an API error on save failure without reloading', async () => {
    const getRates = jest.fn(() => of([]));
    const saveRate = jest.fn(() => throwError(() => ({ error: 'Rate already configured for that date.' })));
    const error = jest.fn();
    const { fixture } = await render({ getRates, saveRate }, { error });

    fixture.componentInstance.feeExGst = 0.19;
    fixture.componentInstance.saveFeeRate();

    expect(error).toHaveBeenCalledWith('Rate already configured for that date.');
    expect(getRates).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.loading).toBe(false);
  });
});
