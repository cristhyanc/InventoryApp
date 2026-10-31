import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { ProductPriceTrackingComponent } from './product-price-tracking.component';
import { ProductService } from '../../../services/product.service';
import { ProductPriceTracking, ProductPriceTrackingStatus } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { RolePreviewService } from '../../../role-preview/role-preview.service';

/** The sentence the API publishes for a blank or generic unit label; this page words no rule itself. */
const UNIT_LABEL_REFUSAL =
  'Supplier prices are searched for by the product\'s name and unit label, so this product needs a real ' +
  'unit label such as "375mL" before tracking can be turned on.';

function state(overrides: Partial<ProductPriceTracking> = {}): ProductPriceTracking {
  return {
    productId: 42,
    tracked: false,
    status: null,
    searchName: null,
    unitLabel: null,
    masterProduct: null,
    canTrack: true,
    trackRefusal: null,
    ...overrides
  };
}

async function render(
  getPriceTracking: jest.Mock = jest.fn(() => of(state())),
  setPriceTracking: jest.Mock = jest.fn(() => of(undefined)),
  sessionActive: Observable<boolean> = of(false),
  previewActive: Observable<boolean> = of(false)
) {
  await TestBed.configureTestingModule({
    imports: [ProductPriceTrackingComponent],
    providers: [
      { provide: ProductService, useValue: { getPriceTracking, setPriceTracking } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } },
      { provide: RolePreviewService, useValue: { active$: previewActive } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductPriceTrackingComponent);
  fixture.componentRef.setInput('productId', 42);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const switchControl = () => host.querySelector<HTMLInputElement>('[data-testid="price-tracking-switch"]')!;
  const text = (testId: string) =>
    host.querySelector<HTMLElement>(`[data-testid="${testId}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
  const toggle = () => {
    switchControl().click();
    fixture.detectChanges();
  };
  const openProduct = (productId: number) => {
    fixture.componentRef.setInput('productId', productId);
    fixture.detectChanges();
  };

  return { fixture, host, switchControl, text, toggle, openProduct, getPriceTracking, setPriceTracking };
}

describe('ProductPriceTrackingComponent', () => {
  describe('the switch', () => {
    it('reads the product state and offers the switch off', async () => {
      const { switchControl, getPriceTracking } = await render();

      expect(getPriceTracking).toHaveBeenCalledWith(42);
      expect(switchControl().checked).toBe(false);
      expect(switchControl().disabled).toBe(false);
    });

    it('shows the switch on for a product the business already tracks', async () => {
      const { switchControl } = await render(
        jest.fn(() => of(state({ tracked: true, status: ProductPriceTrackingStatus.Requested })))
      );

      expect(switchControl().checked).toBe(true);
    });

    it('turns tracking on and re-reads the state the server decided', async () => {
      const reads = jest
        .fn()
        .mockReturnValueOnce(of(state()))
        .mockReturnValueOnce(
          of(
            state({
              tracked: true,
              status: ProductPriceTrackingStatus.Requested,
              searchName: 'Coca-Cola Classic',
              unitLabel: '375mL'
            })
          )
        );
      const { switchControl, toggle, setPriceTracking, text } = await render(reads);

      toggle();

      expect(setPriceTracking).toHaveBeenCalledWith(42, true);
      expect(reads).toHaveBeenCalledTimes(2);
      expect(switchControl().checked).toBe(true);
      expect(text('price-tracking-status')).toBe('Status: Awaiting match');
      expect(text('price-tracking-search-text')).toBe('Searched for as "Coca-Cola Classic 375mL".');
    });

    it('turns tracking off', async () => {
      const reads = jest
        .fn()
        .mockReturnValueOnce(of(state({ tracked: true, status: ProductPriceTrackingStatus.Requested })))
        .mockReturnValueOnce(of(state()));
      const { switchControl, toggle, setPriceTracking } = await render(reads);

      toggle();

      expect(setPriceTracking).toHaveBeenCalledWith(42, false);
      expect(switchControl().checked).toBe(false);
    });

    it('reports a failed write and leaves the switch as it was', async () => {
      const { switchControl, toggle, text } = await render(
        jest.fn(() => of(state())),
        jest.fn(() => throwError(() => new Error('nope')))
      );

      toggle();

      expect(text('price-tracking-error')).toBe('Failed to turn supplier price tracking on.');
      expect(switchControl().checked).toBe(false);
    });

    it('keeps the switch unavailable when the state could not be read', async () => {
      const { switchControl, text } = await render(jest.fn(() => throwError(() => new Error('nope'))));

      expect(switchControl().disabled).toBe(true);
      expect(text('price-tracking-error')).toContain('Failed to load supplier price tracking.');
    });

    it('writes nothing while a support session is active', async () => {
      const { switchControl, toggle, setPriceTracking } = await render(
        jest.fn(() => of(state())),
        jest.fn(() => of(undefined)),
        of(true)
      );

      expect(switchControl().disabled).toBe(true);
      toggle();
      expect(setPriceTracking).not.toHaveBeenCalled();
    });

    it('writes nothing while a role preview is active', async () => {
      const { switchControl, toggle, setPriceTracking } = await render(
        jest.fn(() => of(state())),
        jest.fn(() => of(undefined)),
        of(false),
        of(true)
      );

      expect(switchControl().disabled).toBe(true);
      toggle();
      expect(setPriceTracking).not.toHaveBeenCalled();
    });

    it('discards a write whose product is no longer the one on screen', async () => {
      const reads = jest.fn(() => of(state()));
      const { openProduct, toggle, fixture } = await render(reads);

      toggle();
      openProduct(43);

      // The second product's own read is the last one, and the first product's write reported
      // nothing: the state on screen is 43's.
      expect(reads).toHaveBeenLastCalledWith(43);
      expect(fixture.componentInstance.saving).toBe(false);
    });
  });

  describe('the unit-label message', () => {
    it('shows the server reason and refuses to turn tracking on', async () => {
      const { switchControl, toggle, text, setPriceTracking } = await render(
        jest.fn(() => of(state({ canTrack: false, trackRefusal: UNIT_LABEL_REFUSAL })))
      );

      expect(text('price-tracking-refusal')).toBe(UNIT_LABEL_REFUSAL);
      expect(switchControl().disabled).toBe(true);
      toggle();
      expect(setPriceTracking).not.toHaveBeenCalled();
    });

    it('shows no reason for a product that may be tracked', async () => {
      const { text } = await render();

      expect(text('price-tracking-refusal')).toBeNull();
    });

    /**
     * A product tracked before its unit label was made generic must still be untrackable, or the
     * business would be stuck with a search it cannot withdraw. The reason **is** shown now (issue
     * #938, repaired on review): for a tracked product it is why the mapping is paused and how to
     * resume it, which is the one thing a business needs to read in that state. The earlier
     * assertion that it stayed hidden while tracking was on was the behaviour the review found
     * wrong, so it changed deliberately rather than being dropped.
     */
    it('still lets a tracked product be turned off when it could no longer be turned on', async () => {
      const { switchControl, toggle, text, setPriceTracking } = await render(
        jest.fn(() =>
          of(
            state({
              tracked: true,
              status: ProductPriceTrackingStatus.Requested,
              canTrack: false,
              trackRefusal: UNIT_LABEL_REFUSAL
            })
          )
        )
      );

      expect(switchControl().disabled).toBe(false);
      expect(text('price-tracking-refusal')).toBe(UNIT_LABEL_REFUSAL);
      toggle();
      expect(setPriceTracking).toHaveBeenCalledWith(42, false);
    });
  });

  describe('the status', () => {
    it('reads a requested mapping as awaiting a match, with no matched description', async () => {
      const { text } = await render(
        jest.fn(() => of(state({ tracked: true, status: ProductPriceTrackingStatus.Requested })))
      );

      expect(text('price-tracking-status')).toBe('Status: Awaiting match');
      expect(text('price-tracking-match')).toBeNull();
    });

    it('reads an auto-matched mapping with the master product name and size', async () => {
      const { text } = await render(
        jest.fn(() =>
          of(
            state({
              tracked: true,
              status: ProductPriceTrackingStatus.AutoMatched,
              masterProduct: { name: 'Classic', sizeValue: 375, sizeUnit: 'ml' }
            })
          )
        )
      );

      expect(text('price-tracking-status')).toBe('Status: Matched automatically, awaiting check — Classic 375ml');
    });

    it('reads a matched mapping with the master product name and size', async () => {
      const { text } = await render(
        jest.fn(() =>
          of(
            state({
              tracked: true,
              status: ProductPriceTrackingStatus.Matched,
              masterProduct: { name: 'Classic', sizeValue: 375, sizeUnit: 'ml' }
            })
          )
        )
      );

      expect(text('price-tracking-status')).toBe('Status: Matched — Classic 375ml');
    });

    it('reads a not-found mapping as the recorded answer it is', async () => {
      const { text } = await render(
        jest.fn(() => of(state({ tracked: true, status: ProductPriceTrackingStatus.NotFound })))
      );

      expect(text('price-tracking-status')).toBe('Status: Not found');
      expect(text('price-tracking-match')).toBeNull();
    });

    /**
     * The repair of this issue's review finding, as the page shows it: a mapping paused because the
     * product's unit label was replaced by the generic one says it is paused, names no matched
     * product, prints no "Searched for as" line - nothing is being searched for, and the text the
     * row still holds is the product's previous size - and carries the server's own sentence saying
     * how to resume it. Turning the switch off stays available.
     */
    it('reads a paused mapping as paused, with the reason and no search text', async () => {
      const { text, switchControl } = await render(
        jest.fn(() =>
          of(
            state({
              tracked: true,
              status: ProductPriceTrackingStatus.AwaitingProductDetails,
              searchName: 'Coca-Cola Classic',
              unitLabel: '375mL',
              canTrack: false,
              trackRefusal: UNIT_LABEL_REFUSAL
            })
          )
        )
      );

      expect(text('price-tracking-status')).toBe('Status: Paused - this product cannot be searched for');
      expect(text('price-tracking-match')).toBeNull();
      expect(text('price-tracking-search-text')).toBeNull();
      expect(text('price-tracking-refusal')).toBe(UNIT_LABEL_REFUSAL);
      expect(switchControl().disabled).toBe(false);
    });

    /**
     * And once the product is corrected, the same panel reads as awaiting a match again, with the
     * product's new size as the search text and nothing left of the match the suspension cleared.
     */
    it('reads a corrected mapping as awaiting a match with the new search text', async () => {
      const { text } = await render(
        jest.fn(() =>
          of(
            state({
              tracked: true,
              status: ProductPriceTrackingStatus.Requested,
              searchName: 'Coca-Cola Classic',
              unitLabel: '1.25L'
            })
          )
        )
      );

      expect(text('price-tracking-status')).toBe('Status: Awaiting match');
      expect(text('price-tracking-match')).toBeNull();
      expect(text('price-tracking-search-text')).toBe('Searched for as "Coca-Cola Classic 1.25L".');
      expect(text('price-tracking-refusal')).toBeNull();
    });

    it('shows no status for a product that is not tracked', async () => {
      const { text } = await render();

      expect(text('price-tracking-status')).toBeNull();
      expect(text('price-tracking-search-text')).toBeNull();
    });
  });
});
