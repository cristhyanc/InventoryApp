import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { ProductNeedsOrderingComponent } from './product-needs-ordering.component';
import { SupportSessionService } from '../../support-session/support-session.service';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ToastService } from '../../services/toast.service';
import { Product, ReorderAlertListing } from '../../models/models';

/**
 * Products -> Needs Ordering for a business whose Nayax connection is not usable, or whose Nayax
 * account has not granted the fleet reads (issue #727). `GET /api/products/alerts/low-stock` no
 * longer answers `409` for such a business (issue #726): it answers the storage-only alerts and
 * states the mode in its response metadata, which is what this page reads - it has no second probe
 * for the provider's availability.
 */

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Cola 375mL',
    sku: null,
    description: null,
    unitPrice: 2.5,
    averageUnitCost: 1,
    costingQuantity: null,
    inventoryValue: null,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 2,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 15,
    needToOrder: 13,
    mdbCode: null,
    unit: 'ea',
    lastEatBefore1: null,
    lastEatBefore2: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId: null,
    category: null,
    supplierId: 1,
    supplier: { id: 1, name: 'Acme Supplies' },
    isActive: true,
    isLowStock: true,
    isReorderAlert: true,
    ...overrides
  };
}

async function render(listing$: Observable<ReorderAlertListing>) {
  await TestBed.configureTestingModule({
    imports: [ProductNeedsOrderingComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => listing$, update: jest.fn(() => of(undefined)) } },
      { provide: CategoryService, useValue: { getAll: () => of([]) } },
      { provide: SupplierService, useValue: { getAll: () => of([]) } },
      { provide: SupplierOrderService, useValue: { create: jest.fn(() => of(undefined)) } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
      { provide: SupportSessionService, useValue: { active$: of(false) } }
    ]
  }).compileComponents();

  jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(jest.fn());

  const fixture = TestBed.createComponent(ProductNeedsOrderingComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function headers(host: HTMLElement): (string | undefined)[] {
  return Array.from(host.querySelectorAll('th')).map((th) => th.textContent?.trim());
}

describe('Needs Ordering in storage-only mode (issue #727)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('hides the Machine need column, whose value was never read', async () => {
    const { host } = await render(
      of({ products: [product()], mode: 'StorageOnly', machineNeedUnavailableReason: 'NayaxNotConnected' })
    );

    expect(headers(host)).not.toContain('Machine need');
    expect(headers(host)).toContain('Need to Order');
  });

  it('labels the list as storage-only and says why', async () => {
    const { host } = await render(
      of({ products: [product()], mode: 'StorageOnly', machineNeedUnavailableReason: 'NayaxNotConnected' })
    );

    expect(host.textContent).toContain(
      "Storage stock only: these figures do not include the machines' current replenishment need, because Nayax is not connected."
    );
  });

  it('says a permission was not granted when that is the reason', async () => {
    const { host } = await render(
      of({ products: [product()], mode: 'StorageOnly', machineNeedUnavailableReason: 'PermissionNotGranted' })
    );

    expect(host.textContent).toContain(
      "Storage stock only: these figures do not include the machines' current replenishment need, because Nayax has not granted this application permission to read the machines."
    );
  });

  it('states the scope without claiming a reason when the listing carried none', async () => {
    const { host } = await render(of({ products: [product()], mode: 'StorageOnly', machineNeedUnavailableReason: null }));

    expect(host.textContent).toContain(
      "Storage stock only: these figures do not include the machines' current replenishment need."
    );
    expect(host.textContent).not.toContain('because Nayax');
  });

  it('still lists the alerts and their need to order, which are the business’s own data', async () => {
    const { host } = await render(
      of({ products: [product()], mode: 'StorageOnly', machineNeedUnavailableReason: 'NayaxNotConnected' })
    );

    expect(host.textContent).toContain('Cola 375mL');
    const cells = Array.from(host.querySelectorAll('tbody td')).map((td) => td.textContent?.trim());
    expect(cells).toContain('13');
  });

  it('still lets the alerts be marked as ordered, which needs no provider', async () => {
    const { fixture, host } = await render(
      of({ products: [product()], mode: 'StorageOnly', machineNeedUnavailableReason: 'NayaxNotConnected' })
    );

    (host.querySelector('tbody input[type="checkbox"]') as HTMLInputElement).click();
    fixture.detectChanges();
    const markAsOrdered = Array.from(host.querySelectorAll('button')).find(
      (button) => button.textContent?.trim() === 'Mark as ordered'
    ) as HTMLButtonElement;

    expect(markAsOrdered.disabled).toBe(false);
    markAsOrdered.click();
    fixture.detectChanges();

    expect(host.textContent).toContain('Mark products as ordered');
    expect(fixture.componentInstance.orderQuantities[1]).toBe(13);
  });

  it('keeps the empty and failed states distinguishable', async () => {
    const { host: empty } = await render(
      of({ products: [], mode: 'StorageOnly', machineNeedUnavailableReason: 'NayaxNotConnected' })
    );
    expect(empty.textContent).toContain('No products match your filters');

    TestBed.resetTestingModule();
    const { host: failed } = await render(
      throwError(() => new HttpErrorResponse({ status: 500 })) as Observable<ReorderAlertListing>
    );
    expect(failed.textContent).toContain('Failed to load products needing ordering');
    expect(failed.textContent).not.toContain('No products match your filters');
  });
});

describe('Needs Ordering in machine-aware mode is unchanged (issue #727)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps the Machine need column and adds no storage-only label', async () => {
    const { host } = await render(
      of({ products: [product({ machineReplenishmentNeed: 4 })], mode: 'MachineAware', machineNeedUnavailableReason: null })
    );

    expect(headers(host)).toContain('Machine need');
    expect(host.textContent).not.toContain('Storage stock only');
  });

  it('adds no label for a listing that published no mode at all', async () => {
    const { host } = await render(of({ products: [product()], mode: null, machineNeedUnavailableReason: null }));

    expect(headers(host)).toContain('Machine need');
    expect(host.textContent).not.toContain('Storage stock only');
  });
});
