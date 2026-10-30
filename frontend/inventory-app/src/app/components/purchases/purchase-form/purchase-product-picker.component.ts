import { Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output } from '@angular/core';
import { Subscription } from 'rxjs';
import { Product } from '../../../models/models';
import { IconComponent } from '../../shared/icon.component';
import { PopoverCoordinator } from '../../shared/popover-coordinator.service';

/** How many matches the list shows at once; typing narrows it further. */
const MAX_VISIBLE_MATCHES = 50;

/**
 * The searchable product box on a purchase line (issue #1029), in place of a long `<select>`.
 *
 * It searches only the business's own products the page already loaded, by name, and shows each
 * one's name and its size or unit where the product has one. It invents nothing: no supplier
 * catalogue, no price and no product is created here. The box is a search over the selection, never
 * a value that is submitted; it shows the chosen product again whenever the list closes.
 *
 * It is the same combobox pattern as the product form's category picker: focus stays in the box,
 * Arrow keys move through the list (`aria-activedescendant`), Enter picks, Escape and Tab close, and
 * only one popover is open at a time (see {@link PopoverCoordinator}).
 */
@Component({
  selector: 'app-purchase-product-picker',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './purchase-product-picker.component.html'
})
export class PurchaseProductPickerComponent implements OnChanges, OnDestroy {
  private static nextId = 0;

  @Input() products: Product[] = [];

  /** The chosen product, or `0` while none is chosen. */
  @Input() productId = 0;

  /** The box's accessible name, such as "Product for item 2". */
  @Input({ required: true }) ariaLabel = '';

  @Input() inputId = '';
  @Input() testId = '';

  /** Whether the line's own validation is showing a missing product. */
  @Input() invalid = false;

  /** The id of the message that explains {@link invalid}, so the box can point at it. */
  @Input() describedBy: string | null = null;

  @Output() readonly productIdChange = new EventEmitter<number>();

  /** The person has left the box, which is when a missing product may be pointed out. */
  @Output() readonly touched = new EventEmitter<void>();

  open = false;
  query = '';
  activeIndex = -1;
  matches: Product[] = [];

  private typed = false;
  private readonly instance = PurchaseProductPickerComponent.nextId++;
  private readonly subscription: Subscription;

  constructor(
    private readonly host: ElementRef<HTMLElement>,
    private readonly coordinator: PopoverCoordinator
  ) {
    this.subscription = coordinator.opened$.subscribe((owner) => {
      if (owner !== this && this.open) this.close();
    });
  }

  get listboxId(): string {
    return `purchase-product-list-${this.instance}`;
  }

  get activeDescendantId(): string | null {
    return this.open && this.activeIndex >= 0 ? this.optionId(this.activeIndex) : null;
  }

  get selectedProduct(): Product | undefined {
    return this.products.find((product) => product.id === Number(this.productId));
  }

  /** Whether a search was typed and nothing matched it, which the list says rather than implies. */
  get noMatches(): boolean {
    return this.typed && this.query.trim().length > 0 && this.matches.length === 0;
  }

  ngOnChanges(): void {
    if (!this.open) this.syncQueryToSelection();
    this.refreshMatches();
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
  }

  optionId(index: number): string {
    return `${this.listboxId}-option-${index}`;
  }

  /** A product's size or unit, shown beside its name where the product records one. */
  productDetail(product: Product): string {
    return product.unit?.trim() ?? '';
  }

  openPanel(): void {
    if (this.open) return;
    // Opened rather than typed into, so the whole list is offered rather than only the products
    // whose name contains the chosen one.
    this.open = true;
    this.typed = false;
    this.refreshMatches();
    this.activeIndex = this.matches.findIndex((product) => product.id === Number(this.productId));
    this.coordinator.announceOpen(this);
  }

  onInput(event: Event): void {
    this.query = (event.target as HTMLInputElement).value;
    this.typed = true;
    if (!this.open) {
      this.open = true;
      this.coordinator.announceOpen(this);
    }
    this.refreshMatches();
    this.activeIndex = this.matches.length > 0 ? 0 : -1;
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
        // Enter while the list is open picks from it, and never submits the purchase by accident.
        if (!this.open) return;
        event.preventDefault();
        if (this.activeIndex >= 0 && this.activeIndex < this.matches.length) {
          this.choose(this.matches[this.activeIndex]);
        }
        break;
      case 'Escape':
        if (this.open) {
          event.preventDefault();
          event.stopPropagation();
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

  onBlur(): void {
    this.touched.emit();
  }

  choose(product: Product): void {
    this.productId = product.id;
    this.productIdChange.emit(product.id);
    this.close();
  }

  isSelected(product: Product): boolean {
    return product.id === Number(this.productId);
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.open && !this.host.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  private close(): void {
    this.open = false;
    this.typed = false;
    this.activeIndex = -1;
    this.syncQueryToSelection();
  }

  private syncQueryToSelection(): void {
    this.query = this.selectedProduct?.name ?? '';
  }

  private refreshMatches(): void {
    const filter = this.typed ? this.query.trim().toLowerCase() : '';
    this.matches = this.products
      .filter((product) => product.name.toLowerCase().includes(filter))
      .slice(0, MAX_VISIBLE_MATCHES);
    if (this.activeIndex >= this.matches.length) this.activeIndex = this.matches.length - 1;
  }

  private moveActive(step: number): void {
    const count = this.matches.length;
    if (count === 0) {
      this.activeIndex = -1;
      return;
    }
    this.activeIndex = this.activeIndex < 0 ? (step > 0 ? 0 : count - 1) : (this.activeIndex + step + count) % count;
  }
}
