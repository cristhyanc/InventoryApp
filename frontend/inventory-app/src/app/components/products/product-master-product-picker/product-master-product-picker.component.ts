import { CommonModule } from '@angular/common';
import {
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  OnDestroy,
  OnInit,
  Output
} from '@angular/core';
import { EMPTY, Observable, Subject, Subscription, timer } from 'rxjs';
import { catchError, switchMap, tap } from 'rxjs/operators';
import { MasterProductSuggestion } from '../../../models/models';
import { ProductService } from '../../../services/product.service';
import { SEARCH_DEBOUNCE_MS } from '../../shared/filter-request-trigger';

/**
 * The shortest term the lookup accepts (`MasterProductSuggestionPaging`, issue #1049). Below it the
 * server answers `400`, so the field asks nothing at all rather than spending a refused request on
 * every first keystroke.
 */
export const MASTER_PRODUCT_SUGGESTION_MIN_LENGTH = 2;

/** Where the suggestions have got to for the text currently in the box. */
export type MasterProductSuggestionState = 'idle' | 'loading' | 'loaded' | 'failed';

/**
 * The Name field of New Product, with the shared master product catalogue suggested as it is typed
 * (issue #1052, parent #817).
 *
 * <p>It is a dedicated feature component rather than markup on the routed page because it is a
 * workflow of its own - its own debounced read of `GET api/products/master-product-suggestions`
 * (issue #1049), its own open/active/list state, its own loading, empty and failure states and its
 * own combobox keyboard (docs/architecture.md § Page composition boundary (issue #191)). The page
 * keeps the answers: the Name it displays, and the suggestion the operator took.</p>
 *
 * <p><strong>The Name stays free text.</strong> Every keystroke is reported through
 * {@link productNameChange} exactly as typed, and a term that matches nothing in the shared
 * catalogue is still the Name the product is created with - the suggestions are an offer, never a
 * list the Name has to come from. Nothing here fills a field by itself: a suggestion is reported
 * only when it is activated, by Enter on the keyboard or by pressing the row, and the parent decides
 * what that fills in.</p>
 *
 * <p><strong>Only the text in the box now has suggestions.</strong> The `switchMap` is over the raw
 * keystroke and the debounce is its inner `timer`, so changing the box - or taking a suggestion -
 * drops both the wait that has not fired yet and the request already in flight, immediately rather
 * than 300 ms later. The rows of the previous term go with it, so a stale row is never on screen
 * and so never selectable: an older answer can reach neither the list, the field nor the parent,
 * and nothing left over can reopen the list over a Name the operator has just filled in. A failed
 * lookup is a message beside the field and nothing more - it disables no control, blocks no typing
 * and refuses no save, because the shared catalogue being unreachable is not a reason a business
 * cannot name its own product.</p>
 */
@Component({
  selector: 'app-product-master-product-picker',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './product-master-product-picker.component.html'
})
export class ProductMasterProductPickerComponent implements OnInit, OnDestroy {
  private static nextInstanceId = 0;

  /** The Name to display, which the parent owns: this control holds no Name of its own. */
  @Input() productName = '';

  /** The typed Name, reported as it is typed. Free text, never a suggestion's value. */
  @Output() readonly productNameChange = new EventEmitter<string>();

  /**
   * The suggestion the operator took, with the server's own prefill values on it. The parent fills
   * the Name and the unit label in from it and remembers the selection; this component remembers
   * nothing about it, which is why re-taking a suggestion is always a fresh selection.
   */
  @Output() readonly suggestionSelected = new EventEmitter<MasterProductSuggestion>();

  /** The matches for {@link searchedTerm}, in the server's own order. */
  suggestions: MasterProductSuggestion[] = [];

  /** Whether the server had more matches than the page it served, so the term is worth narrowing. */
  hasMore = false;

  /** Whether {@link suggestions} is a real answer for the term currently in the box. */
  state: MasterProductSuggestionState = 'idle';

  /** The term the current list, empty message or failure message is about. */
  searchedTerm = '';

  /** Whether the suggestion list is open. */
  open = false;

  /** The index in {@link suggestions} the keyboard is on, or `-1` while no row is active. */
  activeIndex = -1;

  private readonly instanceId = ProductMasterProductPickerComponent.nextInstanceId++;
  readonly inputId = `product-master-product-input-${this.instanceId}`;
  readonly listboxId = `product-master-product-listbox-${this.instanceId}`;
  readonly hintId = `product-master-product-hint-${this.instanceId}`;

  /** A term to ask about once it settles, or `null` to drop the pending question and ask nothing. */
  private readonly terms$ = new Subject<string | null>();
  private searchSubscription?: Subscription;

  constructor(
    private readonly productService: ProductService,
    private readonly elementRef: ElementRef<HTMLElement>
  ) {}

  ngOnInit(): void {
    // `switchMap` over the raw keystroke, with the debounce as its inner `timer`, rather than
    // `debounceTime` in front of it. Both shapes ask once per settled term, but this one unsubscribes
    // the previous wait *and* the request it already started the moment anything is typed, so an
    // answer to a term that has already been replaced cannot arrive inside the next debounce window
    // and reopen or repopulate the list. `null` drops the pending question without asking another.
    // No `distinctUntilChanged` on the term: taking a suggestion refills the box without typing, so
    // a term asked once and then typed again is a new question and must be asked again.
    this.searchSubscription = this.terms$
      .pipe(
        switchMap((term) =>
          term === null ? EMPTY : timer(SEARCH_DEBOUNCE_MS).pipe(switchMap(() => this.search(term)))
        )
      )
      .subscribe();
  }

