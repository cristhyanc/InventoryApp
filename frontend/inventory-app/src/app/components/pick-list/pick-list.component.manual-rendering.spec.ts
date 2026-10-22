import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { PickListRefillRequest, PickListRefillResponse, Product } from '../../models/models';
import { LocalMachineService } from '../../services/local-machine.service';
import { MachineService } from '../../services/machine.service';
import { PickListService } from '../../services/pick-list.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { NayaxConnectionService, NayaxConnectionStatusName } from '../../services/nayax-connection.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { PickListComponent } from './pick-list.component';

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

async function render(
  products: Product[] = [product({ id: 1, name: 'Coke', quantityInStock: 20 }), product({ id: 2, name: 'Chips', quantityInStock: 3 })],
  nayaxStatus: NayaxConnectionStatusName = 'Ready',
  recordRefill: jest.Mock = jest.fn(),
  options: { sessionActive?: Observable<boolean>; getAllProducts?: jest.Mock } = {}
) {
  const getAllProducts = options.getAllProducts ?? jest.fn(() => of(products));
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
  await TestBed.configureTestingModule({
    imports: [PickListComponent],
    providers: [
      provideRouter([]),
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: ProductService, useValue: { getAll: getAllProducts } },
      { provide: PickListService, useValue: { get: jest.fn(() => of({ products: [] })), recordRefill } },
      { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: ToastService, useValue: toast },
      { provide: NayaxConnectionService, useValue: { status: () => of({ status: nayaxStatus, machineChecksPending: false }) } },
      { provide: AccessService, useValue: { allows: () => of(true) } },
      { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } }
    ]
  }).compileComponents();

  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(PickListComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;

  const click = (element: HTMLElement) => {
    element.click();
    fixture.detectChanges();
  };
  const type = (input: HTMLInputElement, value: string) => {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const button = (label: string) =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim().startsWith(label));
  const requireButton = (label: string): HTMLButtonElement => {
    const found = button(label);
    if (found === undefined) {
      throw new Error(`Expected a button labelled "${label}".`);
    }
    return found;
  };

  return { fixture, host, click, type, button, requireButton, recordRefill, getAllProducts, toast };
}

