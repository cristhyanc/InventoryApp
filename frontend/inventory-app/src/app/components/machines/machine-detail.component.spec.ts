import { TestBed } from '@angular/core/testing';
import { convertToParamMap, ActivatedRoute, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { MachineDetailComponent } from './machine-detail.component';
import { Machine, Product } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';
import { BreadcrumbService } from '../../layout/breadcrumbs/breadcrumb.service';
import { AccessService } from '../../access/access.service';
import { NayaxConnectionService } from '../../services/nayax-connection.service';
import { SupportSessionService } from '../../support-session/support-session.service';

function supportSessionStub(active: Observable<boolean> = of(false)): SupportSessionService {
  return { active$: active } as unknown as SupportSessionService;
}

/** A stub `AccessService` for the direct-construction tests below, granted by default. */
function accessServiceStub(canViewFinancials = true): AccessService {
  return { allows: () => of(canViewFinancials) } as unknown as AccessService;
}

function product(quantityInStock: number): Product {
  return { id: 55, name: 'Beef Jerky', quantityInStock } as Product;
}

function breadcrumbServiceStub(): { setCurrentPageLabel: jest.Mock } & BreadcrumbService {
  return { setCurrentPageLabel: jest.fn() } as unknown as { setCurrentPageLabel: jest.Mock } & BreadcrumbService;
}

describe('MachineDetailComponent product refresh', () => {
  it('reloads the machine products when the Sync Restock panel reports an applied restock', () => {
    const getProducts = jest.fn()
      .mockReturnValueOnce(of([product(10)]))
      .mockReturnValueOnce(of([product(8)]));
    const machineService = {
      get: () => of({ machineID: 7 } as Machine),
      getProducts
    } as unknown as MachineService;
    const route = { paramMap: of(convertToParamMap({ id: '7' })) } as unknown as ActivatedRoute;
    const component = new MachineDetailComponent(
      route,
      machineService,
      {} as StockService,
      {} as ToastService,
      breadcrumbServiceStub(),
      supportSessionStub(),
      accessServiceStub()
    );

    component.ngOnInit();
    let products: Product[] = [];
    component.products$.subscribe((p) => (products = p));
    expect(products[0].quantityInStock).toBe(10);

    component.refreshProducts();
    component.products$.subscribe((p) => (products = p));

    expect(getProducts).toHaveBeenCalledTimes(2);
    expect(getProducts).toHaveBeenLastCalledWith(7);
    expect(products[0].quantityInStock).toBe(8);
  });
});

/**
 * The breadcrumb's live label for `/machines/:id` (issue #457): the already-loaded `machine$` data
 * this component already requests for its own page, never a second request made just for the
 * breadcrumb.
 */
describe('MachineDetailComponent breadcrumb label', () => {
  function render(machine: Machine | null, id = '7'): { breadcrumbService: { setCurrentPageLabel: jest.Mock } } {
    const machineService = { get: () => of(machine), getProducts: () => of([]) } as unknown as MachineService;
    const route = { paramMap: of(convertToParamMap({ id })) } as unknown as ActivatedRoute;
    const breadcrumbService = breadcrumbServiceStub();
    const component = new MachineDetailComponent(
      route, machineService, {} as StockService, {} as ToastService, breadcrumbService, supportSessionStub(), accessServiceStub()
    );

    component.ngOnInit();
    component.machine$.subscribe();

    return { breadcrumbService };
  }

  it('sets the live breadcrumb label from the already-loaded machine name', () => {
    const { breadcrumbService } = render({ machineID: 7, machineName: 'Snack Attack 42' } as Machine);

    expect(breadcrumbService.setCurrentPageLabel).toHaveBeenCalledWith('Snack Attack 42');
  });

  it('clears the live label rather than inventing one for an unnamed machine', () => {
    const { breadcrumbService } = render({ machineID: 7, machineName: null } as Machine);

    expect(breadcrumbService.setCurrentPageLabel).toHaveBeenCalledWith(null);
  });

  it('clears the live label when the machine fails to load', () => {
    const { breadcrumbService } = render(null, 'not-a-number');

    expect(breadcrumbService.setCurrentPageLabel).toHaveBeenCalledWith(null);
  });
});

/** The shared Nayax status banner (issue #539), composed above the Sync Restock panel. */
describe('MachineDetailComponent Nayax status banner (issue #539)', () => {
  async function renderPage(status: 'Ready' | 'NotConfigured') {
    await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of(convertToParamMap({ id: '7' })) }
        },
        {
          provide: MachineService,
          useValue: { get: () => of({ machineID: 7 } as Machine), getProducts: () => of([]) }
        },
        { provide: StockService, useValue: {} },
        { provide: ToastService, useValue: { success: jest.fn(), warning: jest.fn(), error: jest.fn() } },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status, machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: supportSessionStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineDetailComponent);
    // `machine$`'s own synchronous `of(...)` source resolves `loading$` to `false` within this
    // same pass, which the fixture's default post-check would otherwise read as a changed-after-
    // checked binding; that is this page's pre-existing loading-state shape, unrelated to the
    // banner under test here.
    fixture.detectChanges(false);
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the banner when the connection is not Ready', async () => {
    const host = await renderPage('NotConfigured');

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).not.toBeNull();
  });

  it('hides the banner once the connection is Ready', async () => {
    const host = await renderPage('Ready');

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).toBeNull();
  });
});

