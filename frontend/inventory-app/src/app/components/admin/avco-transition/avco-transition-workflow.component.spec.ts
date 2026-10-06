import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AvcoTransitionWorkflowComponent } from './avco-transition-workflow.component';
import { ToastService } from '../../../services/toast.service';
import {
  InventoryCostBaselineSource,
  InventoryCostTransitionBatchPreview,
  InventoryCostTransitionPreview,
  InventoryCostTransitionService
} from '../../../services/inventory-cost-transition.service';
import { Product } from '../../../models/models';

function transitionPreview(overrides: Partial<InventoryCostTransitionPreview> = {}): InventoryCostTransitionPreview {
  return {
    previewId: 'p1', productId: 1, productName: 'Coke', homeStockQuantity: 5, machineStocks: [],
    machineStockQuantity: 3, openingCostingQuantity: 8, averageUnitCost: 1.5, inventoryValue: 12,
    cutoffAt: '2026-06-15T00:00:00Z', costSource: InventoryCostBaselineSource.ManualAuthoritative,
    legacyReplayedPhysicalQuantity: 0, legacyPhysicalDiscrepancy: 0, dataQualityNote: '',
    ...overrides
  };
}

function batchPreview(overrides: Partial<InventoryCostTransitionBatchPreview> = {}): InventoryCostTransitionBatchPreview {
  return {
    previewId: 'batch', cutoffAt: '2026-06-15T00:00:00Z', costSource: InventoryCostBaselineSource.ManualAuthoritative,
    products: [], productCount: 0, homeStockQuantity: 0, machineStockQuantity: 0, openingCostingQuantity: 0, inventoryValue: 0,
    ...overrides
  };
}

interface TransitionHarness {
  preview: jest.Mock;
  apply: jest.Mock;
  previewAll: jest.Mock;
  applyAll: jest.Mock;
}

