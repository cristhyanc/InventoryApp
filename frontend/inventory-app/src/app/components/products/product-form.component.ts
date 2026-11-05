import { Component, HostListener, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subscription } from 'rxjs';
import { tap } from 'rxjs/operators';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { Category, GstClassification, MasterProductSuggestion } from '../../models/models';
import { ProductGstRuleComponent } from './product-gst-rule/product-gst-rule.component';
import { ProductCreateGstRuleComponent } from './product-create-gst-rule/product-create-gst-rule.component';
import { ProductCategoryPickerComponent } from './product-category-picker/product-category-picker.component';
import { ProductPriceTrackingComponent } from './product-price-tracking/product-price-tracking.component';
import { ProductMasterProductPickerComponent } from './product-master-product-picker/product-master-product-picker.component';
import { MasterProductSelection, isSamePrefillValue, masterProductConflictOf } from './master-product-selection';
import { SupportSessionService } from '../../support-session/support-session.service';
import { apiErrorMessage } from '../shared/api-error-message';
import { IconComponent } from '../shared/icon.component';
import { PendingWorkGuarded } from '../pick-list/pick-list-leave.guard';
import { reorderGuidance } from './product-reorder-guidance';

/** The one sentence a departure asks while there is unsaved work (issue #1111). */
export const PRODUCT_UNSAVED_LEAVE_WARNING = 'You have unsaved changes to this product. Leave this page and discard them?';

/** The departure question while a save has been sent and not answered yet (issue #1111). */
export const PRODUCT_SAVE_IN_FLIGHT_LEAVE_WARNING =
  'Your changes are still being saved. If you leave now you will not see whether they were saved. Leave anyway?';

/** The departure question once a product exists but its GST rule was not stored (issue #829/#1111). */
export const PRODUCT_GST_OUTSTANDING_LEAVE_WARNING =
  'The product was created, but its GST rule was not saved. Leave this page without saving the rule?';

/**
 * What a partly or wholly failed Save changes did (issue #1111): the sections that were stored, the
 * one that failed with the reason, and the ones that were not attempted after it. The page shows it
 * as it is, so a partial save is never reported as a full one.
 */
export interface ProductSaveProblem {
  failed: string;
  reason: string;
  saved: string[];
  notAttempted: string[];
}

/** One write of Edit Product's Save changes, in the order they are made. */
interface ProductSaveStep {
  section: string;
  fallback: string;
  write: () => Observable<void>;
}

/** The editable values a product's own `PUT`/`POST` carries, as compared for unsaved changes. */
type ProductFormFields = ProductFormComponent['form'];

/**
 * New Product and Edit Product (issues #737, #892, #1111): one routed page, one layout, and one
 * primary action.
 *
 * The layout is the approved Restockroo design of issue #1111: a content area about 1,100px wide with
 * a main column (Product details, Opening inventory on New only, Reordering) and a settings column
 * (Product status, GST rule, Supplier price tracking), stacked in that order on a narrow screen, and
 * a sticky action bar with Cancel and the one primary action.
 *
 * **One primary action saves every editable section.** The product's fields, its GST rule and its
 * supplier price tracking are three API resources, so Save changes makes up to three writes in a
 * fixed order - the product, then the rule, then tracking (tracking last, because turning it on
 * copies the *stored* name and unit label as the search text) - and only the sections that changed.
 * The first failure stops the sequence; the page then says what was saved, what failed and why, and
 * what was not attempted, keeps every value on screen, and a second press retries only what is still
 * unsaved. New Product keeps issue #829's create-then-rule flow: the primary action creates the
 * product (with the supplier-tracking choice carried by the create itself), then the rule is written,
 * and a failed rule write turns the primary action into "Retry GST rule", which never creates a
 * second product.
 *
 * **Unsaved changes are real modifications only.** Each section compares what is on screen with
 * what is stored; the dirty state clears only when the write that stored it succeeded. Cancel, the
 * back link and any other in-app departure ask first ({@link confirmLeave}, the route's
 * `canDeactivate`), and a reload or closed tab raises the browser's own prompt.
 */
