import { HttpErrorResponse } from '@angular/common/http';
import { SiteService } from '../../services/site.service';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import {
  LocalMachine,
  MultiMachinePickListRefillRequest,
  MultiMachinePickListRefillResponse,
  PickListRefillRequest,
  PickListRefillResponse,
  Product
} from '../../models/models';
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

function localMachine(overrides: Partial<LocalMachine>): LocalMachine {
  return { id: 1, name: 'Machine', isActive: true, isProviderNamed: false, ...overrides };
}

async function render(
  products: Product[] = [product({ id: 1, name: 'Coke', quantityInStock: 20 }), product({ id: 2, name: 'Chips', quantityInStock: 3 })],
  nayaxStatus: NayaxConnectionStatusName = 'Ready',
  recordRefill: jest.Mock = jest.fn(),
  options: {
    sessionActive?: Observable<boolean>;
    getAllProducts?: jest.Mock;
    localMachines?: LocalMachine[];
    recordMultiMachineRefill?: jest.Mock;
  } = {}
) {
  const getAllProducts = options.getAllProducts ?? jest.fn(() => of(products));
  const recordMultiMachineRefill = options.recordMultiMachineRefill ?? jest.fn();
  const toast = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };
  await TestBed.configureTestingModule({
    imports: [PickListComponent],
    providers: [
      provideRouter([]),
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: ProductService, useValue: { getAll: getAllProducts } },
      {
        provide: PickListService,
        useValue: { get: jest.fn(() => of({ products: [] })), recordRefill, recordMultiMachineRefill }
      },
      { provide: SiteService, useValue: { getList: jest.fn(() => of([])) } },
      { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of(options.localMachines ?? [])) } },
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
  /** Opens manual entry's own Machines dropdown and checks one machine, as the operator does. */
  const selectMachineColumn = (machineName: string) => {
    const trigger = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((b) =>
      b.getAttribute('aria-label')?.startsWith('Machines:')
    );
    if (trigger === undefined) {
      throw new Error('Expected manual entry to offer a Machines dropdown.');
    }
    click(trigger);
    const option = Array.from(host.querySelectorAll('[role="group"] label')).find((label) =>
      label.textContent?.trim() === machineName
    );
    if (option === undefined) {
      throw new Error(`Expected the Machines dropdown to offer "${machineName}".`);
    }
    click(option.querySelector<HTMLInputElement>('input[type="checkbox"]')!);
    click(trigger);
  };

  /** Record refill's one primary action; the view toggle shares its words, so it is found by its test id. */
  const recordButton = () => host.querySelector<HTMLButtonElement>('[data-testid="manual-pick-list-record"]')!;

  return {
    fixture,
    host,
    recordButton,
    click,
    type,
    button,
    requireButton,
    selectMachineColumn,
    recordRefill,
    recordMultiMachineRefill,
    getAllProducts,
    toast
  };
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
  it('offers both the Plan picks and Record refill toggle when the provider is fine', async () => {
    const { button } = await render();

    expect(button('Plan picks')?.getAttribute('aria-pressed')).toBe('true');
    expect(button('Record refill')?.getAttribute('aria-pressed')).toBe('false');
  });

  it('switches to the manual entry table and lists the active products with their storage quantity', async () => {
    const { host, requireButton, click } = await render();

    click(requireButton('Record refill'));

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Coke');
    expect(host.querySelectorAll('[data-testid="manual-pick-list-storage-qty"]')[0].textContent?.trim()).toBe('20');
  });

  it('filters the manual entry list by a typed search term', async () => {
    const { host, requireButton, click, type } = await render();
    click(requireButton('Record refill'));

    const search = host.querySelector<HTMLInputElement>('input[aria-label="Search products"]')!;
    type(search, 'chip');

    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(host.querySelector('tbody')?.textContent).toContain('Chips');
  });

  it('names the manual entry filters: the category select has its own visible, associated label', async () => {
    const { host, requireButton, click } = await render();
    click(requireButton('Record refill'));

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
    expect(button('Plan picks')).toBeUndefined();
    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('records a manual save and shows the Save button disabled until a quantity is entered', async () => {
    const recordRefill = jest.fn(() => of(refillResponse()));
    const { recordButton, host, requireButton, click, type } = await render(undefined, 'Ready', recordRefill);
    click(requireButton('Record refill'));
    const saveButton = recordButton();
    expect(saveButton.disabled).toBe(true);

    const takenInput = host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke"]')!;
    type(takenInput, '2');

    expect(saveButton.disabled).toBe(false);
    click(saveButton);

    expect(recordRefill).toHaveBeenCalledTimes(1);
  });

  it('shows an inline validation message for a malformed "Taken" entry', async () => {
    const { host, requireButton, click, type } = await render();
    click(requireButton('Record refill'));

    const takenInput = host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke"]')!;
    type(takenInput, 'abc');

    expect(host.querySelector('[data-testid="manual-pick-list-row-error"]')?.textContent?.trim()).toBe(
      'Enter a whole number greater than 0.'
    );
  });
});

