import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { ProviderAvailabilityService } from '../../../provider/provider-availability.service';

/**
 * The shared "Nayax isn't fully connected" banner (issue #539), composed into every screen that
 * depends on a live Nayax read: Machines, Sites and the Sync Restock workflow. The Pick List no
 * longer shows it; it states its own unavailability when Nayax refuses it.
 *
 * It shows nothing while the connection is `Ready` or while its status could not be read at all - a
 * page's own data load already reports its own failure, and a banner built on a probe nobody could
 * answer would only add a second, possibly wrong, explanation beside it. Nothing here decides what
 * "not fully connected" means: the server's published `NayaxConnectionStatusName` is the one source
 * for that, exactly as the connection wizard reads it
 * (see docs/architecture.md § Nayax connection status banners).
 *
 * **It reads the shared signal rather than probing for itself** (issue #728). Several of these
 * banners can be on one screen beside a filter and a form that need the same answer;
 * `ProviderAvailabilityService` reads `GET …/nayax-connection/status` once per session, so the
 * banner and everything beside it state one answer instead of three probes' worth.
 *
 * **It decides nothing about who may fix it.** `Integration.Manage` is the capability `GET
 * /api/me/access` reports for an Owner alone (`Inventory.Application.Access.RoleCapabilities`), so
 * it is also what decides whether the setup link is shown. It starts `true`, the same fail-open
 * default `NayaxConnectionWizardComponent` uses, so an unanswered capability read never hides a
 * link an Owner's role would show once the identity is known; the wizard route
 * (`/admin/nayax-settings`) is independently guarded by the same capability, so a stale `true` here
 * only ever offers a link a Manager or Operator could not use, never a boundary.
 */
@Component({
  selector: 'app-nayax-status-banner',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    @if (visible) {
      <div class="alert alert-warning mb-4" role="status" data-testid="nayax-status-banner">
        @if (canManage) {
          <p class="alert-title">Nayax isn't fully connected for this business.</p>
          <p class="mt-1">
            <a
              routerLink="/admin/nayax-settings"
              class="btn-link"
              data-testid="nayax-status-banner-link"
            >Finish the Nayax connection</a>
          </p>
        } @else {
          <p class="alert-title" data-testid="nayax-status-banner-no-link">
            Nayax isn't fully connected for this business. Ask a business Owner to finish the Nayax connection.
          </p>
        }
      </div>
    }
  `
})
export class NayaxStatusBannerComponent implements OnInit, OnDestroy {
  /** Whether the signed-in member's role may reach the wizard. See the fail-open note above. */
  canManage = true;

  /** Shown only once the business's connection status has been read and is not `Ready`. */
  visible = false;

  private readonly manageAccess: Subscription;
  private statusSubscription: Subscription | null = null;

  constructor(private readonly provider: ProviderAvailabilityService, access: AccessService) {
    this.manageAccess = access.allows('Integration.Manage').subscribe((allowed) => (this.canManage = allowed));
  }

  ngOnInit(): void {
    // A status that cannot be read is "not known", never "not ready": the shared signal reports it
    // as `null`, and showing a banner over a failed probe would risk telling an Owner whose
    // connection is fine to go fix something.
    this.statusSubscription = this.provider
      .load()
      .subscribe((availability) => (this.visible = availability.status !== null && availability.status !== 'Ready'));
  }

  ngOnDestroy(): void {
    this.manageAccess.unsubscribe();
    this.statusSubscription?.unsubscribe();
  }
}
