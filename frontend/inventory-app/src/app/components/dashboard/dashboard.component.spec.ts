import { Observable, of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { InventoryValuationSummary } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';

function createComponent(summary$: Observable<InventoryValuationSummary>): DashboardComponent {
  const productService = {
    getAll: () => of([]),
    getLowStock: () => of([]),
    getInventoryValuationSummary: () => summary$
  } as unknown as ProductService;
  const machineService = { getAll: () => of([]) } as unknown as MachineService;
  const siteService = { getAll: () => of([]) } as unknown as SiteService;

  return new DashboardComponent(productService, machineService, siteService);
}

describe('DashboardComponent inventory value tile', () => {
  it('shows the authoritative cost-basis total when costing is complete', () => {
    const summary: InventoryValuationSummary = {
      totalInventoryValue: 123.45,
      isComplete: true,
      productsWithUnknownCost: 0,
      totalProducts: 5
    };
    const component = createComponent(of(summary));
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('$123.45');
    expect(component.inventoryValueHelpText).toBe('Business-owned inventory at cost');
  });

  it('shows a known zero total distinct from unavailable', () => {
    const summary: InventoryValuationSummary = {
      totalInventoryValue: 0,
      isComplete: true,
      productsWithUnknownCost: 0,
      totalProducts: 0
    };
    const component = createComponent(of(summary));
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('$0.00');
  });

  it('shows unavailable, not $0.00, when any product has unknown cost', () => {
    const summary: InventoryValuationSummary = {
      totalInventoryValue: null,
      isComplete: false,
      productsWithUnknownCost: 2,
      totalProducts: 5
    };
    const component = createComponent(of(summary));
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('Unavailable');
    expect(component.inventoryValueHelpText).toBe('2 of 5 products missing cost data');
  });

  it('shows unavailable when the backend request fails', () => {
    const component = createComponent(throwError(() => new Error('network error')) as Observable<InventoryValuationSummary>);
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('Unavailable');
  });
});
