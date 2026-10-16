import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { ToastService } from '../services/toast.service';
import { SupportSessionService, SupportSessionState } from '../support-session/support-session.service';

/**
 * What a tab shows instead of a business page while the administrator's support session is active
 * on the server and this tab holds no id for it (issue #668, `{ kind: 'unresolved' }`).
 *
 * Such a tab cannot present `X-Support-Session`, so every request it made would resolve the
 * administrator's **own** membership. Issue #553's banner would nevertheless have labelled that
 * page "Viewing as &lt;business&gt;" - an administrator's own data presented as another business's
 * support view, which is the one thing this state exists to prevent. So the shell renders this
 * explanation in place of the routed page (`AppComponent`), the "Viewing as" banner stays off
 * (`SupportSessionBannerComponent` renders only `viewing`), and the only action offered is the one
 * that returns the tab to ordinary context: **End session**, the same actor-resolved
 * `POST .../active/end` the banner uses, which needs no session id either.
 *
 * It deliberately offers no "resume" or "re-open" action. The id is memory-only by design and the
 * active read never answers it (docs/architecture.md § Support sessions: the store and the control
 * endpoints), so the honest ways forward are the tab the session was started in, whose `started`
 * broadcast would still resolve this one, or ending this session and starting a new one from the
 * Businesses page's explicit **View as this business** flow.
 */
@Component({
  selector: 'app-support-session-blocked',
  standalone: true,
  template: `
    @if (state.kind === 'unresolved') {
      <div class="alert alert-warning" role="status" data-testid="support-session-unresolved">
        <p class="alert-title">This tab is not part of the support session.</p>
        <p class="mt-1">
          A read-only support session for {{ businessLabel }} is running on your account, but this tab
          does not hold the session's identifier: it is kept in memory only, and a reloaded or newly
          opened tab is never given it again.
        </p>
        <p class="mt-1">
          So nothing is shown here. This tab would read your own businesses rather than
          {{ businessLabel }}, and it will not present that as a support view. Carry on in the tab the
          session was started in, or end the session and start a new one from Super Admin →
          Businesses → View as this business.
        </p>
        <p class="mt-2">
          <button type="button" class="btn btn-secondary btn-sm" [disabled]="ending" (click)="endSession()">
            {{ ending ? 'Ending…' : 'End session' }}
          </button>
        </p>
      </div>
    }
  `
})
export class SupportSessionBlockedComponent implements OnInit, OnDestroy {
  state: SupportSessionState = { kind: 'inactive' };
  ending = false;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly supportSession: SupportSessionService,
    private readonly toast: ToastService
  ) {}

  /** The business the session names, or a neutral phrase when the active read answered no name. */
  get businessLabel(): string {
    return this.state.kind === 'inactive' ? '' : this.state.businessName || 'another business';
  }

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
        // The explanation stays on screen: a failed end leaves the session active on the server, so
        // this tab is still one that must not render business data.
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
