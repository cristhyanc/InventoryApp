import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { CostingRepairComponent } from './costing-repair.component';
import {
  InventoryCostRepairApplied,
  InventoryCostRepairPreview,
  InventoryCostRepairRecord,
  InventoryCostRepairRequest,
  InventoryCostRepairService
} from '../../../services/inventory-cost-repair.service';
import { ToastService } from '../../../services/toast.service';
import { Product } from '../../../models/models';

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Coke Zero',
    unitPrice: 4,
    averageUnitCost: 1.5,
    machinePrice: 4,
    commissionValue: 0,
    suggestedNetValue: 0,
    suggestedPriceValue: 0,
    quantityInStock: 10,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 0,
    restockTo: 0,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    ...overrides
  };
}

function preview(overrides: Partial<InventoryCostRepairPreview> = {}): InventoryCostRepairPreview {
  return {
    productId: 1,
    productName: 'Coke Zero',
    effectiveAt: '2026-01-02T12:00:00Z',
    quantity: 4,
    unitCost: 2,
    totalValue: 8,
    reason: 'Machine stock at the 2026 cutover was never costed.',
    costingQuantityBefore: 0,
    inventoryValueBefore: 0,
    costingQuantityAfter: 4,
    inventoryValueAfter: 8,
    averageUnitCostAfter: 2,
    firstUncostableSale: { transactionId: 501, authorizationTime: '2026-01-03T12:00:00Z' },
    replaysBeforeFirstUncostableSale: true,
    projectedCostingQuantity: 2,
    projectedInventoryValue: 4,
    projectedAverageUnitCost: 2,
    remainingFatalIssues: [],
    ledgerFingerprint: 'fingerprint-1',
    ...overrides
  };
}

interface Harness {
  component: CostingRepairComponent;
  fixture: ReturnType<typeof TestBed.createComponent<CostingRepairComponent>>;
  host: HTMLElement;
  previewFn: jest.Mock;
  applyFn: jest.Mock;
  historyFn: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock };
}

