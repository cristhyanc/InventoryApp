import { CommonModule, DatePipe } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  MASTER_PRODUCT_SIZE_UNIT_LABELS,
  MasterProductSizeUnit,
  ProductMatchReviewDecision,
  ReviewMasterProduct,
  ReviewMatchSuggestion,
  ReviewWaitingProduct,
  SuperAdminPriceCatalogueReviewService
} from '../../../services/super-admin-price-catalogue-review.service';
import { extractReviewErrorMessage, isStaleReviewState } from './review-error';

/**
 * One product awaiting a match, and the five decisions a Super Admin may make about it (issue #928):
 * accept the matcher's suggestion, pick another existing master product, create one, refuse a
 * suggestion, or record that nothing in the catalogue is this product.
 *
 * It is a dedicated feature component rather than markup in the page, per
 * docs/architecture.md § Page composition boundary: it owns its own form values, its submission
 * state and its error, and the routed page composes it through `[product]`/`[masterProducts]` and
 * reacts only to `(decided)`.
 *
 * **It decides nothing.** Which mappings a match reaches, which suggestions may be accepted and what
 * an undo puts back are the server's rules; this component submits one decision and shows the
 * server's own refusal sentence when it refuses. The only thing it withholds locally is a submission
 * with an incomplete form - a create with no brand, for instance - because a request that cannot
 * carry a decision is not one.
 *
 * **It never learns who is waiting.** The contract carries a count of businesses and no identity, so
 * there is nothing here to show even by accident.
 */
@Component({
  selector: 'app-waiting-product-match',
  standalone: true,
  imports: [CommonModule, DatePipe, FormsModule],
  templateUrl: './waiting-product-match.component.html'
})
export class WaitingProductMatchComponent implements OnChanges {
  @Input({ required: true }) product!: ReviewWaitingProduct;

  /** The shared catalogue, as the queue published it: what "pick another master product" offers. */
  @Input() masterProducts: readonly ReviewMasterProduct[] = [];

  /** Raised after a decision the server accepted, so the page can re-read the queue. */
  @Output() readonly decided = new EventEmitter<string>();

  /**
   * Raised when a refusal says this page is looking at state that has moved - a suggestion somebody
   * else decided, or a key nothing is waiting under any more - so the page re-reads the queue while
   * the refusal's own sentence stays on screen.
   */
  @Output() readonly staleState = new EventEmitter<void>();

  submitting = false;
  error: string | null = null;

  /** Which existing master product "Match to this" would use, or `null` while none is chosen. */
  chosenMasterProductId: number | null = null;

  /** Whether the "create a master product" form is open. */
  creating = false;

  newBrand = '';
  newName = '';
  newSizeValue: number | null = null;
  newSizeUnit: MasterProductSizeUnit = MasterProductSizeUnit.Millilitre;

  readonly sizeUnits = Object.entries(MASTER_PRODUCT_SIZE_UNIT_LABELS).map(([value, label]) => ({
    value: Number(value) as MasterProductSizeUnit,
    label
  }));

  constructor(private readonly reviewService: SuperAdminPriceCatalogueReviewService) {}

  /**
   * A new product being reviewed starts from a clean form. The page re-reads the queue after every
   * decision, so without this a half-typed description could be carried onto another product.
   */
  ngOnChanges(): void {
    this.error = null;
    this.submitting = false;
    this.chosenMasterProductId = null;
    this.creating = false;
    this.newBrand = '';
    this.newName = '';
    this.newSizeValue = null;
    this.newSizeUnit = MasterProductSizeUnit.Millilitre;
  }

  /** What a suggestion proposes, in words: an existing master product, or a new one to create. */
  suggestionDescribes(suggestion: ReviewMatchSuggestion): string {
    if (suggestion.masterProduct) {
      return this.describe(suggestion.masterProduct);
    }

    const size =
      suggestion.suggestedSizeValue === null || suggestion.suggestedSizeUnit === null
        ? ''
        : ` ${suggestion.suggestedSizeValue}${MASTER_PRODUCT_SIZE_UNIT_LABELS[suggestion.suggestedSizeUnit]}`;

    return `${suggestion.suggestedBrand ?? ''} ${suggestion.suggestedName ?? ''}${size}`.trim();
  }

  /** Whether accepting this suggestion would create a master product, which is worth saying first. */
  createsAMasterProduct(suggestion: ReviewMatchSuggestion): boolean {
    return suggestion.masterProduct === null;
  }

