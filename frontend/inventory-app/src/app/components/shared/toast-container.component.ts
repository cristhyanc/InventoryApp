import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AsyncPipe } from '@angular/common';
import { ToastService, ToastMessage } from '../../services/toast.service';

@Component({
  selector: 'app-toast-container',
  standalone: true,
  imports: [CommonModule, AsyncPipe],
  template: `
    <div class="toast-container">
      <div *ngFor="let toast of toastService.messages$ | async" class="toast" [ngClass]="toast.type">
        <div class="toast-header">
          <strong>{{ toast.title }}</strong>
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
        border-radius: 0.75rem;
        box-shadow: 0 14px 40px rgba(15, 23, 42, 0.18);
        overflow: hidden;
        color: #fff;
        padding: 0.85rem 1rem;
        animation: slide-in 220ms ease-out;
      }

      .toast.success { background: #16a34a; }
      .toast.error { background: #dc2626; }
      .toast.warning { background: #f59e0b; }
      .toast.info { background: #2563eb; }

      .toast-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 0.75rem;
        font-size: 0.95rem;
        margin-bottom: 0.4rem;
      }

      .toast-close {
        border: none;
        background: transparent;
        color: #fff;
        font-size: 1.1rem;
        cursor: pointer;
        line-height: 1;
      }

      .toast-body {
        font-size: 0.9rem;
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
}
