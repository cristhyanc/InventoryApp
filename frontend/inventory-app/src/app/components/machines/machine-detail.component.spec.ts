import { TestBed } from '@angular/core/testing';
import { convertToParamMap, ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { MachineDetailComponent } from './machine-detail.component';
import { Machine, Product } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';
import { BreadcrumbService } from '../../layout/breadcrumbs/breadcrumb.service';
import { AccessService } from '../../access/access.service';
import { NayaxConnectionService } from '../../services/nayax-connection.service';

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
      breadcrumbServiceStub()
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
    const component = new MachineDetailComponent(route, machineService, {} as StockService, {} as ToastService, breadcrumbService);

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
        { provide: AccessService, useValue: { allows: () => of(true) } }
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