@Component({
  selector: 'app-product-form',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    ProductGstRuleComponent,
    ProductCreateGstRuleComponent,
    ProductCategoryPickerComponent,
    ProductPriceTrackingComponent,
    ProductMasterProductPickerComponent,
    IconComponent
  ],
  templateUrl: './product-form.component.html'
})
export class ProductFormComponent implements OnInit, OnDestroy, PendingWorkGuarded {
  productId: number | null = null;
  categories: Category[] = [];
  saving = false;
  error = '';

  /** The last Save changes that did not store everything (issue #1111), or `null`. */
  saveProblem: ProductSaveProblem | null = null;

  /**
   * Whether a support session is active (issue #554): Save is disabled while it is, because a
   * session grants `InventoryOperate` - the capability this page's write would otherwise be gated
   * on - and the backend refuses the write itself only by its read-only method rule, never by
   * withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * The fields this business's sales provider manages on the loaded product (issue #737):
   * `["name", "categoryId"]` for a product the provider named, empty for a new product or one this
   * business maintains itself. See `Inventory.Domain.Providers.ProviderManagedCatalogueFields`.
   */
  providerManagedFields: string[] = [];

  /** The stored name and active state of the loaded product, for the header (issue #1111). */
  storedName = '';
  storedIsActive = true;

  /**
   * The id of the product this page created, while the rule chosen for it is still being stored
   * (issue #829), or `null` while no product has been created on this page.
   *
   * Creating a product with a GST rule is two writes - `POST api/products`, then
   * `PUT api/products/{id}/gst-rule` on the id it answered - because the rule is its own resource
   * and the product payload is a pinned API contract (docs/architecture.md § Product and supplier
   * GST rules). This page makes the first one and hands the id to
   * `app-product-create-gst-rule` (its `storeFor`), which owns the second one, its failure and its
   * retry. The id
   * stays here because this page created the product: once it is set, the page offers no create
   * at all - its primary action retries the rule alone - which is what makes a duplicate product
   * impossible.
   */
  createdProductId: number | null = null;

  /** The GST rule chosen on New Product (issue #1111), so choosing one counts as a change. */
  newProductGstRule: GstClassification = GstClassification.Unknown;

  /**
   * The master product the Name was filled in from on New Product (issue #1052), or `null` while the
   * operator is naming the product themselves - which includes a selection they have since edited
   * away from.
   *
   * It carries the two values that selection filled in, because those are the whole evidence of the
   * link: `POST api/products` believes the id only while the submitted Name and unit label are still
   * the ones the shared catalogue produced (issue #1051). The page therefore sends a selection only
   * while it still stands, rather than sending one into a refusal.
   */
  masterProductSelection: MasterProductSelection | null = null;

  /**
   * Whether this product's supplier prices are to be tracked - the opt-out beside the selection
   * note, on by default because an operator who picked a product from the shared catalogue asked for
   * its prices. It is sent only beside a selection: `false` is what tells the server to ignore the
   * selection and create the ordinary unlinked, untracked product.
   */
  trackSupplierPrices = true;

  /**
   * The server's own sentence for a create it refused because the selection no longer stands
   * (issue #1051's `409`), or `null`. Nothing was created, so the page stays on the form and offers
   * the two ways on the refusal names: choose the product again, or create it with no link.
   */
  masterProductConflict: string | null = null;

  @ViewChild(ProductGstRuleComponent) private gstRulePanel?: ProductGstRuleComponent;
  @ViewChild(ProductPriceTrackingComponent) private priceTrackingPanel?: ProductPriceTrackingComponent;
  @ViewChild(ProductCreateGstRuleComponent) private createGstRulePanel?: ProductCreateGstRuleComponent;

  private readonly sessionActiveSubscription: Subscription;

  /** Set once everything is stored, so the navigation back to the list asks nothing. */
  private leaving = false;

  form = {
    name: '',
    sku: '',
    description: '',
    quantityInStock: 0,
    initialUnitCost: null as number | null,
    lowStockThreshold: 5,
    restockTo: 5,
    // Empty on New Product: the label is required (issue #895), so the operator types the one the
    // product is counted in rather than saving a "unit" nobody chose.
    unit: '',
    categoryId: '' as number | '',
    isActive: true
  };

  /**
   * The product's fields as last stored - the defaults on New Product, the loaded product on Edit,
   * then what each successful write sent - so {@link productFieldsDirty} is a real comparison.
   */
  private storedFields: string = this.serialise(this.form);

