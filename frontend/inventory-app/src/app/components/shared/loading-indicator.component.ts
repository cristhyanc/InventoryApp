import { Component } from '@angular/core';
import { AsyncPipe, NgIf } from '@angular/common';
import { LoadingService } from '../../services/loading.service';

@Component({
  selector: 'app-loading-indicator',
  standalone: true,
  imports: [NgIf, AsyncPipe],
  template: `
    <div
      class="loading-bar rounded-full bg-md-dark-gradient px-4 py-2 text-md-body text-white shadow-md"
      role="status"
      aria-live="polite"
      *ngIf="loadingService.loading$ | async"
    >
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
