import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { SupplierOrdersComponent } from './supplier-orders.component';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ToastService } from '../../services/toast.service';
import { SupplierOrder } from '../../models/models';

function order(overrides: Partial<SupplierOrder> = {}): SupplierOrder {
  return {
    id: 1,
    supplierId: 1,
    supplier: { id: 1, name: 'Acme Supplies' },
    orderDate: '2026-01-01T00:00:00Z',
    expectedDate: null,
    reference: 'PO-100',
    notes: null,
    status: 0,
    lines: [
      {
        id: 1,
        productId: 1,
        product: { id: 1, name: 'Coke' } as SupplierOrder['lines'][number]['product'],
        quantityOrdered: 10,
        quantityReceived: 0,
        outstandingQuantity: 10,
        unitPrice: 1.5
      }
    ],
    ...overrides
  };
}

interface Harness {
  component: SupplierOrdersComponent;
  getActive: jest.Mock;
  cancel: jest.Mock;
  navigate: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock; warning: jest.Mock; info: jest.Mock };
}

function createHarness(getActive: jest.Mock = jest.fn(() => of([order()]))): Harness {
  const cancel = jest.fn(() => of(undefined));
  const navigate = jest.fn();
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  const supplierOrderService = { getActive, cancel } as unknown as SupplierOrderService;
  const router = { navigate } as unknown as Router;

  const component = new SupplierOrdersComponent(supplierOrderService, router, toast as unknown as ToastService);
  component.ngOnInit();

  return { component, getActive, cancel, navigate, toast };
}

describe('SupplierOrdersComponent loading', () => {
  it('loads active supplier orders on init', () => {
    const { component, getActive } = createHarness();

    expect(getActive).toHaveBeenCalledTimes(1);
    expect(component.orders.map((o) => o.id)).toEqual([1]);
    expect(component.loadState.loaded).toBe(true);
  });

  it('stays in a loading state until the request settles', () => {
    const pending = new Subject<SupplierOrder[]>();
    const { component } = createHarness(jest.fn(() => pending));

    expect(component.loadState.loaded).toBe(false);
    expect(component.loadState.loadFailed).toBe(false);
  });

  it('records a failed load and surfaces a toast error', () => {
    const { component, toast } = createHarness(jest.fn(() => throwError(() => new Error('boom'))));

    expect(component.loadState.loadFailed).toBe(true);
    expect(toast.error).toHaveBeenCalledWith('Failed to load supplier orders.');
  });
});

describe('SupplierOrdersComponent filtering', () => {
  it('filters by supplier name, case-insensitively', () => {
    const orders = [
      order({ id: 1, supplier: { id: 1, name: 'Acme Supplies' } }),
      order({ id: 2, supplier: { id: 2, name: 'Beta Beverages' }, reference: 'PO-200' })
    ];
    const { component } = createHarness(jest.fn(() => of(orders)));

    component.search = 'acme';

    expect(component.filteredOrders.map((o) => o.id)).toEqual([1]);
  });

  it('filters by reference', () => {
    const orders = [
      order({ id: 1, reference: 'PO-100' }),
      order({ id: 2, reference: 'PO-200', supplier: { id: 2, name: 'Beta Beverages' } })
    ];
    const { component } = createHarness(jest.fn(() => of(orders)));

    component.search = 'po-200';

    expect(component.filteredOrders.map((o) => o.id)).toEqual([2]);
  });

  it('filters by status', () => {
    const orders = [order({ id: 1, status: 0 }), order({ id: 2, status: 1 })];
    const { component } = createHarness(jest.fn(() => of(orders)));

    component.statusFilter = 1;

    expect(component.filteredOrders.map((o) => o.id)).toEqual([2]);
  });

  it('combines search and status filters', () => {
    const orders = [
      order({ id: 1, status: 0, supplier: { id: 1, name: 'Acme Supplies' } }),
      order({ id: 2, status: 1, supplier: { id: 2, name: 'Acme Supplies' } })
    ];
    const { component } = createHarness(jest.fn(() => of(orders)));

    component.search = 'acme';
    component.statusFilter = 1;

    expect(component.filteredOrders.map((o) => o.id)).toEqual([2]);
  });

  it('reports active filters only when a search or status filter is set', () => {
    const { component } = createHarness();

    expect(component.hasActiveFilters).toBe(false);

    component.search = 'acme';
    expect(component.hasActiveFilters).toBe(true);

    component.resetFilters();
    expect(component.hasActiveFilters).toBe(false);
    expect(component.search).toBe('');
    expect(component.statusFilter).toBe('');
  });
});