async function render(overrides: Partial<TransitionHarness> = {}, products: Product[] = []) {
  const transition = {
    preview: jest.fn(() => of(transitionPreview())),
    apply: jest.fn(() => of(transitionPreview())),
    previewAll: jest.fn(() => of(batchPreview())),
    applyAll: jest.fn(() => of(batchPreview({ productCount: 2 }))),
    ...overrides
  };
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  await TestBed.configureTestingModule({
    imports: [AvcoTransitionWorkflowComponent],
    providers: [
      { provide: ToastService, useValue: toast },
      { provide: InventoryCostTransitionService, useValue: transition }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AvcoTransitionWorkflowComponent);
  fixture.componentRef.setInput('products', products);
  const baselinesSaved = jest.fn();
  fixture.componentInstance.baselinesSaved.subscribe(baselinesSaved);
  fixture.detectChanges();
  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    transition,
    toast,
    baselinesSaved
  };
}

function product(id: number, name: string, averageUnitCost: number): Product {
  return { id, name, averageUnitCost } as Product;
}

// The `window.confirm` spies are restored here, after each test's assertions: restoring them
// inside the test would also clear the recorded calls those assertions check.
afterEach(() => jest.restoreAllMocks());

/**
 * Moved from `avco-transition.component.spec.ts` with the workflow itself when the preview/apply
 * workflow was extracted from the routed page into this feature component (docs/architecture.md
 * § Page composition boundary, issue #191); the assertions are unchanged. The page's own
 * responsibilities - loading the products and reloading them after a save - stay asserted in
 * `avco-transition.component.spec.ts`.
 */
describe('AvcoTransitionWorkflowComponent product selection (issue #390)', () => {
  it("prefills the verified opening cost with the selected product's current average unit cost and discards any preview", async () => {
    const { component } = await render({}, [product(1, 'Coke', 1.25)]);
    component.transitionPreview = transitionPreview();
    component.transitionBatchPreview = batchPreview();

    component.transitionProductId = 1;
    component.selectTransitionProduct();

    expect(component.transitionAverageUnitCost).toBe(1.25);
    expect(component.transitionPreview).toBeNull();
    expect(component.transitionBatchPreview).toBeNull();
  });

  it('offers every product the host page supplies in the selector', async () => {
    const { host } = await render({}, [product(1, 'Coke', 1.25), product(2, 'Chips', 0.9)]);

    const options = Array.from(host.querySelectorAll('option')).map(option => option.textContent?.trim());
    expect(options).toContain('Coke');
    expect(options).toContain('Chips');
  });

  it('discards any preview when the scope switches between one product and all products', async () => {
    const { component } = await render();
    component.transitionPreview = transitionPreview();
    component.transitionBatchPreview = batchPreview();

    component.changeTransitionScope();

    expect(component.transitionPreview).toBeNull();
    expect(component.transitionBatchPreview).toBeNull();
  });
});

describe('AvcoTransitionWorkflowComponent single-product preview (issue #390)', () => {
  it('previews through InventoryCostTransitionService with the selected product, verified cost and cost source', async () => {
    const { component, transition } = await render({}, [product(1, 'Coke', 1.25)]);
    component.transitionProductId = 1;
    component.transitionAverageUnitCost = 1.25;
    component.transitionCostSource = InventoryCostBaselineSource.ManualEstimated;

    component.previewTransition();

    expect(transition.preview).toHaveBeenCalledWith(1, 1.25, InventoryCostBaselineSource.ManualEstimated);
    expect(transition.previewAll).not.toHaveBeenCalled();
    expect(component.loading).toBe(false);
  });

  it('refuses to preview without a product or with a negative verified cost, and calls no API', async () => {
    const { component, transition, toast } = await render();
    component.transitionProductId = null;

    component.previewTransition();

    expect(transition.preview).not.toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalledWith('Select a product and enter a non-negative verified average unit cost.');

    component.transitionProductId = 1;
    component.transitionAverageUnitCost = -1;
    component.previewTransition();

    expect(transition.preview).not.toHaveBeenCalled();
  });

  it('shows the proposed opening position, the data-quality note and the cost-source label', async () => {
    const preview = transitionPreview({
      costSource: InventoryCostBaselineSource.ManualEstimated,
      dataQualityNote: 'Legacy movements before the cutoff are incomplete.',
      machineStocks: [{ machineId: 4, machineName: 'Machine A', stockQuantity: 3, source: 'Nayax' }]
    });
    const { component, fixture, host } = await render({ preview: jest.fn(() => of(preview)) }, [product(1, 'Coke', 1.25)]);
    component.transitionProductId = 1;

    component.previewTransition();
    fixture.detectChanges();

    const text = host.textContent ?? '';
    expect(text).toContain('Total proposed CostingQuantity');
    expect(text).toContain('Legacy movements before the cutoff are incomplete.');
    expect(text).toContain('Estimated');
    expect(text).toContain('Machine A');
    expect(component.transitionSourceLabel(InventoryCostBaselineSource.ManualAuthoritative)).toBe('Authoritative');
    expect(component.transitionSourceLabel(InventoryCostBaselineSource.ManualEstimated)).toBe('Estimated');
  });

  // The data-quality note is rendered whenever a single-product preview is shown, with no condition
  // of its own, so restyling it cannot start hiding it for an empty note.
  it('renders the data-quality note element even when the note is empty', async () => {
    const preview = transitionPreview({ dataQualityNote: '' });
    const { component, fixture, host } = await render({ preview: jest.fn(() => of(preview)) }, [product(1, 'Coke', 1.25)]);
    component.transitionProductId = 1;

    component.previewTransition();
    fixture.detectChanges();

    const note = host.querySelector('.alert-warning');
    expect(note).not.toBeNull();
    expect(note?.textContent?.trim()).toBe('');
  });

  it('reports the API message and clears loading when the preview fails', async () => {
    const preview = jest.fn(() => throwError(() => ({ error: { message: 'A baseline already exists for this product.' } })));
    const { component, toast } = await render({ preview }, [product(1, 'Coke', 1.25)]);
    component.transitionProductId = 1;

    component.previewTransition();

    expect(toast.error).toHaveBeenCalledWith('A baseline already exists for this product.');
    expect(component.loading).toBe(false);
  });

  it('falls back to the generic preview message when the API returns no message', async () => {
    const preview = jest.fn(() => throwError(() => ({})));
    const { component, toast } = await render({ preview }, [product(1, 'Coke', 1.25)]);
    component.transitionProductId = 1;

    component.previewTransition();

    expect(toast.error).toHaveBeenCalledWith('Unable to preview the transition baseline.');
  });
});

describe('AvcoTransitionWorkflowComponent single-product apply confirmation (issue #390)', () => {
  it('saves the previewed baseline only after the operator confirms, and asks the page to reload the products', async () => {
    const { component, transition, toast, baselinesSaved } = await render({}, [product(1, 'Coke', 1.25)]);
    const preview = transitionPreview();
    component.transitionPreview = preview;
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);

    component.applyTransition();

    expect(confirm).toHaveBeenCalledWith(
      'Save this exact opening quantity, cost, machine-stock snapshot, and cutoff as the permanent AVCO transition baseline?'
    );
    expect(transition.apply).toHaveBeenCalledWith(preview);
    expect(component.transitionPreview).toBeNull();
    expect(toast.success).toHaveBeenCalledWith('Inventory AVCO transition baseline saved.');
    expect(baselinesSaved).toHaveBeenCalledTimes(1);
  });

  it('does not apply when the confirmation is dismissed, and keeps the preview', async () => {
    const { component, transition } = await render();
    component.transitionPreview = transitionPreview();
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);

    component.applyTransition();

    expect(confirm).toHaveBeenCalledTimes(1);
    expect(transition.apply).not.toHaveBeenCalled();
    expect(component.transitionPreview).not.toBeNull();
  });

  it('never applies without a preview, even if the action is invoked', async () => {
    const { component, transition } = await render();
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);

    component.applyTransition();

    // No preview means no confirmation prompt and no apply call at all.
    expect(confirm).not.toHaveBeenCalled();
    expect(transition.apply).not.toHaveBeenCalled();
  });

  it('reports the API message and clears loading when the apply fails', async () => {
    const apply = jest.fn(() => throwError(() => ({ error: 'The preview is stale. Preview again.' })));
    const { component, toast, baselinesSaved } = await render({ apply });
    component.transitionPreview = transitionPreview();
    jest.spyOn(window, 'confirm').mockReturnValue(true);

    component.applyTransition();

    expect(toast.error).toHaveBeenCalledWith('The preview is stale. Preview again.');
    expect(component.loading).toBe(false);
    expect(baselinesSaved).not.toHaveBeenCalled();
  });
});

