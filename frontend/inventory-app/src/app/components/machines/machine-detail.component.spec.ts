import { convertToParamMap, ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { MachineDetailComponent } from './machine-detail.component';
import { Machine, Product } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { StockService } from '../../services/stock.service';
import { ToastService } from '../../services/toast.service';

function product(quantityInStock: number): Product {
  return { id: 55, name: 'Beef Jerky', quantityInStock } as Product;
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
      {} as ToastService
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