/**
 * `.page`'s `flex flex-col gap-6` spaces only its direct children, and `<app-manual-pick-list>` is
 * one of them, so the gap between manual entry's own filter and quantities cards has to come from
 * a wrapper inside the component. The host itself must carry no display utility: the parent hides
 * it with `[hidden]`, and a `flex` class on the host would compete with that rule.
 */
describe('PickListComponent manual entry card spacing', () => {
  const marginUtility = /\b(m[tbxy]?-\d|space-y-\d)\b/;

  it('spaces the filter and quantities cards with one gap-6 wrapper, leaving the host display-free', async () => {
    const { host, requireButton, click } = await render();

    click(requireButton('Record refill'));

    const manual = host.querySelector('app-manual-pick-list') as HTMLElement;
    expect(manual.className).not.toMatch(/\b(flex|grid|block)\b/);
    expect(manual.children).toHaveLength(1);
    const wrapper = manual.firstElementChild as HTMLElement;
    expect(wrapper.classList).toContain('flex');
    expect(wrapper.classList).toContain('flex-col');
    expect(wrapper.classList).toContain('gap-6');
    const cards = Array.from(wrapper.children).filter((child) => child.classList.contains('card'));
    expect(cards).toHaveLength(3); // machines and filters, quantities taken, totals and actions
    cards.forEach((card) => expect(card.className).not.toMatch(marginUtility));
  });

  it('keeps the manual view hidden after switching back to the calculated view', async () => {
    const { host, requireButton, click } = await render();

    click(requireButton('Record refill'));
    click(requireButton('Plan picks'));

    expect((host.querySelector('app-manual-pick-list') as HTMLElement).hidden).toBe(true);
  });
});

