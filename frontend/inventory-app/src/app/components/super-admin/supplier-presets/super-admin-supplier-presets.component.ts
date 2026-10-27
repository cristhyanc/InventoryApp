import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { ToastService } from '../../../services/toast.service';
import {
  SUPPLIER_PRICE_CONNECTOR_LABELS,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from '../../../services/super-admin-supplier-presets.service';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { SupplierPresetDialogComponent } from './supplier-preset-dialog.component';
import { isSafeSupplierUrl } from '../../suppliers/supplier-url';

/**
 * The Super Admin Supplier presets page (issue #848), at `/super-admin/supplier-presets`, over the
 * three endpoints of the platform supplier presets API.
 *
 * It is presentation only: the API decides every rule a create or edit might refuse - the name
 * rules, the website rule and the duplicate-name conflict only it can settle - and this page shows
 * that refusal's own sentence rather than a second copy of the rule.
 *
 * Per docs/architecture.md § Page composition boundary, the page owns the table and the
 * enable/disable confirmation behind the shared `ConfirmationDialogComponent`, while Add and Edit -
 * their own form values, validation and submission state - are a dedicated feature component
 * composed through `[preset]` and its `(saved)`/`(canceled)` outputs.
 *
 * **Presets are not suppliers.** Nothing here reads or writes any business's own suppliers, and
 * copying a preset into a newly approved business is issue #835 child C, not this page.
 */
@Component({
  selector: 'app-super-admin-supplier-presets',
  standalone: true,
  imports: [CommonModule, DatePipe, ConfirmationDialogComponent, SupplierPresetDialogComponent],
  templateUrl: './super-admin-supplier-presets.component.html'
})
export class SuperAdminSupplierPresetsComponent implements OnInit, OnDestroy {
  presets: SuperAdminSupplierPreset[] = [];
  loading = false;
  loadError = false;

  /** The dialog is open when this is set; `null` inside it means Add. */
  editTarget: SuperAdminSupplierPreset | null = null;
  dialogOpen = false;

  /** The preset whose enabled state a confirmation is pending for. */
  toggleTarget: SuperAdminSupplierPreset | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly presetsService: SuperAdminSupplierPresetsService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  load(): void {
    this.loading = true;
    this.loadError = false;
    this.presetsService
      .list()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (presets) => {
          this.presets = presets;
          this.loading = false;
        },
        error: () => {
          this.loading = false;
          this.loadError = true;
        }
      });
  }

  /**
   * Whether a stored website may be rendered as a link, asked of the one supplier website helper
   * there is (issue #847). The API refuses anything but an absolute http(s) address, so this only
   * ever matters for a value that reached the table another way - a direct edit of the database,
   * or a row that predates the rule - and such a value shows as plain text rather than as something
   * clickable, exactly as the Suppliers list does it.
   */
  canLinkTo(preset: SuperAdminSupplierPreset): boolean {
    return isSafeSupplierUrl(preset.url);
  }

  /**
   * What the table shows in the Serves column (issue #931): "Nationwide", the state codes, or
   * `null` for a preset nobody has recorded a market for yet.
   *
   * The third answer is a real state rather than a missing value: a row the #931 migration could not
   * recognise - one renamed before it ran - reads as neither nationwide nor any state, and the table
   * reports that as a gap to fill instead of drawing it as the whole country.
   */
  servedMarket(preset: SuperAdminSupplierPreset): string | null {
    if (preset.servesNationwide) {
      return 'Nationwide';
    }

    return preset.servedStates.length > 0 ? preset.servedStates.join(', ') : null;
  }

  /**
   * What the table shows in the Price source column: the route's own label, or `null` when nothing
   * collects this supplier's prices - the state Costco is in, which the template reports in words
   * rather than as an empty cell.
   */
  connectorLabel(preset: SuperAdminSupplierPreset): string | null {
    return preset.priceConnector === null ? null : SUPPLIER_PRICE_CONNECTOR_LABELS[preset.priceConnector];
  }

  requestAdd(): void {
    this.editTarget = null;
    this.dialogOpen = true;
  }

  requestEdit(preset: SuperAdminSupplierPreset): void {
    this.editTarget = preset;
    this.dialogOpen = true;
  }

  onSaved(preset: SuperAdminSupplierPreset): void {
    const wasEdit = this.editTarget !== null;
    this.dialogOpen = false;
    this.editTarget = null;
    this.toast.success(wasEdit ? `${preset.name} was updated.` : `${preset.name} was added.`);
    this.load();
  }

  onDialogCanceled(): void {
    this.dialogOpen = false;
    this.editTarget = null;
  }

  requestToggle(preset: SuperAdminSupplierPreset): void {
    this.toggleTarget = preset;
  }

  cancelToggle(): void {
    this.toggleTarget = null;
  }

  get toggleTitle(): string {
    if (!this.toggleTarget) {
      return '';
    }
    return this.toggleTarget.isEnabled ? 'Disable supplier preset' : 'Enable supplier preset';
  }

  get toggleMessage(): string {
    const target = this.toggleTarget;
    if (!target) {
      return '';
    }

    return target.isEnabled
      ? `Stop suggesting ${target.name}? Businesses approved from now on will not be offered it. ` +
          'Businesses that already have it keep their own supplier.'
      : `Suggest ${target.name} again to businesses approved from now on?`;
  }

  confirmToggle(): void {
    const target = this.toggleTarget;
    if (!target) {
      return;
    }
    this.toggleTarget = null;

    const isEnabled = !target.isEnabled;

    // The whole resource, because this endpoint is a PUT of it. The supplier-master fields are sent
    // back exactly as the row holds them (issue #931): sending the form's defaults instead would
    // move a JB Metro record's market to nationwide and its prices to GST-inclusive as a side effect
    // of pressing Disable, which is a financial field changed by a button that does not say so.
    const request = {
      name: target.name,
      url: target.url,
      isEnabled,
      servesNationwide: target.servesNationwide,
      servedStates: target.servedStates,
      pricesIncludeGst: target.pricesIncludeGst,
      priceConnector: target.priceConnector,
      priceConnectorKey: target.priceConnectorKey
    };

    this.presetsService.update(target.id, request).subscribe({
      next: (preset) => {
        this.toast.success(isEnabled ? `${preset.name} is suggested again.` : `${preset.name} is no longer suggested.`);
        this.load();
      },
      error: (err: unknown) => {
        this.toast.error(extractErrorMessage(err, 'Unable to change this supplier preset.'));
        // A 409 or 404 means the preset changed or disappeared elsewhere (another tab, another
        // Super Admin), so the row this page shows is stale: re-read the list.
        if (isStaleStateStatus(err)) {
          this.load();
        }
      }
    });
  }
}

function isStaleStateStatus(err: unknown): boolean {
  const status = (err as { status?: unknown } | undefined)?.status;
  return status === 409 || status === 404;
}

function extractErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;
  if (typeof body === 'string') {
    return body;
  }
  const problem = body as { message?: string; title?: string } | undefined;
  return problem?.message ?? problem?.title ?? fallback;
}
