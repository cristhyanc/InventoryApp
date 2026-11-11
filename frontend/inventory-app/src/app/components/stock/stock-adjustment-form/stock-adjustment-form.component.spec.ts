import { SimpleChange } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { StockAdjustmentFormComponent } from './stock-adjustment-form.component';
import { StockService } from '../../../services/stock.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { LocalMachine, RestockCostSuggestion, StockAdjustment, StockAdjustmentReason } from '../../../models/models';

function localMachine(id: number, name: string, isActive = true): LocalMachine {
  return { id, name, isActive, isProviderNamed: false };
}

interface Harness {
  component: StockAdjustmentFormComponent;
  adjust: jest.Mock;
  restockCostSuggestion: jest.Mock;
  applied: jest.Mock;
}

function createHarness(
  options: { adjustResult?: unknown; suggestion?: RestockCostSuggestion | null; sessionActive?: Observable<boolean> } = {}
): Harness {
  const adjust = jest.fn(() => (options.adjustResult === undefined ? of({} as StockAdjustment) : (options.adjustResult as never)));
  const restockCostSuggestion = jest.fn(() =>
    options.suggestion === null
      ? throwError(() => new Error('no suggestion'))
      : of(options.suggestion ?? { unitCost: 1.25, source: 'LastPurchase', purchaseDate: '2026-01-01' })
  );
  const component = new StockAdjustmentFormComponent(
    { adjust, restockCostSuggestion } as unknown as StockService,
    { active$: options.sessionActive ?? of(false) } as unknown as SupportSessionService
  );
  component.productId = 7;
  const applied = jest.fn();
  component.adjustmentApplied.subscribe(applied);

  return { component, adjust, restockCostSuggestion, applied };
}

describe('StockAdjustmentFormComponent restock cost', () => {
  it('asks the existing suggestion endpoint once a positive restock quantity is entered', () => {
    const { component, restockCostSuggestion } = createHarness();

    component.form.quantityChange = 12;
    component.onAdjustmentInputsChanged();

    expect(restockCostSuggestion).toHaveBeenCalledWith(7);
    expect(component.requiresRestockCost).toBe(true);
    expect(component.form.unitCost).toBe(1.25);
    expect(component.restockCostHelp).toContain('Last purchase cost: $1.25');
  });

  it('explains that no previous cost is available when the suggestion fails', () => {
    const { component } = createHarness({ suggestion: null });

    component.form.quantityChange = 12;
    component.onAdjustmentInputsChanged();

    expect(component.form.unitCost).toBeNull();
    expect(component.restockCostHelp).toContain('No previous purchase cost is available');
  });

  it('drops the cost again when the movement stops being a positive restock', () => {
    const { component } = createHarness();
    component.form.quantityChange = 12;
    component.onAdjustmentInputsChanged();

    component.form.reason = StockAdjustmentReason.Damaged;
    component.onAdjustmentInputsChanged();

    expect(component.requiresRestockCost).toBe(false);
    expect(component.form.unitCost).toBeNull();
  });

  it('refuses a positive restock with no unit cost, and a negative one, without calling the API', () => {
    const { component, adjust } = createHarness();
    component.form.quantityChange = 12;
    component.onAdjustmentInputsChanged();
    component.form.unitCost = null;

    component.adjust();
    expect(adjust).not.toHaveBeenCalled();
    expect(component.error).toContain('Unit cost is required');

    component.form.unitCost = -1;
    component.adjust();
    expect(adjust).not.toHaveBeenCalled();
    expect(component.error).toContain('cannot be negative');
  });

  it('sends no unit cost for a movement that does not require one', () => {
    const { component, adjust } = createHarness();
    component.form.quantityChange = -3;
    component.form.reason = StockAdjustmentReason.MachineRefill;
    component.form.machineId = 109;

    component.adjust();

    expect(adjust).toHaveBeenCalledWith(7, expect.objectContaining({ quantityChange: -3, unitCost: null, machineId: 109 }));
  });
});