describe('PickListComponent + ManualPickListComponent machine matrix rendering (issue #904)', () => {
  const machines = () => [localMachine({ id: 5, name: 'Front Lobby' }), localMachine({ id: 6, name: 'Back Room' })];

  function batchResponse(request: MultiMachinePickListRefillRequest): MultiMachinePickListRefillResponse {
    return {
      idempotencyKey: request.idempotencyKey,
      replayed: false,
      notes: null,
      recordedAtUtc: '2026-01-01T00:00:00Z',
      allocations: request.allocations.map((allocation) => ({ ...allocation, stockAdjustmentId: 1 }))
    };
  }

  it('renders one labelled quantity column per selected machine, with the row total after them', async () => {
    const { host, requireButton, click, selectMachineColumn } = await render(undefined, 'Ready', undefined, {
      localMachines: machines()
    });
    click(requireButton('Record refill'));

    selectMachineColumn('Front Lobby');
    selectMachineColumn('Back Room');

    const headers = Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.replace(/\s+/g, ' ').trim());
    expect(headers).toEqual(['Product', 'Storage available', 'Front Lobby', 'Back Room', 'Total quantity taken']);
    // Each selected machine is a chip with its own remove control.
    expect(host.querySelector('button[aria-label="Remove machine column: Front Lobby"]')).not.toBeNull();
    expect(host.querySelector('input[aria-label="Quantity taken: Coke at Front Lobby"]')).not.toBeNull();
    expect(host.querySelector('input[aria-label="Quantity taken: Chips at Back Room"]')).not.toBeNull();
    // The single No machine column is gone: a quantity now belongs to a machine.
    expect(host.querySelector('input[aria-label="Quantity taken: Coke"]')).toBeNull();
  });

  it('keeps the matrix inside one bounded scroll container, so a wide selection never widens the page', async () => {
    const { host, requireButton, click, selectMachineColumn } = await render(undefined, 'Ready', undefined, {
      localMachines: machines()
    });
    click(requireButton('Record refill'));
    selectMachineColumn('Front Lobby');

    const scroll = host.querySelector('[data-testid="manual-pick-list-table-scroll"]');
    expect(scroll).not.toBeNull();
    expect(scroll!.className).toContain('overflow-auto');
    expect(scroll!.querySelector('table')).not.toBeNull();
  });

  it('shows the row total, the whole pick’s storage requirement and the part a filter hides', async () => {
    const { host, requireButton, click, type, selectMachineColumn } = await render(undefined, 'Ready', undefined, {
      localMachines: machines()
    });
    click(requireButton('Record refill'));
    selectMachineColumn('Front Lobby');
    selectMachineColumn('Back Room');

    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!, '3');
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Back Room"]')!, '4');
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Chips at Front Lobby"]')!, '2');

    expect(host.querySelector('[data-testid="manual-pick-list-row-total"]')?.textContent?.trim()).toBe('7');
    expect(host.querySelector('[data-testid="manual-pick-list-total-required"]')?.textContent?.replace(/\s+/g, ' ')).toContain('9');

    type(host.querySelector<HTMLInputElement>('input[aria-label="Search products"]')!, 'coke');

    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(host.querySelector('[data-testid="manual-pick-list-total-required"]')?.textContent?.replace(/\s+/g, ' ')).toContain('9');
    expect(host.querySelector('[data-testid="manual-pick-list-hidden-required"]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'including 2 units on 1 product hidden by the current filters'
    );
  });

  it('saves the whole matrix as one batch request and reports it', async () => {
    const recordMultiMachineRefill = jest.fn((request: MultiMachinePickListRefillRequest) => of(batchResponse(request)));
    const { recordButton, host, requireButton, click, type, selectMachineColumn, recordRefill, toast } = await render(
      undefined,
      'Ready',
      undefined,
      { localMachines: machines(), recordMultiMachineRefill }
    );
    click(requireButton('Record refill'));
    selectMachineColumn('Front Lobby');
    selectMachineColumn('Back Room');
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!, '3');
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Chips at Back Room"]')!, '2');

    click(recordButton());

    expect(recordMultiMachineRefill).toHaveBeenCalledTimes(1);
    expect(recordRefill).not.toHaveBeenCalled();
    expect((recordMultiMachineRefill.mock.calls[0][0] as MultiMachinePickListRefillRequest).allocations).toEqual([
      { machineId: 5, productId: 1, quantityTaken: 3 },
      { machineId: 6, productId: 2, quantityTaken: 2 }
    ]);
    expect(toast.success).toHaveBeenCalledWith('Refill recorded: 2 entries across 2 machines.');
  });

  it('shows a cell’s own error and blocks Save until it is fixed, naming the product and the machine', async () => {
    const { recordButton, host, requireButton, click, type, selectMachineColumn } = await render(undefined, 'Ready', undefined, {
      localMachines: machines()
    });
    click(requireButton('Record refill'));
    selectMachineColumn('Front Lobby');

    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!, 'abc');

    expect(host.querySelector('[data-testid="manual-pick-list-cell-error"]')?.textContent?.trim()).toBe(
      'Enter a whole number greater than 0.'
    );
    expect(host.querySelector('[data-testid="manual-pick-list-invalid-entries"]')?.textContent?.replace(/\s+/g, ' ')).toContain(
      'Coke at Front Lobby: Enter a whole number greater than 0.'
    );
    expect(recordButton().disabled).toBe(true);
  });

  it('asks before removing a machine column that holds quantities, and keeps everything on Cancel', async () => {
    const confirmSpy = jest.spyOn(window, 'confirm').mockReturnValue(false);
    const { host, requireButton, click, type, selectMachineColumn } = await render(undefined, 'Ready', undefined, {
      localMachines: machines()
    });
    click(requireButton('Record refill'));
    selectMachineColumn('Front Lobby');
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!, '3');

    const remove = host.querySelector<HTMLButtonElement>('button[aria-label="Remove machine column: Front Lobby"]')!;
    click(remove);

    expect(confirmSpy).toHaveBeenCalled();
    expect(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!.value).toBe('3');

    confirmSpy.mockReturnValue(true);
    click(host.querySelector<HTMLButtonElement>('button[aria-label="Remove machine column: Front Lobby"]')!);

    expect(host.querySelector('input[aria-label="Quantity taken: Coke at Front Lobby"]')).toBeNull();
    confirmSpy.mockRestore();
  });

  it('offers an explicit Retry save for an uncertain batch outcome and resends the identical request', async () => {
    const requests: MultiMachinePickListRefillRequest[] = [];
    const recordMultiMachineRefill = jest.fn((request: MultiMachinePickListRefillRequest) => {
      requests.push(request);
      return requests.length === 1
        ? throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Unknown Error' }))
        : of(batchResponse(request));
    });
    const { fixture, recordButton, host, requireButton, click, type, selectMachineColumn } = await render(undefined, 'Ready', undefined, {
      localMachines: machines(),
      recordMultiMachineRefill
    });
    click(requireButton('Record refill'));
    selectMachineColumn('Front Lobby');
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!, '3');

    click(recordButton());

    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')?.textContent).toContain(
      "Couldn't confirm whether this refill was recorded."
    );

    // The entries are locked while the outcome is unknown, and the one action offered is Retry save:
    // an attempted edit must not change what the retry sends.
    await fixture.whenStable();
    expect(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!.disabled).toBe(true);
    expect(host.querySelector('[data-testid="manual-pick-list-record"]')).toBeNull();
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke at Front Lobby"]')!, '9');
    click(requireButton('Retry save'));

    expect(requests).toHaveLength(2);
    expect(requests[1].idempotencyKey).toBe(requests[0].idempotencyKey);
    expect(requests[1].allocations).toEqual(requests[0].allocations);
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')).toBeNull();
  });
});

