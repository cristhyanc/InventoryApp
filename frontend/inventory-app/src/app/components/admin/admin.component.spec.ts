import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AdminComponent } from './admin.component';
import { ImportService } from '../../services/import.service';
import { ReportingService } from '../../services/reporting.service';
import { ToastService } from '../../services/toast.service';
import { NayaxSettingsService } from '../../services/nayax-settings.service';
import { SiteService } from '../../services/site.service';
import { ProductService } from '../../services/product.service';
import { InventoryCostBaselineSource, InventoryCostTransitionPreview, InventoryCostTransitionService } from '../../services/inventory-cost-transition.service';
import { InventoryCostRepairService } from '../../services/inventory-cost-repair.service';
import { CostingRepairComponent } from './costing-repair/costing-repair.component';
import { Product } from '../../models/models';

function transitionPreview(overrides: Partial<InventoryCostTransitionPreview> = {}): InventoryCostTransitionPreview {
  return {
    previewId: 'p1', productId: 1, productName: 'Coke', homeStockQuantity: 5, machineStocks: [],
    machineStockQuantity: 3, openingCostingQuantity: 8, averageUnitCost: 1.5, inventoryValue: 12,
    cutoffAt: '2026-06-15T00:00:00Z', costSource: InventoryCostBaselineSource.ManualAuthoritative,
    legacyReplayedPhysicalQuantity: 0, legacyPhysicalDiscrepancy: 0, dataQualityNote: '',
    ...overrides
  };
}

async function render() {
  await TestBed.configureTestingModule({
    imports: [AdminComponent],
    providers: [
      { provide: ImportService, useValue: {} },
      { provide: ReportingService, useValue: { siteCommissionAgreements: jest.fn(() => of([])) } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
      { provide: NayaxSettingsService, useValue: { getRates: jest.fn(() => of([])) } },
      { provide: SiteService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: ProductService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: InventoryCostTransitionService, useValue: {} },
      { provide: InventoryCostRepairService, useValue: {} }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AdminComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
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

describe('AdminComponent Costing Repair composition (issue #361)', () => {
  it('composes the Costing Repair workflow as its own feature component and passes it the loaded products', async () => {
    const product = { id: 1, name: 'Coke Zero' } as Product;
    await TestBed.configureTestingModule({
      imports: [AdminComponent],
      providers: [
        { provide: ImportService, useValue: {} },
        { provide: ReportingService, useValue: { siteCommissionAgreements: jest.fn(() => of([])) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
        { provide: NayaxSettingsService, useValue: { getRates: jest.fn(() => of([])) } },
        { provide: SiteService, useValue: { getAll: jest.fn(() => of([])) } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of([product])) } },
        { provide: InventoryCostTransitionService, useValue: {} },
        { provide: InventoryCostRepairService, useValue: {} }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(AdminComponent);
    fixture.detectChanges();

    const repairElement = (fixture.nativeElement as HTMLElement).querySelector('app-costing-repair');
    expect(repairElement).not.toBeNull();
    const repairComponent = fixture.debugElement.query(
      element => element.componentInstance instanceof CostingRepairComponent
    ).componentInstance as CostingRepairComponent;
    expect(repairComponent.products).toEqual([product]);
  });
});
