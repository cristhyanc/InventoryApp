import { Component, EventEmitter, OnDestroy, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subscription } from 'rxjs';
import { Category, Product } from '../../../../models/models';
import {
  ApplyCatalogueReviewRequest,
  CatalogueReview,
  CatalogueReviewAction,
  CatalogueReviewApplied,
  CatalogueReviewCategory,
  CatalogueReviewDecisionRequest,
  CatalogueReviewField,
  CatalogueReviewProduct,
  CatalogueReviewService,
  NegotiableCatalogueField,
  PROVIDER_MANAGED_CATEGORY_FIELD,
  PROVIDER_MANAGED_NAME_FIELD,
  ProviderFieldDecision,
  UNIT_PRICE_FIELD
} from '../../../../services/catalogue-review.service';
import { CategoryService } from '../../../../services/category.service';
import { ProductService } from '../../../../services/product.service';
import { ToastService } from '../../../../services/toast.service';
import { SupportSessionService } from '../../../../support-session/support-session.service';

/** Which half of the review the operator is deciding. Categories come first, and deliberately so. */
export type CatalogueReviewStep = 'categories' | 'products';

/** What the operator has answered about one reviewed entry, before it becomes a request decision. */
interface EntryDecision {
  action: CatalogueReviewAction;
  localRowId: number | null;
  /** The link picker's search text. Local to the screen; never sent. */
  search: string;
  /** The keep-or-take answer for the price, or `null` while the operator has not answered. */
  price: ProviderFieldDecision | null;
}

/**
 * The provider catalogue review screen (issue #739, parent #329 decision C2): what
 * **Admin → Imports → Import products** now opens, in place of the direct catalogue import.
 *
 * A dedicated feature component, composed by the routed Imports page rather than grown inside it,
 * because it is a workflow with its own two-step state, its own actions and its own loading and
 * error lifecycle (AGENTS.md § Frontend rules; docs/architecture.md § Page composition boundary).
 *
 * <p><strong>It decides nothing about the catalogue.</strong> Every provider name, price, group and
 * link comes from `POST api/catalogue-review`, and the apply carries the operator's decisions and
 * nothing else - no provider value is ever sent back from the browser, which is what keeps the
 * server's stored review the only source of what gets written (issue #738).</p>
 *
 * <p><strong>Categories are decided first, because a product's category depends on them.</strong>
 * The provider owns a linked product's category, and the apply resolves it to the local category
 * carrying the provider's group id - including one this same apply links or creates. So step one
 * settles the groups, and step two shows each product the category it will actually end up in,
 * which is why the steps are ordered rather than merely tabbed.</p>
 *
 * <p><strong>Only the price is negotiated.</strong> A linked row's name, and a product's category,
 * belong to the provider: the screen lists those changes as applied automatically and offers no
 * choice about them. The unit/volume is not offered either - the Lynx product contract publishes no
 * unit or volume label, so the API proposes none and `Product.Unit` stays the business's own
 * (issue #739's out-of-scope note, and the owner's decision of 20 October 2026).</p>
 */
@Component({
  selector: 'app-catalogue-review',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './catalogue-review.component.html'
})
export class CatalogueReviewComponent implements OnInit, OnDestroy {
  /** Asks the hosting page to take the review off screen. Nothing is applied by closing it. */
  @Output() readonly closed = new EventEmitter<void>();

  /** The three sentences the screen states about what linking does, worded once. */
  static readonly ProductLinkNotice =
    "This product's name and category will be replaced by the provider's and become read-only " +
    'here; only its price is yours to decide.';

  static readonly CategoryLinkNotice =
    "This category's name will be replaced by the provider's name and become read-only here.";

  static readonly CreateNotice =
    "A new local row is created with the provider's name, category and price.";

  readonly actions = CatalogueReviewAction;
  readonly decisions = ProviderFieldDecision;
  readonly productLinkNotice = CatalogueReviewComponent.ProductLinkNotice;
  readonly categoryLinkNotice = CatalogueReviewComponent.CategoryLinkNotice;
  readonly createNotice = CatalogueReviewComponent.CreateNotice;

  step: CatalogueReviewStep = 'categories';
  review: CatalogueReview | null = null;
  applied: CatalogueReviewApplied | null = null;
  starting = false;
  applying = false;

  /** The local rows a link may name. Fetched beside the review, because the review reports ids only. */
  localProducts: Product[] = [];
  localCategories: Category[] = [];