  constructor(
    private readonly productService: ProductService,
    private readonly categoryService: CategoryService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  ngOnInit(): void {
    const idParam = this.route.snapshot.paramMap.get('id');
    if (idParam) {
      // The category list feeds the edit page's dropdown only: on New Product the searchable picker
      // reads the options itself (issue #830), so the page makes no second read of the same list.
      // No supplier list is loaded either (issue #787, A1 of #786), and no supplier price comparison
      // (issue #1111 removed it from this page): nothing here needs either.
      this.categoryService.getAll().subscribe((c) => (this.categories = c));

      this.productId = Number(idParam);
      this.productService.get(this.productId).subscribe((p) => {
        this.providerManagedFields = p.providerManagedFields ?? [];
        this.storedName = p.name;
        this.storedIsActive = p.isActive;
        this.form = {
          name: p.name,
          sku: p.sku ?? '',
          description: p.description ?? '',
          quantityInStock: p.quantityInStock,
          initialUnitCost: null,
          lowStockThreshold: p.lowStockThreshold,
          restockTo: p.restockTo,
          // A stored label that is missing or blank opens the field **empty** (issue #895), not
          // prefilled with "unit": the label is required, so the operator chooses one for a legacy
          // product rather than having one substituted - and saved - without being asked.
          unit: p.unit ?? '',
          categoryId: p.categoryId ?? '',
          isActive: p.isActive
        };
        this.storedFields = this.serialise(this.form);
      });
    }
  }

  /**
   * The selected category id for the New Product picker, which reports `null` rather than the
   * form's empty-string "no category" (issue #830). The submitted payload is unchanged.
   */
  get selectedCategoryId(): number | null {
    return this.form.categoryId === '' ? null : this.form.categoryId;
  }

  /**
   * The category the picker reports - always an existing category's id, or `null` for no category.
   * It is the only thing that control changes on this form: every other field, and the GST rule
   * chosen beside it (issue #829), is left exactly as it is, whether a category was selected or
   * created.
   */
  onCategorySelected(categoryId: number | null): void {
    this.form.categoryId = categoryId ?? '';
  }

  /**
   * The Name as the suggesting field reports it (issue #1052): free text, exactly as typed. Typing
   * away from the selected Name drops the selection, because the product being created is then no
   * longer the one that was chosen from the shared catalogue.
   */
  onProductNameInput(name: string): void {
    this.form.name = name;
    this.dropEditedMasterProductSelection();
  }

  /** The Unit Label as its field reports it, under the same rule: editing it away drops the link. */
  onUnitLabelInput(unit: string): void {
    this.form.unit = unit;
    this.dropEditedMasterProductSelection();
  }

  /**
   * A master product was taken from the suggestions (issue #1052). It fills in the Name and the Unit
   * Label from the server's own prefill - never a string assembled here - and remembers the
   * selection with both values, so an edit to either is visible as the edit it is.
   *
   * It is always a *fresh* selection: the opt-out starts on again, and a refusal shown for the
   * previous attempt no longer applies to the product now being created. Nothing else on the form is
   * touched - not the SKU, the category, the opening inventory, the reorder settings or the GST rule
   * chosen beside it (issue #829).
   */
  onMasterProductSelected(suggestion: MasterProductSuggestion): void {
    this.form.name = suggestion.productName;
    this.form.unit = suggestion.unitLabel;
    this.masterProductSelection = {
      masterProductId: suggestion.id,
      productName: suggestion.productName,
      unitLabel: suggestion.unitLabel
    };
    this.trackSupplierPrices = true;
    this.masterProductConflict = null;
  }

  /**
   * "Choose again" on a refused selection: the selection is dropped and the refusal cleared, so the
   * Name field is back to suggesting and taking one refills both values afresh. The typed Name and
   * unit label stay on screen - nothing was created, and the operator's work is theirs.
   */
  chooseMasterProductAgain(): void {
    this.masterProductSelection = null;
    this.masterProductConflict = null;
  }

  /**
   * "Create without link" on a refused selection: the same product, submitted with no selection at
   * all, which is the create every client made before issue #1051. The link is what was refused, not
   * the product.
   */
  createWithoutMasterProduct(): void {
    this.masterProductSelection = null;
    this.masterProductConflict = null;
    this.save();
  }

  /**
   * Drops a selection whose Name or unit label has been edited away from it. The comparison is the
   * server's own normalisation (`isSamePrefillValue`), so the three differences it does not call an
   * edit - the ends, runs of whitespace, case - keep the selection, and a selection that is dropped
   * is gone: typing the prefill back by hand is not choosing the product again.
   */
  private dropEditedMasterProductSelection(): void {
    const selection = this.masterProductSelection;
    if (!selection) {
      return;
    }

    if (
      !isSamePrefillValue(this.form.name, selection.productName) ||
      !isSamePrefillValue(this.form.unit, selection.unitLabel)
    ) {
      this.masterProductSelection = null;
    }
  }

  /** Whether the provider names this product, so its name may not be edited here (issue #737). */
  get nameLocked(): boolean {
    return this.providerManagedFields.includes('name');
  }

  /** Whether the provider names this product's category, so it may not be changed here (issue #737). */
  get categoryLocked(): boolean {
    return this.providerManagedFields.includes('categoryId');
  }

  /** Whether any field of this product is managed by Nayax, which earns the one header badge. */
  get providerManaged(): boolean {
    return this.nameLocked || this.categoryLocked;
  }

  /** The note explaining which fields are locked, or `null` while nothing here is provider-managed. */
  get providerManagedNote(): string | null {
    if (this.nameLocked && this.categoryLocked) {
      return 'Name and category are synced from Nayax.';
    }
    if (this.nameLocked) {
      return 'Name is synced from Nayax.';
    }
    if (this.categoryLocked) {
      return 'Category is synced from Nayax.';
    }
    return null;
  }

  /** The locked category's name, shown read-only (issue #1111), or "None". */
  get categoryName(): string {
    if (this.form.categoryId === '') {
      return 'None';
    }
    return this.categories.find((c) => c.id === this.form.categoryId)?.name ?? '';
  }

  /**
   * Whether the Unit Label field is empty, so the form shows its required message and Save is
   * unavailable (issue #895). Whitespace is no label: the API refuses it too, with this same
   * sentence (`Inventory.Domain.Products.ProductCataloguePolicy.UnitRequiredMessage`).
   *
   * It is `true` on an existing product whose stored label was missing, which is exactly the case
   * the operator is being asked to resolve.
   */
  get unitMissing(): boolean {
    return !this.form.unit.trim();
  }

  /** Whether Restock To is below the Low Stock Threshold, the one cross-field rule of the form. */
  get restockBelowThreshold(): boolean {
    return this.form.restockTo < this.form.lowStockThreshold;
  }

  /** The Reordering guidance for the values on screen, worded from the authoritative rule. */
  get reorderGuidance(): string | null {
    return reorderGuidance(this.form.lowStockThreshold, this.form.restockTo);
  }

  /** Whether the product's own fields differ from what is stored. */
  get productFieldsDirty(): boolean {
    return this.serialise(this.form) !== this.storedFields;
  }

  /**
   * Whether leaving now would lose something (issue #1111): a changed field, a changed GST rule or
   * tracking switch on Edit, a chosen rule or catalogue selection on New, or a created product whose
   * rule is still unsaved.
   */
  get hasUnsavedChanges(): boolean {
    if (this.productId) {
      return this.productFieldsDirty || !!this.gstRulePanel?.dirty || !!this.priceTrackingPanel?.dirty;
    }
    if (this.createdProductId !== null) {
      return true;
    }
    return (
      this.productFieldsDirty ||
      this.newProductGstRule !== GstClassification.Unknown ||
      this.masterProductSelection !== null
    );
  }

  /** The label of the page's one primary action, which names the write it would actually make. */
  get submitLabel(): string {
    if (this.saving) {
      return 'Saving...';
    }
    if (this.createdProductId !== null) {
      return 'Retry GST rule';
    }
    return this.productId ? 'Save changes' : 'Create product';
  }

  /** Whether the page's one primary action is unavailable. */
  get submitDisabled(): boolean {
    if (this.saving || this.sessionActive) {
      return true;
    }
    // Once the product exists the only write left is its rule, whatever the fields now say.
    if (this.createdProductId !== null) {
      return false;
    }

    return this.unitMissing || this.restockBelowThreshold;
  }

  /** The primary action's handler: create or save, or - once a product exists - retry its rule. */
  submit(): void {
    if (this.createdProductId !== null) {
      this.retryGstRule();
      return;
    }
    this.save();
  }

  save(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive || this.saving) {
      return;
    }
    // This page has already created its product (issue #829), so it has no create left to make:
    // what is outstanding is the GST rule, which `app-product-create-gst-rule` owns and retries.
    // Creating the product a second time is exactly the duplicate this guard prevents.
    if (this.createdProductId !== null) {
      return;
    }
    const problem = this.validationProblem();
    if (problem) {
      this.error = problem;
      return;
    }
    this.error = '';
    this.saveProblem = null;
    // A refusal belongs to the submission that was refused: this one is being made now.
    this.masterProductConflict = null;

    if (this.productId) {
      this.saveEdit(this.productId);
    } else {
      this.saveNew();
    }
  }

