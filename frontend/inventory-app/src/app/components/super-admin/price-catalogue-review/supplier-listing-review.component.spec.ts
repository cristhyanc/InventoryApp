import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
  BarcodeScope,
  MasterProductSizeUnit,
  ReviewPendingObservation,
  ReviewSupplierListing,
  SupplierListingMatchStatus,
  SupplierListingReviewOutcome,
  SupplierPriceBasis,
  SupplierStockStatus,
  SuperAdminPriceCatalogueReviewService
} from '../../../services/super-admin-price-catalogue-review.service';
import { SupplierListingReviewComponent } from './supplier-listing-review.component';

const master = { id: 5, brand: 'Coca-Cola', name: 'Classic', sizeValue: 375, sizeUnit: MasterProductSizeUnit.Millilitre };

function pending(overrides: Partial<ReviewPendingObservation> = {}): ReviewPendingObservation {
  return {
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
    barcodeScope: BarcodeScope.Unit,
    unitsPerPack: 30,
    title: 'Alpha Grocer Coca-Cola Classic 375mL 30 pack',
    recordedAtUtc: '2026-10-30T05:00:00Z',
    isFresh: true,
    needsPriceException: false,
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
    barcodeScope: BarcodeScope.Unit,
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
    pendingObservation: pending(),
    ...overrides
  };
}

