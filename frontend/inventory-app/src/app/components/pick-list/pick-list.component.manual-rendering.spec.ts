import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { Product } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { PickListService } from '../../services/pick-list.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { NayaxConnectionService, NayaxConnectionStatusName } from '../../services/nayax-connection.service';
import { PickListComponent } from './pick-list.component';

function product(overrides: Partial<Product>): Product {
  return {
    id: 1,
    name: 'Product',
    unitPrice: 2,
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
  recordRefill: jest.Mock = jest.fn()
) {
  await TestBed.configureTestingModule({
    imports: [PickListComponent],
    providers: [
      provideRouter([]),
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: ProductService, useValue: { getAll: jest.fn(() => of(products)) } },
      { provide: PickListService, useValue: { get: jest.fn(() => of({ products: [] })), recordRefill } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
      { provide: NayaxConnectionService, useValue: { status: () => of({ status: nayaxStatus, machineChecksPending: false }) } },
      { provide: AccessService, useValue: { allows: () => of(true) } }
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

  return { fixture, host, click, type, button, requireButton, recordRefill };
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
    const recordRefill = jest.fn(() =>
      of({
        idempotencyKey: 'k',
        replayed: false,
        machineId: null,
        notes: null,
        recordedAtUtc: '2026-01-01T00:00:00Z',
        rows: [{ productId: 1, quantityTaken: 2, stockAdjustmentId: 1 }]
      })
    );
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
