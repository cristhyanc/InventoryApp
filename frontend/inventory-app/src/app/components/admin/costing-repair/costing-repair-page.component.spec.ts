import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { CostingRepairPageComponent } from './costing-repair-page.component';
import { CostingRepairComponent } from './costing-repair.component';
import { ProductService } from '../../../services/product.service';
import { ToastService } from '../../../services/toast.service';
import { InventoryCostRepairService } from '../../../services/inventory-cost-repair.service';
import { Product } from '../../../models/models';

async function render(getAll: jest.Mock) {
  await TestBed.configureTestingModule({
    imports: [CostingRepairPageComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getAll } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
      { provide: InventoryCostRepairService, useValue: {} }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(CostingRepairPageComponent);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

function hostedRepairComponent(fixture: Awaited<ReturnType<typeof render>>['fixture']): CostingRepairComponent {
  return fixture.debugElement.query(element => element.componentInstance instanceof CostingRepairComponent)
    .componentInstance as CostingRepairComponent;
}

/**
 * Moved from `admin.component.spec.ts` with the Costing Repair workflow (issue #390). The
 * composition assertion of issue #361 is unchanged: the workflow stays its own feature component
 * and the routed page only supplies the product list.
 */
describe('CostingRepairPageComponent Costing Repair composition (issue #361)', () => {
  it('composes the Costing Repair workflow as its own feature component and passes it the loaded products', async () => {
    const product = { id: 1, name: 'Coke Zero' } as Product;
    const { fixture, host } = await render(jest.fn(() => of([product])));

    expect(host.querySelector('app-costing-repair')).not.toBeNull();
    expect(hostedRepairComponent(fixture).products).toEqual([product]);
  });
});

describe('CostingRepairPageComponent product loading (issue #390)', () => {
  it('loads the product list once through ProductService', async () => {
    const getAll = jest.fn(() => of([{ id: 1, name: 'Coke Zero' } as Product]));
    const { component } = await render(getAll);

    expect(getAll).toHaveBeenCalledTimes(1);
    expect(component.products).toEqual([{ id: 1, name: 'Coke Zero' }]);
  });

  it('still renders the workflow with an empty product list when the products cannot be loaded', async () => {
    const { fixture, component, host } = await render(jest.fn(() => throwError(() => new Error('offline'))));

    expect(component.products).toEqual([]);
    expect(host.querySelector('app-costing-repair')).not.toBeNull();
    expect(hostedRepairComponent(fixture).products).toEqual([]);
  });
});
