import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { Purchase } from '../../../models/models';
import { PurchaseService } from '../../../services/purchase.service';
import { ToastService } from '../../../services/toast.service';

/** What a failed attach says when the API offered no sentence of its own. */
export const ATTACH_FAILED_MESSAGE = 'Could not add the document. Try again.';

/**
 * Adds a supporting document to a purchase that has none (issue #976, child of #965).
 *
 * <p>It is its own component rather than markup inside the Purchases list because it is its own
 * small workflow: a file to choose, a request to send, and a saving and error lifecycle of its own
 * on one row out of many - which is exactly the page-composition boundary of issue #191 (see
 * docs/architecture.md § Page composition boundary). The list composes it back in through
 * `@Input`/`@Output` and reloads when it reports a document was added.</p>
 *
 * <p>It is deliberately not a <em>replace</em>: it is only rendered for a purchase with no
 * document, and the API answers `409` if one arrives in the meantime - which is shown as the
 * server's own sentence rather than retried over the document somebody else attached.</p>
 */
@Component({
  selector: 'app-purchase-document-attach',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './purchase-document-attach.component.html'
})
export class PurchaseDocumentAttachComponent {
  @Input({ required: true }) purchase!: Purchase;

  /** Raised once the server has stored the document, so the list can reload and show it. */
  @Output() readonly attached = new EventEmitter<void>();

  selectedFile: File | null = null;
  saving = false;
  error = '';

  constructor(
    private readonly purchaseService: PurchaseService,
    private readonly toastService: ToastService
  ) {}

  /** The file input's id, unique per row so its label points at its own input. */
  get inputId(): string {
    return `purchase-document-${this.purchase.id}`;
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.length ? input.files[0] : null;
    this.error = '';
  }

  attach(): void {
    if (!this.selectedFile || this.saving) {
      return;
    }

    this.saving = true;
    this.error = '';
    this.purchaseService.attachDocument(this.purchase.id, this.selectedFile).subscribe({
      next: () => {
        this.saving = false;
        this.selectedFile = null;
        this.toastService.success('Document added.');
        this.attached.emit();
      },
      error: (failure: HttpErrorResponse) => {
        this.saving = false;
        // The API's own sentence where it sent one - the size and type rules, and the refusal to
        // replace a document that arrived in the meantime, are all worth reading as they are.
        this.error = typeof failure?.error === 'string' && failure.error.trim()
          ? failure.error
          : ATTACH_FAILED_MESSAGE;
      }
    });
  }
}
