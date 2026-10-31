import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { CheapestSupplierSummary, SupplierPriceBasis } from '../../../models/cheapest-supplier';
import { CheapestSupplierService } from '../../../services/cheapest-supplier.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import {
  CHEAPEST_SUPPLIERS_FAILED_MESSAGE,
  CHEAPEST_SUPPLIERS_SUPPORT_SESSION_MESSAGE,
  CheapestSuppliers,
  CheapestSuppliersComponent,
  cheapestSupplierFor,
  cheapestSuppliersPlanLockedMessage
} from './cheapest-suppliers.component';

function summary(productIds: number[]): CheapestSupplierSummary {
  return {
    displayBasis: SupplierPriceBasis.ExclusiveOfGst,
    products: productIds.map((productId) => ({
      productId,
      trackingStatus: null,
      cheapestUnitPrice: null,
      suppliers: []
    }))
  };
}

describe('CheapestSuppliersComponent (issue #930)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(options: { getSummary?: jest.Mock; sessionActive?: boolean } = {}) {
    const getSummary = options.getSummary ?? jest.fn(() => of(summary([])));

    await TestBed.configureTestingModule({
      imports: [CheapestSuppliersComponent],
      providers: [
        provideRouter([]),
        { provide: CheapestSupplierService, useValue: { getSummary } },
        { provide: SupportSessionService, useValue: { active$: of(options.sessionActive ?? false) } }
      ]
    }).compileComponents();

    const fixture: ComponentFixture<CheapestSuppliersComponent> = TestBed.createComponent(CheapestSuppliersComponent);
    const published: CheapestSuppliers[] = [];
    fixture.componentInstance.suppliersChange.subscribe((answer) => published.push(answer));

    const show = (productIds: readonly number[]) => {
      fixture.componentRef.setInput('productIds', productIds);
      fixture.detectChanges();
    };

    return { fixture, host: fixture.nativeElement as HTMLElement, getSummary, published, show };
  }

  function retryButton(host: HTMLElement): HTMLButtonElement | undefined {
    return Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Retry');
  }

  it('asks once for the rows on screen and publishes the answers keyed by product id', async () => {
    const getSummary = jest.fn(() => of(summary([1, 2])));
    const { published, show } = await render({ getSummary });

    show([1, 2]);

    expect(getSummary).toHaveBeenCalledTimes(1);
    expect(getSummary).toHaveBeenCalledWith([1, 2]);

    const answer = published[published.length - 1];
    expect(answer.state).toBe('loaded');
    expect(answer.displayBasis).toBe(SupplierPriceBasis.ExclusiveOfGst);
    expect(answer.byProduct.get(2)?.productId).toBe(2);
    // "Loading" is published first, so no cell reads as settled while the read is in flight.
    expect(published[0].state).toBe('loading');
  });

  it('settles without a request when there are no rows to ask about', async () => {
    const { getSummary, published, show } = await render();

    show([]);

    expect(getSummary).not.toHaveBeenCalled();
    expect(published[published.length - 1].state).toBe('loaded');
  });

  it('re-reads for a new product set and drops the previous answers', async () => {
    const getSummary = jest.fn((ids: readonly number[]) => of(summary([...ids])));
    const { published, show } = await render({ getSummary });

    show([1]);
    show([2]);

    expect(getSummary).toHaveBeenCalledTimes(2);
    const answer = published[published.length - 1];
    expect(answer.byProduct.has(1)).toBe(false);
    expect(answer.byProduct.has(2)).toBe(true);
  });

  it('reports a failed read as its own message with Retry, never as "no prices"', async () => {
    const getSummary = jest.fn(() => throwError(() => new Error('nope')));
    const { host, published, show } = await render({ getSummary });

    show([1]);

    const answer = published[published.length - 1];
    expect(answer.state).toBe('failed');
    expect(cheapestSupplierFor(answer, 1)).toBeUndefined();
    expect(host.textContent).toContain(CHEAPEST_SUPPLIERS_FAILED_MESSAGE);
    expect(retryButton(host)).toBeDefined();
  });

  it('re-enters the read for the same rows when Retry is pressed', async () => {
    const getSummary = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('nope')))
      .mockReturnValueOnce(of(summary([1])));
    const { fixture, host, published, show } = await render({ getSummary });

    show([1]);
    retryButton(host)!.click();
    fixture.detectChanges();

    expect(getSummary).toHaveBeenCalledTimes(2);
    expect(published[published.length - 1].state).toBe('loaded');
  });

  /**
   * The refusal a Free or Starter business gets from the summary endpoint since issue #967:
   * `403 plan_required`, naming the plan that includes `ObservedSupplierPrices`. Issue #979's own
   * inline lock: the column names the plan rather than reporting a generic failure, and offers no
   * Retry, because a `403` this business was already told the answer to is not a transient one.
   */
  it('shows an inline plan lock, naming the required plan, when the business plan refuses the prices', async () => {
    const getSummary = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 403,
            error: { code: 'plan_required', requiredPlan: 'Starter', planFeature: 'ObservedSupplierPrices' }
          })
      )
    );
    const { host, published, show } = await render({ getSummary });

    show([1, 2]);

    const answer = published[published.length - 1];
    expect(answer.state).toBe('planLocked');
    // And never as an answer about a product: "this business has not bought the column" is not
    // "this product has no cheapest supplier".
    expect(cheapestSupplierFor(answer, 1)).toBeUndefined();
    expect(cheapestSupplierFor(answer, 2)).toBeUndefined();
    expect(host.textContent).toContain(cheapestSuppliersPlanLockedMessage('Starter'));
    expect(host.querySelector('a[href="/plan"]')).not.toBeNull();
    expect(retryButton(host)).toBeUndefined();
  });

  it('reports the plan a Pro-only refusal names too', async () => {
    const getSummary = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 403,
            error: { code: 'plan_required', requiredPlan: 'Pro', planFeature: 'ObservedSupplierPrices' }
          })
      )
    );
    const { host, show } = await render({ getSummary });

    show([1]);

    expect(host.textContent).toContain(cheapestSuppliersPlanLockedMessage('Pro'));
  });

  it('asks for nothing in a read-only support session and says the column is unavailable there', async () => {
    const getSummary = jest.fn(() => of(summary([1])));
    const { host, published, show } = await render({ getSummary, sessionActive: true });

    show([1]);

    expect(getSummary).not.toHaveBeenCalled();
    expect(published[published.length - 1].state).toBe('unavailable');
    expect(host.textContent).toContain(CHEAPEST_SUPPLIERS_SUPPORT_SESSION_MESSAGE);
    // No Retry: the endpoint refuses every non-GET request in a session, so retrying could never
    // succeed.
    expect(retryButton(host)).toBeUndefined();
  });
});
