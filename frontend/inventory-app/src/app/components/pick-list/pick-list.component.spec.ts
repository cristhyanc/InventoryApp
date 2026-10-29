import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  LEAVE_WITH_ENTERED_QUANTITIES_WARNING,
  LEAVE_WITH_PLANNING_WORK_WARNING,
  LEAVE_WITH_UNRESOLVED_SUBMISSION_WARNING,
  PickListComponent
} from './pick-list.component';
import { LocalMachineService } from '../../services/local-machine.service';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { SiteService } from '../../services/site.service';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import {
  LocalMachine,
  Machine,
  MultiMachinePickListRefillRequest,
  PickListResult,
  Product,
  SiteListItem
} from '../../models/models';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { AccessService } from '../../access/access.service';
import { NayaxConnectionService, NayaxConnectionStatusName } from '../../services/nayax-connection.service';
import { providerAvailabilityDouble } from '../../provider/provider-availability.spec-helpers';

function localMachine(overrides: Partial<LocalMachine>): LocalMachine {
  return { id: 1, name: 'Machine', isActive: true, isProviderNamed: false, ...overrides };
}

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

function pickListResult(overrides: Partial<PickListResult> = {}): PickListResult {
  return {
    products: [
      {
        productId: 1,
        productName: 'Coke',
        mdbCode: 2,
        storageQuantityInStock: 50,
        totalQuantityToPick: 4,
        storageShortageQuantity: 0,
        machineQuantities: [
          { machineId: 10, mdbCode: 2, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 },
          { machineId: 20, mdbCode: 2, currentQuantity: 10, targetQuantity: 10, quantityToPick: 0 }
        ]
      },
      {
        productId: 2,
        productName: 'Chips',
        mdbCode: 10,
        storageQuantityInStock: 2,
        totalQuantityToPick: 5,
        storageShortageQuantity: 3,
        machineQuantities: [{ machineId: 10, mdbCode: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 }]
      }
    ],
    ...overrides
  };
}

interface Harness {
  component: PickListComponent;
  getAllMachines: jest.Mock;
  getAllProducts: jest.Mock;
  getSiteList: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
}

/** The page on its own: it owns the shared reads, the mode and the navigation guard, and no matrix. */
function createHarness(
  options: {
    machines?: Machine[];
    products?: Product[];
    productReads?: jest.Mock;
    sites?: jest.Mock;
    queryParams?: Record<string, string>;
  } = {}
): Harness {
  const getAllMachines = jest.fn(() => of(options.machines ?? [machine({ machineID: 10, machineName: 'Machine A' })]));
  const getAllProducts = options.productReads ?? jest.fn(() => of(options.products ?? [product({ id: 1, name: 'Coke' })]));
  const getSiteList = options.sites ?? jest.fn(() => of([] as SiteListItem[]));
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  const component = new PickListComponent(
    { getAll: getAllMachines } as unknown as MachineService,
    { getAll: getAllProducts } as unknown as ProductService,
    { getList: getSiteList } as unknown as SiteService,
    toast as unknown as ToastService,
    providerAvailabilityDouble(),
    { snapshot: { queryParamMap: convertToParamMap(options.queryParams ?? {}) } } as unknown as ActivatedRoute
  );
  component.ngOnInit();

  return { component, getAllMachines, getAllProducts, getSiteList, toast };
}

afterEach(() => jest.restoreAllMocks());

describe('PickListComponent shared reads', () => {
  it('reads the machine fleet, the active catalogue and the site list on init', () => {
    const { component, getAllMachines, getAllProducts, getSiteList } = createHarness({
      sites: jest.fn(() => of([{ siteId: 5, name: 'Airport', machineCount: 1 }]))
    });

    expect(getAllMachines).toHaveBeenCalledTimes(1);
    expect(getAllProducts).toHaveBeenCalledTimes(1);
    expect(getSiteList).toHaveBeenCalledTimes(1);
    expect(component.allMachines.map((m) => m.machineID)).toEqual([10]);
    expect(component.allProducts.map((p) => p.id)).toEqual([1]);
    expect(component.sites.map((s) => s.siteId)).toEqual([5]);
  });

  it('leaves the sites empty when the site read fails, rather than inventing a site for a machine', () => {
    const { component } = createHarness({ sites: jest.fn(() => throwError(() => new Error('offline'))) });

    expect(component.sites).toEqual([]);
    expect(component.allMachines).toHaveLength(1);
  });

  it('reports a failed first catalogue read as a failed read, never as an empty catalogue', () => {
    const { component, toast } = createHarness({ productReads: jest.fn(() => throwError(() => new Error('offline'))) });

    expect(component.catalogueState).toBe('failed');
    expect(component.catalogueRefreshFailed).toBe(false);
    expect(toast.error).toHaveBeenCalledWith('Failed to load the product list.');
  });

  it('reports a failed refresh as possibly stale storage quantities, not as lost data', () => {
    const productReads = jest
      .fn()
      .mockReturnValueOnce(of([product({ id: 1, name: 'Coke' })]))
      .mockReturnValueOnce(throwError(() => new Error('offline')));
    const { component, toast } = createHarness({ productReads });

    component.loadProducts();

    expect(component.catalogueState).toBe('loaded');
    expect(component.catalogueRefreshFailed).toBe(true);
    expect(component.allProducts).toHaveLength(1);
    expect(toast.error).toHaveBeenCalledWith('Failed to refresh the product list.');
  });
});

