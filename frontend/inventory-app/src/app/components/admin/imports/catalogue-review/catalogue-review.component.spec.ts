import { TestBed } from '@angular/core/testing';
import { ComponentFixture } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { CatalogueReviewComponent } from './catalogue-review.component';
import {
  ApplyCatalogueReviewRequest,
  CatalogueReview,
  CatalogueReviewAction,
  CatalogueReviewApplied,
  CatalogueReviewService
} from '../../../../services/catalogue-review.service';
import { CategoryService } from '../../../../services/category.service';
import { ProductService } from '../../../../services/product.service';
import { ToastService } from '../../../../services/toast.service';
import { SupportSessionService } from '../../../../support-session/support-session.service';
import { Category, Product } from '../../../../models/models';

/**
 * The catalogue review screen (issue #739, parent #329 decision C2). What these tests hold is the
 * contract between the operator's decisions and the apply request: a link only ever names a row the
 * operator chose, and a deferred entry is sent as nothing at all. Every provider name and category
 * in the UI is the API's own.
 *
 * <p>Since issue #792 (A2 of #786) the screen negotiates nothing: a product carries no selling
 * price, so no price is proposed, rendered or sent - the tests below pin that, including for a
 * stale API response that still carries a `UnitPrice` choice.</p>
 */

const UNLINKED_CATEGORY_ITEM = 101;
const LINKED_CATEGORY_ITEM = 102;
const UNLINKED_PRODUCT_ITEM = 201;
const LINKED_PRODUCT_ITEM = 202;

function review(): CatalogueReview {
  return {
    reviewId: 77,
    startedAtUtc: '2026-10-20T02:00:00Z',
    unitVolumeNote: 'The provider publishes no unit/volume label for this product, so none is proposed.',
    unlinkedCategoryCount: 1,
    unlinkedProductCount: 1,
    unresolvedSaleCount: 4,
    categories: [
      {
        itemId: UNLINKED_CATEGORY_ITEM,
        externalId: '20',
        providerName: 'Beverages',
        linkedCategoryId: null,
        localName: null,
        automaticChanges: [],
        isLinked: false
      },
      {
        itemId: LINKED_CATEGORY_ITEM,
        externalId: '21',
        providerName: 'Snacks',
        linkedCategoryId: 7,
        localName: 'Snack food',
        automaticChanges: [{ field: 'name', localValue: 'Snack food', providerValue: 'Snacks' }],
        isLinked: true
      }
    ],
    products: [
      {
        itemId: UNLINKED_PRODUCT_ITEM,
        externalId: '888',
        providerName: 'Coke Zero 600ml',
        providerGroupExternalId: '20',
        linkedProductId: null,
        localName: null,
        unresolvedSaleCount: 4,
        automaticChanges: [],
        choices: [],
        isLinked: false
      },
      {
        itemId: LINKED_PRODUCT_ITEM,
        externalId: '889',
        providerName: 'Mars Bar 53g',
        providerGroupExternalId: '21',
        linkedProductId: 5,
        localName: 'Mars Bar',
        unresolvedSaleCount: 0,
        automaticChanges: [
          { field: 'name', localValue: 'Mars Bar', providerValue: 'Mars Bar 53g' },
          { field: 'categoryId', localValue: 'Snack food', providerValue: 'Snacks' }
        ],
        choices: [],
        isLinked: true
      }
    ]
  };
}

function applied(): CatalogueReviewApplied {
  return {
    reviewId: 77,
    alreadyApplied: false,
    appliedAtUtc: '2026-10-20T02:05:00Z',
    links: [
      { kind: 'Category', externalId: '20', localRowId: 12, name: 'Beverages', created: false },
      { kind: 'Product', externalId: '888', localRowId: 31, name: 'Coke Zero 600ml', created: true }
    ],
    automaticChanges: [],
    acceptedChanges: [],
    skippedChanges: [],
    unresolvedSaleCount: 4,
    createdCount: 1,
    linkedCount: 1
  };
}

