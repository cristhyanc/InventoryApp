import { Component } from '@angular/core';
import { AsyncPipe, NgIf } from '@angular/common';
import { LoadingService } from '../../services/loading.service';

@Component({
  selector: 'app-loading-indicator',
  standalone: true,
  imports: [NgIf, AsyncPipe],
  template: `
    <div class="loading-bar" role="status" aria-live="polite" *ngIf="loadingService.loading$ | async">
      <span class="spinner" aria-hidden="true"></span>
      <span>Loading...</span>
    </div>
  `,
  styles: [
    `
      .loading-bar {
        position: fixed;
        top: 1rem;
        left: 50%;
        transform: translateX(-50%);
        display: flex;
        align-items: center;
        gap: 0.6rem;
        background: #1e293b;
        color: #fff;
        padding: 0.5rem 1rem;
        border-radius: 9999px;
        box-shadow: 0 14px 40px rgba(15, 23, 42, 0.25);
        font-size: 0.85rem;
        z-index: 200;
      }

      .spinner {
        width: 0.9rem;
        height: 0.9rem;
        border: 2px solid rgba(255, 255, 255, 0.35);
        border-top-color: #fff;
        border-radius: 50%;
        animation: loading-spin 700ms linear infinite;
      }

      @keyframes loading-spin {
        to { transform: rotate(360deg); }
      }
    `
  ]
})
export class LoadingIndicatorComponent {
  constructor(public loadingService: LoadingService) {}
}