  /**
   * Whether a support session is active (issue #661): both calls this screen makes are `POST`s the
   * read-only session rule refuses, and a session grants `Imports.Run`, so the screen disables them
   * itself rather than letting the operator discover the refusal.
   */
  sessionActive = false;

  private readonly answers = new Map<number, EntryDecision>();
  private readonly subscriptions = new Subscription();

  constructor(
    private readonly reviewService: CatalogueReviewService,
    private readonly products: ProductService,
    private readonly categories: CategoryService,
    private readonly toast: ToastService,
    supportSession: SupportSessionService
  ) {
    this.subscriptions.add(supportSession.active$.subscribe((active) => (this.sessionActive = active)));
  }

  ngOnInit(): void {
    this.startReview();
    this.loadLocalCatalogue();
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
  }

  get loading(): boolean {
    return this.starting || this.applying;
  }

  /** The provider groups to decide about, in the order the API reported them. */
  get categoryEntries(): CatalogueReviewCategory[] {
    return this.review?.categories ?? [];
  }

  get productEntries(): CatalogueReviewProduct[] {
    return this.review?.products ?? [];
  }

  /** How many entries carry an answer, so the Apply button can say what it is about to do. */
  get decidedCount(): number {
    return this.requestDecisions().length;
  }

  answer(itemId: number): EntryDecision {
    const existing = this.answers.get(itemId);
    if (existing) return existing;
    const created: EntryDecision = { action: CatalogueReviewAction.Defer, localRowId: null, search: '', price: null };
    this.answers.set(itemId, created);
    return created;
  }

  setAction(itemId: number, action: CatalogueReviewAction): void {
    const answer = this.answer(itemId);
    answer.action = action;
    if (action !== CatalogueReviewAction.LinkExisting) {
      // Only a link names a local row, and only a link negotiates a price: a created row takes the
      // provider's price with nothing to decide, and a deferred entry is not written at all.
      answer.localRowId = null;
      answer.price = null;
    }
  }

  /** The operator-facing name of a field the API reports a change or a choice about. */
  fieldLabel(field: string): string {
    if (field === PROVIDER_MANAGED_NAME_FIELD) return 'name';
    if (field === PROVIDER_MANAGED_CATEGORY_FIELD) return 'category';
    if (field === UNIT_PRICE_FIELD) return 'price';
    return field;
  }

  setSearch(itemId: number, search: string): void {
    this.answer(itemId).search = search;
  }

  /** Records the local row a link names. The `<select>` hands back a string. */
  setLocalRow(itemId: number, localRowId: number | string | null): void {
    const parsed = typeof localRowId === 'string' ? Number.parseInt(localRowId, 10) : localRowId;
    this.answer(itemId).localRowId = parsed != null && Number.isFinite(parsed) ? parsed : null;
  }

  setPrice(itemId: number, decision: ProviderFieldDecision): void {
    this.answer(itemId).price = decision;
  }

  /**
   * The local categories a link may name: the ones this business maintains itself, filtered by the
   * row's search text. A category the provider already manages carries a provider id, so linking it
   * to a second provider group is refused by the server - it is not offered here either.
   */
  categoryCandidates(itemId: number): Category[] {
    const search = this.answer(itemId).search.trim().toLowerCase();
    return this.localCategories
      .filter((category) => !this.isProviderManaged(category.providerManagedFields))
      .filter((category) => search === '' || category.name.toLowerCase().includes(search));
  }

  /** The local products a link may name, on the same two rules. */
  productCandidates(itemId: number): Product[] {
    const search = this.answer(itemId).search.trim().toLowerCase();
    return this.localProducts
      .filter((product) => !this.isProviderManaged(product.providerManagedFields))
      .filter(
        (product) =>
          search === '' ||
          product.name.toLowerCase().includes(search) ||
          (product.sku ?? '').toLowerCase().includes(search)
      );
  }

  /**
   * The one negotiable field this screen offers: the catalogue price, and only while the API
   * proposed it. Anything else the API may ever propose - the declared but never-populated
   * unit/volume among it - is deliberately not rendered, because taking a provider unit/volume is
   * out of scope for issue #739 and the provider publishes none.
   */
  priceChoice(entry: CatalogueReviewProduct): CatalogueReviewField | null {
    return entry.choices.find((choice) => choice.field === UNIT_PRICE_FIELD) ?? null;
  }