  describe(master: ReviewMasterProduct): string {
    return `${master.brand} ${master.name} ${master.sizeValue}${MASTER_PRODUCT_SIZE_UNIT_LABELS[master.sizeUnit]}`;
  }

  /** The matcher's confidence as a percentage, which is how a proportion reads to a person. */
  confidencePercent(confidence: number): number {
    return Math.round(confidence * 100);
  }

  acceptSuggestion(suggestion: ReviewMatchSuggestion): void {
    this.submit({
      waitingKey: this.product.waitingKey,
      decision: ProductMatchReviewDecision.AcceptSuggestion,
      suggestionId: suggestion.id,
      masterProductId: null,
      newBrand: null,
      newName: null,
      newSizeValue: null,
      newSizeUnit: null
    });
  }

  rejectSuggestion(suggestion: ReviewMatchSuggestion): void {
    this.submit({
      waitingKey: this.product.waitingKey,
      decision: ProductMatchReviewDecision.RejectSuggestion,
      suggestionId: suggestion.id,
      masterProductId: null,
      newBrand: null,
      newName: null,
      newSizeValue: null,
      newSizeUnit: null
    });
  }

  chooseMasterProduct(): void {
    if (this.chosenMasterProductId === null) {
      this.error = 'Choose a master product to match this product to.';
      return;
    }

    this.submit({
      waitingKey: this.product.waitingKey,
      decision: ProductMatchReviewDecision.ChooseMasterProduct,
      suggestionId: null,
      masterProductId: this.chosenMasterProductId,
      newBrand: null,
      newName: null,
      newSizeValue: null,
      newSizeUnit: null
    });
  }

  /**
   * Opens the create form, prefilled from the matcher's own suggested description when there is one -
   * which is the issue's own wording, "prefilled from Claude's suggestion". The values stay the
   * reviewer's: nothing is created until they submit this form.
   */
  startCreating(): void {
    this.creating = true;
    this.error = null;

    const suggested = this.product.suggestions.find((suggestion) => suggestion.masterProduct === null);

    this.newBrand = suggested?.suggestedBrand ?? '';
    this.newName = suggested?.suggestedName ?? '';
    this.newSizeValue = suggested?.suggestedSizeValue ?? null;
    this.newSizeUnit = suggested?.suggestedSizeUnit ?? MasterProductSizeUnit.Millilitre;
  }

  cancelCreating(): void {
    this.creating = false;
    this.error = null;
  }

  createMasterProduct(): void {
    if (this.newBrand.trim() === '' || this.newName.trim() === '' || this.newSizeValue === null) {
      this.error = 'A new master product needs a brand, a name and a size.';
      return;
    }

    this.submit({
      waitingKey: this.product.waitingKey,
      decision: ProductMatchReviewDecision.CreateMasterProduct,
      suggestionId: null,
      masterProductId: null,
      newBrand: this.newBrand.trim(),
      newName: this.newName.trim(),
      newSizeValue: this.newSizeValue,
      newSizeUnit: this.newSizeUnit
    });
  }

  markNotFound(): void {
    this.submit({
      waitingKey: this.product.waitingKey,
      decision: ProductMatchReviewDecision.MarkNotFound,
      suggestionId: null,
      masterProductId: null,
      newBrand: null,
      newName: null,
      newSizeValue: null,
      newSizeUnit: null
    });
  }

  private submit(request: Parameters<SuperAdminPriceCatalogueReviewService['decideMatch']>[0]): void {
    this.submitting = true;
    this.error = null;

    this.reviewService.decideMatch(request).subscribe({
      next: (result) => {
        this.submitting = false;
        this.decided.emit(this.describeOutcome(result.masterProductCreated, request.decision));
      },
      error: (err: unknown) => {
        this.submitting = false;
        this.error = extractReviewErrorMessage(err, 'Unable to record this decision.');

        if (isStaleReviewState(err)) {
          this.staleState.emit();
        }
      }
    });
  }

  private describeOutcome(created: boolean, decision: ProductMatchReviewDecision): string {
    if (created) {
      return `${this.product.searchName} is matched to a new master product.`;
    }

    switch (decision) {
      case ProductMatchReviewDecision.RejectSuggestion:
        return 'That suggestion was refused.';
      case ProductMatchReviewDecision.MarkNotFound:
        return `${this.product.searchName} is recorded as matching nothing in the catalogue.`;
      default:
        return `${this.product.searchName} is matched.`;
    }
  }
}