  /**
   * The created product's GST rule is stored, or needed no write because "no rule" is what a new
   * product already carries (issue #829). Only now is the create finished.
   */
  onGstRulePersisted(): void {
    this.saving = false;
    this.createdProductId = null;
    this.leave();
  }

  /**
   * The product was created and its GST rule was not stored (issue #829). The page stays open and
   * does not navigate - a create whose rule was not stored is not a finished create - and the
   * component that owns that write says so beside the rule; the primary action now retries it.
   */
  onGstRuleFailed(): void {
    this.saving = false;
  }

  /** The departure warning that applies right now, or `null` when leaving loses nothing. */
  leaveWarning(): string | null {
    if (this.leaving) {
      return null;
    }
    if (this.saving) {
      return PRODUCT_SAVE_IN_FLIGHT_LEAVE_WARNING;
    }
    if (this.createdProductId !== null) {
      return PRODUCT_GST_OUTSTANDING_LEAVE_WARNING;
    }
    return this.hasUnsavedChanges ? PRODUCT_UNSAVED_LEAVE_WARNING : null;
  }

  /** The route's `canDeactivate` answer: leave freely, or only once the operator confirms. */
  confirmLeave(): boolean {
    const warning = this.leaveWarning();
    return warning === null || window.confirm(warning);
  }

