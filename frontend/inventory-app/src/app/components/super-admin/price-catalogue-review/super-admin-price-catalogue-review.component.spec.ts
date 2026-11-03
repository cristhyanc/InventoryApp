import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';
import {
  BarcodeScope,
  MasterProductSizeUnit,
  PriceCatalogueReviewQueue,
  ProductMatchSuggestionStatus,
  ReviewAutomaticMatch,
  ReviewSupplierListing,
  ReviewWaitingProduct,
  SupplierListingDecisionResult,
  SupplierListingMatchStatus,
  SupplierListingReviewOutcome,
  SupplierPriceBasis,
  SupplierStockStatus,
  SuperAdminPriceCatalogueReviewService
} from '../../../services/super-admin-price-catalogue-review.service';
import { ToastService } from '../../../services/toast.service';
import { SuperAdminPriceCatalogueReviewComponent } from './super-admin-price-catalogue-review.component';
import { SupplierListingReviewComponent } from './supplier-listing-review.component';

const master = { id: 5, brand: 'Coca-Cola', name: 'Classic', sizeValue: 375, sizeUnit: MasterProductSizeUnit.Millilitre };

function waitingProduct(overrides: Partial<ReviewWaitingProduct> = {}): ReviewWaitingProduct {
  return {
    waitingKey: '16:COCA-COLACLASSIC|5:375ML',
    searchName: 'Coca-Cola Classic',
    unitLabel: '375mL',
    businessesWaitingCount: 2,
    firstRequestedAtUtc: '2026-10-27T05:00:00Z',
    lastUpdatedAtUtc: '2026-10-28T05:00:00Z',
    suggestions: [
      {
        id: 9,
        masterProduct: master,
        suggestedBrand: null,
        suggestedName: null,
        suggestedSizeValue: null,
        suggestedSizeUnit: null,
        confidence: 0.6,
        reason: 'The brand and the size agree.',
        collectorRun: 'price-collector-run-3',
        status: ProductMatchSuggestionStatus.Tentative,
        createdAtUtc: '2026-10-28T05:00:00Z',
        updatedAtUtc: '2026-10-28T05:00:00Z'
      }
    ],
    proposedListings: [],
    ...overrides
  };
}

function automaticMatch(overrides: Partial<ReviewAutomaticMatch> = {}): ReviewAutomaticMatch {
  return {
    suggestionId: 11,
    waitingKey: '16:COCA-COLACLASSIC|5:375ML',
    searchName: 'Coca-Cola Classic',
    unitLabel: '375mL',
    masterProduct: master,
    confidence: 1,
    reason: 'The brand, the variant and the size all agree.',
    collectorRun: 'price-collector-run-9',
    appliedAtUtc: '2026-10-29T05:00:00Z',
    ...overrides
  };
}

function listing(overrides: Partial<ReviewSupplierListing> = {}): ReviewSupplierListing {
  return {
    id: 7,
    supplierPresetId: 2,
    supplierName: 'Alpha Grocer',
    supplierProductId: 'SKU-COLA-30',
    url: 'https://alpha.example/cola',
    title: 'Alpha Grocer Coca-Cola Classic 375mL 30 pack',
    variant: 'Classic',
    sizeValue: 375,
    sizeUnit: MasterProductSizeUnit.Millilitre,
    unitsPerPack: 30,
    barcode: '0012345678905',
    barcodeScope: 1,
    matchStatus: SupplierListingMatchStatus.Proposed,
    masterProduct: master,
    waitingKey: null,
    originWaitingKey: null,
    firstSeenAtUtc: '2026-10-27T05:00:00Z',
    lastSeenAtUtc: '2026-10-30T05:00:00Z',
    lastCheckedAtUtc: null,
    lastNotFoundAtUtc: null,
    lastAcceptedPackPrice: null,
    strongBarcodeMatch: null,
    pendingObservation: {
      id: 3,
      priceCollectorRunId: 4,
      priceCollectorWorkItemId: 6,
      reason: 'listing is not confirmed, so the reading is held as a pending observation',
      packPrice: 42.5,
      regularPackPrice: null,
      currency: 'AUD',
      priceBasis: SupplierPriceBasis.InclusiveOfGst,
      supplierGstRate: 0.1,
      isPromotion: false,
      promotionEndsAtUtc: null,
      stockStatus: SupplierStockStatus.InStock,
      observedAtUtc: '2026-10-30T05:00:00Z',
      sourceReference: null,
      sourceUrl: null,
      variant: 'Classic',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      barcode: '0012345678905',
      barcodeScope: 1,
      unitsPerPack: 30,
      title: 'Alpha Grocer Coca-Cola Classic 375mL 30 pack',
      recordedAtUtc: '2026-10-30T05:00:00Z',
      isFresh: true,
      needsPriceException: false
    },
    ...overrides
  };
}