function refillResponse(): PickListRefillResponse {
  return {
    idempotencyKey: 'k',
    replayed: false,
    machineId: null,
    notes: null,
    recordedAtUtc: '2026-01-01T00:00:00Z',
    rows: [{ productId: 1, quantityTaken: 2, stockAdjustmentId: 1 }]
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('PickListComponent + ManualPickListComponent rendering (issue #744)', () => {
  it('offers both the Calculated and Enter quantities taken toggle when the provider is fine', async () => {
    const { button } = await render();

    expect(button('Calculated')).toBeDefined();
    expect(button('Enter quantities taken')).toBeDefined();
  });

  it('switches to the manual entry table and lists the active products with their storage quantity', async () => {
    const { host, requireButton, click } = await render();

    click(requireButton('Enter quantities taken'));

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Coke');
    expect(host.querySelectorAll('[data-testid="manual-pick-list-storage-qty"]')[0].textContent?.trim()).toBe('20');
  });

  it('filters the manual entry list by a typed search term', async () => {
    const { host, requireButton, click, type } = await render();
    click(requireButton('Enter quantities taken'));

    const search = host.querySelector<HTMLInputElement>('input[aria-label="Search products"]')!;
    type(search, 'chip');

    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(host.querySelector('tbody')?.textContent).toContain('Chips');
  });

  it('names the manual entry filters: the category select has its own visible, associated label', async () => {
    const { host, requireButton, click } = await render();
    click(requireButton('Enter quantities taken'));

    const select = host.querySelector<HTMLSelectElement>('select#manual-pick-list-category');
    expect(select).not.toBeNull();
    const label = host.querySelector<HTMLLabelElement>('label[for="manual-pick-list-category"]');
    expect(label?.textContent?.trim()).toContain('Category');
    expect(label?.contains(select as Node)).toBe(true);
    expect(host.querySelector('input[aria-label="Search products"]')).not.toBeNull();
  });

  it('shows no provider-unavailable banner for a usable provider', async () => {
    const { host } = await render(undefined, 'Ready');

    expect(host.querySelector('[data-testid="pick-list-provider-unavailable"]')).toBeNull();
  });

  it('hides the Calculated toggle and opens straight into the manual table without a usable provider', async () => {
    const { host, button } = await render(undefined, 'NotConfigured');

    expect(host.querySelector('[data-testid="pick-list-provider-unavailable"]')?.textContent).toContain(
      'The pick list is unavailable because Nayax is not connected.'
    );
    expect(button('Calculated')).toBeUndefined();
    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('records a manual save and shows the Save button disabled until a quantity is entered', async () => {
    const recordRefill = jest.fn(() => of(refillResponse()));
    const { host, requireButton, click, type } = await render(undefined, 'Ready', recordRefill);
    click(requireButton('Enter quantities taken'));
    const saveButton = requireButton('Save');
    expect(saveButton.disabled).toBe(true);

    const takenInput = host.querySelector<HTMLInputElement>('input[aria-label="Taken: Coke"]')!;
    type(takenInput, '2');

    expect(saveButton.disabled).toBe(false);
    click(saveButton);

    expect(recordRefill).toHaveBeenCalledTimes(1);
  });

  it('shows an inline validation message for a malformed "Taken" entry', async () => {
    const { host, requireButton, click, type } = await render();
    click(requireButton('Enter quantities taken'));

    const takenInput = host.querySelector<HTMLInputElement>('input[aria-label="Taken: Coke"]')!;
    type(takenInput, 'abc');

    expect(host.querySelector('[data-testid="manual-pick-list-row-error"]')?.textContent?.trim()).toBe(
      'Enter a whole number greater than 0.'
    );
  });
});

describe('PickListComponent inventory-impact description per mode (issue #744)', () => {
  const impact = (host: HTMLElement) =>
    host.querySelector('[data-testid="pick-list-inventory-impact"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('describes the calculated view as planning only that saves nothing', async () => {
    const { host } = await render();

    expect(impact(host)).toBe(
      'Planning only. This is a read-only view: it never changes requested quantities or inventory, and nothing selected or picked here is saved.'
    );
  });

  it('describes manual entry as saving a refill and reducing storage, never as read-only', async () => {
    const { host, requireButton, click } = await render();

    click(requireButton('Enter quantities taken'));

    expect(impact(host)).toBe(
      "Entry mode. Nothing is saved until you click Save; Save then records one machine refill per quantity you entered and reduces that product's storage quantity."
    );
    expect(impact(host)).not.toContain('read-only');
    expect(impact(host)).not.toContain('nothing selected or picked here is saved');
  });

  it('describes manual entry as saving when there is no usable provider and manual is the only view', async () => {
    const { host } = await render(undefined, 'NotConfigured');

    expect(impact(host)).toContain("reduces that product's storage quantity");
    expect(impact(host)).not.toContain('read-only');
  });
});

describe('PickListComponent keeps an unresolved manual submission across mode switches (issue #744)', () => {
  it('retries an uncertain save with the identical key and body after switching to calculated and back', async () => {
    const requests: PickListRefillRequest[] = [];
    const recordRefill = jest.fn((request: PickListRefillRequest) => {
      requests.push(request);
      return requests.length === 1
        ? throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Unknown Error' }))
        : of(refillResponse());
    });
    const { fixture, host, requireButton, click, type } = await render(undefined, 'Ready', recordRefill);

    click(requireButton('Enter quantities taken'));
    // ngModel writes the initial value back in a microtask; let it finish before typing over it, so
    // the input's own value is real evidence rather than a race with that write.
    await fixture.whenStable();
    fixture.detectChanges();
    type(host.querySelector<HTMLInputElement>('input[aria-label="Taken: Coke"]')!, '2');
    click(requireButton('Save'));

    // The outcome is unknown, so it is reported where the operator can act on it, not only as a toast.
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')?.textContent).toContain(
      "Couldn't confirm whether this refill was recorded."
    );

    click(requireButton('Calculated'));
    click(requireButton('Enter quantities taken'));

    // The entry and the unconfirmed attempt both survived the round trip, so Save is a retry of the
    // same pick rather than a second one under a new key.
    expect(host.querySelector<HTMLInputElement>('input[aria-label="Taken: Coke"]')!.value).toBe('2');
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')).not.toBeNull();
    click(requireButton('Save'));

    expect(requests).toHaveLength(2);
    expect(requests[1].idempotencyKey).toBe(requests[0].idempotencyKey);
    expect(requests[1].rows).toEqual(requests[0].rows);
  });

  it('refuses to leave manual entry while its save is still in flight, and releases the toggle once it lands', async () => {
    const pending = new Subject<PickListRefillResponse>();
    const recordRefill = jest.fn(() => pending);
    const { fixture, host, requireButton, click, type } = await render(undefined, 'Ready', recordRefill);
    const component = fixture.componentInstance;

    click(requireButton('Enter quantities taken'));
    type(host.querySelector<HTMLInputElement>('input[aria-label="Taken: Coke"]')!, '2');
    click(requireButton('Save'));

    expect(requireButton('Calculated').disabled).toBe(true);
    expect(host.querySelector('[data-testid="pick-list-save-in-flight"]')).not.toBeNull();

    click(requireButton('Calculated'));
    expect(component.mode).toBe('manual');

    pending.next(refillResponse());
    pending.complete();
    fixture.detectChanges();

    expect(requireButton('Calculated').disabled).toBe(false);
    expect(host.querySelector('[data-testid="pick-list-save-in-flight"]')).toBeNull();
  });
});

describe('PickListComponent manual entry during a read-only support session (issue #744)', () => {
  it('offers no Taken boxes and no Save, and sends no refill, while a support session is active', async () => {
    const recordRefill = jest.fn(() => of(refillResponse()));
    const { host, requireButton, click, button, toast } = await render(undefined, 'Ready', recordRefill, { sessionActive: of(true) });

    click(requireButton('Enter quantities taken'));

    expect(host.querySelector('[data-testid="manual-pick-list-support-session"]')?.textContent).toContain('A support session is active');
    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
    expect(host.querySelector('input[aria-label^="Taken:"]')).toBeNull();
    expect(button('Save')).toBeUndefined();
    expect(recordRefill).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')).toBeNull();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('withdraws the Taken boxes and Save when a support session starts after the page opened', async () => {
    const session = new BehaviorSubject(false);
    const recordRefill = jest.fn(() => of(refillResponse()));
    const { host, requireButton, click, type, button, fixture } = await render(undefined, 'Ready', recordRefill, { sessionActive: session });
    click(requireButton('Enter quantities taken'));
    type(host.querySelector<HTMLInputElement>('input[aria-label="Taken: Coke"]')!, '2');
    expect(requireButton('Save').disabled).toBe(false);

    session.next(true);
    fixture.detectChanges();

    expect(host.querySelector('input[aria-label^="Taken:"]')).toBeNull();
    expect(button('Save')).toBeUndefined();
    expect(recordRefill).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')).toBeNull();
  });
});

describe('PickListComponent manual entry catalogue load state (issue #744)', () => {
  const emptyMessage = 'No active products match your filters.';

  it('says the products are loading, not that there are none, while the first read is outstanding', async () => {
    const pending = new Subject<Product[]>();
    const { host, requireButton, click, fixture } = await render(undefined, 'Ready', undefined, { getAllProducts: jest.fn(() => pending) });
    click(requireButton('Enter quantities taken'));

    expect(host.querySelector('[data-testid="manual-pick-list-catalogue-loading"]')).not.toBeNull();
    expect(host.textContent).not.toContain(emptyMessage);

    pending.next([product({ id: 1, name: 'Coke' })]);
    pending.complete();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="manual-pick-list-catalogue-loading"]')).toBeNull();
    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('reports a failed first read with a retry, never as an empty catalogue, and loads on retry', async () => {
    const getAllProducts = jest
      .fn()
      .mockImplementationOnce(() => throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockImplementation(() => of([product({ id: 1, name: 'Coke' })]));
    const { host, requireButton, click } = await render(undefined, 'Ready', undefined, { getAllProducts });
    click(requireButton('Enter quantities taken'));

    expect(host.querySelector('[data-testid="manual-pick-list-catalogue-failed"]')?.textContent).toContain('could not be loaded');
    expect(host.textContent).not.toContain(emptyMessage);

    click(requireButton('Retry'));

    expect(getAllProducts).toHaveBeenCalledTimes(2);
    expect(host.querySelector('[data-testid="manual-pick-list-catalogue-failed"]')).toBeNull();
    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('keeps the empty-catalogue message for a successful read that returned no products', async () => {
    const { host, requireButton, click } = await render([], 'Ready');
    click(requireButton('Enter quantities taken'));

    expect(host.textContent).toContain(emptyMessage);
    expect(host.querySelector('[data-testid="manual-pick-list-catalogue-failed"]')).toBeNull();
  });

  it('says storage quantities may be stale when the refresh after a recorded refill fails, and refreshes on request', async () => {
    const getAllProducts = jest
      .fn()
      .mockImplementationOnce(() => of([product({ id: 1, name: 'Coke', quantityInStock: 20 })]))
      .mockImplementationOnce(() => throwError(() => new HttpErrorResponse({ status: 0 })))
      .mockImplementation(() => of([product({ id: 1, name: 'Coke', quantityInStock: 18 })]));
    const recordRefill = jest.fn(() => of(refillResponse()));
    const { host, requireButton, click, type, toast } = await render(undefined, 'Ready', recordRefill, { getAllProducts });
    click(requireButton('Enter quantities taken'));
    type(host.querySelector<HTMLInputElement>('input[aria-label="Taken: Coke"]')!, '2');

    click(requireButton('Save'));

    expect(host.querySelector('[data-testid="manual-pick-list-refresh-failed"]')?.textContent).toContain('may be out of date');
    expect(toast.error).toHaveBeenCalledWith('Failed to refresh the product list.');
    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);

    click(requireButton('Refresh'));

    expect(host.querySelector('[data-testid="manual-pick-list-refresh-failed"]')).toBeNull();
    expect(host.querySelector('[data-testid="manual-pick-list-storage-qty"]')?.textContent?.trim()).toBe('18');
  });
});
