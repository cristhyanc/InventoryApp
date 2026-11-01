import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subject, Subscription, catchError, map, of, switchMap, tap } from 'rxjs';
import {
  MASTER_PRODUCT_PAGE_SIZE,
  SuperAdminMasterProduct,
  SuperAdminMasterProductPage,
  SuperAdminMasterProductsService
} from '../../../services/super-admin-master-products.service';
import { MASTER_PRODUCT_SIZE_UNIT_LABELS } from '../../../services/super-admin-price-catalogue-review.service';
import { MasterProductDetailComponent } from './master-product-detail.component';
import { MasterProductFormComponent } from './master-product-form.component';

/** What one read of the catalogue asked the server for. */
type CatalogueRequest = { search: string; page: number; pageSize: number };

/** A read tagged with the request it answers; `result` is null when that read failed. */
type CatalogueRead = { request: CatalogueRequest; result: SuperAdminMasterProductPage | null };

/**
 * The Super Admin Master products page (issues #1023 and #1024), at `/super-admin/master-products`,
 * over the shared master-product catalogue.
 *
 * It exists because the catalogue was only visible through the Supplier Price Review queue (issue
 * #928), which shows what is *waiting for a decision* - so a product nothing is waiting on could not
 * be found, searched for or inspected at all.
 *
 * **Since #1024 it also manages the catalogue**: "New master product" adds one before any business
 * has asked for its price, and an Edit action on each row corrects a wrong brand, name or size. There
 * is deliberately **no delete control** - removing a master product is a separate task that follows
 * this one, and the API has no such route.
 *
 * **Every rule a write is held to is the server's**, and a refused write shows the server's own
 * sentence: the normalisation, the duplicate rule, the refusal of a size change while a supplier
 * listing of the product is confirmed, and the refusal of an edit answering a page that has moved on.
 * The catalogue is re-read after every accepted write, so what is on screen is what is stored.
 *
 * The search and the paging are the **server's** too: this page sends what a person typed and which
 * page they asked for, and displays the page the server says it served. Nothing is filtered, sorted or
 * counted here - a client-side filter over one page of a paged catalogue would silently hide the
 * matches on every other page.
 *
 * Per docs/architecture.md § Page composition boundary, the page owns the search box, the table and
 * the page controls, while the two workflows with their own state and lifecycle are dedicated feature
 * components: one product's supplier listings (`[productId]`/`(closed)`) and the create/edit form
 * (`[product]`/`(saved)`/`(cancelled)`).
 *
 * **Nothing about any business is here**, in either direction: the API publishes no business id, name
 * or product id and no count of who is tracking what, so there is nothing of the kind for this page to
 * display.
 *
 * **Only the newest read may describe the page.** Every search and every page step goes through one
 * `switchMap`, which cancels the read still in flight, because the search box stays usable while a
 * read is outstanding: searching "cola" and then "pepsi" must never leave the "pepsi" search box above
 * the "cola" rows, the "cola" total and the "cola" page number. That is the same cancellation the
 * product price-tracking panel uses for the same reason.
 */
@Component({
  selector: 'app-super-admin-master-products',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
    FormsModule,
    MasterProductDetailComponent,
    MasterProductFormComponent
  ],
  templateUrl: './super-admin-master-products.component.html'
})
export class SuperAdminMasterProductsComponent implements OnInit, OnDestroy {
  products: readonly SuperAdminMasterProduct[] = [];
  totalCount = 0;
  page = 1;
  pageSize = MASTER_PRODUCT_PAGE_SIZE;
  hasMore = false;

  loading = false;
  loadError = false;

  /** What is in the search box, which is not the same as what the current page was read with. */
  searchText = '';

  /** The search the products on screen were actually read with, so the empty state can name it. */
  appliedSearch = '';

  /** The product whose supplier listings are open, or `null` when none is. */
  selectedProductId: number | null = null;

  /**
   * Whether the create/edit form is open, and for which product (issue #1024).
   *
   * `null` with {@link formOpen} set means "adding one", and a product means "correcting that row".
   * Two fields rather than one, because `null` has to mean something different from "closed".
   */
  formOpen = false;

  /** The row being corrected, or `null` while a new master product is being added. */
  editing: SuperAdminMasterProduct | null = null;

  /** What the last accepted write did, shown until the next action. */
  saved: string | null = null;

