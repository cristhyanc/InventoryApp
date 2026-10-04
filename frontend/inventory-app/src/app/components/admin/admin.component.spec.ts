import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AdminComponent } from './admin.component';
import { ImportService } from '../../services/import.service';
import { ReportingService } from '../../services/reporting.service';
import { ToastService } from '../../services/toast.service';
import { NayaxSettingsService } from '../../services/nayax-settings.service';
import { SiteService } from '../../services/site.service';
import { Product } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { InventoryCostBaselineSource, InventoryCostTransitionPreview, InventoryCostTransitionService } from '../../services/inventory-cost-transition.service';
import { InventoryCostRepairPreview, InventoryCostRepairRecord, InventoryCostRepairService } from '../../services/inventory-cost-repair.service';

function transitionPreview(overrides: Partial<InventoryCostTransitionPreview> = {}): InventoryCostTransitionPreview {
  return {
    previewId: 'p1', productId: 1, productName: 'Coke', homeStockQuantity: 5, machineStocks: [],
    machineStockQuantity: 3, openingCostingQuantity: 8, averageUnitCost: 1.5, inventoryValue: 12,
    cutoffAt: '2026-06-15T00:00:00Z', costSource: InventoryCostBaselineSource.ManualAuthoritative,
    legacyReplayedPhysicalQuantity: 0, legacyPhysicalDiscrepancy: 0, dataQualityNote: '',
    ...overrides
  };
}

function repairPreview(overrides: Partial<InventoryCostRepairPreview> = {}): InventoryCostRepairPreview {
  return {
    productId: 1, productName: 'Coke Zero', effectiveAt: '2026-01-02T12:00:00Z',
    quantity: 4, unitCost: 2, totalValue: 8, reason: 'Machine stock at the 2026 cutover was never costed.',
    costingQuantityBefore: 0, inventoryValueBefore: 0,
    costingQuantityAfter: 4, inventoryValueAfter: 8, averageUnitCostAfter: 2,
    firstUncostableSale: { transactionId: 555, authorizationTime: '2026-01-03T12:00:00Z' },
    replaysBeforeFirstUncostableSale: true,
    projectedCostingQuantity: 2, projectedInventoryValue: 4, projectedAverageUnitCost: 2,
    remainingFatalIssues: [],
    ledgerFingerprint: 'fp-1',
    ...overrides
  };
}

function repairRecord(overrides: Partial<InventoryCostRepairRecord> = {}): InventoryCostRepairRecord {
  return {
    id: 1, productId: 1, effectiveAt: '2026-01-02T12:00:00Z', quantity: 4, unitCost: 2, totalValue: 8,
    reason: 'Machine stock at the 2026 cutover was never costed.', createdAt: '2026-02-10T04:00:00Z',
    createdByDirectoryTenantId: 'tenant-1', createdByObjectId: 'object-1',
    ...overrides
  };
}

interface RenderOptions {
  products?: Product[];
  repairService?: Partial<Record<'preview' | 'apply' | 'history', jest.Mock>>;
}

