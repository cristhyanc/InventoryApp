import { Component, EventEmitter, Input, Output } from '@angular/core';
import { pageCount, pageRange } from './paging';

/**
 * The footer pager for a list already read in full (issue #1093): "Showing 1-10 of 71 products",
 * an items-per-page selector, and Previous / Page X of Y / Next.
 *
 * It owns no list and no page: the page keeps `pageIndex` and `pageSize` and this component only
 * reports what the operator asked for, so a page-size change can return to page 1 in the one place
 * that also slices the list. Nothing here reads anything - turning a page re-requests nothing.
 */
@Component({
  selector: 'app-list-pagination',
  standalone: true,
  imports: [],
  template: `
    <div class="flex flex-wrap items-center justify-between gap-3 text-sm text-rr-muted" data-testid="list-pagination">
      <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
        <p class="m-0" data-testid="list-pagination-range" aria-live="polite">
          Showing <span class="font-semibold text-rr-navy">{{ range.first }}–{{ range.last }}</span> of
          <span class="font-semibold text-rr-navy">{{ totalItems }}</span> {{ itemLabel }}
        </p>
        <label class="flex items-center gap-2">
          <span>Per page</span>
          <select
            class="min-h-10 w-auto rounded-lg border border-rr-line bg-white px-2 text-sm text-rr-navy"
            data-testid="list-pagination-size"
            [value]="pageSize"
            (change)="onSizeChange($event)"
          >
            @for (size of pageSizeOptions; track size) {
              <option [value]="size" [selected]="size === pageSize">{{ size }} per page</option>
            }
          </select>
        </label>
      </div>
      <div class="flex items-center gap-2">
        <button
          type="button"
          class="rr-button-secondary min-h-10 px-3 disabled:cursor-not-allowed disabled:opacity-50"
          data-testid="list-pagination-previous"
          [disabled]="pageIndex <= 0"
          (click)="go(pageIndex - 1)"
        >Previous</button>
        <span class="whitespace-nowrap text-rr-navy" data-testid="list-pagination-page">Page {{ pageIndex + 1 }} of {{ pages }}</span>
        <button
          type="button"
          class="rr-button-secondary min-h-10 px-3 disabled:cursor-not-allowed disabled:opacity-50"
          data-testid="list-pagination-next"
          [disabled]="pageIndex >= pages - 1"
          (click)="go(pageIndex + 1)"
        >Next</button>
      </div>
    </div>
  `
})
export class ListPaginationComponent {
  @Input({ required: true }) totalItems = 0;
  @Input({ required: true }) pageIndex = 0;
  @Input({ required: true }) pageSize = 10;
  @Input({ required: true }) pageSizeOptions: readonly number[] = [];
  /** The plural noun the range names, e.g. `products`. */
  @Input() itemLabel = 'items';

  @Output() readonly pageIndexChange = new EventEmitter<number>();
  @Output() readonly pageSizeChange = new EventEmitter<number>();

  get pages(): number {
    return pageCount(this.totalItems, this.pageSize);
  }

  get range(): { first: number; last: number } {
    return pageRange(this.pageIndex, this.totalItems, this.pageSize);
  }

  go(index: number): void {
    if (index < 0 || index >= this.pages || index === this.pageIndex) {
      return;
    }
    this.pageIndexChange.emit(index);
  }

  /** Only an offered size is reported; anything else (a tampered option) is ignored. */
  onSizeChange(event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value);
    if (this.pageSizeOptions.includes(value) && value !== this.pageSize) {
      this.pageSizeChange.emit(value);
    }
  }
}