describe('StockAdjustmentFormComponent validation', () => {
  it('refuses a missing or zero quantity', () => {
    const { component, adjust } = createHarness();

    component.adjust();
    expect(adjust).not.toHaveBeenCalled();
    expect(component.error).toContain('non-zero quantity');

    component.form.quantityChange = 0;
    component.adjust();
    expect(adjust).not.toHaveBeenCalled();
  });

  it('records a correction as a negative movement whatever sign was typed', () => {
    const { component, adjust } = createHarness();
    component.form.reason = StockAdjustmentReason.Correction;
    component.form.quantityChange = -4;
    component.onAdjustmentInputsChanged();

    expect(component.form.quantityChange).toBe(4);

    component.adjust();

    expect(adjust).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ quantityChange: -4, reason: StockAdjustmentReason.Correction })
    );
  });

  it('asks for a positive quantity to remove when a correction has none', () => {
    const { component, adjust } = createHarness();
    component.form.reason = StockAdjustmentReason.Correction;

    component.adjust();

    expect(adjust).not.toHaveBeenCalled();
    expect(component.error).toBe('Enter a positive quantity to remove.');
  });
});

describe('StockAdjustmentFormComponent apply outcome', () => {
  it('clears the form and notifies the page on success', () => {
    const { component, adjust, applied } = createHarness();
    component.form.quantityChange = 5;
    component.onAdjustmentInputsChanged();
    component.form.notes = 'restocked';

    component.adjust();

    expect(adjust).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ quantityChange: 5, reason: StockAdjustmentReason.Restock, unitCost: 1.25, notes: 'restocked' })
    );
    expect(applied).toHaveBeenCalledTimes(1);
    expect(component.form.quantityChange).toBeNull();
    expect(component.form.notes).toBe('');
    expect(component.form.reason).toBe(StockAdjustmentReason.Restock);
    expect(component.error).toBe('');
    expect(component.saving).toBe(false);
  });

  it('shows the backend insufficient-stock message and does not report success', () => {
    const { component, applied } = createHarness({
      adjustResult: throwError(() => ({ error: { message: 'Not enough products in stock. Available stock: 2' } }))
    });
    component.form.quantityChange = -5;
    component.form.reason = StockAdjustmentReason.MachineRefill;

    component.adjust();

    expect(component.error).toBe('Not enough products in stock. Available stock: 2');
    expect(component.saving).toBe(false);
    expect(applied).not.toHaveBeenCalled();
  });

  it('also understands the plain-string error body the endpoint used to return', () => {
    const { component } = createHarness({ adjustResult: throwError(() => ({ error: 'Invalid product or resulting quantity' })) });
    component.form.quantityChange = 5;
    component.form.reason = StockAdjustmentReason.Damaged;

    component.adjust();

    expect(component.error).toBe('Invalid product or resulting quantity');
  });

  it('falls back to a readable message when the failure carries no detail', () => {
    const { component } = createHarness({ adjustResult: throwError(() => ({})) });
    component.form.quantityChange = 5;
    component.form.reason = StockAdjustmentReason.Expired;

    component.adjust();

    expect(component.error).toBe('Failed to adjust stock.');
  });
});

/**
 * Apply Adjustment while a support session is active (issue #658). A session grants
 * `InventoryOperate`, so this write would otherwise still be offered.
 */