function queue(overrides: Partial<PriceCatalogueReviewQueue> = {}): PriceCatalogueReviewQueue {
  return {
    waitingProducts: [waitingProduct()],
    automaticMatches: [automaticMatch()],
    listings: [listing()],
    masterProducts: [master],
    ...overrides
  };
}

async function render(service: Partial<SuperAdminPriceCatalogueReviewService>, toast?: Partial<ToastService>) {
  const toastStub = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast };

  await TestBed.configureTestingModule({
    imports: [SuperAdminPriceCatalogueReviewComponent],
    providers: [
      { provide: SuperAdminPriceCatalogueReviewService, useValue: service },
      { provide: ToastService, useValue: toastStub }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SuperAdminPriceCatalogueReviewComponent);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    component: fixture.componentInstance,
    toast: toastStub
  };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

function checkboxWithAccessibleName(host: HTMLElement, name: string): HTMLInputElement {
  const checkbox = host.querySelector<HTMLInputElement>(`input[type="checkbox"][aria-label="${name}"]`);
  expect(checkbox).not.toBeNull();
  return checkbox!;
}

function confirmSelectedButton(host: HTMLElement): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) =>
    /^Confirm selected|^Confirming /.test(button.textContent?.trim() ?? '')
  );
  expect(match).toBeDefined();
  return match!;
}

function check(input: HTMLInputElement, checked: boolean): void {
  input.checked = checked;
  input.dispatchEvent(new Event('change'));
}

function cardFor(
  fixture: ComponentFixture<SuperAdminPriceCatalogueReviewComponent>,
  listingId: number
): SupplierListingReviewComponent {
  const card = fixture.debugElement
    .queryAll(By.directive(SupplierListingReviewComponent))
    .map((debugEl) => debugEl.componentInstance as SupplierListingReviewComponent)
    .find((candidate) => candidate.listing.id === listingId);
  expect(card).toBeDefined();
  return card!;
}

function decisionResult(overrides: Partial<SupplierListingDecisionResult> = {}): SupplierListingDecisionResult {
  return {
    supplierListingId: 7,
    outcome: SupplierListingReviewOutcome.Confirmed,
    matchStatus: SupplierListingMatchStatus.Confirmed,
    priceAccepted: true,
    priceExceptionUsed: false,
    priceReason: null,
    acceptedObservationId: 12,
    acceptedPackPrice: 42.5,
    confirmedUnitsPerPack: 30,
    ...overrides
  };
}