  /**
   * The price the comparison calls "mine", for the one screen on which an operator authorises
   * replacing a real price.
   *
   * For a row the provider is already linked to, that is the review's own local value, because the
   * server compared it when it started the review. For a row that is not linked yet, the started
   * review reports no local value at all - at that moment there is no local row, since naming one is
   * what the operator is doing here - so the price of the product they named is the only truthful
   * answer. The apply negotiates against exactly that product's price, so showing "none yet" opposite
   * the provider's would tell an operator with a $3.20 product that they have no price to lose.
   *
   * Rendered with the two decimals `ProviderCatalogueFieldValue.FromPrice` writes, so both sides of
   * the comparison read alike. `null` is a row the operator has not named yet, which genuinely has
   * no price to show.
   */
  localPrice(entry: CatalogueReviewProduct, price: CatalogueReviewField): string | null {
    if (entry.isLinked) return price.localValue;

    const answer = this.answer(entry.itemId);
    if (answer.action !== CatalogueReviewAction.LinkExisting || answer.localRowId == null) return null;

    const local = this.localProducts.find((product) => product.id === answer.localRowId);
    return local ? local.unitPrice.toFixed(2) : null;
  }

  /**
   * The category this product ends up in once the decisions of step one are applied, named as the
   * provider names it (the provider owns a linked category's name). `null` means the provider's
   * group is linked to no local category and the product is left uncategorised - exactly what the
   * apply does, rather than pointing it at a category that merely shares a number.
   */
  categoryAfterImport(entry: CatalogueReviewProduct): string | null {
    const group = this.groupOf(entry);
    if (!group) return null;
    if (group.isLinked) return group.providerName;

    const answer = this.answer(group.itemId);
    if (answer.action === CatalogueReviewAction.CreateNew) return group.providerName;
    if (answer.action === CatalogueReviewAction.LinkExisting && answer.localRowId != null) return group.providerName;
    return null;
  }

  /** How that category comes about, for the operator who decided it a moment ago. */
  categorySourceAfterImport(entry: CatalogueReviewProduct): string | null {
    const group = this.groupOf(entry);
    if (!group || group.isLinked) return null;

    const answer = this.answer(group.itemId);
    if (answer.action === CatalogueReviewAction.CreateNew) return 'new category';
    if (answer.action === CatalogueReviewAction.LinkExisting && answer.localRowId != null) {
      const local = this.localCategories.find((category) => category.id === answer.localRowId);
      return local ? `renamed from ${local.name}` : null;
    }
    return null;
  }

  goToProducts(): void {
    this.step = 'products';
  }

  backToCategories(): void {
    this.step = 'categories';
  }

  close(): void {
    this.closed.emit();
  }

  /**
   * Applies exactly what the operator answered. An entry nobody answered is sent as nothing at all,
   * which is what makes a half-decided review leave the other half alone.
   *
   * A link that names no local row is refused here rather than sent: the server refuses the whole
   * apply over one such decision, so sending it would throw away every other answer on the screen.
   */
  applyDecisions(): void {
    const review = this.review;
    // Checked here as well as disabled in the template, so the apply is actually unavailable rather
    // than merely inert - the endpoint refuses it either way.
    if (!review || this.loading || this.sessionActive) return;

    const unnamed = this.unnamedLinks();
    if (unnamed.length > 0) {
      this.toast.error(
        `Choose the local ${unnamed[0].kind} to link, or change that entry to Create new or Skip. Nothing was applied.`
      );
      return;
    }

    const request: ApplyCatalogueReviewRequest = {
      reviewId: review.reviewId,
      decisions: this.requestDecisions()
    };

    this.applying = true;
    this.reviewService.apply(request).subscribe({
      next: (applied) => {
        this.applying = false;
        this.applied = applied;
        // The review describes a catalogue reading that has now been decided; a fresh one is the
        // only honest next view, and the server refuses a second apply of the same review anyway.
        this.review = null;
        this.answers.clear();
        this.toast.success(
          applied.alreadyApplied
            ? 'This review had already been applied, so nothing was changed.'
            : `Linked ${applied.linkedCount} and created ${applied.createdCount} catalogue row(s).`
        );
      },
      error: (err) => {
        this.applying = false;
        this.toast.error(this.errorMessage(err, 'The catalogue review could not be applied.'));
      }
    });
  }

