import { CommonModule } from '@angular/common';
import { Component, OnDestroy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { BusinessSettingsService, isLegacyBusinessProfile } from '../../../services/business-settings.service';

/**
 * The dismissible "complete your business profile" prompt (issue #549), composed into the
 * Dashboard and the Business Settings page: it asks an Owner to finish the business's contact
 * email and GST registration, which the migrated business predating the application form (issue
 * #540) never recorded.
 *
 * **It decides nothing about who may see it.** `Business.Manage` is the capability only an Owner
 * holds, and the same one that gates `GET /api/business/settings` itself - the request this
 * component makes is the business's own contact address and tax detail, not something every
 * member needs to render a page. Unlike `NayaxStatusBannerComponent`'s fail-open default, this
 * component does not read the settings at all while the capability is not affirmatively granted:
 * reading them is the Owner-only act, not merely showing a link to them, so a capability read that
 * has not answered yet - or answers `false` - fetches nothing and shows nothing. A page an Owner
 * already composes this into (the Business Settings page itself) gets no second, redundant
 * network effect from the capability settling late, because the fetch only ever starts once.
 *
 * **"Dismissible" is this component instance's own session**, not a stored preference: dismissing
 * it on the Dashboard does not hide it on the Business Settings page, and reloading either page
 * shows it again while the profile is still incomplete. Completing the profile is what actually
 * resolves it; dismissing only acknowledges it for the page currently on screen.
 */
@Component({
  selector: 'app-legacy-business-settings-prompt',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    @if (visible) {
      <div class="alert alert-warning mb-4" role="status" data-testid="legacy-business-settings-prompt">
        <p class="alert-title">Finish setting up this business.</p>
        <p class="mt-1">
          This business is missing its contact email or its GST registration.
          <a
            routerLink="/admin/business-settings"
            class="btn-link"
            data-testid="legacy-business-settings-prompt-link"
          >Complete business settings</a>
        </p>
        <button
          type="button"
          class="btn-link mt-1"
          data-testid="legacy-business-settings-prompt-dismiss"
          (click)="dismiss()"
        >Dismiss</button>
      </div>
    }
  `
})
export class LegacyBusinessSettingsPromptComponent implements OnDestroy {
  /** Shown only once an Owner's incomplete settings have been read, and not yet dismissed. */
  visible = false;

  private dismissed = false;
  private settingsSubscription: Subscription | null = null;
  private readonly manageAccess: Subscription;

  constructor(
    private readonly businessSettings: BusinessSettingsService,
    access: AccessService
  ) {
    this.manageAccess = access.allows('Business.Manage').subscribe((allowed) => {
      if (allowed) {
        this.ensureLoaded();
      } else {
        this.settingsSubscription?.unsubscribe();
        this.settingsSubscription = null;
        this.visible = false;
      }
    });
  }

  ngOnDestroy(): void {
    this.manageAccess.unsubscribe();
    this.settingsSubscription?.unsubscribe();
  }

  dismiss(): void {
    this.dismissed = true;
    this.visible = false;
  }

  private ensureLoaded(): void {
    if (this.settingsSubscription !== null) {
      return;
    }

    this.settingsSubscription = this.businessSettings.get().subscribe({
      next: (settings) => (this.visible = !this.dismissed && isLegacyBusinessProfile(settings)),
      // Not known is never "incomplete": guessing it from a failed read risks telling an Owner
      // whose profile is actually complete to go finish something.
      error: () => (this.visible = false)
    });
  }
}
