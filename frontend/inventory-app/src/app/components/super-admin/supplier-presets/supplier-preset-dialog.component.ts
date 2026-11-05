import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  SUPPLIER_PRICE_CONNECTOR_LABELS,
  SUPPLIER_SEARCH_WEEKDAYS,
  SUPPLIER_SERVED_STATES,
  SupplierPriceConnector,
  SupplierSearchFrequency,
  SupplierSearchScheduleRequest,
  SupplierSearchWeekday,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from '../../../services/super-admin-supplier-presets.service';
import { SUPPLIER_URL_MAX_LENGTH, normalizeSupplierUrl } from '../../suppliers/supplier-url';
import {
  SUPPLIER_SEARCH_FREQUENCY_LABELS,
  SUPPLIER_SEARCH_WEEKDAY_NAMES,
  SupplierSearchSchedule,
  isSearchDateOnly,
  orderedSearchWeekdays,
  searchFreshnessHours
} from './supplier-search-schedule';

/** The server's own name rules, mirrored so the form can say what is wrong before it submits. */
export const SUPPLIER_PRESET_NAME_MAX_LENGTH = 200;
const NAME_REQUIRED_MESSAGE = 'A supplier preset needs a name.';
const NAME_TOO_LONG_MESSAGE = 'A supplier preset name must be 200 characters or fewer.';

/**
 * The supplier-master rules of issue #931, mirrored from
 * `Inventory.Domain.Suppliers.SupplierPresetMasterPolicy` with its own sentences, so the form reports
 * them in the field rather than after a round trip. The server stays the authority: it applies the
 * same rules to whatever arrives, and its refusal is what is shown when one gets through.
 */
export const SUPPLIER_PRESET_CONNECTOR_KEY_MAX_LENGTH = 100;
const SERVED_STATES_REQUIRED_MESSAGE =
  'A supplier preset must serve Australia nationwide, or at least one state or territory.';
const CONNECTOR_KEY_REQUIRED_MESSAGE = 'The Varnmill price connector needs a connector key.';
const CONNECTOR_KEY_TOO_LONG_MESSAGE = 'A price connector key must be 100 characters or fewer.';

/** The one route a connector key belongs to, which is the whole reason the field exists. */
const CONNECTOR_WITH_A_KEY = SupplierPriceConnector.Varnmill;

/**
 * The two search-schedule refusals of issue #1108 that a person can reach, in the server's own
 * sentences (`Inventory.Domain.PriceCatalogue.SupplierSearchSchedulePolicy`), so the field says what
 * is wrong instead of a round trip saying it.
 *
 * Its other two - a daily schedule naming weekdays, and a non-fortnightly schedule carrying a start
 * date - are not mirrored because they are made unreachable instead: the controls that would produce
 * them exist only for the frequencies that use them and are cleared when another frequency is
 * chosen, which is the same agreement the nationwide and connector-key rules already have here.
 */
const SEARCH_WEEKDAY_REQUIRED_MESSAGE =
  'A weekly or fortnightly search schedule names at least one day of the week.';
const FORTNIGHT_START_DATE_REQUIRED_MESSAGE =
  'A fortnightly search schedule needs a start date, which decides which weeks are searched.';

