import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { PurchaseService } from '../../services/purchase.service';
import { GstClassification, Purchase, PurchaseGstSummary, PurchaseValidation } from '../../models/models';
import { ObjectUrlCache } from '../shared/object-url-cache';
import { IconComponent } from '../shared/icon.component';
import { gstClassificationLabel, isChargePresent } from './gst-classification-options';
import { PurchaseDocumentAttachComponent } from './purchase-document-attach/purchase-document-attach.component';
import { SupportSessionService } from '../../support-session/support-session.service';
import { ListPaginationComponent } from '../shared/list-pagination/list-pagination.component';
import { clampPageIndex, pageSlice } from '../shared/list-pagination/paging';

/** The page sizes the Purchases pager offers (issue #1209), the same set as All Products and Expenses. */
export const PURCHASE_PAGE_SIZE_OPTIONS = [5, 10, 20, 50, 100] as const;

/** The page size every visit starts at (issue #1209's approved design). Not persisted. */
export const PURCHASE_DEFAULT_PAGE_SIZE = 10;

/**
 * The purchases list (issue #475: display only). Editing a purchase is the dedicated
 * `/purchases/:id/edit` page (`purchase-edit/purchase-edit-page.component.ts`), which the Edit
 * action links to; this component holds no edit form, no edit state and no update request, which is
 * what keeps the list compact.
 *
 * <p>A purchase may have no supporting document at all (issue #976). This page reports that as
 * "No document" - a real state, never a blank cell - and composes in
 * `PurchaseDocumentAttachComponent` to offer one, which keeps that workflow's own file, saving and
 * error state out of the page.</p>
 */
@Component({
  selector: 'app-purchase-list',
  standalone: true,
  imports: [CommonModule, RouterLink, IconComponent, PurchaseDocumentAttachComponent, ListPaginationComponent],
  templateUrl: './purchase-list.component.html'
})
export class PurchaseListComponent implements OnInit, OnDestroy {
  purchases: Purchase[] = [];

  /**
   * The page of the API's one full read shown on screen (issue #1209), client-side over the same
   * list `loadThumbnails` already draws from - the same strategy as All Products and Expenses - so
   * turning a page or choosing a size re-requests nothing. Every visit starts at page 1, and
   * neither value is persisted.
   */
  pageIndex = 0;
  pageSize: number = PURCHASE_DEFAULT_PAGE_SIZE;
  readonly pageSizeOptions: readonly number[] = PURCHASE_PAGE_SIZE_OPTIONS;

  /**
   * Whether a support session is active (issue #657, extending #554): Add Purchase and Delete are
   * hidden while it is, because a session grants `PurchasingOperate` - the capability this page
   * would otherwise gate those on - and the backend refuses the write itself only by its read-only
   * method rule, never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  // Purchase documents are protected by the API, so they are fetched through HttpClient (which
  // attaches the bearer token) and rendered from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  private thumbnailUrls = new Map<number, string>();
  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private purchaseService: PurchaseService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.objectUrls.releaseAll();
    this.sessionActiveSubscription.unsubscribe();
  }

  load(): void {
    this.purchaseService.getAll().subscribe((r) => {
      this.purchases = r;
      // A refresh (Delete, Add document) can shorten the list: stay on the same page while it
      // still exists, and never land on an empty one past the end (issue #1209).
      this.pageIndex = clampPageIndex(this.pageIndex, this.purchases.length, this.pageSize);
      this.loadThumbnails();
    });
  }

  /** The purchases on the current page, in the API's own order - nothing here re-sorts or re-filters. */
  get pagedPurchases(): Purchase[] {
    return pageSlice(this.purchases, this.pageIndex, this.pageSize);
  }

  onPageIndexChange(index: number): void {
    this.pageIndex = clampPageIndex(index, this.purchases.length, this.pageSize);
    this.loadThumbnails();
  }

  /** A new page size is a fresh view of the same list, so it returns to page 1. */
  onPageSizeChange(size: number): void {
    if (!this.pageSizeOptions.includes(size)) {
      return;
    }
    this.pageSize = size;
    this.pageIndex = 0;
    this.loadThumbnails();
  }

  getValidation(purchase: Purchase): PurchaseValidation | null {
    return this.purchaseService.getValidationFor(purchase.id);
  }

  /**
   * The purchase's input GST exactly as the API calculated it. Nothing here derives GST from an
   * amount: a `null` summary is shown as unavailable rather than as `$0`.
   */
  gstSummary(purchase: Purchase): PurchaseGstSummary | null {
    return this.purchaseService.getGstSummaryFor(purchase.id);
  }

  /** How one stored component's classification is named. */
  classificationLabel(value: GstClassification | null | undefined): string {
    return gstClassificationLabel(value);
  }

  /** Whether a delivery or package charge exists at all; an absent charge has no classification. */
  hasCharge(amount: number | null | undefined): boolean {
    return isChargePresent(amount);
  }

  thumbnailUrl(purchase: Purchase): string | null {
    return this.thumbnailUrls.get(purchase.id) ?? null;
  }

  openDocument(purchase: Purchase): void {
    this.purchaseService.getFile(purchase.id).subscribe({
      next: (blob) => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: (err) => console.error('Failed to open purchase document', err)
    });
  }

  /**
   * Whether this purchase has a supporting document at all (issue #976). The four metadata values
   * are all present or all absent, so the stored name alone answers it.
   */
  hasDocument(purchase: Purchase): boolean {
    return purchase.storedFileName !== null && purchase.storedFileName !== undefined;
  }

  /**
   * Thumbnails for the current page's image-document purchases only (issue #1209): turning a page
   * or changing the size releases every object URL this drew for the previous page before fetching
   * the new page's, so neither a stale thumbnail nor a leaked blob survives the move.
   */
  private loadThumbnails(): void {
    this.objectUrls.releaseAll();
    this.thumbnailUrls = new Map<number, string>();
    this.pagedPurchases
      .filter((purchase) => this.isImage(purchase))
      .forEach((purchase) =>
        this.purchaseService.getFile(purchase.id).subscribe({
          next: (blob) => this.thumbnailUrls.set(purchase.id, this.objectUrls.create(blob)),
          error: (err) => console.error('Failed to load purchase document', err)
        })
      );
  }

  isImage(purchase: Purchase): boolean {
    // A purchase with no document has no content type either, and is no kind of image (issue #976).
    return purchase.contentType?.startsWith('image/') ?? false;
  }

  remove(purchase: Purchase): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
    if (!confirm(`Delete purchase "${purchase.title}"?`)) return;
    this.purchaseService.delete(purchase.id).subscribe(() => this.load());
  }

  formatSize(bytes: number | null): string {
    // Only ever asked for a purchase that has a document; a null size is no size at all and must
    // not be shown as "0 B", which would be a false statement about a document that is not there.
    if (bytes === null || bytes === undefined) return '';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}
