import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from '../../../services/super-admin-supplier-presets.service';
import { SUPPLIER_URL_MAX_LENGTH, normalizeSupplierUrl } from '../../suppliers/supplier-url';

/** The server's own name rules, mirrored so the form can say what is wrong before it submits. */
export const SUPPLIER_PRESET_NAME_MAX_LENGTH = 200;
const NAME_REQUIRED_MESSAGE = 'A supplier preset needs a name.';
const NAME_TOO_LONG_MESSAGE = 'A supplier preset name must be 200 characters or fewer.';

/**
 * The Add and Edit dialog of the Super Admin Supplier presets page (issue #848), over
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
 * business's own supplier form shows. A Super Admin is therefore told about a mistyped address in
 * the field rather than after a round trip, and when the server refuses anyway, including the
 * duplicate-name `409` that only it can decide, its own sentence is what is shown.
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

  name = '';
  url = '';
  isEnabled = true;
  submitting = false;
  errorMessage: string | null = null;
  nameError: string | null = null;
  urlError: string | null = null;

  constructor(private readonly presets: SuperAdminSupplierPresetsService) {}

  get title(): string {
    return this.preset ? 'Edit supplier preset' : 'Add supplier preset';
  }

  ngOnInit(): void {
    this.name = this.preset?.name ?? '';
    this.url = this.preset?.url ?? '';
    this.isEnabled = this.preset?.isEnabled ?? true;
  }

  /** Clears the field and server messages as soon as the value they were about changes. */
  onFieldChanged(): void {
    this.nameError = null;
    this.urlError = null;
    this.errorMessage = null;
  }

  cancel(): void {
    if (this.submitting) {
      return;
    }
    this.canceled.emit();
  }

  submit(): void {
    const website = normalizeSupplierUrl(this.url);
    this.nameError = nameErrorFor(this.name);
    this.urlError = website.error;
    this.errorMessage = null;

    if (this.nameError || this.urlError) {
      return;
    }

    const name = this.name.trim();
    const url = website.url;
    this.submitting = true;

    const request = this.preset
      ? this.presets.update(this.preset.id, { name, url, isEnabled: this.isEnabled })
      : this.presets.create({ name, url });

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
}

function nameErrorFor(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed.length === 0) {
    return NAME_REQUIRED_MESSAGE;
  }

  return trimmed.length > SUPPLIER_PRESET_NAME_MAX_LENGTH ? NAME_TOO_LONG_MESSAGE : null;
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
