import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { convertToParamMap, ActivatedRoute, ParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, Observable, of } from 'rxjs';
import { MachineDetailComponent } from './machine-detail.component';
import { Machine, MachineLatestSales, Product } from '../../models/models';
import { ConfigService } from '../../services/config.service';
import { MachineService } from '../../services/machine.service';
import { BreadcrumbService } from '../../layout/breadcrumbs/breadcrumb.service';
import { AccessService } from '../../access/access.service';
import { NayaxConnectionService } from '../../services/nayax-connection.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { startOfDayUtc } from '../../formatting/business-time-zone';

function supportSessionStub(active: Observable<boolean> = of(false)): SupportSessionService {
  return { active$: active } as unknown as SupportSessionService;
}

/** A stub `Router` for the direct-construction tests below, recording the navigations asked for. */
function routerStub(): { navigate: jest.Mock } & Router {
  return { navigate: jest.fn(() => Promise.resolve(true)) } as unknown as { navigate: jest.Mock } & Router;
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

/** An available window with no sales, for the Sales panel the page now composes (issue #768). */
function emptyLatestSales(machineId = 7): MachineLatestSales {
  return { machineId, isAvailable: true, unavailableReason: null, unavailableDetail: null, sales: [] };
}

describe('MachineDetailComponent product refresh', () => {
  it('reloads the machine products when the Sync Restock panel reports an applied restock', () => {
    const getProducts = jest.fn()
      .mockReturnValueOnce(of([product(10)]))
      .mockReturnValueOnce(of([product(8)]));
    const machineService = {
      get: () => of({ machineID: 7 } as Machine),
      getProducts,
      getLatestSales: () => of(emptyLatestSales())
    } as unknown as MachineService;
    const route = { paramMap: of(convertToParamMap({ id: '7' })), queryParamMap: of(convertToParamMap({})) } as unknown as ActivatedRoute;
    const component = new MachineDetailComponent(
      route,
      machineService,
      breadcrumbServiceStub(),
      supportSessionStub(),
      accessServiceStub(),
      routerStub()
    );

    component.ngOnInit();
    // Products are read when that tab is first opened, not on arrival (issue #768).
    component.openTab('products');
    expect(component.products[0].quantityInStock).toBe(10);

    component.refreshProducts();

    expect(getProducts).toHaveBeenCalledTimes(2);
    expect(getProducts).toHaveBeenLastCalledWith(7);
    expect(component.products[0].quantityInStock).toBe(8);
    expect(component.productsState).toBe('loaded');
  });
});

describe('MachineDetailComponent restockFrom query parameter (issue #948)', () => {
  function componentWith(queryParams: Record<string, string>) {
    const machineService = {
      get: () => of({ machineID: 7 } as Machine),
      getProducts: () => of([]),
      getLatestSales: () => of(emptyLatestSales())
    } as unknown as MachineService;
    const route = {
      paramMap: of(convertToParamMap({ id: '7' })),
      queryParamMap: of(convertToParamMap(queryParams))
    } as unknown as ActivatedRoute;
    const instance = new MachineDetailComponent(
      route, machineService, breadcrumbServiceStub(), supportSessionStub(), accessServiceStub(), routerStub());
    instance.ngOnInit();
    return instance;
  }

  it('passes the Dashboard reminder\'s restockFrom date on for Sync Restock', () => {
    expect(componentWith({ restockFrom: '2026-10-16' }).restockFrom).toBe('2026-10-16');
  });

  it('has no pending From date without the parameter', () => {
    expect(componentWith({}).restockFrom).toBeNull();
  });
});

/**
 * The two tabs the owner asked for (issue #768): Sales first and selected on arrival at every
 * machine, Products second and read only when it is opened.
 */
describe('MachineDetailComponent Sales and Products tabs (issue #768)', () => {
  function component(getProducts = jest.fn(() => of([product(10)]))) {
    const machineService = {
      get: () => of({ machineID: 7 } as Machine),
      getProducts,
      getLatestSales: () => of(emptyLatestSales())
    } as unknown as MachineService;
    const route = { paramMap: of(convertToParamMap({ id: '7' })), queryParamMap: of(convertToParamMap({})) } as unknown as ActivatedRoute;
    const instance = new MachineDetailComponent(
      route,
      machineService,
      breadcrumbServiceStub(),
      supportSessionStub(),
      accessServiceStub(),
      routerStub()
    );
    instance.ngOnInit();
    return { instance, getProducts };
  }

  it('opens on Sales, with Sales listed before Products', () => {
    const { instance } = component();

    expect(instance.activeTab).toBe('sales');
    expect(instance.tabs).toEqual(['sales', 'products']);
  });

  /**
   * The whole point of the lazy read: arriving on a machine makes the Sales request and the
   * machine-products request is not made at all.
   */
  it('makes no machine-products request until the Products tab is opened', () => {
    const { instance, getProducts } = component();

    expect(getProducts).not.toHaveBeenCalled();

    instance.openTab('products');

    expect(getProducts).toHaveBeenCalledTimes(1);
  });

  it('does not repeat the machine-products request when the tabs are switched again', () => {
    const { instance, getProducts } = component();

    instance.openTab('products');
    instance.openTab('sales');
    instance.openTab('products');

    expect(getProducts).toHaveBeenCalledTimes(1);
  });

  /**
   * Nothing to refresh while the table has never been shown, so an applied restock does not make a
   * Nayax call for a tab nobody has opened; opening it later reads the current quantities anyway.
   */
  it('does not reload products after a restock while the Products tab has never been opened', () => {
    const { instance, getProducts } = component();

    instance.refreshProducts();

    expect(getProducts).not.toHaveBeenCalled();
  });

  it('selects the tab an arrow key moves to', () => {
    const { instance } = component();
    const event = { key: 'ArrowRight', preventDefault: jest.fn() } as unknown as KeyboardEvent;

    instance.onTabKeydown(event, 0);

    expect(instance.activeTab).toBe('products');
    expect(event.preventDefault).toHaveBeenCalled();
  });

  it('wraps from the last tab back to the first, and Home and End jump to the ends', () => {
    const { instance } = component();
    const key = (name: string) => ({ key: name, preventDefault: jest.fn() }) as unknown as KeyboardEvent;

    instance.onTabKeydown(key('ArrowRight'), 1);
    expect(instance.activeTab).toBe('sales');

    instance.onTabKeydown(key('End'), 0);
    expect(instance.activeTab).toBe('products');

    instance.onTabKeydown(key('Home'), 1);
    expect(instance.activeTab).toBe('sales');

    instance.onTabKeydown(key('ArrowLeft'), 0);
    expect(instance.activeTab).toBe('products');
  });

  it('ignores a key the tab pattern does not use, leaving the selection and the default action alone', () => {
    const { instance } = component();
    const event = { key: 'a', preventDefault: jest.fn() } as unknown as KeyboardEvent;

    instance.onTabKeydown(event, 0);

    expect(instance.activeTab).toBe('sales');
    expect(event.preventDefault).not.toHaveBeenCalled();
  });
});

/**
 * The Products panel's one read, counted as real HTTP requests through the rendered page (issue
 * #768).
 *
 * <p>Counting the service double's invocations is not enough here: the panel's row count and its
 * table are two bindings over the same read, and an unshared cold `HttpClient` observable bound
 * twice issues the request twice however often the component asked for it once. These cases use the
 * real `MachineService` over `HttpTestingController`, click the real tab buttons, and count the
 * requests that actually left the page.</p>
 */
describe('MachineDetailComponent Products panel read (issue #768)', () => {
  const productsUrl = (machineId: number) => `/api/machines/${machineId}/products`;

  interface Rendered {
    readonly fixture: ComponentFixture<MachineDetailComponent>;
    readonly host: HTMLElement;
    readonly httpMock: HttpTestingController;
    readonly paramMap$: BehaviorSubject<ParamMap>;
  }

  async function renderPage(machineId = 7): Promise<Rendered> {
    const paramMap$ = new BehaviorSubject(convertToParamMap({ id: String(machineId) }));
    await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfigService, useValue: { apiBaseUrl: '/api' } },
        { provide: ActivatedRoute, useValue: { paramMap: paramMap$, queryParamMap: of(convertToParamMap({})) } },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: supportSessionStub() }
      ]
    }).compileComponents();

    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(MachineDetailComponent);
    const rendered = { fixture, host: fixture.nativeElement as HTMLElement, httpMock, paramMap$ };
    answerPageReads(rendered, machineId);
    return rendered;
  }

  /**
   * The page's own machine read and the Sales panel's latest-sales read - everything this spec is
   * not counting - answered so that the tabs render at all.
   */
  function answerPageReads({ fixture, httpMock }: Rendered, machineId: number): void {
    fixture.detectChanges(false);
    // The route id is the local machine (issue #741); the provider's number is a different one.
    httpMock
      .match(`/api/machines/${machineId}`)
      .forEach((request) => request.flush({ machineID: machineId + 9000, localMachineId: machineId } as Machine));
    fixture.detectChanges(false);
    httpMock
      .match(`/api/machines/${machineId}/latest-sales`)
      .forEach((request) => request.flush(emptyLatestSales(machineId)));
    fixture.detectChanges(false);
  }

  function clickTab({ fixture, host }: Rendered, tab: 'sales' | 'products'): void {
    (host.querySelector(`[data-testid="machine-tab-${tab}"]`) as HTMLButtonElement).click();
    fixture.detectChanges(false);
  }

  afterEach(() => TestBed.resetTestingModule());

  it('issues no products request while the page opens on Sales', async () => {
    const rendered = await renderPage();

    expect(rendered.httpMock.match(productsUrl(7))).toHaveLength(0);
  });

  /** The repaired defect: two template bindings over one read, and one request on the wire. */
  it('issues exactly one products request when the Products tab is opened', async () => {
    const rendered = await renderPage();

    clickTab(rendered, 'products');
    const requests = rendered.httpMock.match(productsUrl(7));

    expect(requests).toHaveLength(1);

    requests[0].flush([product(10)]);
    rendered.fixture.detectChanges(false);

    expect(rendered.host.querySelector('[data-testid="machine-products-count"]')!.textContent)
      .toContain('1 products found');
    expect(rendered.host.textContent).toContain('Beef Jerky');
  });

  it('issues no further products request when the tabs are switched again', async () => {
    const rendered = await renderPage();

    clickTab(rendered, 'products');
    rendered.httpMock.match(productsUrl(7)).forEach((request) => request.flush([product(10)]));
    rendered.fixture.detectChanges(false);

    clickTab(rendered, 'sales');
    clickTab(rendered, 'products');

    expect(rendered.httpMock.match(productsUrl(7))).toHaveLength(0);
    expect(rendered.host.textContent).toContain('Beef Jerky');
  });

  /**
   * An outstanding read is not an answered one: "No products" while the request is still in flight
   * is the page claiming something it has not been told.
   */
  it('shows a loading state while the products read is in flight, and no count or empty state', async () => {
    const rendered = await renderPage();

    clickTab(rendered, 'products');
    const request = rendered.httpMock.expectOne(productsUrl(7));

    expect(rendered.host.querySelector('[data-testid="machine-products-loading"]')).not.toBeNull();
    expect(rendered.host.querySelector('[data-testid="machine-products-empty"]')).toBeNull();
    expect(rendered.host.querySelector('[data-testid="machine-products-count"]')).toBeNull();

    request.flush([]);
    rendered.fixture.detectChanges(false);

    expect(rendered.host.querySelector('[data-testid="machine-products-loading"]')).toBeNull();
    expect(rendered.host.querySelector('[data-testid="machine-products-empty"]')).not.toBeNull();
    expect(rendered.host.querySelector('[data-testid="machine-products-count"]')!.textContent)
      .toContain('0 products found');
  });

  it('shows a failed products read as a failure rather than as a machine with no products', async () => {
    const rendered = await renderPage();

    clickTab(rendered, 'products');
    rendered.httpMock
      .expectOne(productsUrl(7))
      .flush('unavailable', { status: 500, statusText: 'Server Error' });
    rendered.fixture.detectChanges(false);

    const failure = rendered.host.querySelector('[data-testid="machine-products-error"]')!;
    expect(failure).not.toBeNull();
    expect(failure.textContent).toContain('does not mean the machine has no products');
    expect(rendered.host.querySelector('[data-testid="machine-products-empty"]')).toBeNull();
    expect(rendered.host.querySelector('[data-testid="machine-products-count"]')).toBeNull();
  });

  /**
   * Navigating to a different machine cancels the outstanding read - so the previous machine's rows
   * cannot arrive into this table at all - and leaves the panel unrequested again.
   */
  it('cancels an in-flight products read and resets the panel when the machine changes', async () => {
    const rendered = await renderPage();

    clickTab(rendered, 'products');
    const machineSevenProducts = rendered.httpMock.expectOne(productsUrl(7));

    rendered.paramMap$.next(convertToParamMap({ id: '8' }));
    answerPageReads(rendered, 8);

    expect(machineSevenProducts.cancelled).toBe(true);
    expect(rendered.host.querySelector('[data-testid="machine-products-loading"]')).toBeNull();
    expect(rendered.host.querySelector('[data-testid="machine-products-empty"]')).toBeNull();
    expect(rendered.host.querySelector('[data-testid="machine-products-count"]')).toBeNull();
    expect(rendered.httpMock.match(productsUrl(8))).toHaveLength(0);

    // The new machine's products are read when its Products tab is opened, and only then.
    clickTab(rendered, 'products');

    expect(rendered.httpMock.match(productsUrl(8))).toHaveLength(1);
  });
});