  private readonly requested = new Subject<CatalogueRequest>();
  private readonly reads: Subscription;

  constructor(private readonly masterProducts: SuperAdminMasterProductsService) {
    this.reads = this.requested
      .pipe(
        tap(() => {
          this.loading = true;
          this.loadError = false;
        }),
        switchMap((request) =>
          this.masterProducts.list(request).pipe(
            map((result): CatalogueRead => ({ request, result })),
            // A failed read is carried through as a result rather than as an error, so one failure
            // cannot end the stream and leave every later search unanswered.
            catchError(() => of<CatalogueRead>({ request, result: null }))
          )
        )
      )
      .subscribe(({ request, result }) => {
        this.loading = false;

        if (result === null) {
          this.products = [];
          this.loadError = true;
          return;
        }

        this.products = result.products;
        this.totalCount = result.totalCount;
        // The server's own answer, not what was asked for: a page size above the maximum is
        // bounded rather than refused, and the page must show the slice it really received.
        this.page = result.page;
        this.pageSize = result.pageSize;
        this.hasMore = result.hasMore;
        // The search this answer was read with, which is the request's and never the search box's:
        // the box may already hold the next thing somebody typed.
        this.appliedSearch = request.search;
      });
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.reads.unsubscribe();
    this.requested.complete();
  }

  /** Reads the current page with the current search, abandoning whatever read is still in flight. */
  load(): void {
    this.requested.next({ search: this.searchText.trim(), page: this.page, pageSize: this.pageSize });
  }

  /** A new search always starts at the first page: page 4 of the previous search means nothing here. */
  search(): void {
    this.page = 1;
    this.selectedProductId = null;
    // The open form is about a row of the page being replaced, and a correction prepared against a
    // row nobody is looking at any more is not one worth keeping half-typed.
    this.closeForm();
    this.load();
  }

  clearSearch(): void {
    this.searchText = '';
    this.search();
  }

  previousPage(): void {
    if (this.page <= 1) {
      return;
    }
    this.page -= 1;
    this.selectedProductId = null;
    this.closeForm();
    this.load();
  }

  nextPage(): void {
    if (!this.hasMore) {
      return;
    }
    this.page += 1;
    this.selectedProductId = null;
    this.closeForm();
    this.load();
  }

  /** The 1-based range of the whole match this page covers, for the "Showing …" line. */
  get firstOnPage(): number {
    return this.totalCount === 0 ? 0 : (this.page - 1) * this.pageSize + 1;
  }

  get lastOnPage(): number {
    return this.totalCount === 0 ? 0 : this.firstOnPage + this.products.length - 1;
  }

  /** The size of one item, as the catalogue spells it: "375 ml", "1.25 l", "each". */
  sizeLabel(product: SuperAdminMasterProduct): string {
    const unit = MASTER_PRODUCT_SIZE_UNIT_LABELS[product.sizeUnit];
    return unit === 'each' ? `${product.sizeValue} each` : `${product.sizeValue} ${unit}`;
  }

  /** Opens or closes one product's supplier listings. */
  toggleListings(product: SuperAdminMasterProduct): void {
    this.selectedProductId = this.selectedProductId === product.id ? null : product.id;
  }

  closeListings(): void {
    this.selectedProductId = null;
  }

  /** Opens the form to add a master product (issue #1024). */
  startCreating(): void {
    this.formOpen = true;
    this.editing = null;
    this.saved = null;
  }

  /**
   * Opens the form to correct one row, with that row's own values - including the `updatedAtUtc` the
   * correction must present back, which is why the whole product is handed over rather than its id.
   */
  startEditing(product: SuperAdminMasterProduct): void {
    this.formOpen = true;
    this.editing = product;
    this.saved = null;
  }

  closeForm(): void {
    this.formOpen = false;
    this.editing = null;
  }

  /**
   * After a write the server accepted: the form closes, what happened is said, and the catalogue is
   * re-read, so what is on screen is what is stored - a corrected brand changes the row's position in
   * the server's own ordering, and a new product may not even be on this page.
   */
  savedChanges(outcome: string): void {
    this.closeForm();
    this.saved = outcome;

    // The listings panel is closed too: it is keyed on a product whose description has just changed,
    // and re-reading the catalogue is what the page is now showing.
    this.selectedProductId = null;
    this.load();
  }
}
