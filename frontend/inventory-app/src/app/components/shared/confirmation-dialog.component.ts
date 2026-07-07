import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-confirmation-dialog',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="modal-backdrop" (click)="cancelChoice()">
      <div class="confirm-modal" (click)="$event.stopPropagation()">
        <h2>{{ title }}</h2>
        <p>{{ message }}</p>
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
        background: #fff;
        border-radius: 12px;
        padding: 1.5rem;
        max-width: 420px;
        width: 100%;
        box-shadow: 0 18px 60px rgba(15, 23, 42, 0.18);
        text-align: left;
      }

      .confirm-modal h2 {
        margin-top: 0;
        margin-bottom: 0.75rem;
        font-size: 1.15rem;
      }

      .confirm-modal p {
        margin-bottom: 1.25rem;
        color: #374151;
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
  @Output() confirmed = new EventEmitter<void>();
  @Output() canceled = new EventEmitter<void>();

  confirmChoice(): void {
    this.confirmed.emit();
  }

  cancelChoice(): void {
    this.canceled.emit();
  }
}
