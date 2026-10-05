import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AvcoTransitionComponent } from './avco-transition.component';
import { AvcoTransitionWorkflowComponent } from './avco-transition-workflow.component';
import { ProductService } from '../../../services/product.service';
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

async function render(
  products: Product[] = [],
  getAll: jest.Mock = jest.fn(() => of(products))
) {
  const transition = {
    preview: jest.fn(() => of(transitionPreview())),
    apply: jest.fn(() => of(transitionPreview())),
    previewAll: jest.fn(() => of(batchPreview())),
    applyAll: jest.fn(() => of(batchPreview({ productCount: 2 })))
  };
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  await TestBed.configureTestingModule({
    imports: [AvcoTransitionComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getAll } },
      { provide: ToastService, useValue: toast },
      { provide: InventoryCostTransitionService, useValue: transition }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AvcoTransitionComponent);
  fixture.detectChanges();
  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    workflow: fixture.debugElement.query(By.directive(AvcoTransitionWorkflowComponent))
      ?.componentInstance as AvcoTransitionWorkflowComponent,
    getAll
  };
}

function product(id: number, name: string, averageUnitCost: number): Product {
  return { id, name, averageUnitCost } as Product;
}

/**
 * The page is a composition boundary (docs/architecture.md § Page composition boundary, issue
 * #191): it loads the products and hosts `AvcoTransitionWorkflowComponent`, which owns the
 * preview/apply workflow and is tested in `avco-transition-workflow.component.spec.ts`.
 */
describe('AvcoTransitionComponent page composition (issues #390, #191)', () => {
  it('composes the AVCO transition workflow component instead of owning the workflow itself', async () => {
    const { workflow } = await render([product(1, 'Coke', 1.25)]);

    expect(workflow).toBeTruthy();
    expect(workflow.products).toEqual([product(1, 'Coke', 1.25)]);
  });

  it('loads the product list the single-product baseline selects from', async () => {
    const { component, getAll } = await render([product(1, 'Coke', 1.25)]);

    expect(getAll).toHaveBeenCalledTimes(1);
    expect(component.products).toEqual([product(1, 'Coke', 1.25)]);
  });

  it('leaves the product list empty when it cannot be loaded', async () => {
    const { component, workflow } = await render([], jest.fn(() => throwError(() => new Error('offline'))));

    expect(component.products).toEqual([]);
    expect(workflow.products).toEqual([]);
  });

  it('reloads the products when the workflow reports a saved baseline', async () => {
    const { fixture, component, workflow, getAll } = await render([product(1, 'Coke', 1.25)]);
    getAll.mockReturnValue(of([product(1, 'Coke', 1.75)]));

    workflow.baselinesSaved.emit();
    fixture.detectChanges();

    expect(getAll).toHaveBeenCalledTimes(2);
    expect(component.products).toEqual([product(1, 'Coke', 1.75)]);
    expect(workflow.products).toEqual([product(1, 'Coke', 1.75)]);
  });
});

/**
 * Moved from `admin.component.spec.ts` with the AVCO Transition workflow (issue #390); the
 * cutoff-display assertions of issue #232 are unchanged. They stay on the page spec and render
 * through the composed workflow component, which is where the cutoff markup now lives.
 */
describe('AvcoTransitionComponent inventory-cost transition cutoff timestamp display (issue #232)', () => {
  it('renders the single-product preview cutoff as Australia/Canberra local time during AEST (UTC+10), not raw UTC', async () => {
    const { fixture, host, workflow } = await render();
    workflow.transitionPreview = transitionPreview({ cutoffAt: '2026-06-15T00:00:00Z' });
    fixture.detectChanges();

    expect(host.textContent).toContain('15/06/2026, 10:00 am');
  });

  it('renders the single-product preview cutoff as Australia/Canberra local time during AEDT (UTC+11), not raw UTC', async () => {
    const { fixture, host, workflow } = await render();
    workflow.transitionPreview = transitionPreview({ cutoffAt: '2026-01-15T00:00:00Z' });
    fixture.detectChanges();

    expect(host.textContent).toContain('15/01/2026, 11:00 am');
  });

  it('renders the batch preview cutoff as Australia/Canberra local time, not raw UTC', async () => {
    const { fixture, host, workflow } = await render();
    workflow.transitionBatchPreview = batchPreview({ cutoffAt: '2026-06-15T00:00:00Z' });
    fixture.detectChanges();

    expect(host.textContent).toContain('15/06/2026, 10:00 am');
  });
});