async function render(options: RenderOptions = {}) {
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
  await TestBed.configureTestingModule({
    imports: [AdminComponent],
    providers: [
      { provide: ImportService, useValue: {} },
      { provide: ReportingService, useValue: { siteCommissionAgreements: jest.fn(() => of([])) } },
      { provide: ToastService, useValue: toast },
      { provide: NayaxSettingsService, useValue: { getRates: jest.fn(() => of([])) } },
      { provide: SiteService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: ProductService, useValue: { getAll: jest.fn(() => of(options.products ?? [])) } },
      { provide: InventoryCostTransitionService, useValue: {} },
      {
        provide: InventoryCostRepairService,
        useValue: {
          preview: jest.fn(() => of(repairPreview())),
          apply: jest.fn(() => of({ repair: repairRecord(), costingQuantity: 2, inventoryValue: 4, averageUnitCost: 2, recostedSaleCount: 1 })),
          history: jest.fn(() => of([])),
          ...options.repairService
        }
      }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AdminComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, toast };
}

describe('AdminComponent inventory-cost transition cutoff timestamp display (issue #232)', () => {
  it('renders the single-product preview cutoff as Australia/Canberra local time during AEST (UTC+10), not raw UTC', async () => {
    const { fixture, host } = await render();
    fixture.componentInstance.transitionPreview = transitionPreview({ cutoffAt: '2026-06-15T00:00:00Z' });
    fixture.detectChanges();

    expect(host.textContent).toContain('15/06/2026, 10:00 am');
  });

  it('renders the single-product preview cutoff as Australia/Canberra local time during AEDT (UTC+11), not raw UTC', async () => {
    const { fixture, host } = await render();
    fixture.componentInstance.transitionPreview = transitionPreview({ cutoffAt: '2026-01-15T00:00:00Z' });
    fixture.detectChanges();

    expect(host.textContent).toContain('15/01/2026, 11:00 am');
  });

  it('renders the batch preview cutoff as Australia/Canberra local time, not raw UTC', async () => {
    const { fixture, host } = await render();
    fixture.componentInstance.transitionBatchPreview = {
      previewId: 'batch', cutoffAt: '2026-06-15T00:00:00Z', costSource: InventoryCostBaselineSource.ManualAuthoritative,
      products: [], productCount: 0, homeStockQuantity: 0, machineStockQuantity: 0, openingCostingQuantity: 0, inventoryValue: 0
    };
    fixture.detectChanges();

    expect(host.textContent).toContain('15/06/2026, 10:00 am');
  });
});

describe('AdminComponent Costing Repair (issue #361)', () => {
  const product: Product = { id: 1, name: 'Coke Zero', unitPrice: 3, averageUnitCost: 2 } as Product;

  it('converts the Sydney-entered effective date/time to the UTC instant the preview request sends, during AEST', async () => {
    const preview = jest.fn(() => of(repairPreview()));
    const { fixture } = await render({ products: [product], repairService: { preview } });
    const component = fixture.componentInstance;

    component.repairProductId = 1;
    component.repairEffectiveAtLocal = '2026-06-15T14:30';
    component.repairQuantity = 4;
    component.repairUnitCost = 2;
    component.repairReason = 'Machine stock at the 2026 cutover was never costed.';
    component.previewRepair();

    expect(preview).toHaveBeenCalledWith(1, '2026-06-15T04:30:00.000Z', 4, 2, 'Machine stock at the 2026 cutover was never costed.');
  });

  it('converts the Sydney-entered effective date/time to the UTC instant the preview request sends, during AEDT', async () => {
    const preview = jest.fn(() => of(repairPreview()));
    const { fixture } = await render({ products: [product], repairService: { preview } });
    const component = fixture.componentInstance;

    component.repairProductId = 1;
    component.repairEffectiveAtLocal = '2026-01-15T14:30';
    component.repairQuantity = 4;
    component.repairUnitCost = 2;
    component.repairReason = 'Machine stock at the 2026 cutover was never costed.';
    component.previewRepair();

    expect(preview).toHaveBeenCalledWith(1, '2026-01-15T03:30:00.000Z', 4, 2, 'Machine stock at the 2026 cutover was never costed.');
  });

  it('renders every issue #359 preview field: positions before/after, the projected position, the first uncostable sale and remaining issues', async () => {
    const { fixture, host } = await render({ products: [product] });
    fixture.componentInstance.repairPreview = repairPreview({
      remainingFatalIssues: [{ code: 'UnknownCost', message: 'Completed Nayax sale 556 has no known cost.' }]
    });
    fixture.detectChanges();

    expect(host.textContent).toContain('0'); // costingQuantityBefore
    expect(host.textContent).toContain('555'); // firstUncostableSale.transactionId
    expect(host.textContent).toContain('UnknownCost');
    expect(host.textContent).toContain('Completed Nayax sale 556 has no known cost.');
  });

  it('disables Apply while the preview still reports a remaining fatal issue', async () => {
    const { fixture, host } = await render({ products: [product] });
    fixture.componentInstance.repairPreview = repairPreview({
      remainingFatalIssues: [{ code: 'UnknownCost', message: 'Completed Nayax sale 556 has no known cost.' }]
    });
    fixture.detectChanges();

    const applyButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.includes('Apply repair'));
    expect(applyButton).toBeDefined();
    expect((applyButton as HTMLButtonElement).disabled).toBe(true);
  });

  it('enables Apply once the preview reports no remaining fatal issues', async () => {
    const { fixture, host } = await render({ products: [product] });
    fixture.componentInstance.repairPreview = repairPreview({ remainingFatalIssues: [] });
    fixture.detectChanges();

    const applyButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.includes('Apply repair'));
    expect((applyButton as HTMLButtonElement).disabled).toBe(false);
  });

  it('clears the stale preview and surfaces the API message when apply reports the ledger changed since preview', async () => {
    const apply = jest.fn(() => throwError(() => ({
      error: { message: "This product's cost history changed after the preview. Run the preview again before applying the repair." }
    })));
    const { fixture, toast } = await render({ products: [product], repairService: { apply } });
    const component = fixture.componentInstance;
    component.repairProductId = 1;
    component.repairPreview = repairPreview();

    jest.spyOn(window, 'confirm').mockReturnValue(true);
    component.applyRepair();

    expect(toast.error).toHaveBeenCalledWith("This product's cost history changed after the preview. Run the preview again before applying the repair.");
    expect(component.repairPreview).toBeNull();
  });

  it('shows the product costing-repair history newest first, in the order the API returned it', async () => {
    const history = [
      repairRecord({ id: 3, effectiveAt: '2026-01-02T13:00:00Z' }),
      repairRecord({ id: 2, effectiveAt: '2026-01-02T12:00:00Z' }),
      repairRecord({ id: 1, effectiveAt: '2026-01-01T12:00:00Z' })
    ];
    const { fixture, host } = await render({ products: [product], repairService: { history: jest.fn(() => of(history)) } });
    fixture.componentInstance.repairProductId = 1;
    fixture.componentInstance.selectRepairProduct();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('[data-testid="repair-history-row"]'));
    expect(rows.map((row) => row.getAttribute('data-repair-id'))).toEqual(['3', '2', '1']);
  });

  it('labels the action as a human-entered historical costing repair that does not change physical stock', async () => {
    const { host } = await render({ products: [product] });

    expect(host.textContent).toMatch(/human-entered historical costing repair/i);
    expect(host.textContent).toMatch(/historical COGS/i);
    expect(host.textContent).toMatch(/does not change physical stock/i);
  });
});
