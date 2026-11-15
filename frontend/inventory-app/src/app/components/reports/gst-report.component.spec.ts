import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { GstReportComponent } from './gst-report.component';
import { GstReport, ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';

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
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      provideNayaxConnectionStatus()
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

    const netGstCard = component.cards(component.report!).find(card => card.key === 'net-gst');
    expect(netGstCard!.label).toBe('Estimated GST payable');
    expect(netGstCard!.value).toBe(-1.23);
  });

  it('shows the GST-free and no-rule sales cards beside taxable sales, exactly as the API returned them', async () => {
    // Issue #1255's fixture as the aid presents it: $33 of taxable sales inclusive, so $30 ex GST,
    // $3 of GST-free sales and $22 of sales of products with no rule inside the taxable total.
    const { host, component } = await render(report({
      taxableSales: 30, gstFreeSales: 3, noGstRuleSales: 22, gstOnSales: 3, netGst: 1
    }));

    const cards = component.cards(component.report!);
    expect(cards.find(card => card.key === 'gst-free-sales')!.value).toBe(3);
    expect(cards.find(card => card.key === 'no-gst-rule-sales')!.value).toBe(22);
    expect(cards.find(card => card.key === 'no-gst-rule-sales')!.label)
      .toBe('Sales of products with no GST rule');
    const text = host.textContent ?? '';
    expect(text).toContain('GST-free sales');
    expect(text).toContain('Sales of products with no GST rule');
    expect(text).toContain('$22.00');
  });

  it('states the GST-on-sales rule on the page, including that the rule is read as it stands today', async () => {
    const { host } = await render(report());

    const rule = host.querySelector('[data-testid="gst-on-sales-rule"]');
    expect(rule).not.toBeNull();
    expect(rule!.textContent).toContain('one eleventh of taxable sales');
    expect(rule!.textContent).toContain('GST rule is GST-free are left out');
    expect(rule!.textContent).toContain('a product with no GST rule is taxable');
    expect(rule!.textContent).toContain('as it stands today');
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
