import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import {
  MasterProductSizeUnit,
  ProductMatchReviewDecision,
  ProductMatchSuggestionStatus,
  ReviewMatchSuggestion,
  ReviewWaitingProduct,
  SuperAdminPriceCatalogueReviewService
} from '../../../services/super-admin-price-catalogue-review.service';
import { WaitingProductMatchComponent } from './waiting-product-match.component';

const master = { id: 5, brand: 'Coca-Cola', name: 'Classic', sizeValue: 375, sizeUnit: MasterProductSizeUnit.Millilitre };
const other = { id: 6, brand: 'Coca-Cola', name: 'Zero Sugar', sizeValue: 375, sizeUnit: MasterProductSizeUnit.Millilitre };

function suggestion(overrides: Partial<ReviewMatchSuggestion> = {}): ReviewMatchSuggestion {
  return {
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
    updatedAtUtc: '2026-10-28T05:00:00Z',
    ...overrides
  };
}

function product(overrides: Partial<ReviewWaitingProduct> = {}): ReviewWaitingProduct {
  return {
    waitingKey: '16:COCA-COLACLASSIC|5:375ML',
    searchName: 'Coca-Cola Classic',
    unitLabel: '375mL',
    businessesWaitingCount: 2,
    firstRequestedAtUtc: '2026-10-27T05:00:00Z',
    lastUpdatedAtUtc: '2026-10-28T05:00:00Z',
    suggestions: [suggestion()],
    proposedListings: [],
    ...overrides
  };
}

