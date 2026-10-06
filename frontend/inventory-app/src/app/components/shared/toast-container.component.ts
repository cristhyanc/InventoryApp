import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AsyncPipe } from '@angular/common';
import { ToastService, ToastType } from '../../services/toast.service';

@Component({
  selector: 'app-toast-container',
  standalone: true,
  imports: [CommonModule, AsyncPipe],
  template: `
    <div class="toast-container">
      <div *ngFor="let toast of toastService.messages$ | async" class="toast alert shadow-md" [ngClass]="alertClassFor(toast.type)">
        <div class="toast-header">
          <strong class="alert-title">{{ toast.title }}</strong>
          <button type="button" class="toast-close" (click)="toastService.remove(toast.id)">×</button>
        </div>
        <div class="toast-body">{{ toast.message }}</div>
      </div>
    </div>
  `,
  styles: [
    `
      .toast-container {
        position: fixed;
        top: 1rem;
        right: 1rem;
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
        z-index: 100;
        max-width: 320px;
      }

      .toast {
        animation: slide-in 220ms ease-out;
      }

      .toast-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 0.75rem;
      }

      .toast-close {
        border: none;
        background: transparent;
        color: inherit;
        font-size: 1.1rem;
        cursor: pointer;
        line-height: 1;
      }

      @keyframes slide-in {
        from { opacity: 0; transform: translateY(-10px); }
        to { opacity: 1; transform: translateY(0); }
      }
    `
  ]
})
export class ToastContainerComponent {
  constructor(public toastService: ToastService) {}

  /** Maps a toast's type to the `.alert-*` class the #410 visual language defines for it. */
  alertClassFor(type: ToastType): string {
    return type === 'error' ? 'alert-danger' : `alert-${type}`;
  }
}
