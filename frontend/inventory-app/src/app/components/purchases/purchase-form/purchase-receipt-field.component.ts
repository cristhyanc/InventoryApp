import { Component, ElementRef, EventEmitter, Input, OnDestroy, Output, ViewChild } from '@angular/core';
import { IconComponent } from '../../shared/icon.component';
import { ObjectUrlCache } from '../../shared/object-url-cache';

/** The saved receipt's name and size, as the purchase read reports them. */
export interface StoredReceipt {
  fileName: string;
  fileSizeBytes: number | null;
}

/**
 * The compact optional receipt control of the purchase form (issue #1029).
 *
 * A file the person chose but has not saved yet can be previewed, replaced or removed freely: none
 * of that touches the server, and the page sends it through the existing upload paths only when the
 * purchase is saved. A receipt the purchase already holds can be previewed; it cannot be replaced or
 * removed here, because the purchases API offers no replace or delete for a stored document (its
 * attach endpoint refuses a purchase that already has one), and this change adds no endpoint. So
 * opening Edit, or cancelling it, never deletes anything.
 *
 * It validates nothing itself: the API's size and type rules decide, as they always have.
 */
@Component({
  selector: 'app-purchase-receipt-field',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './purchase-receipt-field.component.html'
})
export class PurchaseReceiptFieldComponent implements OnDestroy {
  /** The file chosen in this form and not saved yet. */
  @Input() pendingFile: File | null = null;

  /** The receipt the saved purchase already holds, or `null` when it has none. */
  @Input() stored: StoredReceipt | null = null;

  @Input() inputId = 'purchaseReceipt';
  @Input() disabled = false;

  /** A newly chosen file, or `null` when the chosen one was removed. */
  @Output() readonly pendingFileChange = new EventEmitter<File | null>();

  /** The person asked to see the saved receipt; the page fetches it through the authorized API. */
  @Output() readonly previewStored = new EventEmitter<void>();

  @ViewChild('fileInput', { static: true }) private fileInput!: ElementRef<HTMLInputElement>;

  private readonly objectUrls = new ObjectUrlCache();

  ngOnDestroy(): void {
    this.objectUrls.releaseAll();
  }

  /** Attach and Replace both open the same file chooser. */
  chooseFile(): void {
    if (this.disabled) return;
    this.fileInput.nativeElement.click();
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.length ? input.files[0] : null;
    // Cleared so choosing the same file again after a Remove still reports a change.
    input.value = '';
    if (file) this.pendingFileChange.emit(file);
  }

  removePending(): void {
    this.pendingFileChange.emit(null);
  }

  previewPending(): void {
    if (!this.pendingFile) return;
    window.open(this.objectUrls.create(this.pendingFile), '_blank', 'noopener');
  }

  /** A size as a person reads it: bytes, KB or MB. */
  formatSize(bytes: number | null | undefined): string {
    if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return '';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}
