import { TestBed } from '@angular/core/testing';
import { convertToParamMap, ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { SiteProductsComponent } from './site-products.component';
import { SiteService } from '../../services/site.service';
import { Site, SiteProduct } from '../../models/models';
import { AccessService } from '../../access/access.service';
import { NayaxConnectionService, NayaxConnectionStatusName } from '../../services/nayax-connection.service';

function site(overrides: Partial<Site> = {}): Site {
  return {
    siteId: 1,
    siteName: 'Head Office',
    machineCount: 2,
    totalStockPercentage: 80,
    lowProductCount: 0,
    emptyProductCount: 0,
    todayRevenue: 0,
    currentWeekRevenue: 0,
    previousComparableWeekRevenue: 0,
    ...overrides
  };
}

function product(overrides: Partial<SiteProduct> = {}): SiteProduct {
  return {
    productId: 1,
    name: 'Coke',
    averageUnitCost: 1,
    sitePrice: 3,
    estimatedCardProfit: 1,
    quantityInStock: 10,
    maxStock: 20,
    mdbCode: 5,
    machineMdbCodes: [{ machineId: 10, machineLabel: 'Machine 10', mdbCode: 5 }],
    ...overrides
  };
}

function route(id = '1'): ActivatedRoute {
  return { paramMap: of(convertToParamMap({ id })) } as unknown as ActivatedRoute;
}

/** A stub `AccessService` for the direct-construction tests below, granted by default. */
function accessServiceStub(canViewFinancials = true): AccessService {
  return { allows: () => of(canViewFinancials) } as unknown as AccessService;
}

function createComponent(
  products: SiteProduct[],
  siteRoute: ActivatedRoute = route()
): SiteProductsComponent {
  const siteService = {
    getAll: () => of([site()]),
    getProducts: () => of(products)
  } as unknown as SiteService;

  const component = new SiteProductsComponent(siteRoute, siteService, accessServiceStub());
  component.ngOnInit();
  component.products$.subscribe();
  return component;
}

describe('SiteProductsComponent default sort', () => {
  it('defaults to ascending MDB Code with natural numeric ordering (2 before 10)', () => {
    const component = createComponent([
      product({ productId: 1, name: 'Chips', mdbCode: 10 }),
      product({ productId: 2, name: 'Coke', mdbCode: 2 }),
    ]);

    expect(component.sortColumn).toBe('mdbCode');
    expect(component.sortDirection).toBe('asc');
    expect(component.sortedProducts.map((p) => p.productId)).toEqual([2, 1]);
  });

  it('sorts a missing MDB code after every numeric code, in both directions', () => {
    const component = createComponent([
      product({ productId: 1, name: 'Chips', mdbCode: null }),
      product({ productId: 2, name: 'Coke', mdbCode: 2 }),
      product({ productId: 3, name: 'Water', mdbCode: 10 }),
    ]);

    expect(component.sortedProducts.map((p) => p.productId)).toEqual([2, 3, 1]);

    component.sortBy('mdbCode');

    expect(component.sortDirection).toBe('desc');
    expect(component.sortedProducts.map((p) => p.productId)).toEqual([3, 2, 1]);
  });

  it('breaks a tie between equal MDB codes deterministically by product name', () => {
    const component = createComponent([
      product({ productId: 1, name: 'Zest', mdbCode: 5 }),
      product({ productId: 2, name: 'Apple', mdbCode: 5 }),
    ]);

    expect(component.sortedProducts.map((p) => p.productId)).toEqual([2, 1]);
  });
});

describe('SiteProductsComponent column sort toggles', () => {
  it('sorts the clicked column ascending first, then toggles to descending on a second click', () => {
    const component = createComponent([
      product({ productId: 1, name: 'Chips', estimatedCardProfit: 1 }),
      product({ productId: 2, name: 'Coke', estimatedCardProfit: 3 }),
    ]);

    component.sortBy('estimatedCardProfit');
    expect(component.sortColumn).toBe('estimatedCardProfit');
    expect(component.sortDirection).toBe('asc');
    expect(component.sortedProducts.map((p) => p.productId)).toEqual([1, 2]);

    component.sortBy('estimatedCardProfit');
    expect(component.sortDirection).toBe('desc');
    expect(component.sortedProducts.map((p) => p.productId)).toEqual([2, 1]);
  });

  it('switching to a different column resets that column to ascending', () => {
    const component = createComponent([product(), product({ productId: 2 })]);
    // The default column is already 'mdbCode', so one toggle reaches descending.
    component.sortBy('mdbCode');
    expect(component.sortDirection).toBe('desc');

    component.sortBy('name');

    expect(component.sortColumn).toBe('name');
    expect(component.sortDirection).toBe('asc');
  });

  it('keeps a null averageUnitCost/estimatedCardProfit ("Unavailable") sorted after every numeric value', () => {
    const component = createComponent([
      product({ productId: 1, name: 'Chips', averageUnitCost: null, estimatedCardProfit: null }),
      product({ productId: 2, name: 'Coke', averageUnitCost: 1, estimatedCardProfit: 2 }),
    ]);

    component.sortBy('averageUnitCost');
    expect(component.sortedProducts.map((p) => p.productId)).toEqual([2, 1]);

    component.sortBy('averageUnitCost');
    expect(component.sortedProducts.map((p) => p.productId)).toEqual([2, 1]);
  });

  it('reports aria-sort and a sort-direction label only for the active column', () => {
    const component = createComponent([product()]);

    expect(component.ariaSort('mdbCode')).toBe('ascending');
    expect(component.ariaSort('name')).toBe('none');
    expect(component.sortLabel('MDB Code', 'mdbCode')).toBe('MDB Code (ascending)');
    expect(component.sortLabel('Name', 'name')).toBe('Name');

    component.sortBy('mdbCode');

    expect(component.ariaSort('mdbCode')).toBe('descending');
    expect(component.sortLabel('MDB Code', 'mdbCode')).toBe('MDB Code (descending)');
  });
});

describe('SiteProductsComponent multi-machine MDB codes', () => {
  it('keeps every machine/code pair visible for a product mapped on multiple machines, without deduplicating a repeated code', () => {
    const component = createComponent([
      product({
        productId: 1,
        name: 'Coke',
        mdbCode: 2,
        machineMdbCodes: [
          { machineId: 10, machineLabel: 'Lobby', mdbCode: 2 },
          { machineId: 11, machineLabel: 'Break Room', mdbCode: 2 },
        ]
      }),
    ]);

    const row = component.sortedProducts[0];
    expect(row.machineMdbCodes).toEqual([
      { machineId: 10, machineLabel: 'Lobby', mdbCode: 2 },
      { machineId: 11, machineLabel: 'Break Room', mdbCode: 2 },
    ]);
  });

  it('sorts by the row-level representative code (the lowest non-null per-machine code)', () => {
    const component = createComponent([
      product({
        productId: 1,
        name: 'Coke',
        mdbCode: 2,
        machineMdbCodes: [
          { machineId: 10, machineLabel: 'Lobby', mdbCode: 10 },
          { machineId: 11, machineLabel: 'Break Room', mdbCode: 2 },
        ]
      }),
      product({ productId: 2, name: 'Chips', mdbCode: 5, machineMdbCodes: [{ machineId: 10, machineLabel: 'Lobby', mdbCode: 5 }] }),
    ]);

    expect(component.sortedProducts.map((p) => p.productId)).toEqual([1, 2]);
  });
});

describe('SiteProductsComponent rendered table', () => {
  async function render(products: SiteProduct[], nayaxStatus: NayaxConnectionStatusName = 'Ready', canViewFinancials = true) {
    const siteService = { getAll: () => of([site()]), getProducts: () => of(products) } as unknown as SiteService;

    await TestBed.configureTestingModule({
      imports: [SiteProductsComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route() },
        { provide: SiteService, useValue: siteService },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: nayaxStatus, machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(canViewFinancials) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SiteProductsComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const click = (element: HTMLElement) => {
      element.click();
      fixture.detectChanges();
    };

    return { host, click };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('renders an MDB Code header and column, sorted ascending by default', async () => {
    const { host } = await render([
      product({ productId: 1, name: 'Chips', mdbCode: 10, machineMdbCodes: [{ machineId: 10, machineLabel: 'Lobby', mdbCode: 10 }] }),
      product({ productId: 2, name: 'Coke', mdbCode: 2, machineMdbCodes: [{ machineId: 10, machineLabel: 'Lobby', mdbCode: 2 }] }),
    ]);

    const headers = Array.from(host.querySelectorAll('thead th'));
    const headerLabels = headers.map((th) => th.textContent?.trim());
    expect(headerLabels).toEqual([
      'Name', 'MDB Code (ascending)', 'Average Unit Cost', 'Site Price', 'Estimated Card Profit', 'Stock'
    ]);
    expect(headers[1].getAttribute('aria-sort')).toBe('ascending');

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows[0].textContent).toContain('Coke');
    expect(rows[1].textContent).toContain('Chips');
  });

  it('shows every machine/code pair inside one MDB Code cell for a multi-machine product', async () => {
    const { host } = await render([
      product({
        productId: 1,
        name: 'Coke',
        mdbCode: 2,
        machineMdbCodes: [
          { machineId: 10, machineLabel: 'Lobby', mdbCode: 10 },
          { machineId: 11, machineLabel: 'Break Room', mdbCode: 2 },
        ]
      }),
    ]);

    const cell = host.querySelector('[data-testid="site-product-mdb-code"]');
    expect(cell?.textContent).toContain('Lobby: 10');
    expect(cell?.textContent).toContain('Break Room: 2');
  });

  it('renders a dash for a missing MDB code instead of leaving the cell blank or showing "null"', async () => {
    const { host } = await render([
      product({
        productId: 1,
        name: 'Coke',
        mdbCode: null,
        machineMdbCodes: [{ machineId: 10, machineLabel: 'Lobby', mdbCode: null }]
      }),
    ]);

    const cell = host.querySelector('[data-testid="site-product-mdb-code"]');
    expect(cell?.textContent).toContain('Lobby: —');
    expect(cell?.textContent).not.toContain('null');
  });

  it('toggles the sort direction and the visible row order when a header is clicked twice', async () => {
    const { host, click } = await render([
      product({ productId: 1, name: 'Chips', mdbCode: 10 }),
      product({ productId: 2, name: 'Coke', mdbCode: 2 }),
    ]);

    const mdbHeaderButton = Array.from(host.querySelectorAll<HTMLButtonElement>('thead button'))
      .find((button) => button.textContent?.includes('MDB Code'));
    if (mdbHeaderButton === undefined) {
      throw new Error('Expected an MDB Code sort header button.');
    }

    expect(Array.from(host.querySelectorAll('tbody tr'))[0].textContent).toContain('Coke');

    click(mdbHeaderButton);

    expect(mdbHeaderButton.closest('th')?.getAttribute('aria-sort')).toBe('descending');
    expect(Array.from(host.querySelectorAll('tbody tr'))[0].textContent).toContain('Chips');
  });

  it('shows the Nayax status banner when the connection is not Ready (issue #539)', async () => {
    const { host } = await render([product()], 'NotConfigured');

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).not.toBeNull();
  });

  it('hides the Nayax status banner once the connection is Ready (issue #539)', async () => {
    const { host } = await render([product()], 'Ready');

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).toBeNull();
  });
});

/**
 * `Dashboard.ViewFinancials` hides the Estimated Card Profit column, in the table and in the CSV
 * export (issue #699): a role without it - an Operator - sees and exports neither, while a
 * Manager or an Owner sees and exports it exactly as before.
 */
describe('SiteProductsComponent Estimated Card Profit gated on Dashboard.ViewFinancials (issue #699)', () => {
  async function render(products: SiteProduct[], canViewFinancials: boolean) {
    const siteService = { getAll: () => of([site()]), getProducts: () => of(products) } as unknown as SiteService;

    await TestBed.configureTestingModule({
      imports: [SiteProductsComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route() },
        { provide: SiteService, useValue: siteService },
        { provide: NayaxConnectionService, useValue: { status: () => of({ status: 'Ready', machineChecksPending: false }) } },
        { provide: AccessService, useValue: { allows: () => of(canViewFinancials) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SiteProductsComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('shows no Estimated Card Profit header or figure to an Operator, keeping unit cost and site price', async () => {
    const { host } = await render([product({ estimatedCardProfit: 1.5, averageUnitCost: 0.8, sitePrice: 3 })], false);

    expect(host.textContent).not.toContain('Estimated Card Profit');
    expect(host.textContent).not.toContain('$1.50');
    expect(host.textContent).toContain('$0.80');
    expect(host.textContent).toContain('$3.00');
  });

  it('keeps the Estimated Card Profit column exactly as today for a Manager or an Owner', async () => {
    const { host } = await render([product({ estimatedCardProfit: 1.5 })], true);

    expect(host.textContent).toContain('Estimated Card Profit');
    expect(host.textContent).toContain('$1.50');
  });

  it('renders without error when a granted role is sent a product with the figure absent', async () => {
    const { host } = await render([product({ estimatedCardProfit: null })], true);

    expect(host.textContent).toContain('Estimated Card Profit');
    expect(host.textContent).toContain('Unavailable');
    expect(host.textContent).not.toContain('NaN');
    expect(host.textContent).not.toContain('$0.00');
  });

  /** `URL.createObjectURL`/`revokeObjectURL` do not exist in jsdom, so this replaces them directly. */
  function captureExportedCsv(): { captured: () => Promise<string> } {
    let capturedBlob: Blob | null = null;
    (URL as unknown as { createObjectURL: (blob: Blob) => string }).createObjectURL = (blob: Blob) => {
      capturedBlob = blob;
      return 'blob:stub';
    };
    (URL as unknown as { revokeObjectURL: (url: string) => void }).revokeObjectURL = () => undefined;
    HTMLAnchorElement.prototype.click = jest.fn();

    return {
      captured: () =>
        new Promise<string>((resolve, reject) => {
          if (capturedBlob === null) {
            reject(new Error('Expected the export to have created a Blob.'));
            return;
          }
          const reader = new FileReader();
          reader.onload = () => resolve(reader.result as string);
          reader.onerror = () => reject(reader.error);
          reader.readAsText(capturedBlob);
        })
    };
  }

  it('drops Estimated Card Profit from the CSV export for an Operator', async () => {
    const { host } = await render([product({ estimatedCardProfit: 1.5 })], false);
    const { captured } = captureExportedCsv();

    const exportButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Export CSV');
    exportButton?.click();

    expect(await captured()).not.toContain('Estimated Card Profit');
  });

  it('keeps Estimated Card Profit in the CSV export for a Manager or an Owner', async () => {
    const { host } = await render([product({ estimatedCardProfit: 1.5 })], true);
    const { captured } = captureExportedCsv();

    const exportButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Export CSV');
    exportButton?.click();

    expect(await captured()).toContain('Estimated Card Profit');
  });
});
