import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { GstReportComponent } from './gst-report.component';
import { GstReport, ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';

function report(overrides: Partial<GstReport> = {}): GstReport {
  return {
    from: '2026-07-01', to: '2026-07-31',
    taxableSales: 100, gstOnSales: 10, taxableFees: 5, gstOnFees: 1, netGst: 7,
    operatingExpenseGst: 0,
    inventoryPurchaseGst: 2, purchaseLineGst: 1, purchaseChargeGst: 1,
    purchaseUnresolvedComponentCount: 0, purchaseUnresolvedAmount: 0, purchaseGstIncomplete: false,
    dataQuality: {
      missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
      commissionNotPersisted: false, containsUnmappedProducts: false
    },
    ...overrides
  };
}

async function render(value: GstReport) {
  await TestBed.configureTestingModule({
    imports: [GstReportComponent],
    providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } },
      { provide: ReportingService, useValue: { gst: jest.fn(() => of(value)) } },
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(GstReportComponent);
  fixture.detectChanges();
  return { host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

describe('GstReportComponent purchase input GST (issue #432)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the purchase input GST split and the total exactly as the API returned them', async () => {
    const { host } = await render(report({ purchaseLineGst: 1.25, purchaseChargeGst: 0.75, inventoryPurchaseGst: 2 }));

    const text = host.textContent ?? '';
    expect(text).toContain('Purchase input GST');
    expect(text).toContain('$1.25');
    expect(text).toContain('$0.75');
    expect(text).toContain('$2.00');
  });

  it('shows no incomplete warning when every purchase component is classified', async () => {
    const { host } = await render(report());

    expect(host.querySelector('[data-testid="purchase-gst-incomplete"]')).toBeNull();
    expect(host.textContent).toContain('Estimated GST payable');
    expect(host.textContent).not.toContain('Estimated GST payable (incomplete)');
  });

  it('warns that the result is incomplete, with the unresolved count and amount, when components are unclassified', async () => {
    const { host } = await render(report({
      purchaseGstIncomplete: true, purchaseUnresolvedComponentCount: 3, purchaseUnresolvedAmount: 48.5
    }));

    const warning = host.querySelector('[data-testid="purchase-gst-incomplete"]');
    expect(warning).not.toBeNull();
    expect(warning!.textContent).toContain('3 purchase component(s)');
    expect(warning!.textContent).toContain('$48.50');
    expect(host.textContent).toContain('Estimated GST payable (incomplete)');
  });

  it('displays the net GST the API calculated and never recalculates it in the browser', async () => {
    const { component } = await render(report({ netGst: -1.23, inventoryPurchaseGst: 11.23 }));

    const netGstCard = component.cards(component.report!).find(card => card[0].startsWith('Estimated GST payable'));
    expect(netGstCard![1]).toBe(-1.23);
  });

  it('renders the data-quality notes the API supplied', async () => {
    const { host } = await render(report({
      purchaseGstIncomplete: true, purchaseUnresolvedComponentCount: 1, purchaseUnresolvedAmount: 22,
      dataQuality: {
        missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
        commissionNotPersisted: false, containsUnmappedProducts: false,
        notes: ['1 purchase component(s) totalling 22.00 have no GST classification; purchase input GST and net GST are incomplete.']
      }
    }));

    expect(host.textContent).toContain('have no GST classification');
  });
});
