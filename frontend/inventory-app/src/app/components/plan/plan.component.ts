import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, combineLatest, startWith, takeUntil } from 'rxjs';
import { AccessService, MemberAccessState } from '../../access/access.service';
import { Entitlement } from '../../access/entitlement';
import { PlanCatalogueEntry, PlanCatalogueService } from '../../access/plan-catalogue.service';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * The plain-language label for one `entitlement.source` value (issue #970), over the five values
 * `GET /api/me/access` can publish today. An unrecognised value - a newer deployment's - falls back
 * to the raw name rather than a guessed-friendly label, because inventing one for a source this
 * build does not know would be the exact "cancelled/expired" kind of guess AC5 refuses.
 */
const PLAN_SOURCE_LABELS: Readonly<Record<string, string>> = {
  EnforcementDisabled: 'Plan limits are not switched on for this deployment.',
  GrandfatheringNotCompleted: 'Existing businesses are still being grandfathered in.',
  ManualGrant: 'Manual grant',
  BillingSubscription: 'Paid subscription',
  NoActiveRecord: 'No active subscription'
};

/** @see PLAN_SOURCE_LABELS */
export function planSourceLabel(source: string): string {
  return PLAN_SOURCE_LABELS[source] ?? `Plan source: ${source}`;
}

/**
 * The plain-language reason for one `connectedMachines.unavailableReason` value (issue #970). An
 * unrecognised value falls back to the raw name for the same reason {@link planSourceLabel} does.
 */
const UNAVAILABLE_REASON_LABELS: Readonly<Record<string, string>> = {
  NotConnected: 'this business is not connected to Nayax.',
  ConnectionNeedsAttention: 'the Nayax connection needs attention.',
  NeverCounted: 'machines on this account have not been counted yet.',
  AccountNotYetCounted: 'this Nayax account has not been counted yet.'
};

/** @see UNAVAILABLE_REASON_LABELS */
export function unavailableReasonLabel(reason: string): string {
  return UNAVAILABLE_REASON_LABELS[reason] ?? `count unavailable (${reason}).`;
}

/** 1 GB is 1,000,000,000 bytes - the decimal multiple `PlanCatalogue` and the storage quota count. */
const GIGABYTE = 1_000_000_000;

/** Formats a byte count the way every plan figure on this page is shown: decimal gigabytes. */
export function formatGigabytes(bytes: number): string {
  const rounded = Math.round((bytes / GIGABYTE) * 10) / 10;
  return `${Number.isInteger(rounded) ? rounded.toFixed(0) : rounded.toFixed(1)} GB`;
}

/** Formats a catalogue price for display: `Free` for zero, otherwise `$X.XX CUR/mo`. */
export function formatMonthlyPrice(minorUnits: number, currencyCode: string): string {
  return minorUnits === 0 ? 'Free' : `$${(minorUnits / 100).toFixed(2)} ${currencyCode}/mo`;
}

/** Formats the catalogue's per-extra-machine rate, or an em dash when the plan has none. */
export function formatAdditionalMachinePrice(minorUnits: number | null, currencyCode: string): string {
  return minorUnits === null ? '—' : `$${(minorUnits / 100).toFixed(2)} ${currencyCode}/mo each`;
}

/**
 * The Plan page (issue #970, child of #965; after #966, #968 and #978): the business's current
 * effective plan, its storage and connected-machine standing, and the Free/Starter/Pro comparison -
 * reached from the `Settings` section beside Admin, so every member can reach it whatever role they
 * hold.
 *
 * **It reads only two things, and invents nothing beyond them.** The current plan, its source, the
 * storage figures and the connected-machine answer all come from `AccessService.access$` - the same
 * cached `GET /api/me/access` read every other capability-aware surface renders from, so this page
 * re-renders for free on the same triggers that service already has: a support session starting or
 * ending and a role preview changing both call `AccessService.refresh()` elsewhere, and this page is
 * simply subscribed throughout its life rather than reading once. The Free/Starter/Pro comparison is
 * `PlanCatalogueService`'s own one-time read of the static server catalogue, which is identical for
 * every caller and is read once rather than re-read on those triggers.
 *
 * **It displays only what the server actually publishes** (AC5): `ManualGrant` is labelled "Manual
 * grant" without guessing complimentary or grandfathered, `NoActiveRecord` is labelled as having no
 * active subscription rather than cancelled or expired, and a source or unavailable-reason value
 * this build does not recognise falls back to its raw name rather than an invented friendly one.
 * Until enforcement is switched on, the page says plainly that no plan limits currently apply.
 *
 * **The upgrade/manage-billing action is Owner-only, and is withheld entirely inside a support
 * session or a role preview** (AC4): neither is a context in which a write or a billing action
 * belongs, whatever role is being viewed. Until billing exists (#969) the action is text only -
 * "coming soon" - and there is no checkout or success state to withhold in the first place.
 */
@Component({
  selector: 'app-plan',
  standalone: true,
  imports: [BusinessDateTimePipe],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Plan</h1>
          <p class="page-subtitle">Your business's plan, storage and connected machines.</p>
        </div>
      </header>

      <section class="card" data-testid="plan-current">
        <div class="card-body">
          <h2 class="card-title">Current plan</h2>

          @if (accessState.status === 'unread') {
            <p class="text-sm value-muted" data-testid="plan-loading">Loading your plan…</p>
          } @else if (accessState.status === 'unavailable' || entitlement === undefined) {
            <p class="text-sm text-red-600" data-testid="plan-unavailable">
              Your plan could not be loaded. Reload the page to try again.
            </p>
          } @else if (!entitlement.enforced) {
            <p class="text-sm value-muted" data-testid="plan-not-enforced">
              No plan limits currently apply to your business. {{ sourceLabel(entitlement.source) }}
            </p>
          } @else {
            <p class="text-lg font-semibold" data-testid="plan-name">{{ entitlement.plan }}</p>
            <p class="text-sm value-muted" data-testid="plan-source">{{ sourceLabel(entitlement.source) }}</p>

            @if (entitlement.attachmentStorage) {
              <div class="mt-4" data-testid="plan-storage">
                <div class="flex items-center justify-between">
                  <span class="text-sm font-medium">Attachment storage</span>
                  <span class="text-sm value-muted">
                    {{ formatGigabytes(entitlement.attachmentStorage.usedBytes) }} of
                    {{ formatGigabytes(entitlement.attachmentStorage.limitBytes) }}
                  </span>
                </div>
                <div
                  class="mt-2 h-2 overflow-hidden rounded-full bg-md-gray-200"
                  role="progressbar"
                  aria-valuemin="0"
                  aria-valuemax="100"
                  [attr.aria-valuenow]="storagePercent(entitlement.attachmentStorage)"
                  [attr.aria-valuetext]="storageAriaText(entitlement.attachmentStorage)"
                >
                  <div
                    class="h-full rounded-full"
                    [class.bg-rr-teal]="entitlement.attachmentStorage.warningLevel === 'Normal'"
                    [class.bg-md-warning]="entitlement.attachmentStorage.warningLevel === 'Warning'"
                    [class.bg-md-danger]="entitlement.attachmentStorage.warningLevel === 'Full'"
                    [style.width.%]="storageBarWidth(entitlement.attachmentStorage)"
                  ></div>
                </div>
                @if (entitlement.attachmentStorage.warningLevel === 'Full') {
                  <p class="mt-1 text-sm text-md-danger-text" data-testid="plan-storage-full">
                    Storage is full. New attachment uploads are refused until you free up space or upgrade;
                    nothing already stored is affected.
                  </p>
                } @else if (entitlement.attachmentStorage.warningLevel === 'Warning') {
                  <p class="mt-1 text-sm text-md-warning-text" data-testid="plan-storage-warning">
                    Storage is at {{ storagePercent(entitlement.attachmentStorage) }}% of your plan's allowance.
                  </p>
                }
              </div>
            }

            @if (entitlement.connectedMachines) {
              <div class="mt-4" data-testid="plan-machines">
                <span class="text-sm font-medium">Connected machines</span>
                @if (entitlement.connectedMachines.state === 'Unavailable') {
                  <p class="text-sm value-muted" data-testid="plan-machines-unavailable">
                    Count unavailable — {{ unavailableReasonLabel(entitlement.connectedMachines.unavailableReason) }}
                  </p>
                } @else {
                  <p class="text-sm">
                    {{ entitlement.connectedMachines.count }} of {{ entitlement.connectedMachines.allowance }} included
                    @if (entitlement.connectedMachines.state === 'Stale') {
                      <span class="badge badge-warning" data-testid="plan-machines-stale">
                        Stale ({{ entitlement.connectedMachines.ageInDays }}d old)
                      </span>
                    }
                  </p>
                  <p class="text-xs value-muted">As of {{ entitlement.connectedMachines.asOfDateUtc | businessDateTime }}</p>
                  @if ((entitlement.connectedMachines.machinesOverAllowance ?? 0) > 0) {
                    <p class="text-xs value-muted" data-testid="plan-machines-over-allowance">
                      {{ entitlement.connectedMachines.machinesOverAllowance }} over the allowance — billed, never
                      blocked.
                    </p>
                  }
                }
              </div>
            }
          }
        </div>
      </section>

      @if (showAction) {
        <section class="card mt-5" data-testid="plan-action">
          <div class="card-body">
            @if (isOwner) {
              <p class="text-sm value-muted" data-testid="plan-action-owner">Upgrading and managing billing is coming soon.</p>
            } @else {
              <p class="text-sm value-muted" data-testid="plan-action-other">Ask your business Owner to change your plan.</p>
            }
          </div>
        </section>
      }

      <section class="card mt-5" data-testid="plan-catalogue">
        <div class="card-body">
          <h2 class="card-title">Compare plans</h2>

          @if (catalogueLoading) {
            <p class="text-sm value-muted" data-testid="plan-catalogue-loading">Loading plans…</p>
          } @else if (catalogueLoadError) {
            <p class="text-sm text-red-600" data-testid="plan-catalogue-load-error">
              Plans could not be loaded. Reload the page to try again.
            </p>
          } @else {
            <table class="table">
              <thead class="table-head">
                <tr>
                  <th scope="col" class="table-cell">Plan</th>
                  <th scope="col" class="table-cell">Price</th>
                  <th scope="col" class="table-cell">Attachment storage</th>
                  <th scope="col" class="table-cell">Connected machines included</th>
                  @if (hasFinancials) {
                    <th scope="col" class="table-cell" data-testid="plan-catalogue-additional-machine-header">
                      Additional machine
                    </th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (entry of catalogue; track entry.plan) {
                  <tr class="table-row">
                    <td class="table-cell">{{ entry.plan }}</td>
                    <td class="table-cell">{{ formatMonthlyPrice(entry.monthlyPriceMinorUnits, entry.currencyCode) }}</td>
                    <td class="table-cell">{{ formatGigabytes(entry.attachmentStorageLimitBytes) }}</td>
                    <td class="table-cell">{{ entry.includedConnectedMachines }}</td>
                    @if (hasFinancials) {
                      <td class="table-cell" data-testid="plan-catalogue-additional-machine-cell">
                        {{ formatAdditionalMachinePrice(entry.additionalConnectedMachinePriceMinorUnits, entry.currencyCode) }}
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          }
        </div>
      </section>
    </div>
  `
})
export class PlanComponent implements OnInit, OnDestroy {
  accessState: MemberAccessState = { status: 'unread' };
  entitlement: Entitlement | undefined;
  isOwner = false;
  hasFinancials = false;

  /** Whether the upgrade/"ask your Owner" action is shown at all - never inside a support session or a role preview (AC4). */
  showAction = false;

  catalogue: readonly PlanCatalogueEntry[] = [];
  catalogueLoading = true;
  catalogueLoadError = false;

  readonly sourceLabel = planSourceLabel;
  readonly unavailableReasonLabel = unavailableReasonLabel;
  readonly formatGigabytes = formatGigabytes;
  readonly formatMonthlyPrice = formatMonthlyPrice;
  readonly formatAdditionalMachinePrice = formatAdditionalMachinePrice;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly access: AccessService,
    private readonly planCatalogue: PlanCatalogueService,
    private readonly supportSession: SupportSessionService,
    private readonly rolePreview: RolePreviewService
  ) {}

  ngOnInit(): void {
    combineLatest([
      this.access.access$,
      this.supportSession.active$.pipe(startWith(false)),
      this.rolePreview.active$.pipe(startWith(false))
    ])
      .pipe(takeUntil(this.destroying$))
      .subscribe(([state, supportSessionActive, rolePreviewActive]) => {
        this.accessState = state;
        this.entitlement = state.status === 'granted' ? state.access.entitlement : undefined;
        this.isOwner = state.status === 'granted' && state.access.role === 'Owner';
        this.hasFinancials = state.status === 'granted' && state.access.capabilities.has('Dashboard.ViewFinancials');
        this.showAction = state.status === 'granted' && !supportSessionActive && !rolePreviewActive;
      });

    this.planCatalogue.get().subscribe({
      next: (entries) => {
        this.catalogue = entries;
        this.catalogueLoading = false;
      },
      error: () => {
        this.catalogueLoading = false;
        this.catalogueLoadError = true;
      }
    });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /** The real percentage used, never clamped - the figure the warning/full text states. */
  storagePercent(storage: { usedBytes: number; limitBytes: number }): number {
    return storage.limitBytes === 0 ? 0 : Math.round((storage.usedBytes / storage.limitBytes) * 100);
  }

  /** The bar's own fill width, clamped to 100% so an over-quota business does not overflow the track. */
  storageBarWidth(storage: { usedBytes: number; limitBytes: number }): number {
    return Math.min(100, this.storagePercent(storage));
  }

  storageAriaText(storage: { usedBytes: number; limitBytes: number; warningLevel: string }): string {
    return `${this.storagePercent(storage)}% of storage used, ${storage.warningLevel.toLowerCase()}`;
  }
}
