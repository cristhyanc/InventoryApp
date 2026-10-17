import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { PreviewRole } from '../role-preview/role-preview';
import { RolePreviewService } from '../role-preview/role-preview.service';

/**
 * The role-preview banner (issue #721): "Viewing as &lt;role&gt; (preview, read-only)" with its own
 * **Exit preview** action, on every page while the Super Admin is previewing a business role.
 *
 * It is deliberately a second banner rather than a variant of the support-session one, and it
 * looks different: the two answer different questions - *whose* data is on screen, and *as whom*
 * it is being answered - and an administrator can be in both at once, in which case both are
 * shown. `RolePreviewService` owns the preview itself; this component renders its state and asks
 * it to exit.
 */
@Component({
  selector: 'app-role-preview-banner',
  standalone: true,
  template: `
    @if (role !== null) {
      <div class="role-preview-banner" role="status">
        <span>Viewing as {{ role }} (preview, read-only)</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="exitPreview()">Exit preview</button>
      </div>
    }
  `,
  styles: [
    `
      .role-preview-banner {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: center;
        gap: 0.75rem;
        padding: 0.5rem 1rem;
        /* Blue, so it is told apart at a glance from the red support-session banner above it: the
           design system's info tokens (md-info, md-info-text in tailwind.config.js), the same pair
           the info badge and alert use. */
        background-color: rgba(26, 115, 232, 0.15);
        color: #1557b0;
        font-size: 0.875rem;
        font-weight: 500;
        text-align: center;
      }
    `
  ]
})
export class RolePreviewBannerComponent implements OnInit, OnDestroy {
  role: PreviewRole | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(private readonly rolePreview: RolePreviewService) {}

  ngOnInit(): void {
    this.rolePreview.role$.pipe(takeUntil(this.destroying$)).subscribe((role) => (this.role = role));
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  exitPreview(): void {
    this.rolePreview.exit();
  }
}
