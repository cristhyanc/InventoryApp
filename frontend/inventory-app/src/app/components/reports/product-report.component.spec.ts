import { of } from 'rxjs';
import { ActivatedRoute } from '@angular/router';
import { ProductReportComponent } from './product-report.component';
import { ProductRow, ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';

function createComponent(): ProductReportComponent {
  const route = {} as unknown as ActivatedRoute;
  const reports = {} as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;
  return new ProductReportComponent(route, reports, machines);
}

function row(overrides: Partial<ProductRow> = {}): ProductRow {
  return {
    productId: 1, productName: 'Coke 600ml', sales: 210, quantity: 10,
    transactionCount: 10, isUnmapped: false, historicalCostAvailable: true,
    ...overrides
  };
}

describe('ProductReportComponent purchase-cost insight display (issue #207)', () => {
  it('formats Last Cost and Lowest Cost as currency when present', () => {
    const component = createComponent();
    const r = row({ lastCost: 2.4, lowestCost: 2.05 });

    expect(component.lastCostDisplay(r)).toBe('$2.40');
    expect(component.lowestCostDisplay(r)).toBe('$2.05');
  });

  it('shows an unavailable dash, not a zero, when no purchase history is recorded', () => {
    const component = createComponent();
    const r = row({ lastCost: null, lowestCost: null, savingPerUnit: null });

    expect(component.lastCostDisplay(r)).toBe('—');
    expect(component.lowestCostDisplay(r)).toBe('—');
    expect(component.savingDisplay(r)).toBe('—');
  });

  it('formats an equal-cost zero saving using normal currency formatting, not the unavailable dash', () => {
    const component = createComponent();
    const r = row({ lastCost: 2.0, lowestCost: 2.0, savingPerUnit: 0 });

    expect(component.savingDisplay(r)).toBe('$0.00');
  });

  it('formats a positive saving as the Last Cost minus Lowest Cost currency amount', () => {
    const component = createComponent();
    const r = row({ lastCost: 2.4, lowestCost: 2.05, savingPerUnit: 0.35 });

    expect(component.savingDisplay(r)).toBe('$0.35');
  });

  it('displays the supplier name under the cost when known', () => {
    const component = createComponent();

    expect(component.supplierDisplay('Costco')).toBe('Costco');
  });

  it('displays "None" for a purchase with no recorded supplier, per issue #63 semantics', () => {
    const component = createComponent();

    expect(component.supplierDisplay(null)).toBe('None');
    expect(component.supplierDisplay(undefined)).toBe('None');
  });
});
