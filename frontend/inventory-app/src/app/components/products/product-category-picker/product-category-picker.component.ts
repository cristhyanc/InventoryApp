import { CommonModule } from '@angular/common';
import {
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  OnChanges,
  OnDestroy,
  OnInit,
  Output,
  SimpleChanges
} from '@angular/core';
import { Subscription } from 'rxjs';
import { Category } from '../../../models/models';
import { CategoryService } from '../../../services/category.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { apiErrorMessage } from '../../shared/api-error-message';

/**
 * One row of the open list. "No category" is the optional-category semantics the replaced `<select>`
 * carried as its `None` option, and the create row is the explicit action - never a consequence of
 * typing - that this component exists for.
 */
export type CategoryPickerEntry =
  | { readonly kind: 'none' }
  | { readonly kind: 'category'; readonly category: Category }
  | { readonly kind: 'create'; readonly name: string };

/**
 * Choosing the category of a product being created (issue #830): searching this business's existing
 * categories by name, and creating a missing one without leaving the product form or losing a field
 * on it.
 *
 * <p>It is a dedicated feature component rather than markup on the routed page because it is a
 * workflow of its own - its own option read, its own filter and open/active state, its own
 * `POST api/categories` with that write's pending, refusal and retry lifecycle (docs/architecture.md
 * § Page composition boundary (issue #191)). The page keeps only the answer: the category id to
 * submit, reported through <c>categoryIdChange</c>. Nothing here touches the product form, which is
 * what makes a failed or pending category create unable to clear a typed product field or the GST
 * rule chosen beside it (issue #829).</p>
 *
 * <p><strong>What it submits is an id, never text.</strong> The box is a search field, so whenever
 * the list is closed its text is set back to the selected category's name (or empty): text that
 * matched nothing can never be read as a selection, and no name is created by typing it or by
 * leaving the field. Creation is the explicitly activated row alone.</p>
 *
 * <p><strong>Creating is the existing category flow.</strong> `CategoryService.create` posts to
 * `POST api/categories`, whose own validation, per-business duplicate-name rule
 * (`Inventory.Domain.Categories.CategoryCataloguePolicy`) and `Inventory.Operate` capability decide
 * the outcome; this component words none of them and shows the server's refusal verbatim. A refusal
 * also re-reads the options, because the usual reason for one is that the category now exists -
 * created in another tab or by somebody else - and the answer to that is to select it, not to invent
 * a duplicate policy here. A support session is refused the write by the backend's read-only method
 * rule, so the action is not offered in one either (issue #551, as on the Categories page).</p>
 *
 * <p><strong>The create needs the options.</strong> It is offered only for a name none of the
 * options holds, so it is offered only while the options are known: a read still in flight or one
 * that failed is not a business with no such category, and the failure's own message says that
 * nothing can be searched or created here until a Retry succeeds.</p>
 */
@Component({
  selector: 'app-product-category-picker',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './product-category-picker.component.html'
})
export class ProductCategoryPickerComponent implements OnInit, OnChanges, OnDestroy {
  private static nextInstanceId = 0;

  /** The selected category id, or `null` for no category - the product's optional category. */
  @Input() categoryId: number | null = null;

  /**
   * The selection changed. It is always an id of a category that exists, or `null`: the typed text
   * is a search term and is never reported.
   */
  @Output() readonly categoryIdChange = new EventEmitter<number | null>();

  /** The business's categories, as the API answered them, with any created here added. */
  categories: Category[] = [];

  /** The text in the box: a search term while the list is open, the selection's name once closed. */
  query = '';

  /** Whether the option list is open. */
  open = false;

  /** The rows currently offered, recomputed from the options and the typed text. */
  entries: CategoryPickerEntry[] = [];

  /** The index in `entries` the keyboard is on, or `-1` while no row is active. */
  activeIndex = -1;

  loading = true;
  loadError = '';

  /** Whether a category create is in flight: the one thing that prevents a duplicate submission. */
  creating = false;
  createError = '';

  /** Whether a support session is active, in which case no create is offered (issue #551). */
  sessionActive = false;

  private readonly instanceId = ProductCategoryPickerComponent.nextInstanceId++;
  readonly inputId = `product-category-picker-input-${this.instanceId}`;
  readonly listboxId = `product-category-picker-listbox-${this.instanceId}`;
  readonly hintId = `product-category-picker-hint-${this.instanceId}`;

  /** Whether the text has been typed into since the list was opened, so it is a search term. */
  private typed = false;

