import { HttpErrorResponse } from '@angular/common/http';
import { Component, Input, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subject, forkJoin, map, switchMap, takeUntil } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import {
  BusinessApplicationDraft,
  BusinessApplicationErrors,
  BusinessApplicationField,
  MAX_BUSINESS_NAME_LENGTH,
  availableTimeZones,
  toBusinessApplicationRequest,
  validateBusinessApplication
} from '../../../access/business-application-rules';
import { BusinessService } from '../../../services/business.service';
import { BusinessSettings, BusinessSettingsService } from '../../../services/business-settings.service';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';

export const GENERIC_SAVE_ERROR_MESSAGE = "Your business settings couldn't be saved. Try again.";

export const TIME_ZONE_CHANGE_CONFIRMATION_TITLE = 'Change the business time zone?';

/** The acceptance criteria's required warning (issue #549), read by the confirmation dialog. */
export const TIME_ZONE_CHANGE_CONFIRMATION_MESSAGE =
  "Business days and report date boundaries for all your data - past and future - will be " +
  'calculated in the new time zone from now on, including its own daylight-saving transitions. ' +
  'No stored record changes, but a figure already shown for one business day may move to an ' +
  'adjacent day once the business is in the new zone.';

/** Reads the sentence an API refusal carries, falling back to one this component owns. */
function saveErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return GENERIC_SAVE_ERROR_MESSAGE;
  }

  const body = error.error as { message?: unknown } | null;
  if (error.status === 400 && typeof body?.message === 'string' && body.message.trim().length > 0) {
    return body.message;
  }

  return GENERIC_SAVE_ERROR_MESSAGE;
}

/**
 * The Business Settings edit workflow (issue #549, one slice of #511 under #328): the fields
 * `GET`/`PUT /api/business/settings` carry (issue #548) - name, contact email, ABN, GST
 * registration and time zone - for an Owner to change. It is the dedicated feature component the
 * page composition boundary requires (docs/architecture.md § Page composition boundary, issue
 * #191): the form, its draft/touched/validation state, the time zone confirmation, the save and
 * the shell refresh all live here, and `BusinessSettingsComponent` only reads the settings and
 * lays this component out.
 *
 * **The settings arrive as an input, and each one is the new baseline**: the page's own read
 * supplies the first, and a successful save supplies every later one, so the value a time zone
 * change is detected against is always the one the API last confirmed.
 *
 * **Inline validation is the API's**, reused rather than restated: `business-application-rules.ts`
 * mirrors `BusinessSettingsPolicy`, and the application form validates the exact same five
 * settings for the exact same reason, so this component validates and submits through the same
 * functions instead of a second copy that could disagree with the first.
 *
 * **Changing the time zone needs an explicit confirmation** (issue #499's warning, which this is
 * the workflow that has to show): submitting with a time zone different from the one the business
 * was last read with shows {@link TIME_ZONE_CHANGE_CONFIRMATION_MESSAGE} and saves only once it is
 * confirmed. Every other change saves immediately - the warning is about the zone, not about
 * editing the form.
 *
 * **A successful save refreshes the shell before reporting success** (issue #549's acceptance
 * criterion): `BusinessService.refresh()` re-reads `GET /api/business/current` and republishes
 * the time zone date/time formatting reads everywhere, and `AccessService.refresh()` re-reads
 * `GET /api/me/access` so the sidebar's business name and time zone follow too - both before
 * `saved` is set, so nothing can show success while the rest of the shell is still showing the
 * zone just replaced. A save that fails updates neither and shows the API's own refusal sentence.
 * That ordering is why the save stays here rather than being emitted to the page to perform:
 * success is this component's own message to show, and it may not appear before both refreshes
 * have answered. Nothing on the page has to coordinate with a save, so there is no `@Output` for
 * one - the same shape as `<app-costing-repair [products]="products">`.
 */