describe('SuperAdminPriceCatalogueReviewComponent (issue #928)', () => {
  it('renders the three sections the queue is read into', async () => {
    const { host } = await render({ queue: () => of(queue()) });

    const text = host.textContent ?? '';
    expect(text).toContain('Products awaiting a match');
    expect(text).toContain('Automatic matches to check');
    expect(text).toContain('Supplier listings awaiting a decision');
  });

  /**
   * The count of businesses waiting is the one thing the page may say about who wants a price, and it
   * is a number: there is no business name or id in the contract at all.
   */
  it('shows how many businesses are waiting and never which', async () => {
    const { host } = await render({ queue: () => of(queue()) });

    expect(host.textContent).toContain('2 businesses are waiting');
  });

  it('flags an automatic match as automatic, with its run, confidence and reason', async () => {
    const { host } = await render({ queue: () => of(queue()) });

    const text = host.textContent ?? '';
    expect(text).toContain('Auto-matched');
    expect(text).toContain('price-collector-run-9');
    expect(text).toContain('100%');
    expect(text).toContain('The brand, the variant and the size all agree.');
  });

  it('says so, rather than nothing, when there is nothing to review', async () => {
    const { host, component } = await render({
      queue: () => of(queue({ waitingProducts: [], automaticMatches: [], listings: [] }))
    });

    expect(component.isEmpty).toBe(true);
    expect(host.textContent).toContain('Nothing is waiting for a decision.');
  });

  it('reports a failed read as a failed read, never as an empty queue', async () => {
    const { host, component } = await render({ queue: () => throwError(() => new Error('nope')) });

    expect(component.loadError).toBe(true);
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('Unable to load the review queue.');
    expect(host.textContent).not.toContain('Nothing is waiting for a decision.');
  });

  it('confirms an automatic match and re-reads the queue', async () => {
    const confirmAutomaticMatch = jest.fn(() =>
      of({ suggestionId: 11, decision: 1, waitingKey: 'k', masterProduct: master, listingsMovedCount: 0 })
    );
    const read = jest.fn(() => of(queue()));
    const { host, fixture, toast } = await render({ queue: read, confirmAutomaticMatch });

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(confirmAutomaticMatch).toHaveBeenCalledWith(11);
    expect(toast.success).toHaveBeenCalled();
    expect(read).toHaveBeenCalledTimes(2);
  });

  /**
   * An undo is destructive enough to confirm first - it returns every business waiting under the key
   * to awaiting a match - so it goes through the shared confirmation dialog, and the message says
   * what will happen to the listings.
   */
  it('asks before undoing an automatic match, and says what an undo does', async () => {
    const undoAutomaticMatch = jest.fn(() =>
      of({ suggestionId: 11, decision: 2, waitingKey: 'k', masterProduct: master, listingsMovedCount: 2 })
    );
    const read = jest.fn(() => of(queue()));
    const { host, fixture, component, toast } = await render({ queue: read, undoAutomaticMatch });

    buttonWithText(host, 'Undo').click();
    fixture.detectChanges();

    expect(component.undoTarget?.suggestionId).toBe(11);
    expect(host.querySelector('.confirm-modal')?.textContent).toContain('confirmed listings');
    expect(undoAutomaticMatch).not.toHaveBeenCalled();

    component.confirmUndo();
    fixture.detectChanges();

    expect(undoAutomaticMatch).toHaveBeenCalledWith(11);
    expect(toast.success).toHaveBeenCalledWith(expect.stringContaining('2 supplier listings moved back'));
    expect(read).toHaveBeenCalledTimes(2);
  });

  it('cancelling an undo decides nothing', async () => {
    const undoAutomaticMatch = jest.fn();
    const { host, fixture, component } = await render({ queue: () => of(queue()), undoAutomaticMatch });

    buttonWithText(host, 'Undo').click();
    fixture.detectChanges();
    component.cancelUndo();
    fixture.detectChanges();

    expect(component.undoTarget).toBeNull();
    expect(undoAutomaticMatch).not.toHaveBeenCalled();
    expect(host.querySelector('.confirm-modal')).toBeNull();
  });

  /**
   * A refusal that says the page is stale - the match was confirmed in another tab - shows the
   * server's own sentence and re-reads the queue, because the alternative is a reviewer deciding
   * against a screen that is no longer true.
   */
  it('re-reads the queue when a decision is refused as stale', async () => {
    const read = jest.fn(() => of(queue()));
    const { host, fixture, toast } = await render({
      queue: read,
      confirmAutomaticMatch: () =>
        throwError(() => ({ status: 409, error: { message: 'That match is not an automatic match.' } }))
    });

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(toast.error).toHaveBeenCalledWith('That match is not an automatic match.');
    expect(read).toHaveBeenCalledTimes(2);
  });

  it('says what an automatic match was a match of when no mapping is left to describe it', async () => {
    const { component } = await render({ queue: () => of(queue()) });

    expect(component.searchedFor(automaticMatch({ searchName: null, unitLabel: null }))).toBe(
      'the search this key folds from'
    );
  });

  describe('Confirm selected (issue #1055)', () => {
    const listingA = listing({ id: 7, supplierName: 'Alpha Grocer', supplierProductId: 'SKU-COLA-30' });
    const listingB = listing({ id: 8, supplierName: 'Beta Mart', supplierProductId: 'SKU-COLA-24', unitsPerPack: 24 });
    const listingC = listing({ id: 9, supplierName: 'Gamma Foods', supplierProductId: 'SKU-COLA-12', unitsPerPack: 12 });

    function twoListingQueue(): PriceCatalogueReviewQueue {
      return queue({ waitingProducts: [], automaticMatches: [], listings: [listingA, listingB] });
    }

    function threeListingQueue(): PriceCatalogueReviewQueue {
      return queue({ waitingProducts: [], automaticMatches: [], listings: [listingA, listingB, listingC] });
    }

    it('names the supplier and the supplier product id on each listing checkbox', async () => {
      const { host } = await render({ queue: () => of(queue()) });

      checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30');
    });

    it('selects every listing shown, clears them, and shows indeterminate when only some are selected', async () => {
      const { host, fixture, component } = await render({ queue: () => of(twoListingQueue()) });

      const selectAll = checkboxWithAccessibleName(host, 'Select all supplier listings');
      const checkboxA = checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30');
      const checkboxB = checkboxWithAccessibleName(host, 'Select Beta Mart SKU-COLA-24');

      check(checkboxA, true);
      fixture.detectChanges();

      expect(component.isListingSelected(7)).toBe(true);
      expect(component.someListingsSelected).toBe(true);
      expect(component.allListingsSelected).toBe(false);
      expect(selectAll.indeterminate).toBe(true);

      check(selectAll, true);
      fixture.detectChanges();

      expect(component.allListingsSelected).toBe(true);
      expect(checkboxB.checked).toBe(true);

      check(selectAll, false);
      fixture.detectChanges();

      expect(component.isListingSelected(7)).toBe(false);
      expect(component.isListingSelected(8)).toBe(false);
    });

    it('disables Confirm selected with nothing selected, and enables it once something is', async () => {
      const { host, fixture } = await render({ queue: () => of(queue()) });

      expect(confirmSelectedButton(host).disabled).toBe(true);

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      fixture.detectChanges();

      expect(confirmSelectedButton(host).disabled).toBe(false);
      expect(confirmSelectedButton(host).textContent?.trim()).toBe('Confirm selected (1)');
    });

    it('opens the confirmation dialog on click, and Cancel sends nothing', async () => {
      const decideListing = jest.fn();
      const { host, fixture, component } = await render({ queue: () => of(queue()), decideListing });

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      fixture.detectChanges();
      confirmSelectedButton(host).click();
      fixture.detectChanges();

      expect(host.querySelector('.confirm-modal')?.textContent).toContain('Confirm 1 supplier listings?');
      expect(decideListing).not.toHaveBeenCalled();

      buttonWithText(host, 'Cancel').click();
      fixture.detectChanges();

      expect(component.confirmSelectedRequested).toBe(false);
      expect(decideListing).not.toHaveBeenCalled();
      expect(component.isListingSelected(7)).toBe(true);
    });

    it('confirms each selected listing one request at a time, with that card own values and no price exception', async () => {
      const decideListing = jest.fn(() => of(decisionResult()));
      const read = jest.fn(() => of(twoListingQueue()));
      const { host, fixture } = await render({ queue: read, decideListing });

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      check(checkboxWithAccessibleName(host, 'Select Beta Mart SKU-COLA-24'), true);
      fixture.detectChanges();

      // An edit on one card's own form travels with it, exactly as a single Confirm listing would send it.
      cardFor(fixture, 8).unitsPerPack = 36;

      confirmSelectedButton(host).click();
      fixture.detectChanges();
      buttonWithText(host, 'Confirm selected').click();
      fixture.detectChanges();

      expect(decideListing).toHaveBeenCalledTimes(2);
      expect(decideListing).toHaveBeenNthCalledWith(1, 7, {
        outcome: SupplierListingReviewOutcome.Confirmed,
        unitsPerPack: 30,
        barcode: '0012345678905',
        barcodeScope: BarcodeScope.Unit,
        variant: 'Classic',
        sizeValue: 375,
        sizeUnit: MasterProductSizeUnit.Millilitre,
        approvePriceException: false
      });
      expect(decideListing).toHaveBeenNthCalledWith(2, 8, {
        outcome: SupplierListingReviewOutcome.Confirmed,
        unitsPerPack: 36,
        barcode: '0012345678905',
        barcodeScope: BarcodeScope.Unit,
        variant: 'Classic',
        sizeValue: 375,
        sizeUnit: MasterProductSizeUnit.Millilitre,
        approvePriceException: false
      });
      expect(read).toHaveBeenCalledTimes(2);
    });

    it('does not stop at one failure, summarises the run, keeps failures selected with their reasons, and reloads once', async () => {
      const decideListing = jest.fn((id: number) => {
        if (id === 7) {
          return of(decisionResult({ supplierListingId: 7 }));
        }

        if (id === 8) {
          return throwError(() => ({ status: 400, error: { message: 'The identity submitted does not agree.' } }));
        }

        return throwError(() => ({ status: 409, error: { message: 'This supplier listing is not waiting for a decision.' } }));
      });
      const read = jest.fn(() => of(threeListingQueue()));
      const { host, fixture, component, toast } = await render({ queue: read, decideListing });

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      check(checkboxWithAccessibleName(host, 'Select Beta Mart SKU-COLA-24'), true);
      check(checkboxWithAccessibleName(host, 'Select Gamma Foods SKU-COLA-12'), true);
      fixture.detectChanges();

      confirmSelectedButton(host).click();
      fixture.detectChanges();
      buttonWithText(host, 'Confirm selected').click();
      fixture.detectChanges();

      expect(decideListing).toHaveBeenCalledTimes(3);
      expect(toast.warning).toHaveBeenCalledWith(expect.stringContaining('1 confirmed, 2 not confirmed'));
      expect(read).toHaveBeenCalledTimes(2);

      expect(component.isListingSelected(7)).toBe(false);
      expect(component.isListingSelected(8)).toBe(true);
      expect(component.isListingSelected(9)).toBe(true);
      expect(component.bulkConfirmErrorFor(8)).toBe('The identity submitted does not agree.');
      expect(component.bulkConfirmErrorFor(9)).toBe('This supplier listing is not waiting for a decision.');

      const text = host.textContent ?? '';
      expect(text).toContain('The identity submitted does not agree.');
      expect(text).toContain('This supplier listing is not waiting for a decision.');
    });

    it('disables every card, the checkboxes and Select all, and shows progress, while the run is in progress', async () => {
      const firstRequest = new Subject<SupplierListingDecisionResult>();
      const decideListing = jest.fn(() => firstRequest.asObservable());
      const { host, fixture } = await render({ queue: () => of(twoListingQueue()), decideListing });

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      check(checkboxWithAccessibleName(host, 'Select Beta Mart SKU-COLA-24'), true);
      fixture.detectChanges();

      confirmSelectedButton(host).click();
      fixture.detectChanges();
      buttonWithText(host, 'Confirm selected').click();
      fixture.detectChanges();

      expect(confirmSelectedButton(host).disabled).toBe(true);
      expect(confirmSelectedButton(host).textContent?.trim()).toBe('Confirming 1 of 2…');
      expect(checkboxWithAccessibleName(host, 'Select all supplier listings').disabled).toBe(true);
      expect(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30').disabled).toBe(true);
      expect(buttonWithText(host, 'Confirm listing').disabled).toBe(true);
      expect(buttonWithText(host, 'Reject listing').disabled).toBe(true);

      firstRequest.next(decisionResult());
      firstRequest.complete();
      fixture.detectChanges();
    });

    /**
     * The regression this is here for: Refresh stayed clickable while a run was in flight, and the
     * read it started took the cards out from under the run - the loading branch removed them, and
     * the read that came back handed the cards the stored values again. The listing waiting its turn
     * then had either no card to ask or a card holding values nobody had reviewed.
     */
    it('declines a Refresh asked for mid-run, keeping every card and its edits until the run ends', async () => {
      const firstRequest = new Subject<SupplierListingDecisionResult>();
      const decideListing = jest.fn((id: number) =>
        id === 7 ? firstRequest.asObservable() : of(decisionResult({ supplierListingId: id }))
      );
      const read = jest.fn(() => of(twoListingQueue()));
      const { host, fixture, component } = await render({ queue: read, decideListing });

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      check(checkboxWithAccessibleName(host, 'Select Beta Mart SKU-COLA-24'), true);
      fixture.detectChanges();

      const cardB = cardFor(fixture, 8);
      cardB.unitsPerPack = 36;

      confirmSelectedButton(host).click();
      fixture.detectChanges();
      buttonWithText(host, 'Confirm selected').click();
      fixture.detectChanges();

      // The first listing's request is still in flight, so Refresh is not offered, and the read is
      // declined however it is asked for - the button, a stale refusal, or another section's toast.
      const refresh = buttonWithText(host, 'Refresh');
      expect(refresh.disabled).toBe(true);

      refresh.click();
      component.load();
      component.onStaleState();
      component.onDecided('something else on the page was decided');
      fixture.detectChanges();

      expect(read).toHaveBeenCalledTimes(1);
      expect(component.loading).toBe(false);
      expect(cardFor(fixture, 8)).toBe(cardB);
      expect(cardB.unitsPerPack).toBe(36);
      expect(decideListing).toHaveBeenCalledTimes(1);

      firstRequest.next(decisionResult({ supplierListingId: 7 }));
      firstRequest.complete();
      fixture.detectChanges();

      // The run finished with the edit it was given, and the queue is read exactly once afterwards.
      expect(decideListing).toHaveBeenCalledTimes(2);
      expect(decideListing).toHaveBeenNthCalledWith(2, 8, {
        outcome: SupplierListingReviewOutcome.Confirmed,
        unitsPerPack: 36,
        barcode: '0012345678905',
        barcodeScope: BarcodeScope.Unit,
        variant: 'Classic',
        sizeValue: 375,
        sizeUnit: MasterProductSizeUnit.Millilitre,
        approvePriceException: false
      });
      expect(component.bulkConfirmInProgress).toBe(false);
      expect(read).toHaveBeenCalledTimes(2);
      expect(buttonWithText(host, 'Refresh').disabled).toBe(false);
    });

    /**
     * The other sections stay usable during a run, and a decision made in one of them re-reads the
     * queue today. That read waits for the run's own single read rather than replacing its cards.
     */
    it('leaves the cards alone when another section decides something mid-run', async () => {
      const firstRequest = new Subject<SupplierListingDecisionResult>();
      const decideListing = jest.fn(() => firstRequest.asObservable());
      const confirmAutomaticMatch = jest.fn(() =>
        of({ suggestionId: 11, decision: 1, waitingKey: 'k', masterProduct: master, listingsMovedCount: 0 })
      );
      const read = jest.fn(() => of(queue()));
      const { host, fixture, component, toast } = await render({ queue: read, decideListing, confirmAutomaticMatch });

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      fixture.detectChanges();

      const card = cardFor(fixture, 7);
      card.unitsPerPack = 42;

      confirmSelectedButton(host).click();
      fixture.detectChanges();
      buttonWithText(host, 'Confirm selected').click();
      fixture.detectChanges();

      buttonWithText(host, 'Confirm').click();
      fixture.detectChanges();

      expect(confirmAutomaticMatch).toHaveBeenCalledWith(11);
      expect(toast.success).toHaveBeenCalled();
      expect(read).toHaveBeenCalledTimes(1);
      expect(cardFor(fixture, 7)).toBe(card);
      expect(card.unitsPerPack).toBe(42);

      firstRequest.next(decisionResult());
      firstRequest.complete();
      fixture.detectChanges();

      expect(decideListing).toHaveBeenCalledWith(7, expect.objectContaining({ unitsPerPack: 42 }));
      expect(component.bulkConfirmInProgress).toBe(false);
      expect(read).toHaveBeenCalledTimes(2);
    });

    /**
     * Nothing is invented for a selected listing with no card on screen: a confirmation is the pack
     * quantity, barcode and identity that card shows, so the run reports it as not confirmed, says
     * why where its own refusal would have been said, and carries on.
     */
    it('reports a selected listing with no card as not confirmed instead of sending default values', async () => {
      const decideListing = jest.fn(() => of(decisionResult()));
      const read = jest
        .fn()
        .mockReturnValueOnce(of(twoListingQueue()))
        .mockReturnValue(of(queue({ waitingProducts: [], automaticMatches: [], listings: [listingB, listingC] })));
      const { host, fixture, component, toast } = await render({ queue: read, decideListing });

      check(checkboxWithAccessibleName(host, 'Select Alpha Grocer SKU-COLA-30'), true);
      fixture.detectChanges();

      // A listing the run is told to confirm that the view was never given a card for.
      component.queue = { ...component.queue!, listings: [listingA, listingB, listingC] };
      component.selectListing(9);

      expect(() => component.confirmSelected()).not.toThrow();
      fixture.detectChanges();

      expect(decideListing).toHaveBeenCalledTimes(1);
      expect(decideListing).toHaveBeenCalledWith(7, expect.objectContaining({ unitsPerPack: 30 }));
      expect(toast.warning).toHaveBeenCalledWith(expect.stringContaining('1 confirmed, 1 not confirmed'));
      expect(component.bulkConfirmInProgress).toBe(false);
      expect(component.bulkConfirmProgress).toBeNull();
      expect(component.isListingSelected(9)).toBe(true);
      expect(component.bulkConfirmErrorFor(9)).toBe(
        'This listing was no longer on screen when the run reached it, so nothing was sent for it.'
      );
      expect(host.textContent).toContain('no longer on screen when the run reached it');
      expect(read).toHaveBeenCalledTimes(2);
    });
  });
});
