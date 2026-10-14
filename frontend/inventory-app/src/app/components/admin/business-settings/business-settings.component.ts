import { Component, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BusinessSettings, BusinessSettingsService } from '../../../services/business-settings.service';
import { LegacyBusinessSettingsPromptComponent } from '../../shared/legacy-business-settings-prompt/legacy-business-settings-prompt.component';
import { BusinessSettingsFormComponent } from './business-settings-form.component';

export const LOAD_ERROR_MESSAGE = 'Business settings could not be loaded. Reload the page to try again.';

/**
 * The Admin → Business Settings page (issue #549, one slice of #511 under #328). The route
 * already requires `Business.Manage` (`app.routes.ts`), which only an Owner holds.
 *
 * Per docs/architecture.md § Page composition boundary (issue #191) this stays a composition
 * boundary: it makes the page's one `BusinessSettingsService.get()` read, holds that read's own
 * loading/error state, and lays out the legacy-profile prompt and
 * {@link BusinessSettingsFormComponent} through `[settings]`. The edit workflow - the form, its
 * validation, the time zone confirmation, the save and the shell refresh before success - belongs
 * to that component, so no validation, confirmation or save rule exists on this page. It is the
 * same split as `CostingRepairPageComponent`/`CostingRepairComponent` and
 * `AvcoTransitionComponent`/`AvcoTransitionWorkflowComponent`.
 */
@Component({
  selector: 'app-business-settings',
  standalone: true,
  imports: [RouterLink, LegacyBusinessSettingsPromptComponent, BusinessSettingsFormComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Business Settings</h1>
          <p class="page-subtitle">Your business's own name, contact email, ABN, GST registration and time zone.</p>
        </div>
      </header>

      <app-legacy-business-settings-prompt></app-legacy-business-settings-prompt>

      @if (loading) {
        <p class="text-sm value-muted" data-testid="business-settings-loading">Loading business settings…</p>
      } @else if (loadError) {
        <p class="text-sm text-red-600" data-testid="business-settings-load-error">{{ loadErrorMessage }}</p>
      } @else if (loadedSettings) {
        <app-business-settings-form [settings]="loadedSettings"></app-business-settings-form>
      }
    </div>
  `
})
export class BusinessSettingsComponent implements OnInit {
  readonly loadErrorMessage = LOAD_ERROR_MESSAGE;

  loading = true;
  loadError = false;

  /** The settings the form edits, composed in only once the read has answered. */
  loadedSettings: BusinessSettings | null = null;

  constructor(private readonly businessSettings: BusinessSettingsService) {}

  ngOnInit(): void {
    this.businessSettings.get().subscribe({
      next: (settings) => {
        this.loadedSettings = settings;
        this.loading = false;
      },
      // Not known is never "no settings": composing an empty, savable form over a failed read
      // would let an unrelated save overwrite settings this page never actually saw.
      error: () => {
        this.loading = false;
        this.loadError = true;
      }
    });
  }
}