/**
 * The breadcrumb's live label for `/machines/:id` (issue #457): the already-loaded `machine$` data
 * this component already requests for its own page, never a second request made just for the
 * breadcrumb.
 */
describe('MachineDetailComponent breadcrumb label', () => {
  function render(machine: Machine | null, id = '7'): { breadcrumbService: { setCurrentPageLabel: jest.Mock } } {
    const machineService = {
      get: () => of(machine),
      getProducts: () => of([]),
      getLatestSales: () => of(emptyLatestSales())
    } as unknown as MachineService;
    const route = { paramMap: of(convertToParamMap({ id })), queryParamMap: of(convertToParamMap({})) } as unknown as ActivatedRoute;
    const breadcrumbService = breadcrumbServiceStub();
    const component = new MachineDetailComponent(
      route, machineService, breadcrumbService, supportSessionStub(), accessServiceStub(), routerStub()
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
          useValue: { paramMap: of(convertToParamMap({ id: '7' })), queryParamMap: of(convertToParamMap({})) }
        },
        {
          provide: MachineService,
          useValue: {
            get: () => of({ machineID: 7 } as Machine),
            getProducts: () => of([]),
            getLatestSales: () => of(emptyLatestSales())
          }
        },
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
 * The "Record refill" link that replaced the per-product Restock control (issue #744's
 * machine-page consolidation). It is hidden during a support session on the same reasoning Restock
 * was, and the removed control's quantity input and button are gone from the page entirely.
 */
describe('MachineDetailComponent Record refill link (issue #744 machine-page consolidation)', () => {
  async function render(sessionActive: Observable<boolean> = of(false)) {
    await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: '7' })), queryParamMap: of(convertToParamMap({})) } },
        {
          provide: MachineService,
          useValue: {
            // The provider's own number (900) is never what the link must carry - only the local
            // id (7), which GetById already resolves onto every response (issue #741).
            get: () => of({ machineID: 900, localMachineId: 7 } as Machine),
            getProducts: () => of([product(8)]),
            getLatestSales: () => of(emptyLatestSales())
          }
        },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: supportSessionStub(sessionActive) }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineDetailComponent);
    fixture.detectChanges(false);
    // The product table lives in the Products tab, which is not the tab a machine opens on since
    // issue #768.
    fixture.componentInstance.openTab('products');
    fixture.detectChanges(false);
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('links Record refill to manual pick-list entry with the local machine id, never the provider number', async () => {
    const { host } = await render(of(false));

    const link = host.querySelector<HTMLAnchorElement>('[data-testid="machine-record-refill-link"]');
    expect(link).not.toBeNull();
    expect(link!.textContent?.trim()).toBe('Record refill');
    expect(link!.getAttribute('href')).toBe('/pick-list?mode=manual&machineId=7');
  });

  it('hides the Record refill link while a support session is active', async () => {
    const { host } = await render(of(true));

    expect(host.querySelector('[data-testid="machine-record-refill-link"]')).toBeNull();
  });

  it('shows the Record refill link while no session is active', async () => {
    const { host } = await render(of(false));

    expect(host.querySelector('[data-testid="machine-record-refill-link"]')).not.toBeNull();
  });

  it('renders no Restock button or "Taken"/quantity input at all: the control was removed, not hidden', async () => {
    const { host } = await render(of(false));

    expect(Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Restock')).toBeUndefined();
    expect(host.querySelector('input[type="number"]')).toBeNull();
  });

  it('no longer exposes restockProduct()', async () => {
    const { fixture } = await render(of(false));

    expect((fixture.componentInstance as unknown as { restockProduct?: unknown }).restockProduct).toBeUndefined();
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
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: '7' })), queryParamMap: of(convertToParamMap({})) } },
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
            getProducts: () => of(products),
            getLatestSales: () => of(emptyLatestSales())
          }
        },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(canViewFinancials) } },
        { provide: SupportSessionService, useValue: supportSessionStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(MachineDetailComponent);
    fixture.detectChanges(false);
    // The product columns are in the Products tab (issue #768).
    fixture.componentInstance.openTab('products');
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

  it('draws each summary tile as a white stat card rather than a gray tile lost on the gray page canvas', async () => {
    const host = await render(true);

    const tiles = Array.from(host.querySelector('[data-testid="machine-summary-cards"]')?.children ?? []);
    expect(tiles.map((tile) => tile.textContent)).toEqual([
      expect.stringContaining('Machine'),
      expect.stringContaining('Today Gross'),
      expect.stringContaining('Today Direct Profit'),
      expect.stringContaining('Current Week Gross')
    ]);
    for (const tile of tiles) {
      expect(tile.classList).toContain('stat-card');
      expect(tile.classList).not.toContain('bg-md-gray-100');
    }
  });

  it('renders without error when a granted role is sent a machine and product with the figures absent', async () => {
    const host = await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: '7' })), queryParamMap: of(convertToParamMap({})) } },
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
            getProducts: () => of([fullProduct({ suggestedNetValue: null, suggestedPriceValue: null })]),
            getLatestSales: () => of(emptyLatestSales())
          }
        },
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
        fixture.componentInstance.openTab('products');
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