describe('AvcoTransitionWorkflowComponent all-products preview and apply (issue #390)', () => {
  it('previews every product without a baseline through previewAll and renders the batch totals', async () => {
    const batch = batchPreview({
      productCount: 1,
      homeStockQuantity: 5,
      machineStockQuantity: 3,
      openingCostingQuantity: 8,
      inventoryValue: 12,
      products: [transitionPreview()]
    });
    const { component, fixture, host, transition } = await render({ previewAll: jest.fn(() => of(batch)) });
    component.transitionAllProducts = true;
    component.transitionCostSource = InventoryCostBaselineSource.ManualAuthoritative;

    component.previewTransition();
    fixture.detectChanges();

    expect(transition.previewAll).toHaveBeenCalledWith(InventoryCostBaselineSource.ManualAuthoritative);
    expect(transition.preview).not.toHaveBeenCalled();
    const text = host.textContent ?? '';
    expect(text).toContain('Total CostingQuantity');
    expect(text).toContain('Coke');
    expect(text).toContain('Authoritative');
  });

  it('saves all previewed baselines only after a confirmation that states how many products are affected', async () => {
    const { component, transition, toast, baselinesSaved } = await render();
    const batch = batchPreview({ productCount: 2 });
    component.transitionAllProducts = true;
    component.transitionBatchPreview = batch;
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);

    component.applyAllTransitions();

    expect(confirm).toHaveBeenCalledWith('Save the displayed AVCO transition baselines for all 2 products?');
    expect(transition.applyAll).toHaveBeenCalledWith(batch);
    expect(component.transitionBatchPreview).toBeNull();
    expect(toast.success).toHaveBeenCalledWith('2 inventory AVCO transition baselines saved.');
    expect(baselinesSaved).toHaveBeenCalledTimes(1);
  });

  it('does not apply all when the confirmation is dismissed', async () => {
    const { component, transition } = await render();
    component.transitionBatchPreview = batchPreview({ productCount: 2 });
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);

    component.applyAllTransitions();

    expect(confirm).toHaveBeenCalledTimes(1);
    expect(transition.applyAll).not.toHaveBeenCalled();
    expect(component.transitionBatchPreview).not.toBeNull();
  });

  it('reports the API message and clears loading when the batch preview or apply fails', async () => {
    const previewAll = jest.fn(() => throwError(() => ({ error: { title: 'Nayax is unavailable.' } })));
    const applyAll = jest.fn(() => throwError(() => ({})));
    const { component, toast } = await render({ previewAll, applyAll });
    component.transitionAllProducts = true;

    component.previewTransition();

    expect(toast.error).toHaveBeenCalledWith('Nayax is unavailable.');
    expect(component.loading).toBe(false);

    component.transitionBatchPreview = batchPreview({ productCount: 2 });
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    component.applyAllTransitions();

    expect(toast.error).toHaveBeenCalledWith('Unable to save all transition baselines.');
    expect(component.loading).toBe(false);
  });
});
