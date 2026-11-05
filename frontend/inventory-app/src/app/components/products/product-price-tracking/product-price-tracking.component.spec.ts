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
  /**
   * What the page's Save changes does (issue #1111): asks the card to write the switch's request,
   * and records how that write ended.
   */
  const persist = () => {
    const outcome = { completed: false, error: null as unknown };
    fixture.componentInstance.persist().subscribe({
      complete: () => (outcome.completed = true),
      error: (error: unknown) => (outcome.error = error)
    });
    fixture.detectChanges();
    return outcome;
  };

  return { fixture, host, switchControl, text, toggle, persist, openProduct, getPriceTracking, setPriceTracking };
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

    /**
     * Issue #1111: pressing the switch records the request and writes nothing. The page's one Save
     * changes asks the card whether it changed and writes it.
     */
    it('records a turn-on as an unsaved change and writes nothing until asked', async () => {
      const { fixture, switchControl, toggle, setPriceTracking, text } = await render();

      toggle();

      expect(setPriceTracking).not.toHaveBeenCalled();
      expect(switchControl().checked).toBe(true);
      expect(fixture.componentInstance.dirty).toBe(true);
      expect(text('price-tracking-pending')).toContain('Tracking starts when you save.');

      toggle();
      expect(fixture.componentInstance.dirty).toBe(false);
      expect(text('price-tracking-pending')).toBeNull();
    });

    it('turns tracking on when asked and re-reads the state the server decided', async () => {
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
      const { fixture, switchControl, toggle, persist, setPriceTracking, text } = await render(reads);

      toggle();
      const outcome = persist();

      expect(outcome.completed).toBe(true);
      expect(setPriceTracking).toHaveBeenCalledWith(42, true);
      expect(reads).toHaveBeenCalledTimes(2);
      expect(switchControl().checked).toBe(true);
      expect(fixture.componentInstance.dirty).toBe(false);
      expect(text('price-tracking-status-label')).toBe('Awaiting match');
      expect(text('price-tracking-search-text')).toBe('Searched for as "Coca-Cola Classic 375mL".');
    });

    it('turns tracking off when asked', async () => {
      const reads = jest
        .fn()
        .mockReturnValueOnce(of(state({ tracked: true, status: ProductPriceTrackingStatus.Requested })))
        .mockReturnValueOnce(of(state()));
      const { switchControl, toggle, persist, setPriceTracking, text } = await render(reads);

      toggle();
      expect(text('price-tracking-pending')).toBe('Tracking stops when you save.');
      persist();

      expect(setPriceTracking).toHaveBeenCalledWith(42, false);
      expect(switchControl().checked).toBe(false);
    });

    it('writes nothing for an unchanged switch, and completes at once', async () => {
      const { persist, setPriceTracking } = await render();

      expect(persist().completed).toBe(true);
      expect(setPriceTracking).not.toHaveBeenCalled();
    });

    it('hands a failed write to the page and keeps the request, so a second Save retries it', async () => {
      const { fixture, switchControl, toggle, persist } = await render(
        jest.fn(() => of(state())),
        jest.fn(() => throwError(() => new Error('nope')))
      );

      toggle();
      const outcome = persist();

      expect(outcome.error).toBeInstanceOf(Error);
      expect(switchControl().checked).toBe(true);
      expect(fixture.componentInstance.dirty).toBe(true);
    });

    it('keeps the switch unavailable when the state could not be read', async () => {
      const { switchControl, text } = await render(jest.fn(() => throwError(() => new Error('nope'))));

      expect(switchControl().disabled).toBe(true);
      expect(text('price-tracking-error')).toContain('Failed to load supplier price tracking');
    });

    it('holds the switch still while the page is saving', async () => {
      const { fixture, switchControl } = await render();

      fixture.componentRef.setInput('disabled', true);
      fixture.detectChanges();

      expect(switchControl().disabled).toBe(true);
    });

    it('writes nothing while a support session is active', async () => {
      const { fixture, switchControl, toggle, persist, setPriceTracking } = await render(
        jest.fn(() => of(state())),
        jest.fn(() => of(undefined)),
        of(true)
      );

      expect(switchControl().disabled).toBe(true);
      toggle();
      expect(fixture.componentInstance.dirty).toBe(false);
      persist();
      expect(setPriceTracking).not.toHaveBeenCalled();
    });

    it('writes nothing while a role preview is active', async () => {
      const { fixture, switchControl, toggle, setPriceTracking } = await render(
        jest.fn(() => of(state())),
        jest.fn(() => of(undefined)),
        of(false),
        of(true)
      );

      expect(switchControl().disabled).toBe(true);
      toggle();
      expect(fixture.componentInstance.dirty).toBe(false);
      expect(setPriceTracking).not.toHaveBeenCalled();
    });

    it('drops an unsaved request when another product is opened', async () => {
      const reads = jest.fn(() => of(state()));
      const { openProduct, toggle, fixture } = await render(reads);

      toggle();
      openProduct(43);

      expect(reads).toHaveBeenLastCalledWith(43);
      expect(fixture.componentInstance.requested).toBeNull();
      expect(fixture.componentInstance.dirty).toBe(false);
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
      expect(switchControl().checked).toBe(false);
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
      const { switchControl, toggle, persist, text, setPriceTracking } = await render(
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
      persist();
      expect(setPriceTracking).toHaveBeenCalledWith(42, false);
    });
  });

  describe('the status', () => {
    it('reads a requested mapping as awaiting a match, with no matched description', async () => {
      const { text } = await render(
        jest.fn(() => of(state({ tracked: true, status: ProductPriceTrackingStatus.Requested })))
      );

      expect(text('price-tracking-status-label')).toBe('Awaiting match');
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

      expect(text('price-tracking-status-label')).toBe('Matched automatically, awaiting check');
      expect(text('price-tracking-match')).toBe('Classic 375ml');
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

      expect(text('price-tracking-status-label')).toBe('Matched');
      expect(text('price-tracking-match')).toBe('Classic 375ml');
    });

    it('reads a not-found mapping as the recorded answer it is', async () => {
      const { text } = await render(
        jest.fn(() => of(state({ tracked: true, status: ProductPriceTrackingStatus.NotFound })))
      );

      expect(text('price-tracking-status-label')).toBe('Not found');
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

      expect(text('price-tracking-status-label')).toBe('Paused - this product cannot be searched for');
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

      expect(text('price-tracking-status-label')).toBe('Awaiting match');
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

describe('ProductPriceTrackingComponent matching state (issue #1111)', () => {
  it('wraps a long matched product name instead of widening the card', async () => {
    const longName = 'Extremely Long Imported Sparkling Mineral Water With Natural Lemon And Lime Flavour';
    const { host } = await render(
      jest.fn(() =>
        of(
          state({
            tracked: true,
            status: ProductPriceTrackingStatus.Matched,
            masterProduct: { name: longName, sizeValue: 500, sizeUnit: 'ml' }
          })
        )
      )
    );

    const match = host.querySelector('[data-testid="price-tracking-match"]')!;
    expect(match.textContent).toContain(longName);
    expect(match.classList).toContain('break-words');
  });

  it('states only what the server published: no timestamps, supplier counts or sample matches', async () => {
    const { host } = await render(
      jest.fn(() => of(state({ tracked: true, status: ProductPriceTrackingStatus.Requested })))
    );

    expect(host.textContent).not.toMatch(/\d+ suppliers?|last (synced|checked)|ago/i);
    expect(host.querySelector('[data-testid="price-tracking-match"]')).toBeNull();
  });
});