describe('StockAdjustmentFormComponent write control during a support session (issue #658)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('does not write the adjustment while a session is active, even if adjust() is invoked directly', () => {
    const { component, adjust, applied } = createHarness({ sessionActive: of(true) });
    component.form.quantityChange = 5;

    component.adjust();

    expect(adjust).not.toHaveBeenCalled();
    expect(applied).not.toHaveBeenCalled();
  });

  it('tracks sessionActive from the shared support-session flag', () => {
    const { component } = createHarness({ sessionActive: of(true) });

    expect(component.sessionActive).toBe(true);
  });

  it('leaves the write available while no session is active', () => {
    const { component, adjust } = createHarness({ sessionActive: of(false) });
    component.form.quantityChange = -3;
    component.form.reason = StockAdjustmentReason.Damaged;

    component.adjust();

    expect(adjust).toHaveBeenCalled();
    expect(component.sessionActive).toBe(false);
  });

  async function render(sessionActive: Observable<boolean>): Promise<HTMLElement> {
    await TestBed.configureTestingModule({
      imports: [StockAdjustmentFormComponent],
      providers: [
        { provide: StockService, useValue: { adjust: jest.fn(() => of({})), restockCostSuggestion: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: sessionActive } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(StockAdjustmentFormComponent);
    fixture.componentRef.setInput('productId', 7);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('disables Apply Adjustment while a session is active', async () => {
    const host = await render(of(true));

    expect(host.querySelector<HTMLButtonElement>('[data-testid="apply-adjustment"]')?.disabled).toBe(true);
  });

  it('leaves Apply Adjustment enabled while no session is active', async () => {
    const host = await render(of(false));

    expect(host.querySelector<HTMLButtonElement>('[data-testid="apply-adjustment"]')?.disabled).toBe(false);
  });
});

/**
 * The machine field offers the business's **own** active machines (issue #746, G2b of #329), which
 * the page reads from `/api/local-machines` and hands over. It depends on no provider: what it
 * follows is whether that read produced an active machine, failed, or is still outstanding.
 */
describe('StockAdjustmentFormComponent machine field (issue #746)', () => {
  async function render(inputs: {
    machines: LocalMachine[];
    machinesFailed?: boolean;
    machinesLoading?: boolean;
  }): Promise<HTMLElement> {
    await TestBed.configureTestingModule({
      imports: [StockAdjustmentFormComponent],
      providers: [
        { provide: StockService, useValue: { adjust: jest.fn(() => of({})), restockCostSuggestion: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(StockAdjustmentFormComponent);
    fixture.componentRef.setInput('productId', 7);
    fixture.componentRef.setInput('machines', inputs.machines);
    fixture.componentRef.setInput('machinesFailed', inputs.machinesFailed ?? false);
    fixture.componentRef.setInput('machinesLoading', inputs.machinesLoading ?? false);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  const optionLabels = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('#adjustment-machine option')).map((option) => option.textContent?.trim());

  afterEach(() => TestBed.resetTestingModule());

  it('offers every active local machine under its own name', async () => {
    const host = await render({ machines: [localMachine(41, 'Lobby'), localMachine(42, 'Level 3')] });

    expect(host.querySelector('#adjustment-machine')).not.toBeNull();
    expect(optionLabels(host)).toEqual(['— None —', 'Lobby', 'Level 3']);
  });

  it('does not offer a deactivated machine beside the active ones', async () => {
    const host = await render({ machines: [localMachine(41, 'Lobby'), localMachine(42, 'Retired', false)] });

    expect(optionLabels(host)).toEqual(['— None —', 'Lobby']);
  });

  it('hides the field when every recorded machine is deactivated, with no error', async () => {
    const host = await render({ machines: [localMachine(42, 'Retired', false)] });

    expect(host.querySelector('#adjustment-machine')).toBeNull();
    expect(host.querySelector('[data-testid="adjustment-machine-error"]')).toBeNull();
  });

  it('hides the machine field when the business has recorded no machine', async () => {
    const host = await render({ machines: [] });

    expect(host.querySelector('#adjustment-machine')).toBeNull();
    expect(host.querySelector('[data-testid="adjustment-machine-error"]')).toBeNull();
  });

  it('shows an explicit error in place of the field when the machine read failed', async () => {
    const host = await render({ machines: [], machinesFailed: true });

    expect(host.querySelector('#adjustment-machine')).toBeNull();
    expect(host.querySelector('[data-testid="adjustment-machine-error"]')?.textContent).toContain(
      'The machine list could not be loaded'
    );
  });

  it('says the machines are still loading rather than hiding the field as if there were none', async () => {
    const host = await render({ machines: [], machinesLoading: true });

    expect(host.querySelector('#adjustment-machine')).toBeNull();
    expect(host.querySelector('[data-testid="adjustment-machine-loading"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="adjustment-machine-error"]')).toBeNull();
  });

  it('drops a staged machine when the list it came from goes away', () => {
    const { component } = createHarness();
    component.machines = [localMachine(41, 'Lobby')];
    component.form.machineId = 41;

    component.machines = [];
    component.ngOnChanges({ machines: new SimpleChange([localMachine(41, 'Lobby')], [], false) });

    expect(component.form.machineId).toBeNull();
  });

  it('drops a staged machine that has since been deactivated', () => {
    const { component } = createHarness();
    component.machines = [localMachine(41, 'Lobby')];
    component.form.machineId = 41;

    component.machines = [localMachine(41, 'Lobby', false)];
    component.ngOnChanges({ machines: new SimpleChange([localMachine(41, 'Lobby')], component.machines, false) });

    expect(component.form.machineId).toBeNull();
  });

  it('keeps a staged machine while the list still offers it', () => {
    const { component } = createHarness();
    component.machines = [localMachine(41, 'Lobby')];
    component.form.machineId = 41;

    component.ngOnChanges({ machines: new SimpleChange([], component.machines, false) });

    expect(component.form.machineId).toBe(41);
  });
});

describe('StockAdjustmentFormComponent product change', () => {
  it('discards an in-progress adjustment when the selected product changes', () => {
    const { component } = createHarness();
    component.form.quantityChange = 12;
    component.onAdjustmentInputsChanged();
    component.error = 'stale error';

    component.productId = 8;
    component.ngOnChanges({ productId: new SimpleChange(7, 8, false) });

    expect(component.form.quantityChange).toBeNull();
    expect(component.form.unitCost).toBeNull();
    expect(component.restockCostSuggestion).toBeNull();
    expect(component.error).toBe('');
  });

  it('keeps the form on the first binding of the product', () => {
    const { component } = createHarness();
    component.form.quantityChange = 12;

    component.ngOnChanges({ productId: new SimpleChange(undefined, 7, true) });

    expect(component.form.quantityChange).toBe(12);
  });
});

/**
 * Issue #1156: the machine field is enabled only for a Machine refill, and a machine chosen under
 * Machine refill is never submitted with another reason.
 */
describe('StockAdjustmentFormComponent machine applies only to a machine refill (issue #1156)', () => {
  it('treats the machine as applicable only while the reason is Machine refill', () => {
    const { component } = createHarness();

    expect(component.form.reason).toBe(StockAdjustmentReason.Restock);
    expect(component.machineApplicable).toBe(false);

    component.form.reason = StockAdjustmentReason.MachineRefill;
    expect(component.machineApplicable).toBe(true);
  });

  it('drops a machine chosen under Machine refill when the reason changes away from it', () => {
    const { component } = createHarness();
    component.form.reason = StockAdjustmentReason.MachineRefill;
    component.onAdjustmentInputsChanged();
    component.form.machineId = 41;

    component.form.reason = StockAdjustmentReason.Damaged;
    component.onAdjustmentInputsChanged();

    expect(component.form.machineId).toBeNull();
  });

  it('never submits a machine with a reason other than Machine refill', () => {
    const { component, adjust } = createHarness();
    component.form.quantityChange = -2;
    component.form.reason = StockAdjustmentReason.Expired;
    // Staged directly, as a stale value would be, without the reason-change handler running.
    component.form.machineId = 41;

    component.adjust();

    expect(adjust).toHaveBeenCalledWith(7, expect.objectContaining({ reason: StockAdjustmentReason.Expired, machineId: null }));
  });

  describe('rendered', () => {
    async function render(): Promise<{ host: HTMLElement; component: StockAdjustmentFormComponent; detect: () => void; stable: () => Promise<unknown> }> {
      await TestBed.configureTestingModule({
        imports: [StockAdjustmentFormComponent],
        providers: [
          { provide: StockService, useValue: { adjust: jest.fn(() => of({})), restockCostSuggestion: jest.fn(() => of({ unitCost: 1, source: 'LastPurchase', purchaseDate: null })) } },
          { provide: SupportSessionService, useValue: { active$: of(false) } }
        ]
      }).compileComponents();

      const fixture = TestBed.createComponent(StockAdjustmentFormComponent);
      fixture.componentRef.setInput('productId', 7);
      fixture.componentRef.setInput('machines', [localMachine(41, 'Lobby')]);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      return { host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance, detect: () => fixture.detectChanges(), stable: () => fixture.whenStable() };
    }

    afterEach(() => TestBed.resetTestingModule());

    it('disables the machine field until Machine refill is chosen, and says why', async () => {
      const { host, component, detect, stable } = await render();
      const field = () => host.querySelector<HTMLSelectElement>('#adjustment-machine')!;

      expect(field().disabled).toBe(true);
      expect(host.querySelector('[data-testid="adjustment-machine-hint"]')?.textContent).toContain('Only a Machine refill');

      const reason = host.querySelector<HTMLSelectElement>('#adjustment-reason')!;
      reason.value = reason.options[5].value;
      reason.dispatchEvent(new Event('change'));
      detect();
      await stable();
      detect();

      expect(component.form.reason).toBe(StockAdjustmentReason.MachineRefill);
      expect(field().disabled).toBe(false);
    });

    it('explains signed quantities', async () => {
      const { host } = await render();

      expect(host.querySelector('[data-testid="adjustment-quantity-hint"]')?.textContent).toContain('negative number removes');
    });
  });
});

describe('StockAdjustmentFormComponent submission (issue #1156)', () => {
  it('keeps everything entered when the save fails, with the server message', () => {
    const { component, applied } = createHarness({
      adjustResult: throwError(() => ({ error: { title: 'Insufficient stock.' } }))
    });
    component.form.quantityChange = -40;
    component.form.reason = StockAdjustmentReason.Damaged;
    component.form.notes = 'Crushed cartons';
    component.form.EatBefore = '2026-12-01';

    component.adjust();

    expect(component.error).toBe('Insufficient stock.');
    expect(component.saving).toBe(false);
    expect(component.form).toEqual(
      expect.objectContaining({
        quantityChange: -40,
        reason: StockAdjustmentReason.Damaged,
        notes: 'Crushed cartons',
        EatBefore: '2026-12-01'
      })
    );
    expect(applied).not.toHaveBeenCalled();
  });

  it('records one movement however many times Apply is pressed while the first is saving', () => {
    const pending = new Subject<StockAdjustment>();
    const { component, adjust, applied } = createHarness({ adjustResult: pending.asObservable() });
    const saving = jest.fn();
    component.savingChange.subscribe(saving);
    component.form.quantityChange = -3;
    component.form.reason = StockAdjustmentReason.Damaged;

    component.adjust();
    component.adjust();

    expect(adjust).toHaveBeenCalledTimes(1);
    expect(saving).toHaveBeenLastCalledWith(true);

    pending.next({} as StockAdjustment);
    pending.complete();

    expect(applied).toHaveBeenCalledTimes(1);
    expect(saving).toHaveBeenLastCalledWith(false);
  });

  it('cancels only while nothing is being saved', () => {
    const pending = new Subject<StockAdjustment>();
    const { component } = createHarness({ adjustResult: pending.asObservable() });
    const cancelled = jest.fn();
    component.cancelled.subscribe(cancelled);

    component.form.quantityChange = -3;
    component.form.reason = StockAdjustmentReason.Damaged;
    component.adjust();
    component.cancel();
    expect(cancelled).not.toHaveBeenCalled();

    pending.error({ error: 'Failed.' });
    component.cancel();
    expect(cancelled).toHaveBeenCalledTimes(1);
  });
});