/**
 * The Dashboard reminder's "Open Sync Restock" link (issue #1011): arriving with
 * `?syncRestock=open` opens the machine's Sync Restock dialog by itself, through the rendered page
 * and the real `MachineService`, and the sync-restock requests that actually leave the page are
 * counted - opening the dialog is a Nayax fetch, so it must happen exactly once per click.
 */
describe('MachineDetailComponent Open Sync Restock from the Dashboard (issue #1011)', () => {
  const BUSINESS_TIME_ZONE = 'Australia/Sydney';
  const syncRestockUrl = (machineId: number) => `/api/machines/${machineId}/sync-restock`;

  interface Rendered {
    readonly fixture: ComponentFixture<MachineDetailComponent>;
    readonly host: HTMLElement;
    readonly httpMock: HttpTestingController;
    readonly paramMap$: BehaviorSubject<ParamMap>;
    readonly queryParamMap$: BehaviorSubject<ParamMap>;
    readonly route: { snapshot: { paramMap: ParamMap } };
    readonly navigate: jest.SpyInstance;
    readonly timeZone: BusinessTimeZoneService;
  }

  async function renderPage(
    queryParams: Record<string, string>,
    { machineId = 7, timeZoneKnown = true, sessionActive = false } = {}
  ): Promise<Rendered> {
    const paramMap$ = new BehaviorSubject(convertToParamMap({ id: String(machineId) }));
    const queryParamMap$ = new BehaviorSubject(convertToParamMap(queryParams));
    // The router moves the snapshot before it emits, which is what the page reads the machine from.
    const route = {
      paramMap: paramMap$,
      queryParamMap: queryParamMap$,
      snapshot: { paramMap: paramMap$.value }
    };
    paramMap$.subscribe((params) => (route.snapshot.paramMap = params));

    await TestBed.configureTestingModule({
      imports: [MachineDetailComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfigService, useValue: { apiBaseUrl: '/api' } },
        { provide: ActivatedRoute, useValue: route },
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub() },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: SupportSessionService, useValue: supportSessionStub(of(sessionActive)) }
      ]
    }).compileComponents();

    const timeZone = TestBed.inject(BusinessTimeZoneService);
    if (timeZoneKnown) {
      timeZone.publish(BUSINESS_TIME_ZONE);
    }
    const navigate = jest.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(MachineDetailComponent);
    const host = fixture.nativeElement as HTMLElement;
    document.body.appendChild(host);
    const rendered = { fixture, host, httpMock, paramMap$, queryParamMap$, route, navigate, timeZone };
    answerPageReads(rendered, machineId);
    return rendered;
  }

  function answerPageReads({ fixture, httpMock }: Rendered, machineId: number): void {
    fixture.detectChanges();
    httpMock
      .match(`/api/machines/${machineId}`)
      .forEach((request) => request.flush({ machineID: machineId + 9000, localMachineId: machineId } as Machine));
    fixture.detectChanges();
    httpMock
      .match(`/api/machines/${machineId}/latest-sales`)
      .forEach((request) => request.flush(emptyLatestSales(machineId)));
    fixture.detectChanges();
  }

  /** The sync-restock requests made so far, each answered with an empty preview. */
  function flushSyncRequests({ fixture, httpMock }: Rendered, machineId = 7) {
    const requests = httpMock.match((request) => request.url === syncRestockUrl(machineId));
    requests.forEach((request) =>
      request.flush({ machineId, newEventCount: 0, events: [], productImpacts: [], hiddenReconciledCount: 0, message: null }));
    fixture.detectChanges();
    return requests;
  }

  const dialog = ({ host }: Rendered) => host.querySelector<HTMLElement>('[role="dialog"]');

  beforeEach(() => jest.useFakeTimers({ doNotFake: ['nextTick', 'setImmediate', 'queueMicrotask'] })
    .setSystemTime(new Date('2026-10-28T01:00:00Z'))); // 28 Oct in Sydney; the default From is 25 Oct

  afterEach(() => {
    jest.useRealTimers();
    document.body.innerHTML = '';
    TestBed.resetTestingModule();
  });

  it('opens the dialog at an older pending date with exactly one sync request', async () => {
    const rendered = await renderPage({ syncRestock: 'open', restockFrom: '2026-10-16' });

    const requests = flushSyncRequests(rendered);

    expect(dialog(rendered)).not.toBeNull();
    expect(requests).toHaveLength(1);
    expect(requests[0].request.method).toBe('POST');
    expect(requests[0].request.params.get('fromDate')).toBe(startOfDayUtc(2026, 10, 16, BUSINESS_TIME_ZONE).toISOString());
  });

  it('opens the dialog at the usual default when the link has no pending date', async () => {
    const rendered = await renderPage({ syncRestock: 'open' });

    const requests = flushSyncRequests(rendered);

    expect(dialog(rendered)).not.toBeNull();
    expect(requests).toHaveLength(1);
    expect(requests[0].request.params.get('fromDate')).toBe(startOfDayUtc(2026, 10, 25, BUSINESS_TIME_ZONE).toISOString());
  });

  it('keeps the usual default for a later or malformed pending date', async () => {
    const rendered = await renderPage({ syncRestock: 'open', restockFrom: 'not-a-date' });

    const requests = flushSyncRequests(rendered);

    expect(requests).toHaveLength(1);
    expect(requests[0].request.params.get('fromDate')).toBe(startOfDayUtc(2026, 10, 25, BUSINESS_TIME_ZONE).toISOString());
  });

  it('takes the request out of the address, keeping restockFrom, without a new history entry', async () => {
    const rendered = await renderPage({ syncRestock: 'open', restockFrom: '2026-10-16' });

    expect(rendered.navigate).toHaveBeenCalledTimes(1);
    const [commands, extras] = rendered.navigate.mock.calls[0];
    expect(commands).toEqual([]);
    expect(extras).toEqual(expect.objectContaining({
      queryParams: { syncRestock: null },
      queryParamsHandling: 'merge',
      replaceUrl: true
    }));
  });

  it('leaves the dialog closed on an ordinary visit, even with a pending date', async () => {
    const rendered = await renderPage({ restockFrom: '2026-10-16' });

    expect(dialog(rendered)).toBeNull();
    expect(flushSyncRequests(rendered)).toHaveLength(0);
    expect(rendered.navigate).not.toHaveBeenCalled();
  });

  it('does not reopen or sync again after it is closed and the page updates', async () => {
    const rendered = await renderPage({ syncRestock: 'open' });
    flushSyncRequests(rendered);

    (rendered.host.querySelector('[aria-label="Close Sync Restock"]') as HTMLButtonElement).click();
    rendered.fixture.detectChanges();
    // The address the page itself asked for, and a further update of the same machine.
    rendered.queryParamMap$.next(convertToParamMap({}));
    rendered.paramMap$.next(convertToParamMap({ id: '7' }));
    answerPageReads(rendered, 7);

    expect(dialog(rendered)).toBeNull();
    expect(flushSyncRequests(rendered)).toHaveLength(0);
  });

  it('waits for the business time zone instead of opening into its loading error', async () => {
    const rendered = await renderPage({ syncRestock: 'open' }, { timeZoneKnown: false });

    expect(dialog(rendered)).toBeNull();
    expect(flushSyncRequests(rendered)).toHaveLength(0);

    rendered.timeZone.publish(BUSINESS_TIME_ZONE);
    rendered.fixture.detectChanges();

    expect(dialog(rendered)).not.toBeNull();
    expect(dialog(rendered)?.textContent).not.toContain('Loading the business time zone');
    expect(flushSyncRequests(rendered)).toHaveLength(1);
  });

  it('opens nothing and makes no request during a support session', async () => {
    const rendered = await renderPage({ syncRestock: 'open' }, { sessionActive: true });

    expect(dialog(rendered)).toBeNull();
    expect(flushSyncRequests(rendered)).toHaveLength(0);
  });

  it('never shows the previous machine\'s dialog after moving to another machine', async () => {
    const rendered = await renderPage({ syncRestock: 'open' });
    flushSyncRequests(rendered);
    expect(dialog(rendered)).not.toBeNull();

    rendered.queryParamMap$.next(convertToParamMap({}));
    rendered.paramMap$.next(convertToParamMap({ id: '8' }));
    answerPageReads(rendered, 8);

    expect(dialog(rendered)).toBeNull();
    expect(flushSyncRequests(rendered, 8)).toHaveLength(0);
  });

  it('cancels the previous machine\'s sync request when the page moves to another machine', async () => {
    const rendered = await renderPage({ syncRestock: 'open' });
    const inFlight = rendered.httpMock.match((request) => request.url === syncRestockUrl(7));
    expect(inFlight).toHaveLength(1);

    rendered.queryParamMap$.next(convertToParamMap({}));
    rendered.paramMap$.next(convertToParamMap({ id: '8' }));
    answerPageReads(rendered, 8);

    expect(inFlight[0].cancelled).toBe(true);
    expect(dialog(rendered)).toBeNull();
    expect(rendered.host.querySelector<HTMLButtonElement>('button[aria-haspopup="dialog"]')?.textContent?.trim())
      .toBe('Sync Restock');
  });

  it('moves focus into the dialog and returns it to Sync Restock on close', async () => {
    const rendered = await renderPage({ syncRestock: 'open' });
    flushSyncRequests(rendered);

    expect(document.activeElement).toBe(dialog(rendered));

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    rendered.fixture.detectChanges();

    expect(dialog(rendered)).toBeNull();
    expect((document.activeElement as HTMLElement | null)?.textContent?.trim()).toBe('Sync Restock');
  });
});