describe('PickListComponent inventory-impact description per mode (issues #744, #996)', () => {
  const impact = (host: HTMLElement) =>
    host.querySelector('[data-testid="pick-list-inventory-impact"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('describes the calculated view as planning only that saves nothing', async () => {
    const { host } = await render();

    expect(impact(host)).toBe('Planning only. Editing quantities and checking picks does not change inventory.');
  });

  it('describes manual entry as saving a refill and reducing storage, never as read-only', async () => {
    const { host, requireButton, click } = await render();

    click(requireButton('Record refill'));

    expect(impact(host)).toBe('Recording this refill reduces storage stock by the quantities entered.');
    expect(impact(host)).not.toContain('Planning only');
    // No promise of a write to Nayax or of updated remote machine levels.
    expect(impact(host)).not.toContain('Nayax');
  });

  it('describes manual entry as saving when there is no usable provider and manual is the only view', async () => {
    const { host } = await render(undefined, 'NotConfigured');

    expect(impact(host)).toContain('reduces storage stock by the quantities entered');
    expect(impact(host)).not.toContain('Planning only');
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
    const { recordButton, fixture, host, requireButton, click, type } = await render(undefined, 'Ready', recordRefill);

    click(requireButton('Record refill'));
    // ngModel writes the initial value back in a microtask; let it finish before typing over it, so
    // the input's own value is real evidence rather than a race with that write.
    await fixture.whenStable();
    fixture.detectChanges();
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke"]')!, '2');
    click(recordButton());

    // The outcome is unknown, so it is reported where the operator can act on it, not only as a toast.
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')?.textContent).toContain(
      "Couldn't confirm whether this refill was recorded."
    );

    click(requireButton('Plan picks'));
    click(requireButton('Record refill'));

    // The entry and the unconfirmed attempt both survived the round trip, so Save is a retry of the
    // same pick rather than a second one under a new key.
    expect(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke"]')!.value).toBe('2');
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')).not.toBeNull();
    click(requireButton('Retry save'));

    expect(requests).toHaveLength(2);
    expect(requests[1].idempotencyKey).toBe(requests[0].idempotencyKey);
    expect(requests[1].rows).toEqual(requests[0].rows);
  });

  it('refuses to leave manual entry while its save is still in flight, and releases the toggle once it lands', async () => {
    const pending = new Subject<PickListRefillResponse>();
    const recordRefill = jest.fn(() => pending);
    const { recordButton, fixture, host, requireButton, click, type } = await render(undefined, 'Ready', recordRefill);
    const component = fixture.componentInstance;

    click(requireButton('Record refill'));
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke"]')!, '2');
    click(recordButton());

    expect(requireButton('Plan picks').disabled).toBe(true);
    expect(host.querySelector('[data-testid="pick-list-save-in-flight"]')).not.toBeNull();

    click(requireButton('Plan picks'));
    expect(component.mode).toBe('manual');

    pending.next(refillResponse());
    pending.complete();
    fixture.detectChanges();

    expect(requireButton('Plan picks').disabled).toBe(false);
    expect(host.querySelector('[data-testid="pick-list-save-in-flight"]')).toBeNull();
  });
});