describe('PickListComponent navigation guard (issue #996)', () => {
  it('leaves the page without asking while nothing has been planned or entered', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();

    expect(component.unsavedWorkWarning).toBeNull();
    expect(component.confirmLeave()).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('warns about planning work, and keeps the page when the operator cancels', () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { component } = createHarness();

    component.onPlanningWorkChange(true);

    expect(component.unsavedWorkWarning).toBe(LEAVE_WITH_PLANNING_WORK_WARNING);
    expect(component.confirmLeave()).toBe(false);
    expect(confirm).toHaveBeenCalledWith(LEAVE_WITH_PLANNING_WORK_WARNING);
    // Cancelling changes nothing at all: the work is still there to come back to.
    expect(component.planningWorkPresent).toBe(true);
  });

  it('warns about entered quantities that have not been recorded', () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { component } = createHarness();

    component.onManualUnsavedWorkChange({ entriesPresent: true, unresolvedSubmission: false });

    expect(component.unsavedWorkWarning).toBe(LEAVE_WITH_ENTERED_QUANTITIES_WARNING);
    expect(component.confirmLeave()).toBe(true);
  });

  it('warns about the lost retry context first, ahead of either other kind of work', () => {
    const { component } = createHarness();

    component.onPlanningWorkChange(true);
    component.onManualUnsavedWorkChange({ entriesPresent: true, unresolvedSubmission: true });

    expect(component.unsavedWorkWarning).toBe(LEAVE_WITH_UNRESOLVED_SUBMISSION_WARNING);
  });

  it('warns while a save is still in flight, because its outcome would be unknowable', () => {
    const { component } = createHarness();

    component.onManualSaveInFlightChange(true);

    expect(component.unsavedWorkWarning).toBe(LEAVE_WITH_UNRESOLVED_SUBMISSION_WARNING);
  });

  it('asks the browser to confirm a reload only while there is work to lose', () => {
    const { component } = createHarness();
    const quiet = new Event('beforeunload') as BeforeUnloadEvent;
    const preventQuiet = jest.spyOn(quiet, 'preventDefault');

    component.onBeforeUnload(quiet);
    expect(preventQuiet).not.toHaveBeenCalled();

    component.onPlanningWorkChange(true);
    const loaded = new Event('beforeunload') as BeforeUnloadEvent;
    const preventLoaded = jest.spyOn(loaded, 'preventDefault');

    component.onBeforeUnload(loaded);
    expect(preventLoaded).toHaveBeenCalled();
    expect(loaded.returnValue).toBe(true);
  });
});

