import { Component, ElementRef, HostListener, Input, OnChanges, SimpleChanges, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BehaviorSubject } from 'rxjs';
import { ProductService } from '../../../services/product.service';
import { ProductPriceComparison } from '../../../models/models';

/**
 * Product-level supplier price history and comparison (issue #63). It owns its own fetch, loading,
 * and drill-down dialog state - the summary of the lowest/latest actual Purchase cost, and a
 * "View price history" dialog listing every recorded PurchaseItem newest-first - so the product-edit
 * page only has to supply the product identity.
 *
 * The comparison is guidance derived from actual Purchase history, never a substitute for AVCO or a
 * live supplier quote; it is presented read-only and never writes anything back.
 */
@Component({
  selector: 'app-product-price-history',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './product-price-history.component.html'
})
export class ProductPriceHistoryComponent implements OnChanges {
  @Input() productId: number | null = null;

  comparison$ = new BehaviorSubject<ProductPriceComparison | null>(null);
  loading$ = new BehaviorSubject(false);
  error$ = new BehaviorSubject<string | null>(null);
  historyOpen$ = new BehaviorSubject(false);

  @ViewChild('historyTrigger') private historyTrigger?: ElementRef<HTMLButtonElement>;

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLElement> | undefined) {
    panel?.nativeElement.focus();
  }

  constructor(private productService: ProductService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productId']) {
      this.load();
    }
  }

  private load(): void {
    this.comparison$.next(null);
    this.error$.next(null);
    const id = this.productId;
    if (!id) {
      return;
    }

    this.loading$.next(true);
    this.productService.getPriceComparison(id).subscribe({
      next: (comparison) => {
        this.loading$.next(false);
        this.comparison$.next(comparison);
      },
      error: () => {
        this.loading$.next(false);
        this.error$.next('Failed to load price history.');
      }
    });
  }

  openHistory(): void {
    this.historyOpen$.next(true);
  }

  closeHistory(): void {
    if (!this.historyOpen$.value) {
      return;
    }

    this.historyOpen$.next(false);
    this.historyTrigger?.nativeElement.focus();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.historyOpen$.value) {
      this.closeHistory();
    }
  }
}