@Component({
  selector: 'app-business-settings-form',
  standalone: true,
  imports: [FormsModule, ConfirmationDialogComponent],
  template: `
    <section class="card">
      <div class="card-body">
        @if (saved) {
          <p class="mb-3 text-sm text-green-700" data-testid="save-success">Business settings saved.</p>
        }
        @if (submitError) {
          <p class="mb-3 text-sm text-red-600" data-testid="submit-error">{{ submitError }}</p>
        }

        <form class="flex flex-col gap-4" (ngSubmit)="submit()" novalidate>
          <div class="field">
            <label class="field-label" for="businessName">Business name</label>
            <input
              id="businessName"
              name="name"
              [(ngModel)]="draft.name"
              (blur)="touch('name')"
              [attr.maxlength]="maxNameLength"
              autocomplete="organization"
            />
            @if (shownError('name'); as message) {
              <p class="mt-1 text-sm text-red-600" data-testid="error-name">{{ message }}</p>
            }
          </div>

          <div class="field">
            <label class="field-label" for="businessContactEmail">Contact email</label>
            <input
              id="businessContactEmail"
              name="contactEmail"
              type="email"
              [(ngModel)]="draft.contactEmail"
              (blur)="touch('contactEmail')"
              autocomplete="email"
            />
            @if (shownError('contactEmail'); as message) {
              <p class="mt-1 text-sm text-red-600" data-testid="error-contactEmail">{{ message }}</p>
            }
          </div>

          <div class="field">
            <label class="field-label" for="businessAbn">ABN</label>
            <input
              id="businessAbn"
              name="abn"
              inputmode="numeric"
              [(ngModel)]="draft.abn"
              (blur)="touch('abn')"
              placeholder="Optional, e.g. 51 824 753 556"
            />
            @if (shownError('abn'); as message) {
              <p class="mt-1 text-sm text-red-600" data-testid="error-abn">{{ message }}</p>
            }
          </div>

          <fieldset class="field">
            <legend class="field-label">Registered for GST</legend>
            <div class="flex items-center gap-4">
              <label class="inline-flex items-center gap-2">
                <input
                  type="radio"
                  name="gstRegistered"
                  [value]="true"
                  [(ngModel)]="draft.gstRegistered"
                  (change)="touch('gstRegistered')"
                  data-testid="gst-yes"
                />
                Yes
              </label>
              <label class="inline-flex items-center gap-2">
                <input
                  type="radio"
                  name="gstRegistered"
                  [value]="false"
                  [(ngModel)]="draft.gstRegistered"
                  (change)="touch('gstRegistered')"
                  data-testid="gst-no"
                />
                No
              </label>
            </div>
            @if (shownError('gstRegistered'); as message) {
              <p class="mt-1 text-sm text-red-600" data-testid="error-gstRegistered">{{ message }}</p>
            }
          </fieldset>

          <div class="field">
            <label class="field-label" for="businessTimeZone">Time zone</label>
            <input
              id="businessTimeZone"
              name="timeZoneId"
              list="business-settings-time-zones"
              [(ngModel)]="draft.timeZoneId"
              (blur)="touch('timeZoneId')"
              autocomplete="off"
              placeholder="Search, e.g. Australia/Sydney"
            />
            <datalist id="business-settings-time-zones">
              @for (zone of timeZones; track zone) {
                <option [value]="zone"></option>
              }
            </datalist>
            @if (shownError('timeZoneId'); as message) {
              <p class="mt-1 text-sm text-red-600" data-testid="error-timeZoneId">{{ message }}</p>
            }
          </div>

          <div class="page-actions">
            <button type="submit" class="btn btn-primary" [disabled]="saving">
              {{ saving ? 'Saving...' : 'Save' }}
            </button>
          </div>
        </form>
      </div>
    </section>

    @if (pendingTimeZoneChange) {
      <app-confirmation-dialog
        [title]="timeZoneChangeConfirmationTitle"
        [message]="timeZoneChangeConfirmationMessage"
        confirmLabel="Change time zone"
        cancelLabel="Cancel"
        (confirmed)="confirmTimeZoneChange()"
        (canceled)="cancelTimeZoneChange()"
      ></app-confirmation-dialog>
    }
  `
})
export class BusinessSettingsFormComponent implements OnDestroy {
  readonly maxNameLength = MAX_BUSINESS_NAME_LENGTH;
  readonly timeZones: readonly string[] = availableTimeZones();
  readonly timeZoneChangeConfirmationTitle = TIME_ZONE_CHANGE_CONFIRMATION_TITLE;
  readonly timeZoneChangeConfirmationMessage = TIME_ZONE_CHANGE_CONFIRMATION_MESSAGE;

  saving = false;
  saved = false;
  submitError: string | null = null;
  pendingTimeZoneChange = false;

  draft: BusinessApplicationDraft = { name: '', contactEmail: '', abn: '', gstRegistered: null, timeZoneId: '' };

  /** The settings the page read - the baseline a time zone change is detected against. */
  @Input({ required: true })
  set settings(value: BusinessSettings) {
    this.applyLoadedSettings(value);
  }

  private loadedSettings: BusinessSettings | null = null;
  private readonly touched = new Set<BusinessApplicationField>();
  private submitAttempted = false;
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly businessSettings: BusinessSettingsService,
    private readonly business: BusinessService,
    private readonly access: AccessService
  ) {}

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /** The current rule violations, recomputed from the draft so they always match what is typed. */
  get errors(): BusinessApplicationErrors {
    return validateBusinessApplication(this.draft);
  }

  /** The message to show under a field: only once it has been left, or a submit was attempted. */
  shownError(field: BusinessApplicationField): string | null {
    return this.submitAttempted || this.touched.has(field) ? (this.errors[field] ?? null) : null;
  }

  touch(field: BusinessApplicationField): void {
    this.touched.add(field);
  }

  submit(): void {
    if (this.saving) {
      return;
    }

    this.submitAttempted = true;
    this.submitError = null;

    if (Object.keys(this.errors).length > 0) {
      return;
    }

    const changingTimeZone =
      this.loadedSettings !== null && this.draft.timeZoneId.trim() !== this.loadedSettings.timeZoneId;
    if (changingTimeZone) {
      this.pendingTimeZoneChange = true;
      return;
    }

    this.save();
  }

  confirmTimeZoneChange(): void {
    this.pendingTimeZoneChange = false;
    this.save();
  }

  cancelTimeZoneChange(): void {
    this.pendingTimeZoneChange = false;
  }

  private save(): void {
    this.saving = true;
    this.saved = false;
    this.submitError = null;

    this.businessSettings
      .save(toBusinessApplicationRequest(this.draft))
      .pipe(
        // The shell must reflect the new business name, time zone and capabilities before success
        // is shown, never after - see the class doc. Both refresh the same identity's own data, so
        // either order is safe; both are waited for regardless.
        switchMap((settings) =>
          forkJoin([this.business.refresh(), this.access.refresh()]).pipe(map(() => settings))
        ),
        takeUntil(this.destroying$)
      )
      .subscribe({
        next: (settings) => {
          this.applyLoadedSettings(settings);
          this.saving = false;
          this.saved = true;
        },
        error: (error: unknown) => {
          this.saving = false;
          this.submitError = saveErrorMessage(error);
        }
      });
  }

  private applyLoadedSettings(settings: BusinessSettings): void {
    this.loadedSettings = settings;
    this.draft = {
      name: settings.name,
      contactEmail: settings.contactEmail ?? '',
      abn: settings.abn ?? '',
      gstRegistered: settings.gstRegistered,
      timeZoneId: settings.timeZoneId
    };
  }
}