  private sessionActiveSubscription?: Subscription;

  constructor(
    private readonly categoryService: CategoryService,
    private readonly elementRef: ElementRef<HTMLElement>,
    private readonly supportSession: SupportSessionService
  ) {}

  ngOnInit(): void {
    this.sessionActiveSubscription = this.supportSession.active$.subscribe((active) => {
      this.sessionActive = active;
      this.refreshEntries();
    });
    this.loadCategories();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['categoryId'] && !this.open) {
      this.syncQueryToSelection();
      this.refreshEntries();
    }
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription?.unsubscribe();
  }

  /** The selected category, or `null` while none is selected or the options are not loaded yet. */
  get selectedCategory(): Category | null {
    return this.categories.find((category) => category.id === this.categoryId) ?? null;
  }

  /** The option the active row points at, for `aria-activedescendant`. */
  get activeDescendantId(): string | null {
    return this.open && this.activeIndex >= 0 && this.activeIndex < this.entries.length
      ? this.optionId(this.activeIndex)
      : null;
  }

  /**
   * Whether this business's categories are actually known here: read, and read successfully. While
   * they are not, the list can say nothing about them - neither that nothing matches the typed name
   * nor that it is a name no category has - so it says neither.
   */
  get optionsLoaded(): boolean {
    return !this.loading && !this.loadError;
  }

  /** Whether the typed name matched no existing category, which the list says rather than implies. */
  get noMatches(): boolean {
    return (
      this.optionsLoaded &&
      this.filterText.length > 0 &&
      !this.entries.some((entry) => entry.kind === 'category')
    );
  }

  /** The trimmed search term, which is empty until the box has been typed into. */
  get filterText(): string {
    return this.typed ? this.query.trim() : '';
  }

  /** Reads the options. Also the retry offered when that read failed. */
  loadCategories(): void {
    this.loading = true;
    this.loadError = '';
    this.categoryService.getAll().subscribe({
      next: (categories) => {
        this.loading = false;
        this.categories = this.sorted(categories);
        if (!this.open) {
          this.syncQueryToSelection();
        }
        this.refreshEntries();
      },
      error: () => {
        this.loading = false;
        // A product's category is optional, so a failed read must not look like a blocked create:
        // it says what is missing and what can still be done.
        this.loadError =
          'The existing categories could not be loaded, so none can be searched or created here. ' +
          'Retry, or save the product without a category and set it later.';
        this.refreshEntries();
      }
    });
  }

  openPanel(): void {
    if (this.open) {
      return;
    }
    // Opened rather than typed into, so the whole list is offered: filtering by the selected
    // category's own name would hide every other category.
    this.open = true;
    this.typed = false;
    this.activeIndex = -1;
    this.refreshEntries();
  }

  onInput(event: Event): void {
    this.query = (event.target as HTMLInputElement).value;
    this.typed = true;
    this.open = true;
    this.createError = '';
    this.refreshEntries();
    this.activeIndex = this.bestMatchIndex();
  }

  onKeydown(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.openPanel();
        this.moveActive(1);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.openPanel();
        this.moveActive(-1);
        break;
      case 'Enter':
        if (!this.open) {
          // Nothing is open, so Enter belongs to the form this field sits in.
          return;
        }
        // Enter while the list is open acts on the list, and never submits the product form by
        // accident - least of all onto a create action the person has not chosen yet.
        event.preventDefault();
        if (this.activeIndex >= 0 && this.activeIndex < this.entries.length) {
          this.activate(this.entries[this.activeIndex]);
        }
        break;
      case 'Escape':
        if (this.open) {
          event.preventDefault();
          this.close();
        }
        break;
      case 'Tab':
        this.close();
        break;
      default:
        break;
    }
  }

  /** Acts on a row: the selection, the cleared selection, or the explicit create. */
  activate(entry: CategoryPickerEntry): void {
    switch (entry.kind) {
      case 'none':
        this.select(null);
        break;
      case 'category':
        this.select(entry.category.id);
        break;
      case 'create':
        this.createCategory(entry.name);
        break;
    }
  }

  /**
   * Creates the category through the existing categories API and selects what it answers.
   *
   * It writes a category and nothing else: the product form is untouched, and a second press while
   * the first write is in flight is refused rather than creating a second category.
   */
  createCategory(name: string): void {
    const proposed = name.trim();
    // Checked here as well as hidden/disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses a support session either way.
    if (!proposed || this.creating || this.sessionActive) {
      return;
    }

    this.creating = true;
    this.createError = '';
    this.refreshEntries();
    this.categoryService.create({ name: proposed, description: null }).subscribe({
      next: (created) => {
        this.creating = false;
        this.categories = this.sorted([
          ...this.categories.filter((category) => category.id !== created.id),
          created
        ]);
        this.select(created.id);
      },
      error: (error: unknown) => {
        this.creating = false;
        this.createError = apiErrorMessage(
          error,
          'The category was not created. Nothing else on this form was changed - retry, or pick an existing category.'
        );
        // The typed name and the open list both stay exactly as they are. A duplicate-name refusal
        // usually means the category now exists, so the options are re-read and the existing one
        // becomes selectable; a read that fails in turn leaves the options already on screen.
        this.categoryService.getAll().subscribe({
          next: (categories) => {
            this.categories = this.sorted(categories);
            this.refreshEntries();
          },
          error: () => this.refreshEntries()
        });
      }
    });
  }

  isSelectedEntry(entry: CategoryPickerEntry): boolean {
    switch (entry.kind) {
      case 'none':
        return this.categoryId === null;
      case 'category':
        return entry.category.id === this.categoryId;
      case 'create':
        return false;
    }
  }

  entryLabel(entry: CategoryPickerEntry): string {
    switch (entry.kind) {
      case 'none':
        return 'No category';
      case 'category':
        return entry.category.name;
      case 'create':
        return this.creating ? `Creating “${entry.name}”…` : `Create category “${entry.name}”`;
    }
  }

  /** A stable key per row, so re-filtering does not rebuild the rows that stayed. */
  entryKey(entry: CategoryPickerEntry): string {
    switch (entry.kind) {
      case 'none':
        return 'none';
      case 'category':
        return `category-${entry.category.id}`;
      case 'create':
        return 'create';
    }
  }

  optionId(index: number): string {
    return `${this.listboxId}-option-${index}`;
  }

  /** Closes the list when the pointer goes anywhere else, as the shared dropdown already does. */
  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.open && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  private select(id: number | null): void {
    this.categoryId = id;
    this.categoryIdChange.emit(id);
    this.close();
  }

  private close(): void {
    this.open = false;
    this.typed = false;
    this.activeIndex = -1;
    this.createError = '';
    this.syncQueryToSelection();
    this.refreshEntries();
  }

  /** The box always shows the selection once the list is closed, never a term that matched nothing. */
  private syncQueryToSelection(): void {
    this.query = this.selectedCategory?.name ?? '';
  }

  private refreshEntries(): void {
    const filter = this.filterText.toLowerCase();
    const proposed = this.query.trim();
    const matches = this.categories.filter((category) => category.name.toLowerCase().includes(filter));

    const entries: CategoryPickerEntry[] = [
      { kind: 'none' },
      ...matches.map((category) => ({ kind: 'category', category }) as const)
    ];

    // Surrounding whitespace and case are not a second category (`CategoryCataloguePolicy`), so a
    // name that already exists offers no create - only the existing category, to select. That
    // comparison is only answerable over options that were actually read, which is why the create
    // is offered only once they have been: while the read is in flight or has failed, every name
    // looks like a missing one, and the load error already says nothing can be created here.
    const alreadyExists = this.categories.some(
      (category) => category.name.trim().toLowerCase() === proposed.toLowerCase()
    );
    if (this.optionsLoaded && proposed.length > 0 && !alreadyExists && !this.sessionActive) {
      entries.push({ kind: 'create', name: proposed });
    }

    this.entries = entries;
    if (this.activeIndex >= this.entries.length) {
      this.activeIndex = this.entries.length - 1;
    }
  }

  /**
   * The row Enter should act on as a name is typed: the first matching category, else the create
   * action for a name that matches nothing, else nothing at all.
   */
  private bestMatchIndex(): number {
    if (this.filterText.length === 0) {
      return -1;
    }
    const match = this.entries.findIndex((entry) => entry.kind === 'category');
    if (match >= 0) {
      return match;
    }
    return this.entries.findIndex((entry) => entry.kind === 'create');
  }

  private moveActive(step: number): void {
    const count = this.entries.length;
    if (count === 0) {
      this.activeIndex = -1;
      return;
    }
    if (this.activeIndex < 0) {
      this.activeIndex = step > 0 ? 0 : count - 1;
      return;
    }
    this.activeIndex = (this.activeIndex + step + count) % count;
  }

  private sorted(categories: Category[]): Category[] {
    return [...categories].sort((left, right) => left.name.localeCompare(right.name));
  }
}
