import { Component, OnDestroy, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  PlatformDiagnosticsAccess,
  PlatformDiagnosticsService
} from '../../../services/platform-diagnostics.service';
import { PlatformDiagnosticsQueryComponent } from './platform-diagnostics-query.component';

/**
 * The super-admin platform diagnostics page (issue #335), reached from the sidebar's Admin group
 * when the diagnostics API confirms platform-admin access, and reachable by URL regardless.
 *
 * **Entering the URL directly is safe, and this page relies on that rather than on being hidden.**
 * It asks `GET /api/admin/diagnostics/access` on arrival and offers the query form only when that
 * call succeeds, but the form is not the boundary either: `POST /api/admin/diagnostics/query`
 * authorizes every single request against the separately configured Entra `(tid, oid)` platform
 * administrator, so an unauthorized caller who reaches this route, or calls the endpoint without
 * it, reads nothing. Nothing here infers access from a token claim, a business role or a local
 * flag - there is no frontend role source that could diverge from the API's.
 *
 * Per docs/architecture.md § Page composition boundary (issue #191) the page is a composition
 * boundary: it resolves the capability, says what the scope is, and composes
 * `PlatformDiagnosticsQueryComponent`, which owns the statement, the submission and every outcome.
 */
@Component({
  selector: 'app-platform-diagnostics',
  standalone: true,
  imports: [RouterLink, PlatformDiagnosticsQueryComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Platform Diagnostics</h1>
          <p class="page-subtitle">
            Bounded, read-only data-integrity queries for the configured platform administrator.
          </p>
        </div>
      </header>

      <div class="alert alert-warning" role="alert" data-testid="diagnostics-scope-warning">
        <p class="alert-title">Cross-business scope, and read-only.</p>
        <p class="mt-1">
          A query on this page reads across <strong>every business</strong> on this platform, not
          only your own, over a small allow-listed set of identity and foreign-key columns. It
          cannot write, delete, change the schema or repair anything: the only operation offered
          here is a single read, and a repair is a separate, explicitly reviewed maintenance
          operation that is never reachable by submitting SQL. Every query is audited.
        </p>
      </div>

      @if (loading) {
        <p role="status" class="text-sm value-muted" data-testid="diagnostics-access-loading">
          Checking platform-admin access&hellip;
        </p>
      } @else if (denied) {
        <div class="alert alert-danger" role="alert" data-testid="diagnostics-access-denied">
          <p class="alert-title">You are not authorized to run platform diagnostics.</p>
          <p class="mt-1">
            Platform-admin access is a separately configured identity held outside the business
            data. It is not a business role, a membership or a permission that can be granted from
            this application, and no query can be run here. Nothing has been read.
          </p>
          <p class="mt-1"><a routerLink="/" class="btn-link text-sm">Return to the dashboard</a></p>
        </div>
      } @else if (errorMessage) {
        <div class="alert alert-danger" role="alert" data-testid="diagnostics-access-error">
          <p class="alert-title">{{ errorMessage }}</p>
          <p class="mt-1">
            Platform-admin access could not be confirmed, so no query form is offered. Reload the
            page to try again.
          </p>
        </div>
      } @else if (access) {
        <app-platform-diagnostics-query [limits]="access.limits" />
      }
    </div>
  `
})
export class PlatformDiagnosticsComponent implements OnInit, OnDestroy {
  loading = true;
  access: PlatformDiagnosticsAccess | null = null;
  denied = false;
  errorMessage: string | null = null;

  private accessSubscription: Subscription | null = null;

  constructor(private readonly diagnostics: PlatformDiagnosticsService) {}

  ngOnInit(): void {
    this.accessSubscription = this.diagnostics.access().subscribe({
      next: (access) => {
        this.loading = false;
        // `authorized` is always true in a 200 body; a false one is treated as a refusal rather
        // than trusted, so the form can never appear on an answer that did not confirm access.
        if (access.authorized === true) {
          this.access = access;
        } else {
          this.denied = true;
        }
      },
      error: (error: unknown) => {
        this.loading = false;
        const status = (error as { status?: number } | null)?.status;
        if (status === 401 || status === 403) {
          this.denied = true;
          return;
        }
        this.errorMessage = 'Platform diagnostics is unavailable.';
      }
    });
  }

  ngOnDestroy(): void {
    this.accessSubscription?.unsubscribe();
  }
}