describe('PickListComponent rendered structure', () => {
  async function render(
    machines: Machine[] = [machine({ machineID: 10, machineName: 'Machine A' }), machine({ machineID: 20, machineName: 'Machine B' })],
    products: Product[] = [product({ id: 1, name: 'Coke' }), product({ id: 2, name: 'Chips' })],
    pickList: jest.Mock = jest.fn(() => of(pickListResult())),
    nayaxStatus: NayaxConnectionStatusName = 'Ready',
    options: {
      localMachines?: LocalMachine[];
      recordRefill?: jest.Mock;
      recordMultiMachineRefill?: jest.Mock;
      sites?: SiteListItem[];
    } = {}
  ) {
    await TestBed.configureTestingModule({
      imports: [PickListComponent],
      providers: [
        provideRouter([]),
        { provide: MachineService, useValue: { getAll: jest.fn(() => of(machines)) } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of(products)) } },
        { provide: SiteService, useValue: { getList: jest.fn(() => of(options.sites ?? [])) } },
        {
          provide: PickListService,
          useValue: {
            get: pickList,
            recordRefill: options.recordRefill ?? jest.fn(),
            recordMultiMachineRefill: options.recordMultiMachineRefill ?? jest.fn()
          }
        },
        { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of(options.localMachines ?? [])) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: nayaxStatus, machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: { active$: of(false) } }
      ]
    }).compileComponents();

    // The business time zone the shell loads at sign-in (issue #499); the "As of" snapshot instant
    // is rendered in it, and the application's existing business is in Sydney.
    TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

    const fixture = TestBed.createComponent(PickListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const requireElement = <T extends HTMLElement = HTMLElement>(selector: string): T => {
      const element = host.querySelector<T>(selector);
      if (element === null) {
        throw new Error(`Expected the rendered component to contain an element matching "${selector}", but it did not.`);
      }
      return element;
    };
    const button = (label: string) =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim().startsWith(label));
    const requireButton = (label: string): HTMLButtonElement => {
      const found = button(label);
      if (found === undefined) {
        const rendered = Array.from(host.querySelectorAll('button')).map((b) => `"${b.textContent?.trim()}"`).join(', ');
        throw new Error(`Expected a button labelled "${label}", but the rendered buttons were: ${rendered || '(none)'}.`);
      }
      return found;
    };
    const click = (element: HTMLElement) => {
      element.click();
      fixture.detectChanges();
    };
    const dropdownTrigger = (labelPrefix: string): HTMLButtonElement => {
      const found = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) =>
        b.getAttribute('aria-label')?.startsWith(`${labelPrefix}:`)
      );
      if (found === undefined) {
        throw new Error(`Expected a dropdown trigger button labelled "${labelPrefix}".`);
      }
      return found;
    };
    const openDropdown = (labelPrefix: string): void => click(dropdownTrigger(labelPrefix));
    const checkOption = (optionLabel: string): void => {
      const labels = Array.from(host.querySelectorAll('[role="group"] label'));
      const target = labels.find((l) => l.textContent?.trim() === optionLabel);
      if (target === undefined) {
        throw new Error(`Expected an open dropdown option labelled "${optionLabel}".`);
      }
      click(target.querySelector('input')!);
    };
    const applyFilters = (): void => click(requireButton('Apply'));
    /** Stages one machine and applies it, which is how every matrix assertion below gets a plan. */
    const planFor = (...machineNames: string[]): void => {
      openDropdown('Machines');
      machineNames.forEach(checkOption);
      applyFilters();
    };
    /** The desktop matrix' own cells, which are the first of the two layouts in the document. */
    const matrixRows = (): HTMLTableRowElement[] =>
      Array.from(requireElement('[data-testid="pick-list-table-scroll"]').querySelectorAll('tbody tr'));

    return {
      fixture,
      host,
      requireElement,
      button,
      requireButton,
      click,
      dropdownTrigger,
      openDropdown,
      checkOption,
      applyFilters,
      planFor,
      matrixRows
    };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('names the two views and says planning changes no inventory', async () => {
    const { host, button } = await render();

    expect(host.querySelector('h1')?.textContent).toContain('Pick List');
    expect(button('Plan picks')).toBeDefined();
    expect(button('Record refill')).toBeDefined();
    expect(host.querySelector('[data-testid="pick-list-inventory-impact"]')?.textContent).toContain(
      'Planning only. Editing quantities and checking picks does not change inventory.'
    );
    expect(host.querySelector('[data-testid="plan-picks-planning-only"]')?.textContent).toContain(
      'Editing quantities and checking picks does not change inventory'
    );
  });

  it('shows the toolbar, an empty selection and no matrix before anything is applied', async () => {
    const { host, dropdownTrigger } = await render();

    expect(host.querySelector('input#plan-picks-search')).not.toBeNull();
    expect(host.querySelector('select#plan-picks-category')).not.toBeNull();
    expect(dropdownTrigger('Products').textContent).toContain('All products');
    expect(dropdownTrigger('Machines').textContent).toContain('No machines selected');
    expect(host.textContent).toContain('Selected machines (0)');
    expect(host.textContent).toContain('Select at least one machine');
    expect(host.querySelector('table')).toBeNull();
  });

  it('renders the snapshot "As of" time as business-local time during AEST (UTC+10), not raw UTC (issue #232)', async () => {
    jest.useFakeTimers({ now: new Date('2026-06-15T00:00:00Z') });
    try {
      const { host, planFor } = await render();
      planFor('Machine A');

      expect(host.querySelector('[data-testid="pick-list-snapshot"]')?.textContent).toContain('As of 15/06/2026, 10:00 am');
    } finally {
      jest.useRealTimers();
    }
  });

  it('renders the snapshot "As of" time as business-local time during AEDT (UTC+11), not raw UTC (issue #232)', async () => {
    jest.useFakeTimers({ now: new Date('2026-01-15T00:00:00Z') });
    try {
      const { host, planFor } = await render();
      planFor('Machine A');

      expect(host.querySelector('[data-testid="pick-list-snapshot"]')?.textContent).toContain('As of 15/01/2026, 11:00 am');
    } finally {
      jest.useRealTimers();
    }
  });

  it('stages a machine selection without changing the matrix until Apply', async () => {
    const { host, openDropdown, checkOption, dropdownTrigger } = await render();

    openDropdown('Machines');
    checkOption('Machine A');
    checkOption('Machine B');

    expect(dropdownTrigger('Machines').textContent).toContain('All machines');
    expect(host.textContent).toContain('Selected machines (0)');
    expect(host.querySelector('table')).toBeNull();
  });

  it('Apply renders the applied machines as matrix columns and removable chips', async () => {
    const { host, requireElement, click, planFor, dropdownTrigger } = await render();

    planFor('Machine A', 'Machine B');

    const headers = Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    expect(headers).toEqual(['Product', 'MDB Code', 'Storage Qty', 'Total to Pick', 'Machine A', 'Machine B']);
    expect(host.textContent).toContain('Selected machines (2)');

    click(requireElement<HTMLButtonElement>('[aria-label="Remove Machine B"]'));

    expect(host.textContent).toContain('Selected machines (1)');
    expect(dropdownTrigger('Machines').textContent).toContain('1 machine selected');
  });

  it('shows the suggested pick quantity, compact Current/Target, and no MDB code inside a pick cell', async () => {
    const { host, matrixRows, planFor } = await render();

    planFor('Machine A');

    const quantities = Array.from(host.querySelectorAll('[data-testid="plan-picks-pick-quantity"]')).map((span) =>
      span.textContent?.trim()
    );
    expect(quantities).toContain('4');
    const cokeRow = matrixRows()[0];
    expect(cokeRow.textContent).toContain('Coke');
    expect(cokeRow.textContent).toContain('Current: 6 of 10');
    // Issue #996 removes the per-machine MDB code from the pick cells; the row's own column keeps it.
    expect(cokeRow.textContent).not.toContain('MDB: 2');
    const mdbCells = Array.from(host.querySelectorAll('[data-testid="pick-list-mdb-code"]')).map((td) => td.textContent?.trim());
    expect(mdbCells).toEqual(['2', '10']);
  });

  it('renders a dash for a missing MDB code instead of leaving the cell blank or showing "null"', async () => {
    const pickList = jest.fn(() => of(pickListResult({
      products: [{
        productId: 1, productName: 'Coke', mdbCode: null, storageQuantityInStock: 50, totalQuantityToPick: 4, storageShortageQuantity: 0,
        machineQuantities: [{ machineId: 10, mdbCode: null, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 }]
      }]
    })));
    const { host, planFor } = await render(undefined, undefined, pickList);

    planFor('Machine A');

    expect(host.querySelector('[data-testid="pick-list-mdb-code"]')?.textContent?.trim()).toBe('—');
    expect(host.textContent).not.toContain('MDB: null');
  });

  it('shows each row’s storage quantity exactly as returned, including a zero or negative one', async () => {
    const pickList = jest.fn(() => of(pickListResult({
      products: [{
        productId: 1, productName: 'Coke', mdbCode: 2, storageQuantityInStock: 0, totalQuantityToPick: 4, storageShortageQuantity: 4,
        machineQuantities: [{ machineId: 10, mdbCode: 2, currentQuantity: 0, targetQuantity: 10, quantityToPick: 4 }]
      }, {
        productId: 2, productName: 'Chips', mdbCode: 10, storageQuantityInStock: -3, totalQuantityToPick: 5, storageShortageQuantity: 8,
        machineQuantities: [{ machineId: 10, mdbCode: 10, currentQuantity: 0, targetQuantity: 5, quantityToPick: 5 }]
      }]
    })));
    const { host, planFor } = await render(undefined, undefined, pickList);

    planFor('Machine A');

    const storageQtyCells = Array.from(host.querySelectorAll('[data-testid="pick-list-storage-qty"]')).map((td) => td.textContent?.trim());
    expect(storageQtyCells).toEqual(['0', '-3']);
  });

  it('keeps the header rows sticky and the product column pinned inside one bounded, focusable scroll area', async () => {
    const { host, requireElement, planFor } = await render();

    planFor('Machine A', 'Machine B');

    const scrollArea = requireElement('[data-testid="pick-list-table-scroll"]');
    expect(scrollArea.contains(requireElement('table'))).toBe(true);
    expect(Array.from(scrollArea.classList)).toEqual(expect.arrayContaining(['max-h-[70vh]', 'overflow-auto']));
    // Discoverable, and reachable by keyboard: the matrix scrolls sideways, so it has to be focusable.
    expect(scrollArea.getAttribute('tabindex')).toBe('0');
    expect(scrollArea.getAttribute('role')).toBe('region');
    expect(scrollArea.getAttribute('aria-label')).toContain('scrollable');

    for (const th of Array.from(host.querySelectorAll<HTMLTableCellElement>('thead th'))) {
      expect(Array.from(th.classList)).toEqual(expect.arrayContaining(['sticky', 'bg-md-gray-100']));
      // The opaque head surface is the separator: #410 defines no shadow token for a sticky header.
      expect(Array.from(th.classList).filter((cssClass) => cssClass.startsWith('shadow'))).toEqual([]);
    }
    const productHeader = host.querySelector<HTMLTableCellElement>('thead th');
    expect(Array.from(productHeader?.classList ?? [])).toEqual(expect.arrayContaining(['sticky', 'left-0', 'top-0']));
    const productCell = host.querySelector<HTMLTableCellElement>('tbody th[scope="row"]');
    expect(Array.from(productCell?.classList ?? [])).toEqual(expect.arrayContaining(['sticky', 'left-0']));
  });

  it('groups adjacent machine columns under one site header where the site read knows one', async () => {
    const machines = [
      machine({ machineID: 10, machineName: 'Machine A', siteId: 5 }),
      machine({ machineID: 20, machineName: 'Machine B', siteId: null })
    ];
    const { requireElement, planFor } = await render(machines, undefined, undefined, undefined, {
      sites: [{ siteId: 5, name: 'Airport', machineCount: 1 }]
    });

    planFor('Machine A', 'Machine B');

    const siteRow = requireElement('[data-testid="plan-picks-site-header-row"]');
    const siteHeaders = Array.from(siteRow.querySelectorAll('th[scope="colgroup"]')).map((th) => ({
      label: th.textContent?.trim(),
      colspan: th.getAttribute('colspan')
    }));
    expect(siteHeaders).toEqual([
      { label: 'Airport', colspan: '1' },
      { label: 'No site recorded', colspan: '1' }
    ]);
  });

  it('draws no site header row at all when no applied machine has a known site', async () => {
    const { host, planFor } = await render();

    planFor('Machine A');

    expect(host.querySelector('[data-testid="plan-picks-site-header-row"]')).toBeNull();
  });

  it('points out the horizontal scroll once there are more machine columns than fit', async () => {
    const machines = [10, 20, 30].map((id) => machine({ machineID: id, machineName: `Machine ${id}` }));
    const pickList = jest.fn(() => of(pickListResult()));
    const { host, planFor } = await render(machines, undefined, pickList);

    planFor('Machine 10', 'Machine 20', 'Machine 30');

    expect(host.querySelector('[data-testid="plan-picks-scroll-hint"]')?.textContent).toContain('Scroll sideways');
  });

  it('edits a pick quantity: the pencil opens a focused, selected value that Enter commits', async () => {
    const { host, requireElement, click, planFor, fixture } = await render();
    planFor('Machine A');

    click(requireElement<HTMLButtonElement>('[aria-label="Edit pick quantity: Coke at Machine A"]'));

    const input = requireElement<HTMLInputElement>('[data-testid="plan-picks-edit-input"]');
    expect(input.value).toBe('4');
    expect(document.activeElement).toBe(input);
    expect([input.selectionStart, input.selectionEnd]).toEqual([0, 1]);

    input.value = '6';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="plan-picks-edit-input"]')).toBeNull();
    const quantities = Array.from(host.querySelectorAll('[data-testid="plan-picks-pick-quantity"]')).map((s) => s.textContent?.trim());
    expect(quantities).toContain('6');
    expect(host.querySelector('[data-testid="plan-picks-suggested"]')?.textContent).toContain('Suggested: 4');
  });

  it('shows an accessible inline message for a malformed quantity and commits nothing', async () => {
    const { host, requireElement, click, planFor, fixture } = await render();
    planFor('Machine A');
    click(requireElement<HTMLButtonElement>('[aria-label="Edit pick quantity: Coke at Machine A"]'));

    const input = requireElement<HTMLInputElement>('[data-testid="plan-picks-edit-input"]');
    input.value = '2.5';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    click(requireElement<HTMLButtonElement>('[data-testid="plan-picks-commit-edit"]'));

    const error = requireElement('[data-testid="plan-picks-edit-error"]');
    expect(error.getAttribute('role')).toBe('alert');
    expect(error.textContent).toContain('whole number of 0 or more');
    expect(requireElement('[data-testid="plan-picks-edit-input"]').getAttribute('aria-invalid')).toBe('true');
    expect(host.querySelector('[data-testid="plan-picks-suggested"]')).toBeNull();
  });

  it('restores the quantity when the edit is cancelled', async () => {
    const { host, requireElement, click, planFor, fixture } = await render();
    planFor('Machine A');
    click(requireElement<HTMLButtonElement>('[aria-label="Edit pick quantity: Coke at Machine A"]'));
    const input = requireElement<HTMLInputElement>('[data-testid="plan-picks-edit-input"]');
    input.value = '99';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    click(requireElement<HTMLButtonElement>('[data-testid="plan-picks-cancel-edit"]'));

    expect(host.querySelector('[data-testid="plan-picks-suggested"]')).toBeNull();
    const quantities = Array.from(host.querySelectorAll('[data-testid="plan-picks-pick-quantity"]')).map((s) => s.textContent?.trim());
    expect(quantities).toContain('4');
  });

  it('checks a pick off with a labelled checkbox beside the quantity, with a word for the state', async () => {
    const { host, requireElement, click, planFor } = await render();
    planFor('Machine A');

    const checkbox = requireElement<HTMLInputElement>('input[aria-label="Picked: Coke at Machine A"]');
    expect(checkbox.type).toBe('checkbox');
    expect(host.querySelector('[data-testid="plan-picks-progress"]')?.textContent).toBe('0 of 2 picks completed');

    click(checkbox);

    expect(host.querySelector('[data-testid="plan-picks-progress"]')?.textContent).toBe('1 of 2 picks completed');
    // The state is a word as well as a tick, so it is never carried by colour alone.
    expect(checkbox.closest('label')?.textContent).toContain('Picked');
  });

  it('offers neither an edit nor a checkbox on a cell with nothing suggested', async () => {
    const { host, planFor } = await render();

    planFor('Machine A', 'Machine B');

    expect(host.querySelector('[aria-label="Edit pick quantity: Coke at Machine B"]')).toBeNull();
    expect(host.querySelector('input[aria-label="Picked: Coke at Machine B"]')).toBeNull();
  });

  it('shows the whole run’s summary beside what the filters leave visible, with no progress bar over the rows', async () => {
    const { host, planFor } = await render();

    planFor('Machine A');

    expect(host.querySelector('[data-testid="plan-picks-total-units"]')?.textContent).toContain('9');
    expect(host.querySelector('[data-testid="plan-picks-remaining"]')?.textContent).toContain('2 picks remaining');
    expect(host.querySelector('[data-testid="plan-picks-visible-summary"]')?.textContent).toContain('Showing 2 of 2 products');
    expect(host.querySelector('[data-testid="plan-picks-shortage-count"]')?.textContent).toContain('1 product is short of storage');
    expect(host.querySelector('[data-testid="pick-list-progress-percent"]')).toBeNull();
  });

  it('shows a visible shortage badge for a product whose pick exceeds physical storage', async () => {
    const { host, matrixRows, planFor } = await render();

    planFor('Machine A');

    const badge = host.querySelector('[data-testid="storage-shortage-badge"]');
    expect(badge?.textContent?.trim()).toBe('Storage short 3');
    expect(matrixRows()[1].contains(badge!)).toBe(true);
  });

  it('filters to only the remaining picks and only the shortages', async () => {
    const { host, requireElement, click, planFor, matrixRows } = await render();
    planFor('Machine A');

    click(requireElement<HTMLInputElement>('[data-testid="plan-picks-only-shortages"]'));

    expect(matrixRows()).toHaveLength(1);
    expect(matrixRows()[0].textContent).toContain('Chips');

    click(requireElement<HTMLInputElement>('[data-testid="plan-picks-only-shortages"]'));
    click(requireElement<HTMLInputElement>('input[aria-label="Picked: Coke at Machine A"]'));
    click(requireElement<HTMLInputElement>('[data-testid="plan-picks-only-remaining"]'));

    expect(matrixRows()).toHaveLength(1);
    expect(matrixRows()[0].textContent).toContain('Chips');
    // The run's own figures are unchanged by what the filter hides.
    expect(host.querySelector('[data-testid="plan-picks-total-units"]')?.textContent).toContain('9');
    expect(host.querySelector('[data-testid="plan-picks-progress"]')?.textContent).toBe('1 of 2 picks completed');
  });

  it('offers a Refresh that re-reads the applied selection, separately from Apply', async () => {
    const pickList = jest.fn(() => of(pickListResult()));
    const { requireElement, click, planFor } = await render(undefined, undefined, pickList);
    planFor('Machine A');
    pickList.mockClear();

    click(requireElement<HTMLButtonElement>('[data-testid="plan-picks-refresh"]'));

    expect(pickList).toHaveBeenCalledTimes(1);
    expect(pickList).toHaveBeenCalledWith([10]);
  });

  it('offers a Retry on a failed read and never tells the operator to press Apply again', async () => {
    const pickList = jest.fn(() => throwError(() => new Error('offline')));
    const { requireElement, planFor } = await render(undefined, undefined, pickList);

    planFor('Machine A');

    const failure = requireElement('[data-testid="plan-picks-load-failed"]');
    expect(failure.getAttribute('role')).toBe('alert');
    expect(failure.textContent).toContain('Use Refresh to try again');
    expect(failure.textContent).not.toContain('Apply');
    expect(requireElement('[data-testid="plan-picks-refresh"]').textContent).toContain('Retry');
  });

  it('asks before Reset discards planning work and keeps everything when refused', async () => {
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { host, requireElement, requireButton, click, planFor } = await render();
    planFor('Machine A');
    click(requireElement<HTMLInputElement>('input[aria-label="Picked: Coke at Machine A"]'));

    click(requireButton('Reset'));

    expect(confirm).toHaveBeenCalled();
    expect(host.textContent).toContain('Selected machines (1)');
    expect(host.querySelector('[data-testid="plan-picks-progress"]')?.textContent).toBe('1 of 2 picks completed');
  });

  it('Reset clears the selections, the plan and the planning work once confirmed', async () => {
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    const { host, requireElement, requireButton, click, planFor, dropdownTrigger } = await render();
    planFor('Machine A');
    click(requireElement<HTMLInputElement>('input[aria-label="Picked: Coke at Machine A"]'));

    click(requireButton('Reset'));

    expect(host.textContent).toContain('Selected machines (0)');
    expect(host.textContent).toContain('Select at least one machine');
    expect(dropdownTrigger('Machines').textContent).toContain('No machines selected');
    expect(dropdownTrigger('Products').textContent).toContain('All products');
    expect(host.querySelector('table')).toBeNull();
  });

  it('a product-only Apply filters the visible rows without an extra Pick List request', async () => {
    const pickList = jest.fn(() => of(pickListResult()));
    const { openDropdown, checkOption, applyFilters, planFor, matrixRows } = await render(undefined, undefined, pickList);
    planFor('Machine A');
    pickList.mockClear();

    openDropdown('Products');
    checkOption('Coke'); // Products defaults to all-selected, so unchecking Coke leaves only Chips staged.
    applyFilters();

    expect(pickList).not.toHaveBeenCalled();
    expect(matrixRows()).toHaveLength(1);
    expect(matrixRows()[0].textContent).toContain('Chips');
  });

  it('renders no product or machine images anywhere on the page', async () => {
    const { host, planFor } = await render();

    planFor('Machine A', 'Machine B');

    expect(host.querySelectorAll('img').length).toBe(0);
  });

  it('does not show the Nayax status banner, even when the connection is not Ready', async () => {
    const { host } = await render(undefined, undefined, undefined, 'PendingPermissions');

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).toBeNull();
    expect(host.textContent).not.toContain("Nayax isn't fully connected");
  });

  it('does not make the Storage Qty header sortable', async () => {
    const { host, planFor } = await render();

    planFor('Machine A');

    const headers = Array.from(host.querySelectorAll('thead th'));
    const storageQtyHeader = headers.find((th) => th.textContent?.trim() === 'Storage Qty');
    expect(storageQtyHeader?.querySelector('button')).toBeNull();
    expect(storageQtyHeader?.hasAttribute('aria-sort')).toBe(false);
  });

  it('sorts by the Product and MDB Code headers, reporting aria-sort as it goes', async () => {
    const { host, requireButton, click, planFor, matrixRows } = await render();
    planFor('Machine A');
    const headers = Array.from(host.querySelectorAll('thead th'));
    expect(headers[0].getAttribute('aria-sort')).toBe('none');
    expect(headers[1].getAttribute('aria-sort')).toBe('none');

    click(requireButton('Product'));

    expect(matrixRows()[0].textContent).toContain('Chips');
    expect(headers[0].getAttribute('aria-sort')).toBe('ascending');
    expect(requireButton('Product').textContent?.trim()).toBe('Product (ascending)');

    click(requireButton('Product'));

    expect(matrixRows()[0].textContent).toContain('Coke');
    expect(headers[0].getAttribute('aria-sort')).toBe('descending');

    click(requireButton('MDB Code'));

    expect(matrixRows()[0].textContent).toContain('Coke');
    expect(headers[1].getAttribute('aria-sort')).toBe('ascending');
    expect(headers[0].getAttribute('aria-sort')).toBe('none');
    expect(requireButton('Product').textContent?.trim()).toBe('Product');
  });

  it('offers product cards with a machine focus for the narrow layout, keeping Current/Target on them', async () => {
    const { host, requireElement, planFor } = await render();

    planFor('Machine A', 'Machine B');

    const cards = requireElement('[data-testid="plan-picks-cards"]');
    expect(cards.querySelectorAll('li').length).toBe(2);
    expect(cards.textContent).toContain('Current: 6 of 10');
    expect(requireElement<HTMLSelectElement>('[data-testid="plan-picks-machine-focus"]').options.length).toBe(2);
    expect(host.querySelector('[data-testid="plan-picks-focus-summary"]')?.textContent).toContain('Machine A');
  });

  it('hides Plan picks rather than destroying it, so a mode switch loses no planning work', async () => {
    const pickList = jest.fn(() => of(pickListResult()));
    const { host, requireElement, requireButton, click, planFor } = await render(undefined, undefined, pickList);
    planFor('Machine A');
    click(requireElement<HTMLInputElement>('input[aria-label="Picked: Coke at Machine A"]'));
    pickList.mockClear();

    click(requireButton('Record refill'));
    expect((host.querySelector('app-plan-picks') as HTMLElement).hidden).toBe(true);
    click(requireButton('Plan picks'));

    expect((host.querySelector('app-plan-picks') as HTMLElement).hidden).toBe(false);
    expect(host.querySelector('[data-testid="plan-picks-progress"]')?.textContent).toBe('1 of 2 picks completed');
    expect(host.textContent).toContain('Selected machines (1)');
    // Nothing was re-read: the snapshot the plan was built from is still the one on screen.
    expect(pickList).not.toHaveBeenCalled();
  });

  it('keeps a Record refill run’s local machine columns out of the planning view’s provider machine ids (issues #794, #904)', async () => {
    const recordMultiMachineRefill: jest.Mock = jest.fn((request: MultiMachinePickListRefillRequest) =>
      of({
        idempotencyKey: request.idempotencyKey,
        replayed: false,
        notes: null,
        recordedAtUtc: '2026-01-01T00:00:00Z',
        allocations: request.allocations.map((allocation) => ({ ...allocation, stockAdjustmentId: allocation.productId * 100 }))
      })
    );
    const pickList = jest.fn(() => of(pickListResult()));
    const { fixture, requireElement, requireButton, click, openDropdown, checkOption, planFor } = await render(
      undefined,
      undefined,
      pickList,
      undefined,
      { localMachines: [localMachine({ id: 5, name: 'Front Lobby' })], recordMultiMachineRefill }
    );

    planFor('Machine A');
    expect(pickList).toHaveBeenCalledWith([10]);

    click(requireButton('Record refill'));
    // Record refill's own Machines dropdown is the business's local machines; the planning view's
    // is the provider fleet, and neither read ever receives the other's ids.
    openDropdown('Machines');
    checkOption('Front Lobby');
    const takenInput = requireElement<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]');
    takenInput.value = '2';
    takenInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    click(requireElement<HTMLButtonElement>('[data-testid="manual-pick-list-record"]'));

    expect(recordMultiMachineRefill).toHaveBeenCalledTimes(1);
    const submitted: MultiMachinePickListRefillRequest = recordMultiMachineRefill.mock.calls[0][0];
    expect(submitted.allocations).toEqual([{ machineId: 5, productId: 1, quantityTaken: 2 }]);
    expect(pickList).toHaveBeenCalledTimes(1);
    expect(pickList).toHaveBeenCalledWith([10]);
  });
});
