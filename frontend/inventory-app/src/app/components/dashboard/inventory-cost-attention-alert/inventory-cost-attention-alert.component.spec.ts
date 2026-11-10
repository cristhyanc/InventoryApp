import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import {
  InventoryCostAttentionService,
  ProductNeedingCostAttention
} from '../../../services/inventory-cost-attention.service';
import { InventoryCostAttentionAlertComponent } from './inventory-cost-attention-alert.component';

async function render(products: Observable<ProductNeedingCostAttention[]>) {
  await TestBed.configureTestingModule({
    imports: [InventoryCostAttentionAlertComponent],
    providers: [{ provide: InventoryCostAttentionService, useValue: { list: () => products } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(InventoryCostAttentionAlertComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function product(productId: number, productName: string | null): ProductNeedingCostAttention {
  return { productId, productName, failureCode: 'data_quality', attemptCount: 3, lastAttemptAtUtc: '2026-11-10T03:03:54Z' };
}

describe('InventoryCostAttentionAlertComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('names the product whose cost could not be recalculated and says the sales still sync', async () => {
    const { host, fixture } = await render(of([product(881054620672182, 'Monster Original')]));

    expect(fixture.componentInstance.isShown).toBe(true);
    expect(host.querySelector('[data-testid="inventory-cost-attention-alert-title"]')?.textContent?.trim()).toBe(
      'Costs for 1 product could not be recalculated'
    );
    const detail = host.querySelector('[data-testid="inventory-cost-attention-alert-detail"]')?.textContent ?? '';
    expect(detail).toContain('Monster Original');
    expect(detail).toContain('Record the missing purchase');
    expect(detail).toContain('Nayax sales are still being synchronized.');
  });

  it('counts several products in the plural and names a missing product by its id', async () => {
    const { fixture } = await render(of([product(1, 'Coke'), product(2, null)]));

    expect(fixture.componentInstance.title).toBe('Costs for 2 products could not be recalculated');
    expect(fixture.componentInstance.detail).toContain('Coke, Product 2:');
  });

  it('is hidden while no product needs attention', async () => {
    const { host, fixture } = await render(of([]));

    expect(fixture.componentInstance.isShown).toBe(false);
    expect(host.querySelector('[data-testid="inventory-cost-attention-alert"]')).toBeNull();
  });

  it('is hidden, rather than claiming nothing needs attention, when the read fails', async () => {
    const { host, fixture } = await render(throwError(() => new Error('offline')));

    expect(fixture.componentInstance.isShown).toBe(false);
    expect(host.querySelector('[data-testid="inventory-cost-attention-alert"]')).toBeNull();
  });
});