  /** Asks the browser to confirm a reload or a closed tab while there is work to lose. */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.leaveWarning() !== null) {
      event.preventDefault();
    }
  }

  private leave(): void {
    this.leaving = true;
    this.router.navigate(['/products']);
  }

  /** The first problem with the values on screen, worded as the API words it, or `null`. */
  private validationProblem(): string | null {
    if (!this.form.name.trim()) {
      return 'Product name is required.';
    }
    // Checked here as well as shown beside the field and disabled on the button, so a blank label is
    // never sent (issue #895). The API refuses it too - outright on a create, and on an update
    // whenever the stored label is nonblank - and words it the same way.
    if (this.unitMissing) {
      return 'Unit label is required.';
    }
    if (!this.productId && this.form.initialUnitCost !== null && this.form.initialUnitCost < 0) {
      return 'Initial unit cost cannot be negative.';
    }
    if (this.form.lowStockThreshold === null || this.form.restockTo === null) {
      return 'Low Stock Threshold and Restock To are required.';
    }
    if (this.form.lowStockThreshold < 0) {
      return 'Low Stock Threshold cannot be negative.';
    }
    if (this.form.restockTo < 0) {
      return 'Restock To cannot be negative.';
    }
    if (this.restockBelowThreshold) {
      return 'Restock To must be greater than or equal to the Low Stock Threshold.';
    }
    return null;
  }

  /** The fields `PUT api/products/{id}` and `POST api/products` share, from the form on screen. */
  private productPayload() {
    return {
      name: this.form.name.trim(),
      sku: this.form.sku || null,
      description: this.form.description || null,
      lowStockThreshold: this.form.lowStockThreshold,
      restockTo: this.form.restockTo,
      unit: this.form.unit.trim(),
      categoryId: this.form.categoryId === '' ? null : this.form.categoryId,
      isActive: this.form.isActive
    };
  }

  /**
   * Edit Product's Save changes (issue #1111): the product, then the GST rule, then supplier price
   * tracking, each only when it changed, each only after the one before it succeeded.
   */
  private saveEdit(id: number): void {
    const steps: ProductSaveStep[] = [];
    if (this.productFieldsDirty) {
      const sent = this.serialise(this.form);
      const payload = this.productPayload();
      steps.push({
        section: 'Product details',
        fallback: 'Failed to update product.',
        write: () =>
          this.productService.update(id, payload).pipe(
            tap(() => {
              this.storedFields = sent;
              this.storedName = payload.name;
              this.storedIsActive = payload.isActive;
            })
          )
      });
    }
    const gstRule = this.gstRulePanel;
    if (gstRule?.dirty) {
      steps.push({ section: 'GST rule', fallback: 'Failed to save the GST rule.', write: () => gstRule.persist() });
    }
    const tracking = this.priceTrackingPanel;
    if (tracking?.dirty) {
      steps.push({
        section: 'Supplier price tracking',
        fallback: tracking.requested
          ? 'Failed to turn supplier price tracking on.'
          : 'Failed to turn supplier price tracking off.',
        write: () => tracking.persist()
      });
    }

    // Nothing changed: there is nothing to write, and the edit is finished as it stands.
    if (steps.length === 0) {
      this.leave();
      return;
    }

    this.saving = true;
    this.runSteps(steps, 0);
  }

  private runSteps(steps: ProductSaveStep[], index: number): void {
    if (index >= steps.length) {
      this.saving = false;
      this.leave();
      return;
    }

    const step = steps[index];
    step.write().subscribe({
      next: () => undefined,
      complete: () => this.runSteps(steps, index + 1),
      error: (error: unknown) => {
        this.saving = false;
        this.saveProblem = {
          failed: step.section,
          reason: apiErrorMessage(error, step.fallback),
          saved: steps.slice(0, index).map((s) => s.section),
          notAttempted: steps.slice(index + 1).map((s) => s.section)
        };
      }
    });
  }

  /** New Product's Create product: the create, then (by the composed component) its GST rule. */
  private saveNew(): void {
    this.saving = true;
    // The selection is sent only while it stands (issue #1052): a create carrying neither field
    // is byte for byte the create this page has always made, which is what keeps a product nobody
    // picked from the catalogue completely unaffected by this feature.
    const selection = this.masterProductSelection;
    this.productService.create({
      ...this.productPayload(),
      quantityInStock: this.form.quantityInStock,
      initialUnitCost: this.form.initialUnitCost,
      ...(selection
        ? {
            selectedMasterProductId: selection.masterProductId,
            trackSupplierPrices: this.trackSupplierPrices
          }
        : {})
    }).subscribe({
      next: (created) => {
        // The product exists from here on. Its GST rule is `app-product-create-gst-rule`'s to
        // store (issue #829), so the page stays in its saving state and finishes on that
        // component's report rather than treating the create alone as the whole outcome.
        this.createdProductId = created.id;
        if (!this.createGstRulePanel?.storeFor(created.id)) {
          this.saving = false;
        }
      },
      error: (error: unknown) => {
        // A refused selection (issue #1051) created nothing at all, and it is the one failure with
        // two actions beside it rather than a message to read - so it is shown as itself, in the
        // server's own words, instead of as the page's generic create failure.
        const conflict = masterProductConflictOf(error);
        if (conflict) {
          this.masterProductConflict = conflict.message;
        } else {
          this.error = apiErrorMessage(error, 'Failed to create product.');
        }
        this.saving = false;
      }
    });
  }

  /** The primary action once the product exists and its rule did not save: the rule alone. */
  private retryGstRule(): void {
    if (this.saving || this.sessionActive) {
      return;
    }
    this.saving = true;
    if (!this.createGstRulePanel?.save()) {
      this.saving = false;
    }
  }

  private serialise(fields: ProductFormFields): string {
    return JSON.stringify([
      fields.name,
      fields.sku,
      fields.description,
      fields.quantityInStock,
      fields.initialUnitCost,
      fields.lowStockThreshold,
      fields.restockTo,
      fields.unit,
      fields.categoryId,
      fields.isActive
    ]);
  }
}
