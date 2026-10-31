import { Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, Output } from '@angular/core';
import { Product } from '../../../models/models';
import { IconComponent } from '../../shared/icon.component';

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
 * Arrow keys move through the list (`aria-activedescendant`), Enter picks, and Escape, Tab, a click
 * elsewhere or focus moving anywhere outside the box close it, so it never stays open beside a help tip.
 */
@Component({
  selector: 'app-purchase-product-picker',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './purchase-product-picker.component.html'
})
export class PurchaseProductPickerComponent implements OnChanges {
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

  constructor(private readonly host: ElementRef<HTMLElement>) {}

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
  }

  onInput(event: Event): void {
    this.query = (event.target as HTMLInputElement).value;
    this.typed = true;
    this.open = true;
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

  /** Focus leaving the box closes the list; an option keeps focus in the box, so picking one does not. */
  onBlur(event: FocusEvent): void {
    const movedTo = event.relatedTarget;
    if (this.open && !(movedTo instanceof Node && this.host.nativeElement.contains(movedTo))) {
      this.close();
    }
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
    if (this.activeIndex >= 0) {
      this.activeIndex = (this.activeIndex + step + count) % count;
      return;
    }
    this.activeIndex = step > 0 ? 0 : count - 1;
  }
}
