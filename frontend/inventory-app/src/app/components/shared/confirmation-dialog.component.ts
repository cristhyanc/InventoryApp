import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-confirmation-dialog',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="modal-backdrop" (click)="cancelChoice()">
      <div class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg" (click)="$event.stopPropagation()">
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800">{{ title }}</h2>
        <p class="mb-5 text-md-body text-md-gray-600">{{ message }}</p>
        <div class="confirm-actions">
          <button class="btn btn-secondary btn-sm" type="button" (click)="cancelChoice()">{{ cancelLabel }}</button>
          <button class="btn btn-danger btn-sm" type="button" (click)="confirmChoice()">{{ confirmLabel }}</button>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      .modal-backdrop {
        position: fixed;
        inset: 0;
        background: rgba(15, 23, 42, 0.5);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 50;
        padding: 1rem;
      }

      .confirm-modal {
        max-width: 420px;
        width: 100%;
      }

      .confirm-actions {
        display: flex;
        justify-content: flex-end;
        gap: 0.75rem;
      }
    `
  ]
})
export class ConfirmationDialogComponent {
  @Input() title = 'Confirm';
  @Input() message = 'Are you sure?';
  @Input() confirmLabel = 'Confirm';
  @Input() cancelLabel = 'Cancel';
  @Output() readonly confirmed = new EventEmitter<void>();
  @Output() readonly canceled = new EventEmitter<void>();

  confirmChoice(): void {
    this.confirmed.emit();
  }

  cancelChoice(): void {
    this.canceled.emit();
  }
}
