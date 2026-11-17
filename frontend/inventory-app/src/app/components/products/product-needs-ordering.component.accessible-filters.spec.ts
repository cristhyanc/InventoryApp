import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProductNeedsOrderingComponent } from './product-needs-ordering.component';
import { AccessService } from '../../access/access.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { ToastService } from '../../services/toast.service';
import { SupplierPriceBasis } from '../../models/cheapest-supplier';

/**
 * Audit #1225, finding FE-08: the Needs ordering search box and category filter had no accessible
 * name at all, so a screen-reader user could not tell what either control was for. Their accessible
 * names now match the wording the Products list's own search and category filters use.
 */

/** A native text input's role is `textbox`; the checkbox selection column is excluded on purpose. */
function textboxes(host: HTMLElement): HTMLInputElement[] {
  return Array.from(host.querySelectorAll<HTMLInputElement>('input')).filter((input) => input.type !== 'checkbox');
}

/** A native `<select>`'s role is `combobox`. */
function comboboxes(host: HTMLElement): HTMLSelectElement[] {
  return Array.from(host.querySelectorAll<HTMLSelectElement>('select'));
}

function accessibleName(el: HTMLElement): string {
  const ariaLabel = el.getAttribute('aria-label');
  if (ariaLabel) {
    return ariaLabel.trim();
  }
  const labelledBy = el.getAttribute('aria-labelledby');
  if (labelledBy) {
    return labelledBy
      .split(/\s+/)
      .map((id) => el.ownerDocument.getElementById(id)?.textContent?.trim() ?? '')
      .filter(Boolean)
      .join(' ');
  }
  const labels = (el as HTMLInputElement | HTMLSelectElement).labels;
  if (labels && labels.length > 0) {
    return Array.from(labels)
      .map((label) => label.textContent?.trim() ?? '')
      .filter(Boolean)
      .join(' ');
  }
  return '';
}

async function render() {
  await TestBed.configureTestingModule({
    imports: [ProductNeedsOrderingComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => of({ products: [], mode: 'MachineAware', machineNeedUnavailableReason: null }) } },
      { provide: CategoryService, useValue: { getAll: () => of([]) } },
      { provide: SupplierService, useValue: { getAll: () => of([]) } },
      { provide: SupplierOrderService, useValue: { create: jest.fn(() => of(undefined)) } },
      { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
      {
        provide: CheapestSupplierService,
        useValue: { getSummary: () => of({ displayBasis: SupplierPriceBasis.InclusiveOfGst, products: [] }) }
      },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      { provide: AccessService, useValue: { allows: () => of(true) } }
    ]
  }).compileComponents();

  jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(jest.fn());

  const fixture = TestBed.createComponent(ProductNeedsOrderingComponent);
  fixture.detectChanges();
  return { host: fixture.nativeElement as HTMLElement };
}

describe('Needs ordering search and category filters are labelled for screen readers (issue #1279, audit #1225 FE-08)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('gives the search box the same accessible name as the Products list search filter', async () => {
    const { host } = await render();

    const searchBox = textboxes(host).find((input) => accessibleName(input) === 'Search name or SKU');

    expect(searchBox).toBeTruthy();
  });

  it('gives the category filter the same accessible name as the Products list category filter', async () => {
    const { host } = await render();

    const categoryFilter = comboboxes(host).find((select) => accessibleName(select) === 'Category:');

    expect(categoryFilter).toBeTruthy();
  });
});