  /** Starts a fresh review after an apply, without leaving the screen. */
  restart(): void {
    if (this.loading || this.sessionActive) return;
    this.applied = null;
    this.step = 'categories';
    this.startReview();
    this.loadLocalCatalogue();
  }

  private startReview(): void {
    this.starting = true;
    this.answers.clear();
    this.subscriptions.add(
      this.reviewService.start().subscribe({
        next: (review) => {
          this.starting = false;
          this.review = review;
          // Seeded up front, so reading an entry's answer from the template is a pure read: every
          // entry starts on Skip, which is the only honest default for a decision nobody took.
          for (const entry of [...review.categories, ...review.products]) this.answer(entry.itemId);
        },
        error: (err) => {
          this.starting = false;
          this.review = null;
          this.toast.error(this.errorMessage(err, 'The provider catalogue could not be read.'));
        }
      })
    );
  }

  private loadLocalCatalogue(): void {
    // Retired products are included: a product somebody deactivated is still the provider's
    // product, and linking it is better than creating a duplicate of it.
    this.subscriptions.add(
      this.products.getAll({ includeRetired: true }).subscribe({
        next: (products) => (this.localProducts = products),
        error: () => this.toast.error('Your own products could not be listed, so linking to one is unavailable.')
      })
    );
    this.subscriptions.add(
      this.categories.getAll().subscribe({
        next: (categories) => (this.localCategories = categories),
        error: () => this.toast.error('Your own categories could not be listed, so linking to one is unavailable.')
      })
    );
  }

  /** The links the operator chose without naming a row, by the kind of row they are about. */
  private unnamedLinks(): { kind: string }[] {
    const unnamed: { kind: string }[] = [];
    for (const entry of this.categoryEntries) {
      const answer = this.answers.get(entry.itemId);
      if (answer?.action === CatalogueReviewAction.LinkExisting && answer.localRowId == null)
        unnamed.push({ kind: 'category' });
    }
    for (const entry of this.productEntries) {
      const answer = this.answers.get(entry.itemId);
      if (answer?.action === CatalogueReviewAction.LinkExisting && answer.localRowId == null)
        unnamed.push({ kind: 'product' });
    }
    return unnamed;
  }

  private requestDecisions(): CatalogueReviewDecisionRequest[] {
    const decisions: CatalogueReviewDecisionRequest[] = [];
    for (const entry of this.categoryEntries) {
      const decision = this.decisionOf(entry.itemId, null);
      if (decision) decisions.push(decision);
    }
    for (const entry of this.productEntries) {
      const decision = this.decisionOf(entry.itemId, entry);
      if (decision) decisions.push(decision);
    }
    return decisions;
  }

  /**
   * One entry's decision, or `null` when the operator answered nothing about it. A price answer on
   * its own is a decision: that is how a linked product's price change is accepted or skipped
   * without the entry being linked or created again.
   */
  private decisionOf(itemId: number, product: CatalogueReviewProduct | null): CatalogueReviewDecisionRequest | null {
    const answer = this.answers.get(itemId);
    if (!answer) return null;

    const price = product && this.priceChoice(product) ? answer.price : null;
    if (answer.action === CatalogueReviewAction.Defer && price == null) return null;

    const decision: CatalogueReviewDecisionRequest = { itemId, action: answer.action };
    if (answer.action === CatalogueReviewAction.LinkExisting && answer.localRowId != null) {
      decision.localRowId = answer.localRowId;
    }
    if (price != null) {
      decision.fields = [{ field: NegotiableCatalogueField.UnitPrice, decision: price }];
    }
    return decision;
  }

  private groupOf(entry: CatalogueReviewProduct): CatalogueReviewCategory | null {
    const groupId = entry.providerGroupExternalId;
    if (!groupId) return null;
    return this.categoryEntries.find((category) => category.externalId === groupId) ?? null;
  }

  private isProviderManaged(fields: string[] | null | undefined): boolean {
    return (fields?.length ?? 0) > 0;
  }

  // Some admin actions answer a plain string body and others a ProblemDetails object; both shapes
  // are handled here, matching the fallback the other Admin workflows already use.
  private errorMessage(err: unknown, fallback: string): string {
    const body = (err as { error?: unknown } | undefined)?.error;
    if (typeof body === 'string') return body;
    const problem = body as { message?: string; title?: string } | undefined;
    return problem?.message ?? problem?.title ?? fallback;
  }
}