async function render(
  waiting: ReviewWaitingProduct,
  decideMatch: jest.Mock = jest.fn(() =>
    of({
      waitingKey: waiting.waitingKey,
      decision: ProductMatchReviewDecision.AcceptSuggestion,
      masterProduct: master,
      masterProductCreated: false,
      suggestionId: 9,
      listingsMovedCount: 1
    })
  )
) {
  await TestBed.configureTestingModule({
    imports: [WaitingProductMatchComponent],
    providers: [{ provide: SuperAdminPriceCatalogueReviewService, useValue: { decideMatch } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(WaitingProductMatchComponent);
  fixture.componentRef.setInput('product', waiting);
  fixture.componentRef.setInput('masterProducts', [master, other]);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance, decideMatch };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('WaitingProductMatchComponent (issue #928)', () => {
  it('shows the product by its search name and unit label, with the count of businesses waiting', async () => {
    const { host } = await render(product());

    const text = host.textContent ?? '';
    expect(text).toContain('Coca-Cola Classic');
    expect(text).toContain('375mL');
    expect(text).toContain('2 businesses are waiting');
  });

  it('shows the matcher own confidence and reason, which is the evidence a reviewer reads', async () => {
    const { host } = await render(product());

    const text = host.textContent ?? '';
    expect(text).toContain('60%');
    expect(text).toContain('price-collector-run-3');
    expect(text).toContain('The brand and the size agree.');
  });

  it('accepts a suggestion that names an existing master product', async () => {
    const { host, decideMatch, component } = await render(product());
    const decided = jest.fn();
    component.decided.subscribe(decided);

    buttonWithText(host, 'Accept match').click();

    expect(decideMatch).toHaveBeenCalledWith(
      expect.objectContaining({
        waitingKey: '16:COCA-COLACLASSIC|5:375ML',
        decision: ProductMatchReviewDecision.AcceptSuggestion,
        suggestionId: 9
      })
    );
    expect(decided).toHaveBeenCalled();
  });

  it('refuses a suggestion without matching anything', async () => {
    const { host, decideMatch } = await render(product());

    buttonWithText(host, 'Reject suggestion').click();

    expect(decideMatch).toHaveBeenCalledWith(
      expect.objectContaining({ decision: ProductMatchReviewDecision.RejectSuggestion, suggestionId: 9 })
    );
  });

  /**
   * A suggestion that describes a new master product says so before it is accepted, because
   * accepting it is what creates that product - this surface is its only writer.
   */
  it('says when accepting a suggestion would create a master product', async () => {
    const described = suggestion({
      masterProduct: null,
      suggestedBrand: 'Coca-Cola',
      suggestedName: 'Classic',
      suggestedSizeValue: 375,
      suggestedSizeUnit: MasterProductSizeUnit.Millilitre
    });
    const { host } = await render(product({ suggestions: [described] }));

    expect(host.textContent).toContain('New master product');
    expect(host.textContent).toContain('Coca-Cola Classic 375ml');
    buttonWithText(host, 'Accept and create');
  });

  it('matches the key to another existing master product the reviewer picks', async () => {
    const { host, fixture, component, decideMatch } = await render(product());

    component.chosenMasterProductId = other.id;
    fixture.detectChanges();
    buttonWithText(host, 'Match to this').click();

    expect(decideMatch).toHaveBeenCalledWith(
      expect.objectContaining({
        decision: ProductMatchReviewDecision.ChooseMasterProduct,
        masterProductId: other.id
      })
    );
  });

  it('submits nothing when no master product has been picked', async () => {
    const { host, component, decideMatch } = await render(product());

    buttonWithText(host, 'Match to this').click();

    expect(decideMatch).not.toHaveBeenCalled();
    expect(component.error).toContain('Choose a master product');
  });

  /** The issue own wording: the create form is prefilled from the matcher's suggested description. */
  it('prefills the create form from the suggested description, and submits what the reviewer kept', async () => {
    const described = suggestion({
      masterProduct: null,
      suggestedBrand: 'Coca-Cola',
      suggestedName: 'Classic',
      suggestedSizeValue: 375,
      suggestedSizeUnit: MasterProductSizeUnit.Millilitre
    });
    const { host, fixture, component, decideMatch } = await render(product({ suggestions: [described] }));

    buttonWithText(host, 'Create a master product').click();
    fixture.detectChanges();

    expect(component.newBrand).toBe('Coca-Cola');
    expect(component.newName).toBe('Classic');
    expect(component.newSizeValue).toBe(375);
    expect(component.newSizeUnit).toBe(MasterProductSizeUnit.Millilitre);

    component.newName = 'Classic No Sugar';
    buttonWithText(host, 'Create and match').click();

    expect(decideMatch).toHaveBeenCalledWith(
      expect.objectContaining({
        decision: ProductMatchReviewDecision.CreateMasterProduct,
        newBrand: 'Coca-Cola',
        newName: 'Classic No Sugar',
        newSizeValue: 375,
        newSizeUnit: MasterProductSizeUnit.Millilitre
      })
    );
  });

  it('submits no half-described master product', async () => {
    const { host, fixture, component, decideMatch } = await render(product({ suggestions: [] }));

    buttonWithText(host, 'Create a master product').click();
    fixture.detectChanges();
    buttonWithText(host, 'Create and match').click();

    expect(decideMatch).not.toHaveBeenCalled();
    expect(component.error).toContain('needs a brand, a name and a size');
  });

  it('records that nothing in the catalogue is this product', async () => {
    const { host, decideMatch } = await render(product());

    buttonWithText(host, 'Not found').click();

    expect(decideMatch).toHaveBeenCalledWith(
      expect.objectContaining({ decision: ProductMatchReviewDecision.MarkNotFound })
    );
  });

  /**
   * Every rule here is the server's, so a refusal shows the server's own sentence rather than a
   * second copy of the rule - and a stale one asks the page to re-read.
   */
  it('shows the server own refusal and asks the page to re-read a stale one', async () => {
    const decideMatch = jest.fn(() =>
      throwError(() => ({ status: 409, error: { message: 'That match suggestion is not waiting for a decision.' } }))
    );
    const { host, component } = await render(product(), decideMatch);
    const stale = jest.fn();
    component.staleState.subscribe(stale);

    buttonWithText(host, 'Accept match').click();

    expect(component.error).toBe('That match suggestion is not waiting for a decision.');
    expect(stale).toHaveBeenCalled();
  });

  it('starts from a clean form when another product is reviewed', async () => {
    const { fixture, component } = await render(product());

    component.newBrand = 'Typed';
    component.chosenMasterProductId = other.id;
    fixture.componentRef.setInput('product', product({ waitingKey: 'another-key' }));
    fixture.detectChanges();

    expect(component.newBrand).toBe('');
    expect(component.chosenMasterProductId).toBeNull();
  });
});
