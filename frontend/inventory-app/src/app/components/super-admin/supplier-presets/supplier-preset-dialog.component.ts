import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  SUPPLIER_PRICE_CONNECTOR_LABELS,
  SUPPLIER_SERVED_STATES,
  SupplierPriceConnector,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from '../../../services/super-admin-supplier-presets.service';
import { SUPPLIER_URL_MAX_LENGTH, normalizeSupplierUrl } from '../../suppliers/supplier-url';

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
 * The Add and Edit dialog of the Super Admin Supplier presets page (issues #848 and #931), over
 * `POST /api/super-admin/supplier-presets` and `PUT .../{id}`.
 *
 * It owns its own form values, submission state and error, which is what makes it a distinct
 * workflow per docs/architecture.md § Page composition boundary - the routed page composes it
 * through `[preset]` and reacts only to `(saved)`/`(canceled)`. A `null` `preset` is an Add and an
 * existing one is an Edit; the only difference is which call is made and whether the enabled toggle
 * is offered, because a new preset is always created enabled.
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
 * is chosen, so a key for a keyless route cannot be submitted either.
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

  name = '';
  url = '';
  isEnabled = true;

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

  submitting = false;
  errorMessage: string | null = null;
  nameError: string | null = null;
  urlError: string | null = null;
  statesError: string | null = null;
  connectorKeyError: string | null = null;

  constructor(private readonly presets: SuperAdminSupplierPresetsService) {}

  get title(): string {
    return this.preset ? 'Edit supplier preset' : 'Add supplier preset';
  }

  /** Whether the chosen route is the one identified by a connector key. */
  get connectorTakesAKey(): boolean {
    return this.connectorValue === String(CONNECTOR_WITH_A_KEY);
  }

  ngOnInit(): void {
    this.name = this.preset?.name ?? '';
    this.url = this.preset?.url ?? '';
    this.isEnabled = this.preset?.isEnabled ?? true;

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
  }

  stateInputId(state: string): string {
    return `preset-state-${state}`;
  }

  /** Clears the field and server messages as soon as the value they were about changes. */
  onFieldChanged(): void {
    this.nameError = null;
    this.urlError = null;
    this.statesError = null;
    this.connectorKeyError = null;
    this.errorMessage = null;
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

    this.nameError = nameErrorFor(this.name);
    this.urlError = website.error;
    this.statesError =
      this.servesNationwide || servedStates.length > 0 ? null : SERVED_STATES_REQUIRED_MESSAGE;
    this.connectorKeyError = connectorKeyErrorFor(connector, connectorKey);
    this.errorMessage = null;

    if (this.nameError || this.urlError || this.statesError || this.connectorKeyError) {
      return;
    }

    const name = this.name.trim();
    const url = website.url;
    this.submitting = true;

    const master = {
      servesNationwide: this.servesNationwide,
      servedStates,
      pricesIncludeGst: this.pricesIncludeGst,
      priceConnector: connector,
      priceConnectorKey: connectorKey.length > 0 ? connectorKey : null
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