describe('SupplierOrdersComponent actions', () => {
  it('navigates to the purchase workflow with the supplier order id when receiving', () => {
    const { component, navigate } = createHarness();
    const target = order({ id: 42 });

    component.receiveOrder(target);

    expect(navigate).toHaveBeenCalledWith(['/purchases/new'], { queryParams: { supplierOrderId: 42 } });
  });

  it('cancels an order and reloads the list on success', () => {
    const { component, cancel, getActive, toast } = createHarness();
    getActive.mockClear();

    component.cancelOrder(order({ id: 7 }));

    expect(cancel).toHaveBeenCalledWith(7);
    expect(toast.success).toHaveBeenCalledWith('Supplier order cancelled.');
    expect(getActive).toHaveBeenCalledTimes(1);
  });

  it('shows an error toast when cancellation fails and does not reload', () => {
    const cancel = jest.fn(() => throwError(() => new Error('boom')));
    const getActive = jest.fn(() => of([order()]));
    const supplierOrderService = { getActive, cancel } as unknown as SupplierOrderService;
    const navigate = jest.fn();
    const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
    const component = new SupplierOrdersComponent(supplierOrderService, { navigate } as unknown as Router, toast as unknown as ToastService);
    component.ngOnInit();
    getActive.mockClear();

    component.cancelOrder(order({ id: 7 }));

    expect(toast.error).toHaveBeenCalledWith('Unable to cancel this supplier order.');
    expect(getActive).not.toHaveBeenCalled();
  });

  it('maps status codes to display labels', () => {
    const { component } = createHarness();

    expect(component.orderStatus(0)).toBe('Ordered');
    expect(component.orderStatus(1)).toBe('Partially received');
    expect(component.orderStatus(2)).toBe('Received');
    expect(component.orderStatus(3)).toBe('Cancelled');
    expect(component.orderStatus(99)).toBe('Unknown');
  });
});

describe('SupplierOrdersComponent rendered structure', () => {
  async function render(getActive: jest.Mock = jest.fn(() => of([order()]))) {
    const cancel = jest.fn(() => of(undefined));
    const navigate = jest.fn();

    await TestBed.configureTestingModule({
      imports: [SupplierOrdersComponent],
      providers: [
        { provide: SupplierOrderService, useValue: { getActive, cancel } },
        { provide: Router, useValue: { navigate } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SupplierOrdersComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    return { fixture, host, navigate, cancel };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('states that the page shows open orders only', async () => {
    const { host } = await render();

    expect(host.querySelector('h1')?.textContent).toContain('Supplier Orders');
    expect(host.textContent).toContain('open orders only');
  });

  it('shows an empty state when there are no active supplier orders', async () => {
    const { host } = await render(jest.fn(() => of([])));

    expect(host.textContent).toContain('No active supplier orders.');
  });

  it('shows a no-match state when filters exclude every order', async () => {
    const { fixture, host } = await render(jest.fn(() => of([order({ supplier: { id: 1, name: 'Acme Supplies' } })])));
    const component = fixture.componentInstance;
    component.search = 'nonexistent-supplier';
    fixture.detectChanges();

    expect(host.textContent).toContain('No supplier orders match your filters.');
  });

  it('shows an error state when the load fails', async () => {
    const { host } = await render(jest.fn(() => throwError(() => new Error('boom'))));

    expect(host.textContent).toContain('Failed to load supplier orders.');
  });

  it('renders order fields and a Receive action for an Ordered order', async () => {
    const { host } = await render(jest.fn(() => of([order({ status: 0 })])));

    expect(host.textContent).toContain('Acme Supplies');
    expect(host.textContent).toContain('PO-100');
    expect(host.textContent).toContain('Ordered');
    expect(host.textContent).toContain('Coke');
    expect(Array.from(host.querySelectorAll('button')).some((b) => b.textContent?.includes('Receive / Create Purchase'))).toBe(true);
    expect(Array.from(host.querySelectorAll('button')).some((b) => b.textContent?.trim() === 'Cancel')).toBe(true);
  });

  it('offers both Receive and Cancel for a Partially received order, same as an Ordered one', async () => {
    const { host } = await render(jest.fn(() => of([order({ status: 1 })])));

    expect(Array.from(host.querySelectorAll('button')).some((b) => b.textContent?.includes('Receive / Create Purchase'))).toBe(true);
    expect(Array.from(host.querySelectorAll('button')).some((b) => b.textContent?.trim() === 'Cancel')).toBe(true);
  });

  it('hides both actions for a status this page never receives from the active-orders endpoint (defensive)', async () => {
    const { host } = await render(jest.fn(() => of([order({ status: 2 })])));

    expect(Array.from(host.querySelectorAll('button')).some((b) => b.textContent?.includes('Receive / Create Purchase'))).toBe(false);
    expect(Array.from(host.querySelectorAll('button')).some((b) => b.textContent?.trim() === 'Cancel')).toBe(false);
  });

  it('navigates to the purchase workflow when Receive is clicked', async () => {
    const { host, fixture, navigate } = await render(jest.fn(() => of([order({ id: 5, status: 0 })])));

    const button = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.includes('Receive / Create Purchase'))!;
    button.click();
    fixture.detectChanges();

    expect(navigate).toHaveBeenCalledWith(['/purchases/new'], { queryParams: { supplierOrderId: 5 } });
  });
});