  ngOnDestroy(): void {
    this.searchSubscription?.unsubscribe();
    this.terms$.complete();
  }

  /** The option the active row points at, for `aria-activedescendant`. */
  get activeDescendantId(): string | null {
    return this.open && this.activeIndex >= 0 && this.activeIndex < this.suggestions.length
      ? this.optionId(this.activeIndex)
      : null;
  }

  /** Whether the term was searched and the shared catalogue holds nothing like it. */
  get noMatches(): boolean {
    return this.state === 'loaded' && this.suggestions.length === 0;
  }

  /**
   * One suggestion as the list reads it: the server's own `{Brand} {Name}`, then the size with a
   * space - `45 g` beside `175 g` - so two sizes of one product are told apart at a glance. The size
   * is spaced for reading and is deliberately not the unit label (`45g`), which is the value a
   * selection fills the Unit Label field with.
   */
  suggestionLabel(suggestion: MasterProductSuggestion): string {
    return `${suggestion.productName} · ${suggestion.sizeValue} ${suggestion.sizeUnit}`;
  }

  optionId(index: number): string {
    return `${this.listboxId}-option-${index}`;
  }

  onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    // Reported first and always: the Name is free text, and nothing about the suggestions may delay
    // or alter what the operator typed.
    this.productNameChange.emit(value);

    const term = value.trim();
    if (term.length < MASTER_PRODUCT_SUGGESTION_MIN_LENGTH) {
      this.closeAndForget();
      // Nothing is asked below the minimum, and the question already in flight is dropped here: an
      // answer on its way must not reopen the list for text the box no longer holds.
      this.terms$.next(null);
      return;
    }

    // The previous term's rows go now, not when the next answer arrives. A row that matched text the
    // operator has already changed is not an offer any more, so it is neither shown nor selectable
    // while the new term is being searched.
    this.suggestions = [];
    this.hasMore = false;
    this.open = true;
    this.activeIndex = -1;
    this.state = 'loading';
    this.searchedTerm = term;
    this.terms$.next(term);
  }

  onKeydown(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
        if (this.suggestions.length > 0) {
          event.preventDefault();
          this.open = true;
          this.moveActive(1);
        }
        break;
      case 'ArrowUp':
        if (this.suggestions.length > 0) {
          event.preventDefault();
          this.open = true;
          this.moveActive(-1);
        }
        break;
      case 'Enter':
        if (!this.open) {
          // Nothing is open, so Enter belongs to the form this field sits in.
          return;
        }
        // Enter while the list is open acts on the list: it takes the active suggestion, and it
        // never creates the product by accident under a list covering the page.
        event.preventDefault();
        if (this.activeIndex >= 0 && this.activeIndex < this.suggestions.length) {
          this.select(this.suggestions[this.activeIndex]);
        }
        break;
      case 'Escape':
        if (this.open) {
          event.preventDefault();
          // The typed Name stays exactly as it is: this list is an offer, so dismissing it takes
          // nothing away.
          this.open = false;
          this.activeIndex = -1;
        }
        break;
      case 'Tab':
        this.open = false;
        this.activeIndex = -1;
        break;
      default:
        break;
    }
  }

  /** Takes a suggestion: the one thing that reports one, by Enter or by pressing the row. */
  select(suggestion: MasterProductSuggestion): void {
    if (!this.suggestions.includes(suggestion)) {
      // A row the list no longer holds is not an offer: it would fill the Name and the unit label in
      // from a product that matched text the operator has already changed.
      return;
    }

    this.closeAndForget();
    // Whatever the box was still waiting on goes with the list. The Name and the unit label this
    // selection is about to fill in must not be overwritten, and no obsolete list may reopen.
    this.terms$.next(null);
    this.suggestionSelected.emit(suggestion);
  }

  /** Closes the list when the pointer goes anywhere else, as the category picker already does. */
  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.open && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.open = false;
      this.activeIndex = -1;
    }
  }

  /**
   * The read for one settled term, which is always long enough to ask about: {@link onInput} drops
   * the question below the minimum rather than spending a refused request on it. A failure is caught
   * here rather than killing the stream, so the next settled term asks again.
   */
  private search(term: string): Observable<unknown> {
    return this.productService.getMasterProductSuggestions(term).pipe(
      tap((answer) => {
        this.suggestions = answer.products;
        this.hasMore = answer.hasMore;
        this.searchedTerm = term;
        this.state = 'loaded';
        this.open = true;
        this.activeIndex = -1;
      }),
      catchError(() => {
        this.suggestions = [];
        this.hasMore = false;
        this.searchedTerm = term;
        this.state = 'failed';
        this.open = true;
        this.activeIndex = -1;
        return EMPTY;
      })
    );
  }

  private closeAndForget(): void {
    this.open = false;
    this.activeIndex = -1;
    this.state = 'idle';
    this.searchedTerm = '';
    this.suggestions = [];
    this.hasMore = false;
  }

  private moveActive(step: number): void {
    const count = this.suggestions.length;
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
}
