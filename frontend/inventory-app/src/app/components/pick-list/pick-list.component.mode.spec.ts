import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { PickListService } from '../../services/pick-list.service';
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
}

function createHarness(
  options: {
    status?: SpecConnectionStatus;
    machines?: Machine[];
    products?: Product[];
    pickList?: jest.Mock;
    /** A refusal to record against `machineStock` before the component reads it, simulating evidence an earlier screen in this session already produced. */
    machineStockAlreadyRefused?: boolean;
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
  const pickListService = { get: pickList } as unknown as PickListService;
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } as unknown as ToastService;

  const component = new PickListComponent(machineService, productService, pickListService, toast, provider);
  component.ngOnInit();

  return { component, getPickList: pickList, provider };
}

/** Stages and applies a machine selection - the normal "pick machines, then Apply" flow. */
function applyMachines(component: PickListComponent, machineIds: number[]): void {
  component.stagedMachineIds = machineIds;
  component.applyFilters();
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
      'The calculated pick list is unavailable because Nayax has not granted this application permission to read it.'
    );
  });

  it('switches to manual mode once a live Apply discovers the machineStock permission is refused', () => {
    const getPickList = jest.fn(() => throwError(() => nayaxPermissionNotGranted()));
    const { component } = createHarness({ pickList: getPickList });
    expect(component.mode).toBe('calculated');

    applyMachines(component, [10]);

    expect(component.mode).toBe('manual');
    expect(component.calculatedUnavailableReason).toContain('calculated pick list is unavailable');
  });

  it('a calculated-mode request failure that is not a stated unavailability does not force manual mode', () => {
    const getPickList = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 502 })));
    const { component } = createHarness({ pickList: getPickList });

    applyMachines(component, [10]);

    expect(component.mode).toBe('calculated');
  });

  it('lets the operator switch to manual mode even when calculated is fully available', () => {
    const { component } = createHarness();

    component.switchMode('manual');

    expect(component.mode).toBe('manual');
  });

  it('switching back to calculated preserves the already-fetched matrix (calculated mode unchanged)', () => {
    const { component } = createHarness();
    applyMachines(component, [10]);
    expect(component.pickListProducts).toHaveLength(1);

    component.switchMode('manual');
    component.switchMode('calculated');

    expect(component.mode).toBe('calculated');
    expect(component.pickListProducts).toHaveLength(1);
    expect(component.appliedMachineIds).toEqual([10]);
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

  it('refreshes the active product catalogue a manual refill just changed', () => {
    const getAllProducts = jest.fn(() => of([product({ id: 1, name: 'Coke', quantityInStock: 20 })]));
    const machineService = { getAll: jest.fn(() => of([])) } as unknown as MachineService;
    const productService = { getAll: getAllProducts } as unknown as ProductService;
    const pickListService = { get: jest.fn(() => of(pickListResult())) } as unknown as PickListService;
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } as unknown as ToastService;
    const component = new PickListComponent(machineService, productService, pickListService, toast, providerAvailabilityDouble('Ready'));
    component.ngOnInit();
    getAllProducts.mockClear();

    component.refreshProductsAfterManualRefill();

    expect(getAllProducts).toHaveBeenCalledTimes(1);
  });
});
