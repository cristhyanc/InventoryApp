import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { ToastService } from '../services/toast.service';
import { SupportSessionService, SupportSessionState } from '../support-session/support-session.service';

/**
 * The support-session banner (issue #553): "Viewing as &lt;business&gt; — read-only — ends at
 * &lt;time&gt;" with its own **End session** action, on every page while the Super Admin's support
 * session is active - started from the Businesses page's "View as this business" dialog, shared
 * with every other open tab, and gone the moment it ends, expires, or the server answers
 * `support_session_ended` on any call (`SupportSessionService`, which owns all of that; this
 * component only renders its state and asks it to end the session).
 *
 * **It renders only an effective support view** (`{ kind: 'viewing' }`, issue #668). A tab that
 * knows a session is running but holds no id for it cannot present the header, so its requests
 * resolve the administrator's own membership and this banner's "Viewing as" claim would be false
 * about everything on the page; such a tab renders `SupportSessionBlockedComponent` instead of a
 * page and this banner shows nothing.
 */
@Component({
  selector: 'app-support-session-banner',
  standalone: true,
  imports: [CommonModule, DatePipe],
  template: `
    @if (state.kind === 'viewing') {
      <div class="support-session-banner" role="status">
        <span>Viewing as {{ state.businessName }} — read-only — ends at {{ state.expiresAtUtc | date: 'shortTime' }}</span>
        <button type="button" class="btn btn-secondary btn-sm" [disabled]="ending" (click)="endSession()">
          {{ ending ? 'Ending…' : 'End session' }}
        </button>
      </div>
    }
  `,
  styles: [
    `
      .support-session-banner {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: center;
        gap: 0.75rem;
        padding: 0.5rem 1rem;
        background-color: rgb(251 140 0 / 0.15);
        color: rgb(138 75 0);
        font-size: 0.875rem;
        font-weight: 500;
        text-align: center;
      }
    `
  ]
})
export class SupportSessionBannerComponent implements OnInit, OnDestroy {
  state: SupportSessionState = { kind: 'inactive' };
  ending = false;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly supportSession: SupportSessionService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.supportSession.state$.pipe(takeUntil(this.destroying$)).subscribe((state) => (this.state = state));
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  endSession(): void {
    this.ending = true;
    this.supportSession.end().subscribe({
      next: () => {
        this.ending = false;
      },
      error: (err: unknown) => {
        this.ending = false;
        this.toast.error(extractErrorMessage(err, 'Unable to end the support session.'));
      }
    });
  }
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
