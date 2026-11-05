import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { ToastService } from '../../../services/toast.service';
import {
  SUPPLIER_PRICE_CONNECTOR_LABELS,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService,
  UpdateSupplierPresetKeepingScheduleRequest
} from '../../../services/super-admin-supplier-presets.service';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { SupplierPresetDialogComponent } from './supplier-preset-dialog.component';
import { isSafeSupplierUrl } from '../../suppliers/supplier-url';
import { describeSearchSchedule } from './supplier-search-schedule';

/**
 * The Super Admin Supplier presets page (issue #848), at `/super-admin/supplier-presets`, over the
 * three endpoints of the platform supplier presets API.
 *
 * It is presentation only: the API decides every rule a create or edit might refuse - the name
 * rules, the website rule and the duplicate-name conflict only it can settle - and this page shows
 * that refusal's own sentence rather than a second copy of the rule.
 *
 * Since issue #1109 the table also reports each supplier's **search schedule** and the freshness
 * limit the API derived from it (issue #1108), and the dialog edits that schedule. The limit in the
 * table is the value the API reported, never a recalculation of it.
 *
 * Per docs/architecture.md § Page composition boundary, the page owns the table and the two
 * confirmations behind the shared `ConfirmationDialogComponent` - enable/disable, and the
 * *Search prices* switch of issue #1077 - while Add and Edit -
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

  /**
   * The preset whose *searching* state a confirmation is pending for (issue #1077). Its own field
   * rather than a mode on {@link toggleTarget}, so a pending confirmation cannot be answered about
   * the wrong switch - the two buttons change different columns.
   */
  searchToggleTarget: SuperAdminSupplierPreset | null = null;

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

  /**
   * Whether this preset's searching switch decides anything at all (issue #1077). It does not for a
   * preset nothing collects: the collector searches a supplier only when the switch is on *and* a
   * price connector names a route, so a Costco-shaped row has no search to start or stop and the
   * table says "No price route" instead of offering a button that would change nothing visible.
   */
  canSearchPrices(preset: SuperAdminSupplierPreset): boolean {
    return preset.priceConnector !== null;
  }

  /**
   * What the table shows in the Search schedule column (issue #1109): the supplier's own timetable
   * in words - "Daily", "Weekly: Wed, Sat", "Fortnightly: Thu (on weeks from 9 Nov 2026)" - read
   * from the schedule fields the API publishes.
   *
   * The freshness hours beside it in the template are the API's own reported
   * `priceCheckFreshnessHours`, not a second calculation of it: the server derives the stored value
   * from the schedule, and the table reports what is stored. The dialog is the one place the rule is
   * mirrored, and only to preview a schedule nobody has saved yet.
   */
  scheduleText(preset: SuperAdminSupplierPreset): string {
    return describeSearchSchedule({
      frequency: preset.searchFrequency,
      daysOfWeek: preset.searchDaysOfWeek,
      fortnightStartDate: preset.fortnightStartDate
    });
  }

  /**
   * Why nothing is searched at this supplier despite the schedule beside it, or `null` when the
   * schedule is in force.
   *
   * The schedule stays visible and editable for a paused supplier - setting a timetable before the
   * searches start is the point of having two switches - but a timetable shown on its own would read
   * as "searched on these days", so the row says which of the two halves is missing. The
   * `priceCheckFreshnessHours` beside it is still true either way: it ages out prices already
   * collected, which switching searching off does not remove.
   */
  notSearchedNote(preset: SuperAdminSupplierPreset): string | null {
    if (!this.canSearchPrices(preset)) {
      return 'No price route, so nothing is searched.';
    }

    return preset.searchPrices ? null : 'Search prices is off, so nothing is searched.';
  }

  requestToggle(preset: SuperAdminSupplierPreset): void {
    this.toggleTarget = preset;
  }

  cancelToggle(): void {
    this.toggleTarget = null;
  }

  requestSearchToggle(preset: SuperAdminSupplierPreset): void {
    this.searchToggleTarget = preset;
  }

  cancelSearchToggle(): void {
    this.searchToggleTarget = null;
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

  get searchToggleTitle(): string {
    if (!this.searchToggleTarget) {
      return '';
    }
    return this.searchToggleTarget.searchPrices ? 'Stop searching prices' : 'Search prices';
  }

  get searchToggleMessage(): string {
    const target = this.searchToggleTarget;
    if (!target) {
      return '';
    }

    return target.searchPrices
      ? `Stop searching prices at ${target.name}? Nothing already collected is removed - its ` +
          'prices stay and age out as usual - and businesses are still offered this supplier.'
      : `Search prices at ${target.name} again from the next collection run?`;
  }

  confirmToggle(): void {
    const target = this.toggleTarget;
    if (!target) {
      return;
    }
    this.toggleTarget = null;

    const isEnabled = !target.isEnabled;

    this.put(target, { ...this.rowWithoutItsSchedule(target), isEnabled }, (preset) =>
      isEnabled ? `${preset.name} is suggested again.` : `${preset.name} is no longer suggested.`
    );
  }

  confirmSearchToggle(): void {
    const target = this.searchToggleTarget;
    if (!target) {
      return;
    }
    this.searchToggleTarget = null;

    const searchPrices = !target.searchPrices;

    this.put(target, { ...this.rowWithoutItsSchedule(target), searchPrices }, (preset) =>
      searchPrices
        ? `${preset.name} prices are searched again.`
        : `${preset.name} prices are no longer searched.`
    );
  }

  /**
   * The row as either toggle re-states it: every field it holds **except the search schedule**.
   *
   * This endpoint is a `PUT` of the whole resource, so a button that sent the form's defaults
   * instead would move a JB Metro record's market to nationwide and its prices to GST-inclusive as a
   * side effect of pressing Disable - a financial field changed by a button that does not say so.
   * Since issue #1077 that includes each other's flag: pressing Disable must not start or stop a
   * search, and pressing Stop searching must not withdraw the supplier from businesses.
   *
   * **The schedule is the one part of the row a toggle deliberately says nothing about** (issues
   * #1108 and #1109). The server reads a `PUT` that states none of the three schedule fields as
   * "keep the stored schedule", so saying nothing keeps whatever timetable is stored *at the moment
   * the button is pressed*. Re-sending the three fields from this list row would instead submit a
   * schedule edit built from a value read when the page last loaded: a timetable saved since - by
   * another Super Admin, or by this one in another tab - would be reverted to the stale one, its
   * freshness limit recalculated and `ScheduleEffectiveFromUtc` advanced, making every one of that
   * supplier's products due again, because somebody pressed Disable. The request type makes the
   * omission unsendable rather than merely intended
   * ({@link UpdateSupplierPresetKeepingScheduleRequest}); the dialog, where a person is actually
   * looking at the schedule, is the one place that states it.
   */
  private rowWithoutItsSchedule(
    preset: SuperAdminSupplierPreset
  ): UpdateSupplierPresetKeepingScheduleRequest {
    return {
      name: preset.name,
      url: preset.url,
      isEnabled: preset.isEnabled,
      searchPrices: preset.searchPrices,
      servesNationwide: preset.servesNationwide,
      servedStates: preset.servedStates,
      pricesIncludeGst: preset.pricesIncludeGst,
      priceConnector: preset.priceConnector,
      priceConnectorKey: preset.priceConnectorKey
    };
  }

  /** The one call and the one failure handling both toggles share. */
  private put(
    target: SuperAdminSupplierPreset,
    request: UpdateSupplierPresetKeepingScheduleRequest,
    succeeded: (preset: SuperAdminSupplierPreset) => string
  ): void {
    this.presetsService.update(target.id, request).subscribe({
      next: (preset) => {
        this.toast.success(succeeded(preset));
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