const LOCAL_PRODUCTS: Product[] = [
  {
    id: 5,
    name: 'Mars Bar',
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: 0,
    maxStockInMachine: 0,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 0,
    restockTo: 0,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    providerManagedFields: ['name', 'categoryId']
  },
  {
    id: 9,
    name: 'Coca-Cola No Sugar 600ml',
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: 0,
    maxStockInMachine: 0,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 0,
    restockTo: 0,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    providerManagedFields: []
  },
  {
    id: 11,
    name: 'Spring Water 600ml',
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: 0,
    maxStockInMachine: 0,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 0,
    restockTo: 0,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    providerManagedFields: []
  }
];

const LOCAL_CATEGORIES: Category[] = [
  { id: 7, name: 'Snack food', providerManagedFields: ['name'] },
  { id: 12, name: 'Soft drinks', providerManagedFields: [] }
];

interface Harness {
  fixture: ComponentFixture<CatalogueReviewComponent>;
  host: HTMLElement;
  component: CatalogueReviewComponent;
  start: jest.Mock;
  apply: jest.Mock;
  toast: { success: jest.Mock; error: jest.Mock };
}

async function render(
  options: {
    start?: jest.Mock;
    apply?: jest.Mock;
    products?: Product[];
    categories?: Category[];
    sessionActive?: Observable<boolean>;
  } = {}
): Promise<Harness> {
  const start = options.start ?? jest.fn(() => of(review()));
  const apply = options.apply ?? jest.fn(() => of(applied()));
  const toast = { success: jest.fn(), error: jest.fn() };

  await TestBed.configureTestingModule({
    imports: [CatalogueReviewComponent],
    providers: [
      { provide: CatalogueReviewService, useValue: { start, apply } },
      { provide: ProductService, useValue: { getAll: () => of(options.products ?? LOCAL_PRODUCTS) } },
      { provide: CategoryService, useValue: { getAll: () => of(options.categories ?? LOCAL_CATEGORIES) } },
      {
        provide: ToastService,
        useValue: { ...toast, warning: jest.fn(), info: jest.fn() }
      },
      { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(CatalogueReviewComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance, start, apply, toast };
}

afterEach(() => TestBed.resetTestingModule());

function text(host: HTMLElement): string {
  return host.textContent?.replace(/\s+/g, ' ') ?? '';
}

function row(host: HTMLElement, itemId: number): HTMLElement {
  const element = host.querySelector<HTMLElement>(`[data-testid="review-entry-${itemId}"]`);
  if (!element) throw new Error(`No review row rendered for entry ${itemId}.`);
  return element;
}

function button(host: HTMLElement, label: string): HTMLButtonElement {
  const found = Array.from(host.querySelectorAll('button')).find((x) => x.textContent?.includes(label));
  if (!found) throw new Error(`No "${label}" button rendered.`);
  return found;
}

/** The decision one entry was applied with, or `undefined` when the apply sent none for it. */
function decisionFor(apply: jest.Mock, itemId: number) {
  const request = apply.mock.calls[0][0] as ApplyCatalogueReviewRequest;
  return request.decisions.find((decision) => decision.itemId === itemId);
}

describe('CatalogueReviewComponent categories step (issue #739)', () => {
  it('starts the review when it opens and reviews the categories first', async () => {
    const { host, start, component } = await render();

    expect(start).toHaveBeenCalledTimes(1);
    expect(component.step).toBe('categories');
    expect(text(host)).toContain('Step 1 of 2');
    // The product entries belong to the second step and must not be decided from the first.
    expect(host.querySelector(`[data-testid="review-entry-${UNLINKED_PRODUCT_ITEM}"]`)).toBeNull();
    expect(row(host, UNLINKED_CATEGORY_ITEM)).toBeTruthy();
  });

  it('links an unlinked provider category to an existing local category the operator searches for', async () => {
    const { fixture, component, apply } = await render();

    component.setAction(UNLINKED_CATEGORY_ITEM, CatalogueReviewAction.LinkExisting);
    component.setSearch(UNLINKED_CATEGORY_ITEM, 'soft');
    fixture.detectChanges();

    expect(component.categoryCandidates(UNLINKED_CATEGORY_ITEM).map((category) => category.id)).toEqual([12]);

    component.setLocalRow(UNLINKED_CATEGORY_ITEM, 12);
    component.goToProducts();
    fixture.detectChanges();
    component.applyDecisions();

    expect(decisionFor(apply, UNLINKED_CATEGORY_ITEM)).toEqual({
      itemId: UNLINKED_CATEGORY_ITEM,
      action: CatalogueReviewAction.LinkExisting,
      localRowId: 12
    });
  });

  it('offers no category the provider already manages, because linking one is refused', async () => {
    const { component } = await render();

    expect(component.categoryCandidates(UNLINKED_CATEGORY_ITEM).map((category) => category.id)).toEqual([12]);
  });

  it('states that a linked category takes the provider name and becomes read-only', async () => {
    const { fixture, host, component } = await render();

    component.setAction(UNLINKED_CATEGORY_ITEM, CatalogueReviewAction.LinkExisting);
    fixture.detectChanges();

    expect(text(row(host, UNLINKED_CATEGORY_ITEM))).toContain(
      "will be replaced by the provider's name and become read-only"
    );
  });

  it('creates a new local category for an unlinked provider category, naming no local row', async () => {
    const { fixture, component, apply } = await render();

    component.setAction(UNLINKED_CATEGORY_ITEM, CatalogueReviewAction.CreateNew);
    component.goToProducts();
    fixture.detectChanges();
    component.applyDecisions();

    expect(decisionFor(apply, UNLINKED_CATEGORY_ITEM)).toEqual({
      itemId: UNLINKED_CATEGORY_ITEM,
      action: CatalogueReviewAction.CreateNew
    });
  });

  it('lists an already-linked category rename as applied automatically and offers no choice about it', async () => {
    const { host } = await render();

    const linked = row(host, LINKED_CATEGORY_ITEM);
    expect(text(linked)).toContain('Applied automatically');
    expect(text(linked)).toContain('Snacks');
    expect(linked.querySelectorAll('input[type="radio"]')).toHaveLength(0);
  });

  it('sends no decision for a category the operator left alone', async () => {
    const { fixture, component, apply } = await render();

    component.goToProducts();
    fixture.detectChanges();
    component.applyDecisions();

    expect(decisionFor(apply, UNLINKED_CATEGORY_ITEM)).toBeUndefined();
    expect(decisionFor(apply, LINKED_CATEGORY_ITEM)).toBeUndefined();
  });
});

describe('CatalogueReviewComponent products step (issue #739)', () => {
  async function onProducts(options: Parameters<typeof render>[0] = {}): Promise<Harness> {
    const harness = await render(options);
    harness.component.goToProducts();
    harness.fixture.detectChanges();
    return harness;
  }

  it('offers the categories that exist after the categories step', async () => {
    const { fixture, component, host } = await render();

    // Before deciding, the provider's group is linked to nothing, so the product gets no category.
    component.goToProducts();
    fixture.detectChanges();
    expect(text(row(host, UNLINKED_PRODUCT_ITEM))).toContain('Category after this import: none');

    component.backToCategories();
    component.setAction(UNLINKED_CATEGORY_ITEM, CatalogueReviewAction.CreateNew);
    component.goToProducts();
    fixture.detectChanges();

    expect(text(row(host, UNLINKED_PRODUCT_ITEM))).toContain('Category after this import: Beverages');
  });

  it('links an unlinked provider product to a local product the operator searches for', async () => {
    const { fixture, component, apply } = await onProducts();

    component.setAction(UNLINKED_PRODUCT_ITEM, CatalogueReviewAction.LinkExisting);
    component.setSearch(UNLINKED_PRODUCT_ITEM, 'cola');
    fixture.detectChanges();

    expect(component.productCandidates(UNLINKED_PRODUCT_ITEM).map((product) => product.id)).toEqual([9]);

    component.setLocalRow(UNLINKED_PRODUCT_ITEM, 9);
    component.applyDecisions();

    expect(decisionFor(apply, UNLINKED_PRODUCT_ITEM)).toEqual({
      itemId: UNLINKED_PRODUCT_ITEM,
      action: CatalogueReviewAction.LinkExisting,
      localRowId: 9
    });
  });

  it('offers no product the provider already manages', async () => {
    const { component } = await onProducts();

    expect(component.productCandidates(UNLINKED_PRODUCT_ITEM).map((product) => product.id)).toEqual([9, 11]);
  });

  it("states that a linked product's name and category are replaced by the provider's and become read-only", async () => {
    const { fixture, host, component } = await onProducts();

    component.setAction(UNLINKED_PRODUCT_ITEM, CatalogueReviewAction.LinkExisting);
    fixture.detectChanges();

    expect(text(row(host, UNLINKED_PRODUCT_ITEM))).toContain(
      "name and category will be replaced by the provider's and become read-only"
    );
  });

  /**
   * Issue #792 (A2 of #786): a product carries no selling price, so linking offers no keep-or-take
   * choice, renders no price comparison, and the decision the apply carries is the link alone.
   */
  it('offers no price choice when linking, and sends the link alone', async () => {
    const { fixture, host, component, apply } = await onProducts();

    component.setAction(UNLINKED_PRODUCT_ITEM, CatalogueReviewAction.LinkExisting);
    component.setLocalRow(UNLINKED_PRODUCT_ITEM, 9);
    fixture.detectChanges();

    const entry = row(host, UNLINKED_PRODUCT_ITEM);
    expect(entry.querySelector('[data-testid="price-choice"]')).toBeNull();
    expect(entry.querySelector('[data-testid="price-comparison"]')).toBeNull();
    expect(text(entry)).not.toContain('Unit/volume');

    component.applyDecisions();

    expect(decisionFor(apply, UNLINKED_PRODUCT_ITEM)).toEqual({
      itemId: UNLINKED_PRODUCT_ITEM,
      action: CatalogueReviewAction.LinkExisting,
      localRowId: 9
    });
  });

  it('offers no price choice for a product the provider already manages either', async () => {
    const { host } = await onProducts();

    const entry = row(host, LINKED_PRODUCT_ITEM);
    expect(entry.querySelector('[data-testid="price-choice"]')).toBeNull();
    expect(entry.querySelector('[data-testid="price-comparison"]')).toBeNull();
  });

  /**
   * A stale server - one still running the previous release - could answer with `UnitPrice` and
   * `UnitVolume` choices. Neither is rendered and neither is sent back: the screen never offers a
   * field it cannot write, and the apply would refuse a price decision anyway.
   */
  it('renders and sends nothing for a choice an older API still proposes', async () => {
    const withLegacyChoices = review();
    withLegacyChoices.products[1].choices = [
      { field: 'UnitPrice', localValue: '3.00', providerValue: '3.50' },
      { field: 'UnitVolume', localValue: 'can', providerValue: 'bottle' }
    ];
    const { host, component, apply } = await onProducts({ start: jest.fn(() => of(withLegacyChoices)) });

    const entry = row(host, LINKED_PRODUCT_ITEM);
    expect(text(entry)).not.toContain('bottle');
    expect(text(entry)).not.toContain('3.50');
    expect(entry.querySelector('[data-testid="price-choice"]')).toBeNull();

    component.applyDecisions();

    // The entry carries no action of its own, so it is deferred - sent as nothing at all, with no
    // field answer that could reach the removed product field.
    expect(decisionFor(apply, LINKED_PRODUCT_ITEM)).toBeUndefined();
  });

  it('refuses to apply while a link names no local row, rather than losing every other answer', async () => {
    const { component, apply, toast } = await onProducts();

    component.setAction(UNLINKED_PRODUCT_ITEM, CatalogueReviewAction.LinkExisting);
    component.applyDecisions();

    expect(apply).not.toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalledWith(
      'Choose the local product to link, or change that entry to Create new or Skip. Nothing was applied.'
    );
  });

  /**
   * An already-linked product has nothing left to decide since issue #792: its name and category
   * are the provider's and applied automatically, and there is no price to accept or skip. So it
   * contributes no decision to the apply at all.
   */
  it('sends no decision for an already-linked product the operator left alone', async () => {
    const { component, apply } = await onProducts();

    component.applyDecisions();

    expect(decisionFor(apply, LINKED_PRODUCT_ITEM)).toBeUndefined();
  });

  it("lists an already-linked product's provider name and category changes as applied automatically", async () => {
    const { host } = await onProducts();

    const entry = row(host, LINKED_PRODUCT_ITEM);
    expect(text(entry)).toContain('Applied automatically');
    expect(text(entry)).toContain('Mars Bar 53g');
    expect(text(entry)).toContain('Snacks');
  });

  it('sends the whole set of decisions once, naming the review it decided', async () => {
    const { component, apply } = await onProducts();

    component.setAction(UNLINKED_PRODUCT_ITEM, CatalogueReviewAction.CreateNew);
    component.applyDecisions();

    expect(apply).toHaveBeenCalledTimes(1);
    expect((apply.mock.calls[0][0] as ApplyCatalogueReviewRequest).reviewId).toBe(77);
  });

  it('reports what the apply did and stops offering it again', async () => {
    const { fixture, host, component, toast } = await onProducts();

    component.setAction(UNLINKED_PRODUCT_ITEM, CatalogueReviewAction.CreateNew);
    component.applyDecisions();
    fixture.detectChanges();

    expect(component.applied?.reviewId).toBe(77);
    expect(text(host)).toContain('Created 1');
    expect(text(host)).toContain('Linked 1');
    expect(toast.success).toHaveBeenCalled();
    expect(host.querySelector(`[data-testid="review-entry-${UNLINKED_PRODUCT_ITEM}"]`)).toBeNull();
  });

  it('reports the server refusal and keeps the decisions on screen so they can be corrected', async () => {
    const apply = jest.fn(() =>
      throwError(() => ({ error: { message: 'Nothing was applied. Entry 201: ...' } })));
    const { fixture, component, toast, host } = await onProducts({ apply });

    component.setAction(UNLINKED_PRODUCT_ITEM, CatalogueReviewAction.CreateNew);
    component.applyDecisions();
    fixture.detectChanges();

    expect(toast.error).toHaveBeenCalledWith('Nothing was applied. Entry 201: ...');
    expect(component.applied).toBeNull();
    expect(row(host, UNLINKED_PRODUCT_ITEM)).toBeTruthy();
  });
});

describe('CatalogueReviewComponent lifecycle (issue #739)', () => {
  it('reports a failed start and offers no decisions at all', async () => {
    const start = jest.fn(() => throwError(() => ({ error: 'Nayax is not connected.' })));
    const { component, toast } = await render({ start });

    expect(component.review).toBeNull();
    expect(toast.error).toHaveBeenCalledWith('Nayax is not connected.');
  });

  it('applies nothing while a support session is active, because the session is read-only', async () => {
    const { fixture, component, apply, host } = await render({ sessionActive: of(true) });

    component.goToProducts();
    fixture.detectChanges();
    component.applyDecisions();

    expect(apply).not.toHaveBeenCalled();
    expect(button(host, 'Apply').disabled).toBe(true);
  });

  it('closes the review on request without applying anything', async () => {
    const { component, apply } = await render();
    const closed = jest.fn();
    component.closed.subscribe(closed);

    component.close();

    expect(closed).toHaveBeenCalledTimes(1);
    expect(apply).not.toHaveBeenCalled();
  });
});