/**
 * Restock while a support session is active (issue #658). A session grants `InventoryOperate`, so
 * this write would otherwise still render.
 */
describe('MachineDetailComponent write control during a support session (issue #658)', () => {
  async function render(sessionActive: Observable<boolean>) {
    const stockAdjust = jest.fn(() => of({}));
    await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: '7' })) } },
        {
          provide: MachineService,
          useValue: {
            get: () => of({ machineID: 7 } as Machine),
            getProducts: () => of([product(8)])
          }
        },
        { provide: StockService, useValue: { adjust: stockAdjust } },
        { provide: ToastService, useValue: { success: jest.fn(), warning: jest.fn(), error: jest.fn() } },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: supportSessionStub(sessionActive) }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineDetailComponent);
    fixture.detectChanges(false);
    return { fixture, host: fixture.nativeElement as HTMLElement, stockAdjust };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('hides the Restock control while a session is active', async () => {
    const { host } = await render(of(true));

    expect(Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Restock')).toBeUndefined();
  });

  it('shows the Restock control while no session is active', async () => {
    const { host } = await render(of(false));

    expect(Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Restock')).toBeDefined();
  });

  it('does not write the restock while a session is active, even if restockProduct() is invoked directly', async () => {
    const { fixture, stockAdjust } = await render(of(true));

    fixture.componentInstance.restockProduct(product(8), { machineID: 7 } as Machine, 5);

    expect(stockAdjust).not.toHaveBeenCalled();
  });
});

/**
 * `Dashboard.ViewFinancials` hides the revenue/direct-profit summary cards and the products
 * table's Estimated Card Profit/50% Card Margin Price columns (issue #699): a role without it -
 * an Operator - sees neither, while a Manager or an Owner sees them exactly as before.
 */
describe('MachineDetailComponent financial figures gated on Dashboard.ViewFinancials (issue #699)', () => {
  function fullProduct(overrides: Partial<Product> = {}): Product {
    return {
      id: 55,
      name: 'Beef Jerky',
      mdbCode: 12,
      averageUnitCost: 2,
      machinePrice: 5,
      suggestedNetValue: 1.5,
      suggestedPriceValue: 4.75,
      quantityInStock: 10,
      maxStockInMachine: 20,
      ...overrides
    } as Product;
  }

  async function render(canViewFinancials: boolean, products: Product[] = [fullProduct()]) {
    await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: '7' })) } },
        {
          provide: MachineService,
          useValue: {
            get: () =>
              of({
                machineID: 7,
                machineName: 'Snack Attack 42',
                todayGrossRevenue: 88,
                currentWeekGrossRevenue: 444,
                todayDirectProfit: 30
              } as Machine),
            getProducts: () => of(products)
          }
        },
        { provide: StockService, useValue: {} },
        { provide: ToastService, useValue: { success: jest.fn(), warning: jest.fn(), error: jest.fn() } },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(canViewFinancials) } },
        { provide: SupportSessionService, useValue: supportSessionStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineDetailComponent);
    fixture.detectChanges(false);
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('shows no revenue, profit, margin or estimated-profit text to an Operator', async () => {
    const host = await render(false);

    expect(host.textContent).not.toContain('Today Gross');
    expect(host.textContent).not.toContain('Today Direct Profit');
    expect(host.textContent).not.toContain('Current Week Gross');
    expect(host.textContent).not.toContain('Estimated Card Profit');
    expect(host.textContent).not.toContain('50% Card Margin Price');
    expect(host.textContent).not.toContain('$88.00');
    expect(host.textContent).not.toContain('$1.50');
    expect(host.textContent).toContain('Snack Attack 42');
    expect(host.textContent).toContain('Beef Jerky');
  });

  it('keeps the summary cards and product columns exactly as today for a Manager or an Owner', async () => {
    const host = await render(true);

    expect(host.textContent).toContain('Today Gross');
    expect(host.textContent).toContain('$88.00');
    expect(host.textContent).toContain('Estimated Card Profit');
    expect(host.textContent).toContain('50% Card Margin Price');
    expect(host.textContent).toContain('$1.50');
    expect(host.textContent).toContain('$4.75');
  });

  it('renders without error when a granted role is sent a machine and product with the figures absent', async () => {
    const host = await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: '7' })) } },
        {
          provide: MachineService,
          useValue: {
            get: () =>
              of({
                machineID: 7,
                machineName: 'Unknown figures',
                todayGrossRevenue: null,
                currentWeekGrossRevenue: null,
                todayDirectProfit: null
              } as Machine),
            getProducts: () => of([fullProduct({ suggestedNetValue: null, suggestedPriceValue: null })])
          }
        },
        { provide: StockService, useValue: {} },
        { provide: ToastService, useValue: { success: jest.fn(), warning: jest.fn(), error: jest.fn() } },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: supportSessionStub() }
      ]
    })
      .compileComponents()
      .then(() => {
        const fixture = TestBed.createComponent(MachineDetailComponent);
        fixture.detectChanges(false);
        return fixture.nativeElement as HTMLElement;
      });

    expect(host.textContent).toContain('Unknown figures');
    expect(host.textContent).toContain('Unavailable');
    expect(host.textContent).toContain('Profit unavailable');
    expect(host.textContent).not.toContain('NaN');
    expect(host.textContent).not.toContain('$0.00');
  });
});