async function render(
  row: ReviewSupplierListing,
  decideListing: jest.Mock = jest.fn(() =>
    of({
      supplierListingId: row.id,
      outcome: SupplierListingReviewOutcome.Confirmed,
      matchStatus: SupplierListingMatchStatus.Confirmed,
      priceAccepted: true,
      priceExceptionUsed: false,
      priceReason: null,
      acceptedObservationId: 12,
      acceptedPackPrice: 42.5,
      confirmedUnitsPerPack: 30
    })
  )
) {
  // Reset first, so a test that renders two listings in turn - the price exception is offered for one
  // and not the other - can configure the module twice.
  TestBed.resetTestingModule();

  await TestBed.configureTestingModule({
    imports: [SupplierListingReviewComponent],
    providers: [{ provide: SuperAdminPriceCatalogueReviewService, useValue: { decideListing } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierListingReviewComponent);
  fixture.componentRef.setInput('listing', row);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance, decideListing };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('SupplierListingReviewComponent (issue #928)', () => {
  /** The first criterion's own list of evidence, over the wire and onto the screen. */
  it('shows every piece of evidence the collector submitted', async () => {
    const { host } = await render(listing());

    const text = host.textContent ?? '';
    expect(text).toContain('Alpha Grocer');
    expect(text).toContain('SKU-COLA-30');
    expect(text).toContain('Alpha Grocer Coca-Cola Classic 375mL 30 pack');
    expect(text).toContain('Classic');
    expect(text).toContain('375ml');
    expect(text).toContain('0012345678905');
    expect(text).toContain('30');
    expect(text).toContain('Work item 6');
    expect(text).toContain('42.50');
    expect(text).toContain('listing is not confirmed');
  });

  /** Supplier text is plain text, and the listing's own page is the only link offered. */
  it('offers the listing own page as the only link', async () => {
    const { host } = await render(listing());

    const links = Array.from(host.querySelectorAll('a'));
    expect(links).toHaveLength(1);
    expect(links[0].getAttribute('href')).toBe('https://alpha.example/cola');
    expect(links[0].getAttribute('rel')).toBe('noopener noreferrer');
  });

  it('shows a stored address that is not a safe http(s) one as plain text', async () => {
    const { host } = await render(listing({ url: 'javascript:alert(1)' }));

    expect(host.querySelectorAll('a')).toHaveLength(0);
    expect(host.textContent).toContain('javascript:alert(1)');
  });

  it('marks a barcode that a confirmed listing already carries, and says which', async () => {
    const { host } = await render(
      listing({
        strongBarcodeMatch: { supplierListingId: 11, supplierName: 'Woolworths', masterProduct: master }
      })
    );

    const text = host.textContent ?? '';
    expect(text).toContain('Barcode match');
    expect(text).toContain('Woolworths · Coca-Cola Classic 375ml');
  });

  /** Unknown stays unknown: no pack size means no per-unit price, which is an answer. */
  it('says a pack quantity was not published rather than showing a number', async () => {
    const { host } = await render(listing({ unitsPerPack: null }));

    expect(host.textContent).toContain('Not published');
  });

  it('confirms the listing with the identity as reviewed, corrections included', async () => {
    const { host, component, decideListing } = await render(listing());
    const decided = jest.fn();
    component.decided.subscribe(decided);

    component.unitsPerPack = 36;
    buttonWithText(host, 'Confirm listing').click();

    expect(decideListing).toHaveBeenCalledWith(7, {
      outcome: SupplierListingReviewOutcome.Confirmed,
      unitsPerPack: 36,
      barcode: '0012345678905',
      barcodeScope: BarcodeScope.Unit,
      variant: 'Classic',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      approvePriceException: false
    });
    expect(decided).toHaveBeenCalledWith(expect.stringContaining('its pending price was accepted'));
  });

  /**
   * "Confirmed with no current price" is a real and expected answer, so the reviewer is told which
   * of the two happened and why - the server's own reason, not a second copy of the rule.
   */
  it('reports a confirmation that accepted no price with the server own reason', async () => {
    const decideListing = jest.fn(() =>
      of({
        supplierListingId: 7,
        outcome: SupplierListingReviewOutcome.Confirmed,
        matchStatus: SupplierListingMatchStatus.Confirmed,
        priceAccepted: false,
        priceExceptionUsed: false,
        priceReason: 'the pending reading is older than this supplier price freshness limit',
        acceptedObservationId: null,
        acceptedPackPrice: null,
        confirmedUnitsPerPack: 30
      })
    );
    const { host, component } = await render(listing(), decideListing);
    const decided = jest.fn();
    component.decided.subscribe(decided);

    buttonWithText(host, 'Confirm listing').click();

    expect(decided).toHaveBeenCalledWith(expect.stringContaining('confirmed with no current price'));
  });

  it('refuses the listing without correcting anything', async () => {
    const decideListing = jest.fn(() =>
      of({
        supplierListingId: 7,
        outcome: SupplierListingReviewOutcome.Rejected,
        matchStatus: SupplierListingMatchStatus.Rejected,
        priceAccepted: false,
        priceExceptionUsed: false,
        priceReason: null,
        acceptedObservationId: null,
        acceptedPackPrice: null,
        confirmedUnitsPerPack: null
      })
    );
    const { host, component } = await render(listing(), decideListing);
    const decided = jest.fn();
    component.decided.subscribe(decided);

    buttonWithText(host, 'Reject listing').click();

    expect(decideListing).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ outcome: SupplierListingReviewOutcome.Rejected })
    );
    expect(decided).toHaveBeenCalledWith(expect.stringContaining('was refused'));
  });

  /**
   * The price exception is offered only when the band is what would otherwise refuse the reading,
   * which is the server's own answer rather than something this page works out.
   */
  it('offers the price exception only when the server says the band would refuse the reading', async () => {
    const { host } = await render(listing());

    expect(host.textContent).not.toContain('Approve this price exception');

    const { host: flagged } = await render(
      listing({
        matchStatus: SupplierListingMatchStatus.NeedsReview,
        lastAcceptedPackPrice: 40,
        pendingObservation: pending({ packPrice: 9, needsPriceException: true })
      })
    );

    expect(flagged.textContent).toContain('Approve this price exception');
    expect(flagged.textContent).toContain('more than 50% below or 100% above');
  });

  it('submits the approved exception when the reviewer ticks it', async () => {
    const { host, component, decideListing } = await render(
      listing({
        matchStatus: SupplierListingMatchStatus.NeedsReview,
        lastAcceptedPackPrice: 40,
        pendingObservation: pending({ packPrice: 9, needsPriceException: true })
      })
    );

    component.approvePriceException = true;
    buttonWithText(host, 'Confirm listing').click();

    expect(decideListing).toHaveBeenCalledWith(7, expect.objectContaining({ approvePriceException: true }));
  });

  it('says a stale reading will not be priced, before anything is confirmed', async () => {
    const { host } = await render(listing({ pendingObservation: pending({ isFresh: false }) }));

    expect(host.textContent).toContain('older than the supplier');
  });

  it('says a listing holding no reading will be confirmed with no price', async () => {
    const { host } = await render(listing({ pendingObservation: null }));

    expect(host.textContent).toContain('holding no reading');
  });

  it('shows the server own refusal and asks the page to re-read a stale one', async () => {
    const decideListing = jest.fn(() =>
      throwError(() => ({ status: 409, error: { message: 'This supplier listing is not waiting for a decision.' } }))
    );
    const { host, component } = await render(listing(), decideListing);
    const stale = jest.fn();
    component.staleState.subscribe(stale);

    buttonWithText(host, 'Confirm listing').click();

    expect(component.error).toBe('This supplier listing is not waiting for a decision.');
    expect(stale).toHaveBeenCalled();
  });

  /**
   * The form shows what is stored, never what was last typed: the page re-reads the queue after
   * every decision, and a correction carried onto another listing would be a pack quantity nobody
   * reviewed.
   */
  it('starts from what a fresh held reading reports, keeping the stored value for what it leaves out', async () => {
    const { component } = await render(
      listing({
        pendingObservation: pending({ variant: null, sizeValue: 46, sizeUnit: MasterProductSizeUnit.Gram, unitsPerPack: 1 })
      })
    );

    expect(component.variant).toBe('Classic');
    expect(component.sizeValue).toBe(46);
    expect(component.sizeUnit).toBe(MasterProductSizeUnit.Gram);
    expect(component.unitsPerPack).toBe(1);
  });

  it('keeps the stored identity when the held reading is stale', async () => {
    const { component } = await render(listing({ pendingObservation: pending({ isFresh: false, unitsPerPack: 1 }) }));

    expect(component.unitsPerPack).toBe(30);
  });

  it('starts from the stored identity when another listing is reviewed', async () => {
    const { fixture, component } = await render(listing());

    component.unitsPerPack = 99;
    fixture.componentRef.setInput('listing', listing({ id: 8, unitsPerPack: 24, pendingObservation: null }));
    fixture.detectChanges();

    expect(component.unitsPerPack).toBe(24);
    expect(component.approvePriceException).toBe(false);
  });

  /**
   * The page's "Confirm selected" run (issue #1055) asks each card for exactly what its own
   * Confirm listing button would send, with any edits included, and the price exception never
   * approved in bulk even when the reviewer ticked it on this card.
   */
  it('builds the bulk confirmation request from this card own current values, never approving the price exception', async () => {
    const { component } = await render(
      listing({
        matchStatus: SupplierListingMatchStatus.NeedsReview,
        lastAcceptedPackPrice: 40,
        pendingObservation: pending({ packPrice: 9, needsPriceException: true })
      })
    );

    component.unitsPerPack = 36;
    component.approvePriceException = true;

    expect(component.buildConfirmationRequest()).toEqual({
      outcome: SupplierListingReviewOutcome.Confirmed,
      unitsPerPack: 36,
      barcode: '0012345678905',
      barcodeScope: BarcodeScope.Unit,
      variant: 'Classic',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      approvePriceException: false
    });
  });

  it('disables its own fields and buttons while a bulk run is in progress', async () => {
    const { host, fixture, component } = await render(listing());

    fixture.componentRef.setInput('bulkDisabled', true);
    fixture.detectChanges();

    expect(component.controlsDisabled).toBe(true);
    expect(buttonWithText(host, 'Confirm listing').disabled).toBe(true);
    expect(buttonWithText(host, 'Reject listing').disabled).toBe(true);
  });

  /**
   * A bulk run disables every card the moment it starts, and that must not be read as a new listing:
   * the run confirms each selected listing with exactly what its own card shows, so resetting the
   * form here would send the stored pack quantity for every card whose request has not gone yet.
   */
  it('keeps this card edits when the page disables it for a bulk run, or reports a reason on it', async () => {
    const { fixture, component } = await render(listing());

    component.unitsPerPack = 36;
    component.variant = 'Zero Sugar';

    fixture.componentRef.setInput('bulkDisabled', true);
    fixture.detectChanges();

    expect(component.unitsPerPack).toBe(36);
    expect(component.variant).toBe('Zero Sugar');

    fixture.componentRef.setInput('externalError', 'This supplier listing is not waiting for a decision.');
    fixture.detectChanges();

    expect(component.unitsPerPack).toBe(36);
    expect(component.buildConfirmationRequest().unitsPerPack).toBe(36);
  });

  /**
   * The page's bulk run keeps a failed listing's refusal reason outside this card (issue #1055),
   * because the one queue re-read after the run replaces `listing` and resets this card's own
   * `error` - so the card shows the externally supplied reason the same way it shows its own.
   */
  it('shows a bulk run own refusal reason for this listing, the same way a single confirm does', async () => {
    const { host, fixture } = await render(listing());

    fixture.componentRef.setInput('externalError', 'This supplier listing is not waiting for a decision.');
    fixture.detectChanges();

    expect(host.querySelector('[role="alert"]')?.textContent).toBe(
      'This supplier listing is not waiting for a decision.'
    );
  });
});
