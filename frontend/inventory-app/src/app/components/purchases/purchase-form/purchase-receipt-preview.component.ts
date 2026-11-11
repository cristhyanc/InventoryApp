import { Component, Input, OnDestroy } from '@angular/core';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ObjectUrlCache } from '../../shared/object-url-cache';
import { IconComponent } from '../../shared/icon.component';

/**
 * The scanned receipt itself, beside the lines it produced (issue #1166).
 *
 * <p>It exists so the pack maths and the prices on screen can be checked against the paper without
 * leaving the form: a photo is shown as an image, and a PDF is handed to the browser's own viewer
 * with an Open in a new tab fallback for a browser that has none. The file is the one the page
 * already holds - the prepared bytes that were scanned and that the save will upload - so nothing
 * is fetched, and the object URL it is shown through is revoked when this component goes away or
 * the file is replaced.</p>
 *
 * <p>It is presentational and reads nothing: no API call, no state of its own, and no opinion about
 * what the receipt says.</p>
 */
@Component({
  selector: 'app-purchase-receipt-preview',
  standalone: true,
  imports: [IconComponent],
  template: `
    @if (file) {
      <figure class="m-0 flex flex-col gap-2" data-testid="purchase-scanned-receipt">
        <figcaption class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-xs text-rr-muted">
          <span class="flex min-w-0 items-center gap-2">
            <app-icon name="receipt" [size]="16" />
            <span class="min-w-0 break-all">{{ file.name }}</span>
          </span>
          <!-- Beside the lines there is only so much room, so the paper itself is one click away. -->
          <button
            type="button"
            class="btn-link"
            data-testid="purchase-scanned-receipt-open-full"
            [attr.aria-label]="'Open ' + file.name + ' full size in a new tab'"
            (click)="openInNewTab()"
          >
            Open full size
          </button>
        </figcaption>
        @if (isImage) {
          <img
            [src]="url"
            [alt]="'The scanned receipt ' + file.name"
            class="max-h-[32rem] w-full rounded-lg border border-rr-line bg-rr-canvas object-contain"
            data-testid="purchase-scanned-receipt-image"
          />
        } @else {
          <object
            [data]="resourceUrl"
            type="application/pdf"
            class="h-[32rem] w-full rounded-lg border border-rr-line bg-rr-canvas"
            [attr.aria-label]="'The scanned receipt ' + file.name"
            data-testid="purchase-scanned-receipt-document"
          >
            <p class="m-0 p-3 text-sm text-rr-muted">
              This browser cannot show the receipt here.
              <button
                type="button"
                class="btn-link"
                data-testid="purchase-scanned-receipt-open"
                (click)="openInNewTab()"
              >
                Open it in a new tab
              </button>
            </p>
          </object>
        }
      </figure>
    }
  `
})
export class PurchaseReceiptPreviewComponent implements OnDestroy {
  /** The receipt to show, or `null` when there is none to show. */
  @Input()
  set file(file: File | null) {
    if (file === this.shown) return;

    // One object URL per file, and the previous one released: the browser keeps the bytes alive
    // until it is revoked.
    this.objectUrls.releaseAll();
    this.shown = file;
    this.url = file === null ? null : this.objectUrls.create(file);
    this.resourceUrl = this.url === null ? null : this.sanitizer.bypassSecurityTrustResourceUrl(this.url);
  }
  get file(): File | null {
    return this.shown;
  }

  /** The object URL the image is shown through, or `null` when there is nothing to show. */
  url: string | null = null;

  /**
   * The same URL as a resource URL, which is what `<object data>` is. It is this browser's own blob
   * of a file the person chose in this form a moment ago - never a URL from a response or a route.
   */
  resourceUrl: SafeResourceUrl | null = null;

  private shown: File | null = null;

  private readonly objectUrls = new ObjectUrlCache();

  constructor(private readonly sanitizer: DomSanitizer) {}

  ngOnDestroy(): void {
    this.objectUrls.releaseAll();
  }

  /** Whether the browser can simply render this receipt as a picture. */
  get isImage(): boolean {
    return this.shown !== null && this.shown.type.startsWith('image/');
  }

  openInNewTab(): void {
    if (this.url !== null) {
      window.open(this.url, '_blank', 'noopener');
    }
  }
}