async function createHarness(
  previewFn: jest.Mock = jest.fn(() => of(preview())),
  applyFn: jest.Mock = jest.fn(() =>
    of({
      repair: {
        id: 9,
        productId: 1,
        effectiveAt: '2026-01-02T12:00:00Z',
        quantity: 4,
        unitCost: 2,
        totalValue: 8,
        reason: 'Machine stock at the 2026 cutover was never costed.',
        createdAt: '2026-02-10T04:00:00Z',
        createdByDirectoryTenantId: 'tenant-1',
        createdByObjectId: 'object-1'
      },
      costingQuantity: 2,
      inventoryValue: 4,
      averageUnitCost: 2,
      recostedSaleCount: 2
    } as InventoryCostRepairApplied)
  ),
  historyFn: jest.Mock = jest.fn(() => of([] as InventoryCostRepairRecord[]))
): Promise<Harness> {
  const toast = { success: jest.fn(), error: jest.fn() };
  await TestBed.configureTestingModule({
    imports: [CostingRepairComponent],
    providers: [
      {
        provide: InventoryCostRepairService,
        useValue: { preview: previewFn, apply: applyFn, history: historyFn }
      },
      { provide: ToastService, useValue: toast }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(CostingRepairComponent);
  fixture.componentInstance.products = [product()];
  fixture.detectChanges();
  return { component: fixture.componentInstance, fixture, host: fixture.nativeElement as HTMLElement, previewFn, applyFn, historyFn, toast };
}

describe('CostingRepairComponent (issue #361)', () => {
  it('rejects a preview request when quantity, unit cost or reason are missing, without calling the API', async () => {
    const { component, previewFn, toast } = await createHarness();
    component.productId = 1;
    component.quantity = null;
    component.unitCost = 2;
    component.reason = 'A valid detailed reason';

    component.previewRepair();

    expect(previewFn).not.toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalled();
  });

  it('converts the Sydney-entered effective date/time to its UTC instant before calling preview', async () => {
    const { component, previewFn } = await createHarness();
    component.productId = 1;
    component.quantity = 4;
    component.unitCost = 2;
    component.reason = 'Machine stock at the 2026 cutover was never costed.';
    // 2026-01-02 12:00 in Sydney (AEDT, UTC+11) is 2026-01-02T01:00:00Z.
    component.effectiveAtLocal = '2026-01-02T12:00';

    component.previewRepair();

    const request = previewFn.mock.calls[0][0] as InventoryCostRepairRequest;
    expect(request.effectiveAt).toBe('2026-01-02T01:00:00.000Z');
    expect(request.productId).toBe(1);
    expect(request.quantity).toBe(4);
    expect(request.unitCost).toBe(2);
  });

  it('shows the preview fields and enables Apply once a preview is returned', async () => {
    const { component, fixture, host } = await createHarness();
    component.productId = 1;
    component.quantity = 4;
    component.unitCost = 2;
    component.reason = 'Machine stock at the 2026 cutover was never costed.';

    component.previewRepair();
    fixture.detectChanges();

    expect(component.preview).not.toBeNull();
    expect(host.textContent).toContain('Costing quantity before');
    expect(host.textContent).toContain('Projected inventory value');
    const applyButton = Array.from(host.querySelectorAll('button')).find(b => b.textContent?.includes('Apply repair'));
    expect(applyButton?.disabled).toBe(false);
  });

  it('shows the remaining fatal issues a partial repair would leave behind', async () => {
    const previewFn = jest.fn(() => of(preview({
      remainingFatalIssues: [{ code: 'UnknownCost', message: 'Completed Nayax sale 502 has no known cost.' }]
    })));
    const { component, fixture, host } = await createHarness(previewFn);
    component.productId = 1;
    component.quantity = 1;
    component.unitCost = 2;
    component.reason = 'Machine stock at the 2026 cutover was never costed.';

    component.previewRepair();
    fixture.detectChanges();

    expect(host.textContent).toContain('Completed Nayax sale 502 has no known cost.');
  });

  it('shows the API validation error and leaves Apply disabled when the preview call fails', async () => {
    const previewFn = jest.fn(() => throwError(() => ({ error: { message: 'A costing repair must add a positive quantity.' } })));
    const { component, toast, fixture } = await createHarness(previewFn);
    component.productId = 1;
    component.quantity = 4;
    component.unitCost = 2;
    component.reason = 'Machine stock at the 2026 cutover was never costed.';

    component.previewRepair();
    fixture.detectChanges();

    expect(component.preview).toBeNull();
    expect(toast.error).toHaveBeenCalledWith('A costing repair must add a positive quantity.');
  });

  it('applies exactly the previewed proposal, not the current form values', async () => {
    const applyFn = jest.fn(() =>
      of({
        repair: {
          id: 9, productId: 1, effectiveAt: '2026-01-02T12:00:00Z', quantity: 4, unitCost: 2, totalValue: 8,
          reason: 'Machine stock at the 2026 cutover was never costed.', createdAt: '2026-02-10T04:00:00Z',
          createdByDirectoryTenantId: 'tenant-1', createdByObjectId: 'object-1'
        },
        costingQuantity: 2, inventoryValue: 4, averageUnitCost: 2, recostedSaleCount: 2
      } as InventoryCostRepairApplied)
    );
    const { component, toast } = await createHarness(undefined, applyFn);
    component.preview = preview();
    window.confirm = jest.fn(() => true);
    // The form fields have since been edited; the apply must still use the previewed values.
    component.quantity = 999;

    component.applyRepair();

    expect(applyFn).toHaveBeenCalledWith(preview());
    expect(toast.success).toHaveBeenCalledWith('Costing repair saved. Recosted 2 sale(s).');
    expect(component.preview).toBeNull();
  });

  it('does not apply when the confirmation dialog is declined', async () => {
    const { component, applyFn } = await createHarness();
    component.preview = preview();
    window.confirm = jest.fn(() => false);

    component.applyRepair();

    expect(applyFn).not.toHaveBeenCalled();
  });

  it('clears the stale preview and surfaces the stale-preview message asking to preview again', async () => {
    const applyFn = jest.fn(() => throwError(() => ({
      error: { message: "This product's cost history changed after the preview. Run the preview again before applying the repair." }
    })));
    const { component, toast } = await createHarness(undefined, applyFn);
    component.preview = preview();
    window.confirm = jest.fn(() => true);

    component.applyRepair();

    expect(component.preview).toBeNull();
    expect(toast.error).toHaveBeenCalledWith(
      "This product's cost history changed after the preview. Run the preview again before applying the repair."
    );
  });

  it('loads and displays the product history newest first as the API returns it, and resets the preview on product change', async () => {
    const historyFn = jest.fn(() => of([
      { id: 2, productId: 1, effectiveAt: '2026-01-03T00:00:00Z', quantity: 2, unitCost: 1, totalValue: 2, reason: 'Second repair, more specific detail.', createdAt: '2026-01-04T00:00:00Z', createdByDirectoryTenantId: 't', createdByObjectId: 'o' },
      { id: 1, productId: 1, effectiveAt: '2026-01-02T00:00:00Z', quantity: 1, unitCost: 1, totalValue: 1, reason: 'First repair, more specific detail.', createdAt: '2026-01-03T00:00:00Z', createdByDirectoryTenantId: 't', createdByObjectId: 'o' }
    ] as InventoryCostRepairRecord[]));
    const { component, fixture, host } = await createHarness(undefined, undefined, historyFn);
    component.preview = preview();
    component.productId = 1;

    component.selectProduct();
    fixture.detectChanges();

    expect(component.preview).toBeNull();
    expect(historyFn).toHaveBeenCalledWith(1);
    const reasons = Array.from(host.querySelectorAll('tbody tr')).map(row => row.textContent);
    expect(reasons[0]).toContain('Second repair, more specific detail.');
    expect(reasons[1]).toContain('First repair, more specific detail.');
  });
});
