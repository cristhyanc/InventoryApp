import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
  MasterProductSizeUnit,
  PriceCatalogueReviewQueue,
  ProductMatchSuggestionStatus,
  ReviewAutomaticMatch,
  ReviewSupplierListing,
  ReviewWaitingProduct,
  SupplierListingMatchStatus,
  SupplierPriceBasis,
  SupplierStockStatus,
  SuperAdminPriceCatalogueReviewService
} from '../../../services/super-admin-price-catalogue-review.service';
import { ToastService } from '../../../services/toast.service';
import { SuperAdminPriceCatalogueReviewComponent } from './super-admin-price-catalogue-review.component';

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
});
