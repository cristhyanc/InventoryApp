import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { SiteService } from '../../services/site.service';
import { ToastService } from '../../services/toast.service';
import { Machine, PickListResult, Product } from '../../models/models';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { SpecConnectionStatus, providerAvailabilityDouble } from '../../provider/provider-availability.spec-helpers';
import { PickListComponent } from './pick-list.component';

function machine(overrides: Partial<Machine>): Machine {
  return {
    machineID: 1,
    machineName: null,
    machineNumber: null,
    todayGrossRevenue: 0,
    currentWeekGrossRevenue: 0,
    lastWeekGrossRevenue: 0,
    twoWeeksAgoGrossRevenue: 0,
    todayDirectProfit: null,
    twoWeeksAgoDirectProfit: null,
    lastWeekDirectProfit: null,
    currentWeekDirectProfit: null,
    previousComparableWeekGrossRevenue: 0,
    previousComparableWeekDirectProfit: null,
    monthToDateGrossRevenue: 0,
    monthToDateDirectProfit: null,
    profitabilityStatus: null,
    ...overrides
  };
}

function product(overrides: Partial<Product>): Product {
  return {
    id: 1,
    name: 'Product',
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 10,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 2,
    restockTo: 10,
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

function pickListResult(): PickListResult {
  return {
    products: [
      {
        productId: 1,
        productName: 'Coke',
        mdbCode: 2,
        storageQuantityInStock: 50,
        totalQuantityToPick: 4,
        storageShortageQuantity: 0,
        machineQuantities: [{ machineId: 10, mdbCode: 2, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 }]
      }
    ]
  };
}

function nayaxPermissionNotGranted(): HttpErrorResponse {
  return new HttpErrorResponse({ status: 409, error: { code: 'nayax_permission_not_granted' } });
}

interface Harness {
  component: PickListComponent;
  getPickList: jest.Mock;
  provider: ProviderAvailabilityService;
  toast: ToastService;
}

function routeStub(queryParams: Record<string, string> = {}): ActivatedRoute {
  return { snapshot: { queryParamMap: convertToParamMap(queryParams) } } as unknown as ActivatedRoute;
}

function createHarness(
  options: {
    status?: SpecConnectionStatus;
    machines?: Machine[];
    products?: Product[];
    pickList?: jest.Mock;
    /** A refusal to record against `machineStock` before the component reads it, simulating evidence an earlier screen in this session already produced. */
    machineStockAlreadyRefused?: boolean;
    queryParams?: Record<string, string>;
  } = {}
): Harness {
  const provider = providerAvailabilityDouble(options.status ?? 'Ready');
  if (options.machineStockAlreadyRefused) {
    provider.noteRefusal(nayaxPermissionNotGranted(), 'machineStock');
  }

  const machines = options.machines ?? [machine({ machineID: 10, machineName: 'Machine A' })];
  const products = options.products ?? [product({ id: 1, name: 'Coke' })];
  const pickList = options.pickList ?? jest.fn(() => of(pickListResult()));

  const machineService = { getAll: jest.fn(() => of(machines)) } as unknown as MachineService;
  const productService = { getAll: jest.fn(() => of(products)) } as unknown as ProductService;
  const siteService = { getList: jest.fn(() => of([])) } as unknown as SiteService;
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } as unknown as ToastService;

  const component = new PickListComponent(
    machineService, productService, siteService, toast, provider, routeStub(options.queryParams)
  );
  component.ngOnInit();

  return { component, getPickList: pickList, provider, toast };
}

describe('PickListComponent mode selection (issue #744)', () => {
  it('defaults to calculated mode and offers the toggle when the provider and its permissions are fine', () => {
    const { component } = createHarness();

    expect(component.mode).toBe('calculated');
    expect(component.calculatedUnavailableReason).toBe('');
  });

  it('opens straight into manual mode, with a reason, when there is no usable provider', () => {
    const { component } = createHarness({ status: 'NotConfigured' });

    expect(component.mode).toBe('manual');
    expect(component.calculatedUnavailableReason).toBe('The pick list is unavailable because Nayax is not connected.');
  });

  it('opens straight into manual mode when Nayax has already refused the machineStock permission this session', () => {
    const { component } = createHarness({ machineStockAlreadyRefused: true });

    expect(component.mode).toBe('manual');
    expect(component.calculatedUnavailableReason).toBe(
      'Plan picks is unavailable because Nayax has not granted this application permission to read it.'
    );
  });

  it('switches to manual mode once a live snapshot read proves the machineStock permission is refused', () => {
    const { component } = createHarness();
    expect(component.mode).toBe('calculated');

    component.onPlanLoadFailed(nayaxPermissionNotGranted());

    expect(component.mode).toBe('manual');
    expect(component.calculatedUnavailableReason).toContain('Plan picks is unavailable');
  });

  it('a snapshot failure that is not a stated unavailability does not force manual mode', () => {
    const { component, toast } = createHarness();

    component.onPlanLoadFailed(new HttpErrorResponse({ status: 502 }));

    expect(component.mode).toBe('calculated');
    expect(component.calculatedUnavailableReason).toBe('');
    expect(toast.error).toHaveBeenCalledWith('Failed to load the pick list.');
  });

  it('lets the operator switch to manual mode even when calculated is fully available', () => {
    const { component } = createHarness();

    component.switchMode('manual');

    expect(component.mode).toBe('manual');
  });

  it('creates the manual view on first use and keeps it for every later switch', () => {
    const { component } = createHarness();
    expect(component.manualViewCreated).toBe(false);

    component.switchMode('manual');
    component.switchMode('calculated');

    expect(component.mode).toBe('calculated');
    expect(component.manualViewCreated).toBe(true);
  });

  it('refuses to leave manual mode while a manual save is still in flight', () => {
    const { component } = createHarness();
    component.switchMode('manual');
    component.onManualSaveInFlightChange(true);

    component.switchMode('calculated');

    expect(component.mode).toBe('manual');

    component.onManualSaveInFlightChange(false);
    component.switchMode('calculated');

    expect(component.mode).toBe('calculated');
  });

  it('refuses to switch back to calculated once it is genuinely unavailable', () => {
    const { component } = createHarness({ machineStockAlreadyRefused: true });

    component.switchMode('calculated');

    expect(component.mode).toBe('manual');
  });

  it('opens straight into manual mode when the "Record refill" link asked for it, while keeping the toggle offered', () => {
    const { component } = createHarness({ queryParams: { mode: 'manual' } });

    expect(component.mode).toBe('manual');
    expect(component.manualViewCreated).toBe(true);
    // A real connection is still fine, so the toggle must stay offered - the link only chooses the
    // starting view, it does not lock the operator out of the calculated matrix.
    expect(component.calculatedUnavailableReason).toBe('');
  });

  it('passes the linked machine id through as the manual view\'s preselection', () => {
    const { component } = createHarness({ queryParams: { mode: 'manual', machineId: '42' } });

    expect(component.preselectedMachineId).toBe(42);
  });

  it('ignores a malformed machineId query parameter rather than preselecting a wrong machine', () => {
    const { component } = createHarness({ queryParams: { mode: 'manual', machineId: 'not-a-number' } });

    expect(component.preselectedMachineId).toBeNull();
  });

  it('leaves the default calculated-mode behaviour alone for ordinary navigation with no query parameters', () => {
    const { component } = createHarness();

    expect(component.mode).toBe('calculated');
    expect(component.manualViewCreated).toBe(false);
    expect(component.preselectedMachineId).toBeNull();
  });

  it('refreshes the active product catalogue a manual refill just changed', () => {
    const getAllProducts = jest.fn(() => of([product({ id: 1, name: 'Coke', quantityInStock: 20 })]));
    const machineService = { getAll: jest.fn(() => of([])) } as unknown as MachineService;
    const productService = { getAll: getAllProducts } as unknown as ProductService;
    const siteService = { getList: jest.fn(() => of([])) } as unknown as SiteService;
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } as unknown as ToastService;
    const component = new PickListComponent(
      machineService, productService, siteService, toast, providerAvailabilityDouble('Ready'), routeStub()
    );
    component.ngOnInit();
    getAllProducts.mockClear();

    component.refreshProductsAfterManualRefill();

    expect(getAllProducts).toHaveBeenCalledTimes(1);
  });
});
