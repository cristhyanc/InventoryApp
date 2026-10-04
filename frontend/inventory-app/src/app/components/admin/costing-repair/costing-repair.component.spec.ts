import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
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

/** Reaches a preview the way the operator does: select product 1, fill the form, preview. */
function previewForProductOne(component: CostingRepairComponent): void {
  component.productId = 1;
  component.selectProduct();
  component.quantity = 4;
  component.unitCost = 2;
  component.reason = 'Machine stock at the 2026 cutover was never costed.';
  component.previewRepair();
  expect(component.preview).not.toBeNull();
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
    previewForProductOne(component);
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
    previewForProductOne(component);
    window.confirm = jest.fn(() => false);

    component.applyRepair();

    expect(applyFn).not.toHaveBeenCalled();
  });

  it('clears the stale preview and surfaces the stale-preview message asking to preview again', async () => {
    const applyFn = jest.fn(() => throwError(() => ({
      error: { message: "This product's cost history changed after the preview. Run the preview again before applying the repair." }
    })));
    const { component, toast } = await createHarness(undefined, applyFn);
    previewForProductOne(component);
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

  describe('product switching (issue #361 review P1)', () => {
    const productA = product({ id: 1, name: 'Coke Zero' });
    const productB = product({ id: 2, name: 'Nu Water' });

    function record(id: number, productId: number, reason: string): InventoryCostRepairRecord {
      return {
        id, productId, effectiveAt: '2026-01-02T00:00:00Z', quantity: 1, unitCost: 1, totalValue: 1, reason,
        createdAt: '2026-01-03T00:00:00Z', createdByDirectoryTenantId: 't', createdByObjectId: 'o'
      };
    }

    function fillForm(component: CostingRepairComponent): void {
      component.quantity = 4;
      component.unitCost = 2;
      component.reason = 'Machine stock at the 2026 cutover was never costed.';
      component.effectiveAtLocal = '2026-01-02T12:00';
    }

    function select(component: CostingRepairComponent, productId: number | null): void {
      component.productId = productId;
      component.selectProduct();
    }

    /** A service double whose every call returns a Subject the test resolves by hand, in any order. */
    function controlled() {
      const previews: Subject<InventoryCostRepairPreview>[] = [];
      const histories = new Map<number, Subject<InventoryCostRepairRecord[]>[]>();
      const previewFn = jest.fn(() => { const s = new Subject<InventoryCostRepairPreview>(); previews.push(s); return s; });
      const historyFn = jest.fn((productId: number) => {
        const s = new Subject<InventoryCostRepairRecord[]>();
        histories.set(productId, [...(histories.get(productId) ?? []), s]);
        return s;
      });
      return { previews, histories, previewFn, historyFn };
    }

    async function switchingHarness() {
      const c = controlled();
      const harness = await createHarness(c.previewFn, undefined, c.historyFn);
      harness.component.products = [productA, productB];
      return { ...harness, ...c };
    }

    it('discards product A\'s preview response once product B is selected, so Apply cannot submit it', async () => {
      const { component, previews, applyFn, toast } = await switchingHarness();
      select(component, 1);
      fillForm(component);
      component.previewRepair();
      expect(component.loading).toBe(true);

      select(component, 2);
      previews[0].next(preview({ productId: 1 }));
      previews[0].complete();

      expect(component.preview).toBeNull();
      expect(component.loading).toBe(false);
      window.confirm = jest.fn(() => true);
      component.applyRepair();
      expect(applyFn).not.toHaveBeenCalled();
      expect(toast.error).not.toHaveBeenCalled();
    });

    it('cancels the obsolete preview request when the product changes', async () => {
      const { component, previews } = await switchingHarness();
      select(component, 1);
      fillForm(component);
      component.previewRepair();

      select(component, 2);

      expect(previews[0].observed).toBe(false);
    });

    it('keeps only the latest preview across A -> B -> A switching', async () => {
      const { component, previews } = await switchingHarness();
      select(component, 1);
      fillForm(component);
      component.previewRepair();
      select(component, 2);
      select(component, 1);
      component.previewRepair();

      previews[0].next(preview({ productId: 1, ledgerFingerprint: 'old' }));
      expect(component.preview).toBeNull();
      expect(component.loading).toBe(true);

      previews[1].next(preview({ productId: 1, ledgerFingerprint: 'new' }));
      expect(component.preview?.ledgerFingerprint).toBe('new');
      expect(component.loading).toBe(false);
    });

    it('does not let an obsolete preview failure touch the current loading or error state', async () => {
      const { component, previews, toast } = await switchingHarness();
      select(component, 1);
      fillForm(component);
      component.previewRepair();
      select(component, 2);
      fillForm(component);
      component.previewRepair();

      previews[0].error({ error: { message: 'Product A failed.' } });

      expect(component.loading).toBe(true);
      expect(toast.error).not.toHaveBeenCalled();
    });

    it('refuses to apply a preview that belongs to a different product than the one selected', async () => {
      const { component, applyFn, toast } = await createHarness();
      component.products = [productA, productB];
      component.productId = 2;
      component.preview = preview({ productId: 1 });
      window.confirm = jest.fn(() => true);

      component.applyRepair();

      expect(applyFn).not.toHaveBeenCalled();
      expect(component.preview).toBeNull();
      expect(toast.error).toHaveBeenCalledWith('This preview is not for the selected product. Preview the repair again before applying it.');
    });

    it('clears product A\'s history as soon as B is selected and ignores A\'s late history response', async () => {
      const { component, fixture, host, histories } = await switchingHarness();
      select(component, 1);
      histories.get(1)![0].next([record(1, 1, 'Product A repair reason.')]);
      fixture.detectChanges();
      expect(host.textContent).toContain('Product A repair reason.');

      select(component, 2);
      fixture.detectChanges();
      expect(component.history).toEqual([]);
      expect(host.textContent).not.toContain('Product A repair reason.');
      expect(host.textContent).toContain('Loading repair history');

      histories.get(2)![0].next([record(2, 2, 'Product B repair reason.')]);
      histories.get(1)![0].next([record(3, 1, 'Late product A repair reason.')]);
      fixture.detectChanges();

      expect(component.history.map(r => r.id)).toEqual([2]);
      expect(host.textContent).toContain('Product B repair reason.');
      expect(host.textContent).not.toContain('Late product A repair reason.');
    });

    it('ignores an out-of-order A history arriving after A -> B -> A and keeps the newest A request\'s result', async () => {
      const { component, histories } = await switchingHarness();
      select(component, 1);
      select(component, 2);
      select(component, 1);

      histories.get(1)![1].next([record(5, 1, 'Newest request.')]);
      histories.get(1)![0].next([record(4, 1, 'Oldest request.')]);

      expect(component.history.map(r => r.id)).toEqual([5]);
    });

    it('shows no stale records when B\'s history fails to load after switching from A', async () => {
      const { component, fixture, host, histories, toast } = await switchingHarness();
      select(component, 1);
      histories.get(1)![0].next([record(1, 1, 'Product A repair reason.')]);

      select(component, 2);
      histories.get(2)![0].error(new Error('boom'));
      fixture.detectChanges();

      expect(component.history).toEqual([]);
      expect(host.textContent).not.toContain('Product A repair reason.');
      expect(host.textContent).toContain('The costing repair history could not be loaded.');
      expect(toast.error).toHaveBeenCalledWith('Unable to load the costing repair history.');
    });

    it('does not report an obsolete history failure for a product no longer selected', async () => {
      const { component, histories, toast } = await switchingHarness();
      select(component, 1);
      select(component, 2);

      histories.get(1)![0].error(new Error('boom'));

      expect(component.historyError).toBe(false);
      expect(toast.error).not.toHaveBeenCalled();
    });

    it('disables the product selector while a repair is being applied', async () => {
      const applied = new Subject<InventoryCostRepairApplied>();
      const { component, fixture, host } = await createHarness(undefined, jest.fn(() => applied));
      previewForProductOne(component);
      window.confirm = jest.fn(() => true);

      component.applyRepair();
      fixture.detectChanges();
      await fixture.whenStable();

      expect(host.querySelector('select')?.disabled).toBe(true);
    });
  });

  describe('effective time validation (issue #361 review P2)', () => {
    async function filledHarness(effectiveAtLocal: string) {
      const harness = await createHarness();
      harness.component.productId = 1;
      harness.component.quantity = 4;
      harness.component.unitCost = 2;
      harness.component.reason = 'Machine stock at the 2026 cutover was never costed.';
      harness.component.effectiveAtLocal = effectiveAtLocal;
      return harness;
    }

    it('rejects a time inside the October daylight-saving gap with a form message and makes no API call', async () => {
      const { component, fixture, host, previewFn, toast } = await filledHarness('2026-10-04T02:30');

      component.previewRepair();
      fixture.detectChanges();

      expect(previewFn).not.toHaveBeenCalled();
      expect(component.effectiveAtError).toContain('does not exist in Sydney');
      expect(host.textContent).toContain('does not exist in Sydney');
      expect(toast.error).toHaveBeenCalledWith(component.effectiveAtError);
    });

    it('rejects a time inside the April repeated hour with guidance and makes no API call', async () => {
      const { component, previewFn } = await filledHarness('2026-04-05T02:30');

      component.previewRepair();

      expect(previewFn).not.toHaveBeenCalled();
      expect(component.effectiveAtError).toContain('happens twice in Sydney');
    });

    it.each([
      ['2026-10-04T01:59', '2026-10-03T15:59:00.000Z'],
      ['2026-10-04T03:00', '2026-10-03T16:00:00.000Z'],
      ['2026-04-05T01:59', '2026-04-04T14:59:00.000Z'],
      ['2026-04-05T03:00', '2026-04-04T17:00:00.000Z']
    ])('sends the exact UTC instant for valid time %s next to a transition', async (local, expectedUtc) => {
      const { component, previewFn } = await filledHarness(local);

      component.previewRepair();

      expect(component.effectiveAtError).toBeNull();
      expect((previewFn.mock.calls[0][0] as InventoryCostRepairRequest).effectiveAt).toBe(expectedUtc);
    });
  });
});