describe('PickListComponent manual entry during a read-only support session (issue #744)', () => {
  it('offers no Taken boxes and no Save, and sends no refill, while a support session is active', async () => {
    const recordRefill = jest.fn(() => of(refillResponse()));
    const { recordButton, host, requireButton, click, toast } = await render(undefined, 'Ready', recordRefill, { sessionActive: of(true) });

    click(requireButton('Record refill'));

    expect(host.querySelector('[data-testid="manual-pick-list-support-session"]')?.textContent).toContain('A support session is active');
    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
    expect(host.querySelector('input[aria-label^="Quantity taken:"]')).toBeNull();
    expect(recordButton()).toBeNull();
    expect(recordRefill).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')).toBeNull();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('withdraws the Taken boxes and Save when a support session starts after the page opened', async () => {
    const session = new BehaviorSubject(false);
    const recordRefill = jest.fn(() => of(refillResponse()));
    const { recordButton, host, requireButton, click, type, fixture } = await render(undefined, 'Ready', recordRefill, { sessionActive: session });
    click(requireButton('Record refill'));
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke"]')!, '2');
    expect(recordButton().disabled).toBe(false);

    session.next(true);
    fixture.detectChanges();

    expect(host.querySelector('input[aria-label^="Quantity taken:"]')).toBeNull();
    expect(recordButton()).toBeNull();
    expect(recordRefill).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="manual-pick-list-unresolved"]')).toBeNull();
  });
});

describe('PickListComponent manual entry catalogue load state (issue #744)', () => {
  const emptyMessage = 'No active products match your filters.';

  it('says the products are loading, not that there are none, while the first read is outstanding', async () => {
    const pending = new Subject<Product[]>();
    const { host, requireButton, click, fixture } = await render(undefined, 'Ready', undefined, { getAllProducts: jest.fn(() => pending) });
    click(requireButton('Record refill'));

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
    click(requireButton('Record refill'));

    expect(host.querySelector('[data-testid="manual-pick-list-catalogue-failed"]')?.textContent).toContain('could not be loaded');
    expect(host.textContent).not.toContain(emptyMessage);

    click(requireButton('Retry'));

    expect(getAllProducts).toHaveBeenCalledTimes(2);
    expect(host.querySelector('[data-testid="manual-pick-list-catalogue-failed"]')).toBeNull();
    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('keeps the empty-catalogue message for a successful read that returned no products', async () => {
    const { host, requireButton, click } = await render([], 'Ready');
    click(requireButton('Record refill'));

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
    const { recordButton, host, requireButton, click, type, toast } = await render(undefined, 'Ready', recordRefill, { getAllProducts });
    click(requireButton('Record refill'));
    type(host.querySelector<HTMLInputElement>('input[aria-label="Quantity taken: Coke"]')!, '2');

    click(recordButton());

    expect(host.querySelector('[data-testid="manual-pick-list-refresh-failed"]')?.textContent).toContain('may be out of date');
    expect(toast.error).toHaveBeenCalledWith('Failed to refresh the product list.');
    expect(host.querySelectorAll('tbody tr')).toHaveLength(1);

    click(requireButton('Refresh'));

    expect(host.querySelector('[data-testid="manual-pick-list-refresh-failed"]')).toBeNull();
    expect(host.querySelector('[data-testid="manual-pick-list-storage-qty"]')?.textContent?.trim()).toBe('18');
  });
});
