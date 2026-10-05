import { SimpleChange } from '@angular/core';
import { of, throwError } from 'rxjs';
import { StockAdjustmentFormComponent } from './stock-adjustment-form.component';
import { StockService } from '../../../services/stock.service';
import { RestockCostSuggestion, StockAdjustment, StockAdjustmentReason } from '../../../models/models';

interface Harness {
  component: StockAdjustmentFormComponent;
  adjust: jest.Mock;
  restockCostSuggestion: jest.Mock;
  applied: jest.Mock;
}

function createHarness(options: { adjustResult?: unknown; suggestion?: RestockCostSuggestion | null } = {}): Harness {
  const adjust = jest.fn(() => (options.adjustResult === undefined ? of({} as StockAdjustment) : (options.adjustResult as never)));
  const restockCostSuggestion = jest.fn(() =>
    options.suggestion === null
      ? throwError(() => new Error('no suggestion'))
      : of(options.suggestion ?? { unitCost: 1.25, source: 'LastPurchase', purchaseDate: '2026-01-01' })
  );
  const component = new StockAdjustmentFormComponent({ adjust, restockCostSuggestion } as unknown as StockService);
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
    component.form.machineId = 9;

    component.adjust();

    expect(adjust).toHaveBeenCalledWith(7, expect.objectContaining({ quantityChange: -3, unitCost: null, machineId: 9 }));
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