/**
 * The Add and Edit dialog of the Super Admin Supplier presets page (issues #848 and #931), over
 * `POST /api/super-admin/supplier-presets` and `PUT .../{id}`.
 *
 * It owns its own form values, submission state and error, which is what makes it a distinct
 * workflow per docs/architecture.md § Page composition boundary - the routed page composes it
 * through `[preset]` and reacts only to `(saved)`/`(canceled)`. A `null` `preset` is an Add and an
 * existing one is an Edit; the only difference is which call is made and whether the enabled toggle
 * is offered, because a new preset is always created enabled. The *Search prices* switch of issue
 * #1077 is offered on both, because "add this supplier but do not search it yet" is a real thing to
 * want of a route that charges per search.
 *
 * **Field validation mirrors the server and never replaces it.** The name rule is
 * `SupplierPresetPolicy`'s; the website rule is the one supplier-website helper there is,
 * `components/suppliers/supplier-url.ts`, which mirrors issue #847's
 * `Inventory.Application.Suppliers.SupplierUrlPolicy` - the same rule and the same sentences a
 * business's own supplier form shows; the served-state and connector rules are
 * `SupplierPresetMasterPolicy`'s. A Super Admin is therefore told about a mistyped address or a
 * missing connector key in the field rather than after a round trip, and when the server refuses
 * anyway, including the duplicate-name `409` that only it can decide, its own sentence is what is
 * shown.
 *
 * **Two of the server's refusals are made unreachable rather than mirrored**, which is the better
 * kind of agreement with a rule: the state checkboxes only exist while nationwide is off and are
 * cleared when it is switched back on, so "nationwide *and* NSW" cannot be submitted; and the
 * connector key field only exists for the one route that reads it and is cleared when another route
 * is chosen, so a key for a keyless route cannot be submitted either. The search schedule of issue
 * #1109 is offered on the same terms: the weekday checkboxes exist for Weekly and Fortnightly, the
 * fortnight start date for Fortnightly alone, and a frequency change clears what the new frequency
 * does not use.
 *
 * **The schedule (issues #1108 and #1109) is the supplier's own timetable, and the freshness limit
 * it derives is shown rather than offered.** The server derives `priceCheckFreshnessHours` from the
 * schedule and ignores anything a request says about it, so the dialog previews that answer with
 * `supplier-search-schedule.ts` - the one mirror of the backend rule, pinned to the shared test-case
 * file both sides read - and submits only the three fields the API accepts. The fortnight start date
 * is handled as the `YYYY-MM-DD` characters it arrives as, never through a `Date`: it names a day of
 * the Sydney collection calendar rather than a moment, and parsing it would let a Super Admin in
 * another zone move the on week by a day.
 */
