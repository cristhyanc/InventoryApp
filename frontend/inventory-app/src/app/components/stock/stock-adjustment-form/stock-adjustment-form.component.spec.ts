import { SimpleChange } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { StockAdjustmentFormComponent } from './stock-adjustment-form.component';
import { StockService } from '../../../services/stock.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { Machine, RestockCostSuggestion, StockAdjustment, StockAdjustmentReason } from '../../../models/models';

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
 * The machine field depends on the page having a machine list at all (issue #728): without a
 * provider, or after a machine read the page could not complete, there is nothing to attribute an
 * adjustment to and "— None —" alone is not a choice.
 */
describe('StockAdjustmentFormComponent machine field (issue #728)', () => {
  async function render(machines: Machine[]): Promise<HTMLElement> {
    await TestBed.configureTestingModule({
      imports: [StockAdjustmentFormComponent],
      providers: [
        { provide: StockService, useValue: { adjust: jest.fn(() => of({})), restockCostSuggestion: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(StockAdjustmentFormComponent);
    fixture.componentRef.setInput('productId', 7);
    fixture.componentRef.setInput('machines', machines);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('offers the machine field while the page has machines to offer', async () => {
    const host = await render([{ machineID: 9, localMachineId: 109, machineName: 'Lobby' } as Machine]);

    expect(host.querySelector('#adjustment-machine')).not.toBeNull();
  });

  it('hides the machine field when no machine list is available', async () => {
    const host = await render([]);

    expect(host.querySelector('#adjustment-machine')).toBeNull();
  });

  it('drops a staged machine when the list it came from goes away', () => {
    const { component } = createHarness();
    component.machines = [{ machineID: 9, localMachineId: 109, machineName: 'Lobby' } as Machine];
    component.form.machineId = 109;

    component.machines = [];
    component.ngOnChanges({ machines: new SimpleChange([{ machineID: 9, localMachineId: 109 }], [], false) });

    expect(component.form.machineId).toBeNull();
  });

  it('keeps a staged machine while the list still holds it', () => {
    const { component } = createHarness();
    component.machines = [{ machineID: 9, localMachineId: 109, machineName: 'Lobby' } as Machine];
    component.form.machineId = 109;

    component.ngOnChanges({ machines: new SimpleChange([], [{ machineID: 9, localMachineId: 109 }], false) });

    expect(component.form.machineId).toBe(109);
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
