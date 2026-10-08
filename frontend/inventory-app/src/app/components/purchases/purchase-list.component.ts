import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { PurchaseService } from '../../services/purchase.service';
import { GstClassification, Purchase, PurchaseGstSummary, PurchaseValidation } from '../../models/models';
import { ObjectUrlCache } from '../shared/object-url-cache';
import { IconComponent } from '../shared/icon.component';
import { gstClassificationLabel, isChargePresent } from './gst-classification-options';

/**
 * The purchases list (issue #475: display only). Editing a purchase is the dedicated
 * `/purchases/:id/edit` page (`purchase-edit/purchase-edit-page.component.ts`), which the Edit
 * action links to; this component holds no edit form, no edit state and no update request, which is
 * what keeps the list compact.
 */
@Component({
  selector: 'app-purchase-list',
  standalone: true,
  imports: [CommonModule, RouterLink, IconComponent],
  templateUrl: './purchase-list.component.html'
})
export class PurchaseListComponent implements OnInit, OnDestroy {
  purchases: Purchase[] = [];

  // Purchase documents are protected by the API, so they are fetched through HttpClient (which
  // attaches the bearer token) and rendered from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  private thumbnailUrls = new Map<number, string>();

  constructor(private purchaseService: PurchaseService) {}

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.objectUrls.releaseAll();
  }

  load(): void {
    this.purchaseService.getAll().subscribe((r) => {
      this.purchases = r;
      this.loadThumbnails();
    });
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

  private loadThumbnails(): void {
    this.objectUrls.releaseAll();
    this.thumbnailUrls = new Map<number, string>();
    this.purchases
      .filter((purchase) => this.isImage(purchase))
      .forEach((purchase) =>
        this.purchaseService.getFile(purchase.id).subscribe({
          next: (blob) => this.thumbnailUrls.set(purchase.id, this.objectUrls.create(blob)),
          error: (err) => console.error('Failed to load purchase document', err)
        })
      );
  }

  isImage(purchase: Purchase): boolean {
    return purchase.contentType.startsWith('image/');
  }

  remove(purchase: Purchase): void {
    if (!confirm(`Delete purchase "${purchase.title}"?`)) return;
    this.purchaseService.delete(purchase.id).subscribe(() => this.load());
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}