@Component({
  selector: 'app-supplier-preset-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="modal-backdrop" role="dialog" aria-modal="true" [attr.aria-label]="title">
      <div class="confirm-modal rounded-md-dialog bg-white p-6 text-left shadow-md-lg">
        <h2 class="mb-3 text-md-card-title font-semibold text-md-gray-800">{{ title }}</h2>

        <label class="mb-1 block text-sm font-medium text-md-gray-700" for="preset-name">Name</label>
        <input
          id="preset-name"
          type="text"
          class="mb-1 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
          [attr.maxlength]="nameMaxLength"
          [attr.aria-invalid]="nameError !== null"
          [disabled]="submitting"
          [(ngModel)]="name"
          (ngModelChange)="onFieldChanged()"
        />
        @if (nameError) {
          <p class="mb-2 text-sm text-red-600" role="alert">{{ nameError }}</p>
        }

        <label class="mb-1 mt-2 block text-sm font-medium text-md-gray-700" for="preset-url">
          Website (optional)
        </label>
        <input
          id="preset-url"
          type="url"
          class="mb-1 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
          [attr.maxlength]="urlMaxLength"
          [attr.aria-invalid]="urlError !== null"
          [disabled]="submitting"
          placeholder="https://example.com"
          [(ngModel)]="url"
          (ngModelChange)="onFieldChanged()"
        />
        @if (urlError) {
          <p class="mb-2 text-sm text-red-600" role="alert">{{ urlError }}</p>
        }

        <fieldset class="mb-1 mt-3">
          <legend class="mb-1 block text-sm font-medium text-md-gray-700">Serves</legend>
          <label class="flex items-center gap-2 text-sm text-md-gray-700" for="preset-nationwide">
            <input
              id="preset-nationwide"
              type="checkbox"
              [disabled]="submitting"
              [(ngModel)]="servesNationwide"
              (ngModelChange)="onNationwideChanged()"
            />
            Australia nationwide
          </label>

          @if (!servesNationwide) {
            <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1">
              @for (state of servedStateCodes; track state) {
                <label class="flex items-center gap-2 text-sm text-md-gray-700" [attr.for]="stateInputId(state)">
                  <input
                    [id]="stateInputId(state)"
                    type="checkbox"
                    [disabled]="submitting"
                    [(ngModel)]="stateSelections[state]"
                    (ngModelChange)="onFieldChanged()"
                  />
                  {{ state }}
                </label>
              }
            </div>
          }
        </fieldset>
        @if (statesError) {
          <p class="mb-2 text-sm text-red-600" role="alert">{{ statesError }}</p>
        }

        <label
          class="mb-1 mt-3 flex items-center gap-2 text-sm text-md-gray-700"
          for="preset-prices-include-gst"
        >
          <input
            id="preset-prices-include-gst"
            type="checkbox"
            [disabled]="submitting"
            [(ngModel)]="pricesIncludeGst"
          />
          Prices include GST
        </label>

        <label class="mb-1 mt-3 block text-sm font-medium text-md-gray-700" for="preset-connector">
          Price connector
        </label>
        <select
          id="preset-connector"
          class="mb-1 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
          [disabled]="submitting"
          [(ngModel)]="connectorValue"
          (ngModelChange)="onConnectorChanged()"
        >
          <option value="">No price collection</option>
          @for (option of connectorOptions; track option.value) {
            <option [value]="option.value">{{ option.label }}</option>
          }
        </select>

        @if (connectorTakesAKey) {
          <label class="mb-1 mt-2 block text-sm font-medium text-md-gray-700" for="preset-connector-key">
            Connector key
          </label>
          <input
            id="preset-connector-key"
            type="text"
            class="mb-1 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
            [attr.maxlength]="connectorKeyMaxLength"
            [attr.aria-invalid]="connectorKeyError !== null"
            [disabled]="submitting"
            placeholder="coles"
            [(ngModel)]="connectorKey"
            (ngModelChange)="onFieldChanged()"
          />
        }
        @if (connectorKeyError) {
          <p class="mb-2 text-sm text-red-600" role="alert">{{ connectorKeyError }}</p>
        }

        <!-- Offered on an Add as well as an Edit, unlike the enabled switch below: a route that
             charges per search is one somebody may want recorded before it is switched on. -->
        <label
          class="mb-1 mt-3 flex items-center gap-2 text-sm text-md-gray-700"
          for="preset-search-prices"
        >
          <input
            id="preset-search-prices"
            type="checkbox"
            [disabled]="submitting"
            [(ngModel)]="searchPrices"
          />
          Search prices for this supplier
        </label>

        <!-- Always offered, whether or not the switch above is on (issue #1109): a supplier's
             timetable is worth setting before its searches start, and a paused supplier's schedule
             has to stay visible and editable rather than disappearing with the searching. -->
        <fieldset class="mb-1 mt-3">
          <legend class="mb-1 block text-sm font-medium text-md-gray-700">Search schedule</legend>

          <label class="mb-1 block text-sm font-medium text-md-gray-700" for="preset-search-frequency">
            Frequency
          </label>
          <select
            id="preset-search-frequency"
            class="mb-1 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
            [disabled]="submitting"
            [(ngModel)]="frequencyValue"
            (ngModelChange)="onFrequencyChanged()"
          >
            @for (option of frequencyOptions; track option.value) {
              <option [value]="option.value">{{ option.label }}</option>
            }
          </select>

          @if (scheduleNamesWeekdays) {
            <fieldset class="mt-2">
              <legend class="mb-1 block text-sm font-medium text-md-gray-700">Search days</legend>
              <div class="flex flex-wrap gap-x-4 gap-y-1">
                @for (weekday of searchWeekdays; track weekday) {
                  <label
                    class="flex items-center gap-2 text-sm text-md-gray-700"
                    [attr.for]="weekdayInputId(weekday)"
                  >
                    <input
                      [id]="weekdayInputId(weekday)"
                      type="checkbox"
                      [disabled]="submitting"
                      [(ngModel)]="daySelections[weekday]"
                      (ngModelChange)="onFieldChanged()"
                    />
                    {{ weekdayName(weekday) }}
                  </label>
                }
              </div>
            </fieldset>
            @if (searchDaysError) {
              <p class="mb-2 mt-1 text-sm text-red-600" role="alert">{{ searchDaysError }}</p>
            }
          }

          @if (scheduleNamesAFortnightStartDate) {
            <label class="mb-1 mt-2 block text-sm font-medium text-md-gray-700" for="preset-fortnight-start">
              Fortnight start date
            </label>
            <!-- A date-only value, bound as the YYYY-MM-DD characters the API sends and takes back:
                 no Date and no browser zone is involved, so a Super Admin outside Sydney cannot
                 shift the on week by a day. -->
            <input
              id="preset-fortnight-start"
              type="date"
              class="mb-1 w-full rounded-md-control border border-md-gray-300 p-2 text-sm"
              [attr.aria-invalid]="fortnightStartDateError !== null"
              [disabled]="submitting"
              [(ngModel)]="fortnightStartDate"
              (ngModelChange)="onFieldChanged()"
            />
            <p class="mb-1 text-sm text-md-gray-500">
              The week this date falls in, Monday to Sunday, is an on week. The weeks alternate on and off
              from there.
            </p>
            @if (fortnightStartDateError) {
              <p class="mb-2 text-sm text-red-600" role="alert">{{ fortnightStartDateError }}</p>
            }
          }

          <!-- Read-only: the server derives the stored limit from the schedule and ignores any value
               a request states, so this is a preview of what it will decide. -->
          <p id="preset-search-freshness" class="mt-2 text-sm text-md-gray-700" aria-live="polite">
            @if (freshnessHoursPreview !== null) {
              Prices fresh for {{ freshnessHoursPreview }} h
            } @else {
              Choose at least one search day to see how long prices stay fresh.
            }
          </p>

          <p class="mt-1 text-sm text-md-gray-500">
            Search days are Sydney time. A day a run misses is caught up on the next run.
          </p>

          @if (!searchPrices) {
            <p class="mt-1 text-sm text-md-gray-500">
              Search prices is off, so nothing is searched on these days until it is switched back on.
            </p>
          }
        </fieldset>

        @if (preset) {
          <label class="mb-3 mt-3 flex items-center gap-2 text-sm text-md-gray-700" for="preset-enabled">
            <input
              id="preset-enabled"
              type="checkbox"
              [disabled]="submitting"
              [(ngModel)]="isEnabled"
            />
            Suggested to newly approved businesses
          </label>
        }

        @if (errorMessage) {
          <p class="mb-3 text-sm text-red-600" role="alert">{{ errorMessage }}</p>
        }

        <div class="confirm-actions">
          <button type="button" class="btn btn-secondary btn-sm" [disabled]="submitting" (click)="cancel()">
            Cancel
          </button>
          <button type="button" class="btn btn-primary btn-sm" [disabled]="submitting" (click)="submit()">
            {{ submitting ? 'Saving…' : 'Save' }}
          </button>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      .modal-backdrop {
        position: fixed;
        inset: 0;
        background: rgba(15, 23, 42, 0.5);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 50;
        padding: 1rem;
      }

      .confirm-modal {
        max-width: 420px;
        width: 100%;
        max-height: calc(100vh - 2rem);
        overflow-y: auto;
      }

      .confirm-actions {
        display: flex;
        justify-content: flex-end;
        gap: 0.75rem;
      }
    `
  ]
})
export class SupplierPresetDialogComponent implements OnInit {
  /** The preset being edited, or `null` to add one. */
  @Input() preset: SuperAdminSupplierPreset | null = null;

  @Output() readonly saved = new EventEmitter<SuperAdminSupplierPreset>();
  @Output() readonly canceled = new EventEmitter<void>();

  readonly nameMaxLength = SUPPLIER_PRESET_NAME_MAX_LENGTH;
  readonly urlMaxLength = SUPPLIER_URL_MAX_LENGTH;
  readonly connectorKeyMaxLength = SUPPLIER_PRESET_CONNECTOR_KEY_MAX_LENGTH;

  /** The eight choices, in the server's own order rather than alphabetically. */
  readonly servedStateCodes = SUPPLIER_SERVED_STATES;

  /** The declared collection routes, as the select's string values and their labels. */
  readonly connectorOptions = Object.values(SupplierPriceConnector)
    .filter((value): value is SupplierPriceConnector => typeof value === 'number')
    .map((value) => ({ value: String(value), label: SUPPLIER_PRICE_CONNECTOR_LABELS[value] }));

  /** The declared search frequencies, as the select's string values and their labels. */
  readonly frequencyOptions = Object.values(SupplierSearchFrequency)
    .filter((value): value is SupplierSearchFrequency => typeof value === 'number')
    .map((value) => ({ value: String(value), label: SUPPLIER_SEARCH_FREQUENCY_LABELS[value] }));

  /** The seven choices, in the server's own Monday-to-Sunday order rather than the browser's. */
  readonly searchWeekdays = SUPPLIER_SEARCH_WEEKDAYS;

  name = '';
  url = '';
  isEnabled = true;

  /**
   * Whether the price collector searches this supplier (issue #1077). It starts on for a new preset,
   * which is what the server defaults an omitted value to, and is pre-filled from the row on an
   * edit. It is submitted on both calls and is independent of {@link isEnabled}: neither switch
   * moves the other.
   */
  searchPrices = true;

  /**
   * Whether the preset serves the whole country. A new preset starts nationwide, because that is
   * what most suppliers on this list are and because the alternative - starting with no market at
   * all - would open the form on a refusal.
   */
  servesNationwide = true;

  /**
   * Which states are ticked, by code. Only read while {@link servesNationwide} is off, and cleared
   * when it is switched back on, so the two can never both be submitted.
   */
  stateSelections: Record<string, boolean> = {};

  /** Whether the published prices include GST. Sent explicitly on every write; the server requires it. */
  pricesIncludeGst = true;

  /** The chosen route as the select's own string value; `''` is no route at all. */
  connectorValue = '';

  connectorKey = '';

  /**
   * The chosen frequency as the select's own string value (issue #1108). A new preset starts
   * {@link SupplierSearchFrequency.Daily}, which is what the server stores for a create that states
   * no schedule and what the collector did before schedules existed.
   */
  frequencyValue = String(SupplierSearchFrequency.Daily);

  /**
   * Which search days are ticked, by the server's own weekday code. Only read while the chosen
   * frequency names weekdays, and cleared when Daily is chosen, so "every day *and* Thursday" -
   * which the server refuses - cannot be submitted.
   */
  daySelections: Record<string, boolean> = {};

  /**
   * The fortnight start date as the `YYYY-MM-DD` characters the API sent, or `''` for none.
   *
   * **A string, deliberately, and never a `Date`.** It names a day of the collection calendar
   * rather than a moment, so it is read from and sent to the API verbatim; parsing it would move a
   * fortnightly supplier's on week by a day for a Super Admin outside Sydney. Only read while
   * Fortnightly is chosen, and cleared when another frequency is, so a date nothing reads cannot be
   * submitted either.
   */
  fortnightStartDate = '';

  submitting = false;
  errorMessage: string | null = null;
  nameError: string | null = null;
  urlError: string | null = null;
  statesError: string | null = null;
  connectorKeyError: string | null = null;
  searchDaysError: string | null = null;
  fortnightStartDateError: string | null = null;

  constructor(private readonly presets: SuperAdminSupplierPresetsService) {}

  get title(): string {
    return this.preset ? 'Edit supplier preset' : 'Add supplier preset';
  }

  /** Whether the chosen route is the one identified by a connector key. */
  get connectorTakesAKey(): boolean {
    return this.connectorValue === String(CONNECTOR_WITH_A_KEY);
  }

  /** Whether the chosen frequency is searched on named weekdays rather than every day. */
  get scheduleNamesWeekdays(): boolean {
    return this.selectedFrequency() !== SupplierSearchFrequency.Daily;
  }

  /** Whether the chosen frequency has off weeks for a start date to place. */
  get scheduleNamesAFortnightStartDate(): boolean {
    return this.selectedFrequency() === SupplierSearchFrequency.Fortnightly;
  }

  /**
   * The freshness limit the schedule on screen would derive, in hours, or `null` while it does not
   * yet derive one - which is the state it is in before a weekday is chosen.
   *
   * Read-only on purpose: the server derives the stored value from the schedule and ignores
   * anything a request says about it (issue #1108), so this previews its answer with the same rule
   * rather than offering a number to submit.
   */
  get freshnessHoursPreview(): number | null {
    return searchFreshnessHours(this.scheduleBeingEdited());
  }

  ngOnInit(): void {
    this.name = this.preset?.name ?? '';
    this.url = this.preset?.url ?? '';
    this.isEnabled = this.preset?.isEnabled ?? true;
    this.searchPrices = this.preset?.searchPrices ?? true;

    // An existing preset is shown exactly as it is stored, including a row the issue #931 migration
    // could not recognise: that reads as neither nationwide nor any state, and the form shows it that
    // way so the gap is visible and the Save refuses it, rather than claiming the whole country on
    // somebody's behalf.
    this.servesNationwide = this.preset?.servesNationwide ?? true;
    this.stateSelections = {};
    for (const state of this.preset?.servedStates ?? []) {
      this.stateSelections[state] = true;
    }

    this.pricesIncludeGst = this.preset?.pricesIncludeGst ?? true;
    this.connectorValue = this.preset?.priceConnector === null ? '' : String(this.preset?.priceConnector ?? '');
    this.connectorKey = this.preset?.priceConnectorKey ?? '';

    // The stored schedule exactly as it arrived (issue #1109). The start date is assigned as the
    // characters the API sent, with nothing parsing them in between.
    this.frequencyValue = String(this.preset?.searchFrequency ?? SupplierSearchFrequency.Daily);
    this.daySelections = {};
    for (const weekday of orderedSearchWeekdays(this.preset?.searchDaysOfWeek)) {
      this.daySelections[weekday] = true;
    }
    this.fortnightStartDate = this.preset?.fortnightStartDate ?? '';
  }

  stateInputId(state: string): string {
    return `preset-state-${state}`;
  }

  weekdayInputId(weekday: SupplierSearchWeekday): string {
    return `preset-search-day-${weekday}`;
  }

  weekdayName(weekday: SupplierSearchWeekday): string {
    return SUPPLIER_SEARCH_WEEKDAY_NAMES[weekday];
  }

  /** Clears the field and server messages as soon as the value they were about changes. */
  onFieldChanged(): void {
    this.nameError = null;
    this.urlError = null;
    this.statesError = null;
    this.connectorKeyError = null;
    this.searchDaysError = null;
    this.fortnightStartDateError = null;
    this.errorMessage = null;
  }

  /**
   * Choosing a frequency drops whatever the new one does not use, for the reason the nationwide and
   * connector-key switches do it: the server refuses a daily schedule that names weekdays and a
   * non-fortnightly one that carries a start date, so the form cannot produce either.
   */
  onFrequencyChanged(): void {
    if (!this.scheduleNamesWeekdays) {
      this.daySelections = {};
    }
    if (!this.scheduleNamesAFortnightStartDate) {
      this.fortnightStartDate = '';
    }
    this.onFieldChanged();
  }

  /**
   * Switching to nationwide drops whatever states were ticked, so the request carries one claim
   * rather than a combination the server refuses.
   */
  onNationwideChanged(): void {
    if (this.servesNationwide) {
      this.stateSelections = {};
    }
    this.onFieldChanged();
  }

  /**
   * Choosing a route that takes no key drops the key, for the same reason: a key nothing reads is a
   * value that looks meaningful and is not, and the server refuses it.
   */
  onConnectorChanged(): void {
    if (!this.connectorTakesAKey) {
      this.connectorKey = '';
    }
    this.onFieldChanged();
  }

  cancel(): void {
    if (this.submitting) {
      return;
    }
    this.canceled.emit();
  }

  submit(): void {
    const website = normalizeSupplierUrl(this.url);
    const servedStates = this.selectedStates();
    const connector = this.selectedConnector();
    const connectorKey = this.connectorKey.trim();
    const schedule = this.scheduleBeingEdited();

    this.nameError = nameErrorFor(this.name);
    this.urlError = website.error;
    this.statesError =
      this.servesNationwide || servedStates.length > 0 ? null : SERVED_STATES_REQUIRED_MESSAGE;
    this.connectorKeyError = connectorKeyErrorFor(connector, connectorKey);
    this.searchDaysError = searchDaysErrorFor(schedule);
    this.fortnightStartDateError = fortnightStartDateErrorFor(schedule);
    this.errorMessage = null;

    if (
      this.nameError ||
      this.urlError ||
      this.statesError ||
      this.connectorKeyError ||
      this.searchDaysError ||
      this.fortnightStartDateError
    ) {
      return;
    }

    const name = this.name.trim();
    const url = website.url;
    this.submitting = true;

    const master = {
      searchPrices: this.searchPrices,
      servesNationwide: this.servesNationwide,
      servedStates,
      pricesIncludeGst: this.pricesIncludeGst,
      priceConnector: connector,
      priceConnectorKey: connectorKey.length > 0 ? connectorKey : null,
      // Exactly the API's three schedule fields, with the values the chosen frequency does not use
      // sent as the empty list and null rather than as whatever was last typed. This dialog is the
      // one caller that states them: a person is looking at the schedule here and the values on
      // screen are the ones to store, whereas the list's toggle buttons say nothing about it and
      // leave the stored timetable alone (issue #1109).
      ...scheduleRequestOf(schedule)
    };

    const request = this.preset
      ? this.presets.update(this.preset.id, { name, url, isEnabled: this.isEnabled, ...master })
      : this.presets.create({ name, url, ...master });

    request.subscribe({
      next: (preset) => {
        this.submitting = false;
        this.saved.emit(preset);
      },
      error: (err: unknown) => {
        this.submitting = false;
        this.errorMessage = extractErrorMessage(err, 'Unable to save this supplier preset.');
      }
    });
  }

  /**
   * The ticked states in the server's own order - so one market has one spelling on the wire too -
   * and none at all for a nationwide preset.
   */
  private selectedStates(): string[] {
    return this.servesNationwide
      ? []
      : this.servedStateCodes.filter((state) => this.stateSelections[state] === true);
  }

  private selectedConnector(): SupplierPriceConnector | null {
    return this.connectorValue === '' ? null : (Number(this.connectorValue) as SupplierPriceConnector);
  }

  private selectedFrequency(): SupplierSearchFrequency {
    return Number(this.frequencyValue) as SupplierSearchFrequency;
  }

  /**
   * The schedule the form currently describes: the chosen frequency, the ticked days in the
   * server's own order, and the start date only where the frequency has off weeks for it. One
   * method, so the freshness preview, the validation and the submission can never be reading three
   * different schedules.
   */
  private scheduleBeingEdited(): SupplierSearchSchedule {
    const frequency = this.selectedFrequency();

    return {
      frequency,
      daysOfWeek:
        frequency === SupplierSearchFrequency.Daily
          ? []
          : orderedSearchWeekdays(
              this.searchWeekdays.filter((weekday) => this.daySelections[weekday] === true)
            ),
      fortnightStartDate:
        frequency === SupplierSearchFrequency.Fortnightly && isSearchDateOnly(this.fortnightStartDate.trim())
          ? this.fortnightStartDate.trim()
          : null
    };
  }
}

/** The schedule as the API's own three field names, which is the whole of what a write sends. */
function scheduleRequestOf(schedule: SupplierSearchSchedule): SupplierSearchScheduleRequest {
  return {
    searchFrequency: schedule.frequency,
    searchDaysOfWeek: schedule.daysOfWeek,
    fortnightStartDate: schedule.fortnightStartDate
  };
}

function searchDaysErrorFor(schedule: SupplierSearchSchedule): string | null {
  return schedule.frequency !== SupplierSearchFrequency.Daily && schedule.daysOfWeek.length === 0
    ? SEARCH_WEEKDAY_REQUIRED_MESSAGE
    : null;
}

/**
 * A fortnightly schedule needs a start date. A value that is not a plain `YYYY-MM-DD` date counts
 * as none - rather than as a mistake with a sentence of its own - because the server's vocabulary
 * has no third answer here, and the one message is what a Super Admin has to act on either way.
 */
function fortnightStartDateErrorFor(schedule: SupplierSearchSchedule): string | null {
  return schedule.frequency === SupplierSearchFrequency.Fortnightly && schedule.fortnightStartDate === null
    ? FORTNIGHT_START_DATE_REQUIRED_MESSAGE
    : null;
}

function nameErrorFor(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed.length === 0) {
    return NAME_REQUIRED_MESSAGE;
  }

  return trimmed.length > SUPPLIER_PRESET_NAME_MAX_LENGTH ? NAME_TOO_LONG_MESSAGE : null;
}

function connectorKeyErrorFor(connector: SupplierPriceConnector | null, key: string): string | null {
  if (connector !== CONNECTOR_WITH_A_KEY) {
    // Every other route, and no route, takes no key - and the field the key would come from is not
    // even rendered for them, so there is nothing left to refuse.
    return null;
  }

  if (key.length === 0) {
    return CONNECTOR_KEY_REQUIRED_MESSAGE;
  }

  return key.length > SUPPLIER_PRESET_CONNECTOR_KEY_MAX_LENGTH ? CONNECTOR_KEY_TOO_LONG_MESSAGE : null;
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
