import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { DashboardService } from '../../services/dashboard.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';
import { AccessService } from '../../access/access.service';
import {
  DashboardInventorySummary,
  DashboardOrderingSummary,
  DashboardRefillSummary,
  DashboardSalesThisWeek,
  DashboardSummary,
  ReorderMode,
  UnavailableDashboardSection,
  isDashboardSectionUnavailable,
  Product,
  Machine,
  Site
} from '../../models/models';
import {
  NayaxUnavailability,
  SUPPORT_VIEW_REASON,
  nayaxUnavailabilityOfError,
  nayaxUnavailabilityOfReason,
  nayaxUnavailableMessage,
  storageOnlyReorderLabel
} from '../../provider/nayax-availability';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import { trendLabel, trendClass } from '../../formatting/revenue-trend';
import { siteStockClass } from '../../formatting/site-stock-status';
import { money } from '../reports/report-formatting';
import { IconComponent } from '../shared/icon.component';
import { LegacyBusinessSettingsPromptComponent } from '../shared/legacy-business-settings-prompt/legacy-business-settings-prompt.component';
import { MachineRestockSyncReminderComponent } from './machine-restock-sync-reminder/machine-restock-sync-reminder.component';
import { NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE } from '../../support-session/support-session-messages';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * What this page knows about the business's sales provider, read from its own summary (issue #727).
 *
 * Nayax is an optional add-on, so this is a normal property of a working business rather than an
 * error state. `GET /api/dashboard/summary` already answers the question: `needsRefill` is a real
 * card for a business whose fleet was read, and `{ available: false, reason }` for one whose was
 * not, with the reason saying which of these this is (issue #726). The page therefore needs no
 * second probe - the shared, application-wide availability signal is issue #728.
 *
 * `unknown` is the state before the summary has been read, and after a summary that failed: a
 * summary nobody read says nothing about the provider, so the page keeps its connected behaviour
 * rather than hiding a connected business's live sections on a transient failure.
 */
export type DashboardProviderAvailability =
  | 'unknown'
  | 'usable'
  | 'notConnected'
  | 'permissionNotGranted'
  | 'supportView';

/**
 * Where the Reorder Alerts purchase-cost read has got to (issue #789). The four are answers to
 * different questions and must never stand in for one another: nothing has been asked (`idle` -
 * the capability is unanswered or refused, or there is no alert product to ask about), the answer
 * is outstanding (`loading`), the answer arrived (`loaded`, where a product it carries no entry for
 * has never been bought), and the request failed (`failed`, where nothing is known about any
 * product). Only `loaded` may present `—`: "never bought" is a fact about the purchase history,
 * and a request nobody answered is not evidence of it.
 */
export type PurchaseCostLoadState = 'idle' | 'loading' | 'loaded' | 'failed';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    IconComponent,
    LegacyBusinessSettingsPromptComponent,
    MachineRestockSyncReminderComponent,
    BusinessDateTimePipe
  ],
  templateUrl: './dashboard.component.html'
})
export class DashboardComponent implements OnInit, OnDestroy {
  lowStock: Product[] = [];
  machines: Machine[] = [];
  sites: Site[] = [];

  /**
   * The Sites and Machines sections start as loading, not as empty: whether they are loaded at all
   * is decided by the summary (see {@link refreshSummary}), and an outstanding decision must not
   * show as "No sites available yet".
   */
  isLoadingSites = true;
  isLoadingMachines = true;
  isLoadingLowStock = false;

  /**
   * What to say instead of a list when a section's own request failed (issue #727). Never `null`
   * together with an empty list for a failed load: a request nobody answered is not a business with
   * nothing in it, and "No sites available yet" or "Everything looks good" would be a claim about
   * data this page was never given.
   */
  sitesError: string | null = null;
  machinesError: string | null = null;
  reorderAlertsError: string | null = null;
  isSalesSyncFailed = false;

  /**
   * Whether the synchronization was refused by the business's run limit (issue #681, review of
   * PR #696). The allowance is the business's, so this is what a member sees when colleagues have
   * just opened the Dashboard several times between them. A refusal proves runs started inside the
   * last two minutes, so it is shown as "refreshed recently", never as {@link isSalesSyncFailed}.
   */
  isSalesSyncRecentlyRefreshed = false;

  /**
   * When the sales these figures are calculated from last finished synchronizing, as a true
   * instant, or `null` when this page does not know (issue #843).
   *
   * Two answers set it and they are both "current": a run that happened - where the instant is now,
   * because the page was waiting for it - and a run the server skipped because it had finished less
   * than three minutes ago, where the instant is the server's own. It stays `null` for everything
   * that is *not* an answer about freshness: a failure, the run limit's `429`, and a support
   * session. Never a fabricated instant for those - "synced 0 min ago" for figures nobody read
   * would be exactly the wrong claim.
   */
  salesSyncedAtUtc: string | null = null;

  /** Whether a Refresh the operator asked for is still in flight; see {@link refreshSales}. */
  isRefreshingSales = false;

  summary: DashboardSummary | null = null;
  isLoadingSummary = false;
  isSummaryFailed = false;

  /** What the summary said about the provider; see {@link DashboardProviderAvailability}. */
  providerAvailability: DashboardProviderAvailability = 'unknown';

  /**
   * Which facts the reorder-alert list below was calculated from, and - in storage-only mode - why
   * the machines are missing from it, as `GET /api/products/alerts/low-stock` published them
   * (issue #726). Read from the list's own response rather than from the summary's ordering card,
   * so the table and its label always describe one read.
   */
  reorderMode: ReorderMode | null = null;
  reorderMachineNeedUnavailableReason: NayaxUnavailability | null = null;

  /**
   * Whether the latest-sales synchronization was deliberately not attempted because a support
   * session is active (issue #661). That is a different thing from {@link isSalesSyncFailed}: a
   * session resolves no Nayax credentials at all (issue #551), so the call could only have been
   * refused, and saying it "could not be synchronized" would describe an attempt never made.
   */
  isSalesSyncSkippedInSupportView = false;

  /** The one sentence this application shows for anything Nayax a support session cannot reach. */
  readonly nayaxUnavailableMessage = NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE;

  /**
   * Whether a support session is active (issue #661). This page offers no write control of its
   * own, but arriving on it does make one write-shaped call - `POST /api/nayax-sales/sync` - and
   * the Dashboard is where starting a session lands, so the flag guards that call too. See
   * `SupportSessionService.active$`.
   */
  private sessionActive = false;

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials` (issue #699), read from
   * `AccessService` rather than from the Sites/Machines responses themselves: those endpoints do
   * not yet redact revenue, profit, margin or trend figures for an Operator (#697/#698 will), so
   * this is the frontend's own hide until they do. Starts `true` - the fail-open default every
   * other capability-gated control in this codebase uses - so a figure is never hidden from the
   * role it belongs to merely because the capability has not been read yet.
   */
  canViewFinancials = true;

  /** See {@link PurchaseCostLoadState}. */
  purchaseCostState: PurchaseCostLoadState = 'idle';

  /** The sentence shown in place of the two purchase-cost columns' figures when the read failed. */
  readonly purchaseCostUnavailableMessage = 'The last and cheapest purchase costs could not be loaded.';

  /**
   * The last and cheapest purchase cost of each alert product that has a purchase history, by
   * product id (issue #789), exactly as `GET /api/products/purchase-cost-summary` published it.
   * Nothing here picks a cheapest, sorts a history or derives a saving: those are decided once, by
   * the backend's `SupplierPriceComparisonPolicy` (issue #788).
   *
   * It only ever holds the answer for the product set currently displayed - it is emptied before
   * every request, and an answer from a superseded request is discarded; see
   * {@link refreshPurchaseCosts}.
   */
  private purchaseCosts = new Map<number, ProductPurchaseCostSummary>();

  /**
   * Bumped by every purchase-cost request, so a slower earlier one - for a product set that is no
   * longer displayed, or the failure a Retry has already replaced - cannot publish its answer over
   * the current one.
   */
  private purchaseCostRequest = 0;

  /** Whether a reorder-alert listing has been read: its products are what there is to cost. */
  private hasReorderAlerts = false;

  /**
   * Whether `Dashboard.ViewFinancials` has been *answered*. {@link canViewFinancials} starts `true`
   * so a figure is never hidden from the role it belongs to while the answer is outstanding, which
   * is the right default for hiding, but not for *asking*: the purchase-cost request is only ever
   * sent once the capability is known to be granted.
   */
  private isFinancialsAccessAnswered = false;

  private readonly sessionActiveSubscription: Subscription;
  private readonly financialsAccessSubscription: Subscription;

  constructor(
    private readonly productService: ProductService,
    private readonly machineService: MachineService,
    private readonly siteService: SiteService,
    private readonly nayaxSalesSyncService: NayaxSalesSyncService,
    private readonly dashboardService: DashboardService,
    private readonly productPurchaseCostService: ProductPurchaseCostService,
    supportSession: SupportSessionService,
    access: AccessService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.financialsAccessSubscription = access.allows('Dashboard.ViewFinancials').subscribe((allowed) => {
      this.canViewFinancials = allowed;
      this.isFinancialsAccessAnswered = true;
      // The answer can arrive after the reorder-alert list, which carries the products to ask
      // about; the read is deferred until both are known, and re-evaluated if the answer changes.
      if (this.hasReorderAlerts) this.refreshPurchaseCosts();
    });
  }

  ngOnInit(): void {
    this.refreshLowStock();
    // The summary decides whether the live-fleet half of this page runs at all, so it starts it
    // rather than running beside it; see `refreshSummary`.
    this.refreshSummary();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
    this.financialsAccessSubscription.unsubscribe();
  }

  /**
   * The four headline cards' authoritative source (issue #459/#460): one request, so "Sales this
   * week", "Needs refill", "Needs ordering" and "Inventory" always describe the same backend read.
   * A request failure clears `summary` rather than leaving a stale one, so a failed metric is never
   * presented as a real zero.
   *
   * It is also where this page learns whether the business has a usable sales provider (issue
   * #727), which is why the live-fleet half of the page - the sales synchronization and the Sites
   * and Machines sections - is started from here instead of beside it. Without that order the
   * synchronization would already have been sent by the time the answer arrived, and "sales sync is
   * not called when the provider is not usable" cannot be satisfied after the fact. A business with
   * a usable provider gets the same calls in the same order as before, one summary later.
   */
  refreshSummary(): void {
    this.isLoadingSummary = true;
    this.isSummaryFailed = false;
    this.dashboardService.getSummary().subscribe({
      next: (summary) => {
        this.summary = summary;
        this.isLoadingSummary = false;
        this.providerAvailability = providerAvailabilityOf(summary);
        this.refreshSalesDashboard();
      },
      error: () => {
        this.summary = null;
        this.isSummaryFailed = true;
        this.isLoadingSummary = false;
        this.providerAvailability = 'unknown';
        this.refreshSalesDashboard();
      }
    });
  }

  /**
   * Coordinates the home dashboard's Sites and Machines figures so both read the same freshness
   * boundary (issue #187): the latest Nayax sales are synchronized once, then Sites and Machines
   * are loaded from the resulting persisted `NayaxSales` data. If synchronization fails, existing
   * persisted data is still loaded rather than fabricated as zero, but `isSalesSyncFailed` flags
   * that the figures may be stale so the UI never presents them as freshly synchronized. A `429`
   * from the business's run limit (issue #681) is not a failure: the business refreshed moments
   * ago, so it loads the same persisted figures under `isSalesSyncRecentlyRefreshed` instead.
   *
   * Inside a support session (issue #661) the synchronization is not attempted at all: it is a
   * `POST`, and it is a Nayax call, and a session is refused on both counts (issue #551). The
   * persisted figures are still loaded, flagged as not synchronized rather than as failed.
   *
   * For a business with no usable provider (issue #727) none of this runs: there is nothing to
   * synchronize, the call could only be refused, and the two sections it refreshes are not
   * rendered. No notice is shown either - a sync that was never attempted neither failed nor
   * succeeded, and the page says what the business *does* have instead.
   *
   * Since issue #843 the server may answer that it skipped the run because this business
   * synchronized less than three minutes ago. That is a third answer and not a refusal: the figures
   * below are current, so no notice is shown and {@link salesSyncedAtUtc} carries the instant the
   * server reported instead. `force` is the Refresh control's, and the only thing that bypasses it.
   */
  refreshSalesDashboard(force = false): void {
    this.isSalesSyncFailed = false;
    this.isSalesSyncRecentlyRefreshed = false;
    this.isSalesSyncSkippedInSupportView = false;
    this.salesSyncedAtUtc = null;
    if (!this.isProviderUsable) {
      this.isLoadingSites = false;
      this.isLoadingMachines = false;
      return;
    }
    this.isLoadingSites = true;
    this.isLoadingMachines = true;
    if (this.sessionActive) {
      this.isSalesSyncSkippedInSupportView = true;
      this.refreshMachines();
      this.refreshSites();
      return;
    }
    this.isRefreshingSales = true;
    this.nayaxSalesSyncService.syncLatest(force).subscribe({
      next: (result) => {
        this.isRefreshingSales = false;
        // A skipped run reports when the sales were synchronized; a run that happened has just
        // finished, so the instant is this moment.
        this.salesSyncedAtUtc = result.lastSyncedAtUtc ?? new Date().toISOString();
        this.refreshMachines();
        this.refreshSites();
      },
      error: (error: unknown) => {
        this.isRefreshingSales = false;
        if (error instanceof HttpErrorResponse && error.status === 429) {
          this.isSalesSyncRecentlyRefreshed = true;
        } else {
          this.isSalesSyncFailed = true;
        }
        this.refreshMachines();
        this.refreshSites();
      }
    });
  }

  /**
   * The Refresh control: synchronizes the latest sales even when the server would have skipped the
   * run as recent, then reloads Sites and Machines from the result (issue #843).
   *
   * It exists because the three-minute skip is a default and not a verdict - an operator who has
   * just restocked a machine or taken a payment wants to see it now - and it is the only caller
   * that forces a run. A forced run is still bounded by the business's run limit, so holding the
   * button down costs the upstream the same ten runs per two minutes it already allowed, answered
   * as the existing "refreshed recently" notice.
   */
  refreshSales(): void {
    this.refreshSalesDashboard(true);
  }

  refreshSites(): void {
    this.isLoadingSites = true;
    this.sitesError = null;
    this.siteService.getAll().subscribe({
      next: (sites) => {
        this.sites = sites;
        this.isLoadingSites = false;
      },
      error: (error: unknown) => {
        this.sites = [];
        this.sitesError = sectionUnavailableText(error, 'The Sites section');
        this.isLoadingSites = false;
      }
    });
  }

  refreshLowStock(): void {
    this.isLoadingLowStock = true;
    this.reorderAlertsError = null;
    this.productService.getLowStock().subscribe({
      next: (listing) => {
        this.lowStock = listing.products.filter((product) => product.isActive !== false);
        this.reorderMode = listing.mode;
        this.reorderMachineNeedUnavailableReason = listing.machineNeedUnavailableReason;
        this.isLoadingLowStock = false;
        this.hasReorderAlerts = true;
        this.refreshPurchaseCosts();
      },
      error: (error: unknown) => {
        this.lowStock = [];
        // A mode nobody published must not survive the failure and label the next render.
        this.reorderMode = null;
        this.reorderMachineNeedUnavailableReason = null;
        this.reorderAlertsError = sectionUnavailableText(error, 'The reorder-alert list');
        this.isLoadingLowStock = false;
        this.hasReorderAlerts = false;
        // Costs read for a list that is no longer displayed are dropped with it.
        this.refreshPurchaseCosts();
      }
    });
  }

  /**
   * Reads the last and cheapest recorded purchase cost of every product the Reorder Alerts table is
   * about, in one request (issue #789, over issue #788's bulk endpoint), and is also what the Retry
   * control calls.
   *
   * It is the one place the three preconditions live, because each of them has to leave the columns
   * saying something different. Without `Dashboard.ViewFinancials` the request is never sent at all
   * - these are supplier purchasing figures, and a role that may not see them must not cause them
   * to be fetched - and the same holds while the capability is still unanswered. An **empty** alert
   * list is asked nothing either: `getSummaries([])` deliberately means "every product of the
   * business" (issue #788), and this table is only ever about the products in front of the reader.
   *
   * Every call supersedes the previous one and clears what it read, so a changed product set never
   * renders the previous set's costs, and a response that arrives for a superseded request - a
   * slower first read, or the failure a Retry has already replaced - is discarded instead of being
   * published over the current answer.
   */
  refreshPurchaseCosts(): void {
    const request = ++this.purchaseCostRequest;
    this.purchaseCosts = new Map();
    this.purchaseCostState = 'idle';

    const productIds = this.lowStock.map((product) => product.id);
    if (!this.arePurchaseCostColumnsShown || !this.isFinancialsAccessAnswered || productIds.length === 0) return;

    this.purchaseCostState = 'loading';
    this.productPurchaseCostService.getSummaries(productIds).subscribe({
      next: (summaries) => {
        if (request !== this.purchaseCostRequest) return;
        this.purchaseCosts = new Map(summaries.map((summary) => [summary.productId, summary]));
        this.purchaseCostState = 'loaded';
      },
      error: () => {
        if (request !== this.purchaseCostRequest) return;
        this.purchaseCostState = 'failed';
      }
    });
  }

  refreshMachines(): void {
    this.isLoadingMachines = true;
    this.machinesError = null;
    this.machineService.getAll().subscribe({
      next: (m) => {
        this.machines = m;
        this.isLoadingMachines = false;
      },
      error: (error: unknown) => {
        this.machines = [];
        this.machinesError = sectionUnavailableText(error, 'The Machines section');
        this.isLoadingMachines = false;
      }
    });
  }

  money(value: number): string {
    return money(value);
  }

  /**
   * The week's sales, or `null` when this caller was not given them (issue #527): a role without
   * `Dashboard.ViewFinancials` receives a summary with no `salesThisWeek` object at all.
   */
  get salesThisWeek(): DashboardSalesThisWeek | null {
    return this.summary?.salesThisWeek ?? null;
  }

  /**
   * Whether the business has a sales provider this page can show provider figures for (issue
   * #727). Only an answered `NayaxNotConnected` makes it false: `PendingPermissions` with one read
   * ungranted is a usable connection with one feature missing (decision P9 of issue #329), a
   * support session keeps its existing behaviour, and an unread summary claims nothing.
   */
  get isProviderUsable(): boolean {
    return this.providerAvailability !== 'notConnected';
  }

  /**
   * How old the sales behind this page's figures are, or `null` when that is not known (issue
   * #843) - a failure, the run limit, a support session, or a provider there is nothing to
   * synchronize for.
   *
   * Whole minutes, floored, because that is the precision the statement is worth: the exact
   * wall-clock instant is on the same element's `title`, in the business's own timezone. An age of
   * zero - or a negative one, which a browser clock behind the server's produces - reads as "just
   * now" rather than "0 min ago", which would look like a stopped clock.
   */
  get salesSyncedLabel(): string | null {
    if (this.salesSyncedAtUtc === null) return null;
    const syncedAt = new Date(this.salesSyncedAtUtc);
    if (Number.isNaN(syncedAt.getTime())) return null;
    const minutes = Math.floor((Date.now() - syncedAt.getTime()) / 60_000);
    return minutes <= 0 ? 'Sales synced just now' : `Sales synced ${minutes} min ago`;
  }

  /**
   * Whether to offer the Refresh control. It belongs to the live-fleet half of the page, so it is
   * hidden for a business with no usable provider, and inside a support session, where the call
   * resolves no Nayax credentials at all and could only be refused (issue #551). It stays offered
   * after a failure and after the run limit's refusal: asking again is exactly what an operator
   * wants then, and the limit answers for itself.
   */
  get isSalesRefreshShown(): boolean {
    return this.isLiveFleetShown && !this.sessionActive;
  }

  /**
   * Whether to render the Sites and Machines sections and the "Needs refill" card - everything on
   * this page whose every figure is a figure about machines. Without a provider they are not shown
   * at all rather than shown as empty or unavailable: there is no fleet to report on, and the page
   * is about what the business does have.
   */
  get isLiveFleetShown(): boolean {
    return this.isProviderUsable;
  }

  /**
   * Whether to render the "Sales this week" card at all (issues #528 and #727).
   *
   * It is hidden when a summary that *was* loaded does not carry the figure, which is what the
   * API's redaction looks like, and for a business with no usable provider, whose sales this
   * application does not have. A summary that is still loading or that failed keeps the card,
   * reporting "Unavailable" exactly as before: not knowing the figure yet, or at all, is a
   * different thing from having been told it is not this member's to see.
   */
  get isSalesCardShown(): boolean {
    return this.isProviderUsable && (this.summary === null || this.summary.salesThisWeek !== undefined);
  }

  /**
   * Whether to render the "Inventory" card, on the same rule: its primary value is the
   * business-owned inventory valuation, so a caller the valuation was withheld from is shown no
   * inventory card rather than a card with its figure removed. An *incomplete* valuation is
   * `inventoryValueAtCost: null`, which is present and keeps the card (see `inventoryValueDisplay`).
   */
  get isInventoryCardShown(): boolean {
    return this.summary === null || this.summary.inventory.inventoryValueAtCost !== undefined;
  }

  /**
   * The "Sales this week" card's value and comparison line, read without the template having to
   * narrow an optional object. They are only rendered while `isSalesCardShown` is true, so the
   * `null` branch covers a summary that is still loading or that failed, never a redacted one.
   */
  get salesValueDisplay(): string {
    const week = this.salesThisWeek;
    return week === null ? 'Unavailable' : money(week.sales);
  }

  get salesComparisonDisplay(): string {
    const week = this.salesThisWeek;
    return week === null ? 'Comparison unavailable' : this.salesComparisonText(week);
  }

  get salesComparisonDisplayClass(): string {
    const week = this.salesThisWeek;
    return week === null ? 'value-muted' : this.salesComparisonClass(week);
  }

  /**
   * The "Sales this week" comparison line. The percentage/amount is never recomputed here - only
   * `week.changePercent`/`week.comparisonNote`, already decided by
   * `Inventory.Domain.Reporting.Dashboard.PeriodRevenueComparisonPolicy`, choose what is shown.
   */
  salesComparisonText(week: DashboardSalesThisWeek): string {
    if (!week.isComparisonAvailable) {
      return week.comparisonNote ?? 'Comparison with last week is unavailable';
    }
    if (week.changePercent == null) {
      return week.comparisonNote ?? 'No prior-period sales to compare';
    }
    const arrow = week.changePercent >= 0 ? '↑' : '↓';
    return `${arrow} ${Math.abs(week.changePercent).toFixed(1)}% vs same time last week`;
  }

  salesComparisonClass(week: DashboardSalesThisWeek): string {
    if (!week.isComparisonAvailable || week.changePercent == null) return 'value-muted';
    return week.changePercent >= 0 ? 'value-positive' : 'value-negative';
  }

  /**
   * The "Needs refill" card's primary value: the figure, or - inside a support session (issue
   * #554) - "Unavailable", matching the sales/inventory cards' own unavailable display rather than
   * a `0` that would read as a known, adequately stocked fleet.
   */
  refillValueDisplay(refill: DashboardRefillSummary | UnavailableDashboardSection): string {
    return isDashboardSectionUnavailable(refill) ? 'Unavailable' : String(refill.machinesNeedingRefill);
  }

  refillValueClass(refill: DashboardRefillSummary | UnavailableDashboardSection): string {
    return isDashboardSectionUnavailable(refill) ? 'value-muted' : '';
  }

  /**
   * The "Needs refill" supporting detail. `machinesEvaluated` is what makes a zero honest: zero of
   * zero machines is nothing to evaluate yet, zero of many is every evaluated machine adequately
   * stocked. A withheld card carries the reason's own sentence (issue #727): inside a support
   * session (issue #554) the fleet is never read at all, and for a feature Nayax has not granted
   * the card says that instead - the two are different answers and were one sentence until #727.
   */
  refillFooterText(refill: DashboardRefillSummary | UnavailableDashboardSection): string {
    if (isDashboardSectionUnavailable(refill)) {
      return this.unavailableSectionText(refill.reason, 'Machine refill status');
    }
    if (refill.machinesEvaluated === 0) return 'No machines to evaluate yet';
    if (refill.machinesNeedingRefill === 0) return `All ${refill.machinesEvaluated} machines adequately stocked`;
    return `${refill.lowSelectionCount} low · ${refill.emptySelectionCount} empty selections across ${refill.machinesEvaluated} machines evaluated`;
  }

  refillFooterClass(refill: DashboardRefillSummary | UnavailableDashboardSection): string {
    if (isDashboardSectionUnavailable(refill)) return 'value-muted';
    if (refill.machinesEvaluated === 0) return 'value-muted';
    return refill.machinesNeedingRefill === 0 ? 'value-positive' : 'value-muted';
  }

  /** The "Needs ordering" card's primary value, on the same rule as {@link refillValueDisplay}. */
  orderingValueDisplay(ordering: DashboardOrderingSummary | UnavailableDashboardSection): string {
    return isDashboardSectionUnavailable(ordering) ? 'Unavailable' : String(ordering.productsNeedingOrdering);
  }

  orderingValueClass(ordering: DashboardOrderingSummary | UnavailableDashboardSection): string {
    return isDashboardSectionUnavailable(ordering) ? 'value-muted' : '';
  }

  /**
   * The storage-only label the "Needs ordering" card carries, or `null` for a machine-aware count
   * (issue #727). A storage-only count is a complete answer about storage rather than a partial one
   * about the fleet, and saying so is what keeps it from being read as a machine-aware figure. The
   * reason comes from the refill section of the same response, which is where this summary states
   * why the machines are missing.
   */
  get orderingModeNote(): string | null {
    const ordering = this.summary?.needsOrdering;
    if (ordering === undefined || isDashboardSectionUnavailable(ordering) || ordering.mode !== 'StorageOnly') {
      return null;
    }
    return storageOnlyReorderLabel(this.summaryMachineNeedUnavailableReason);
  }

  /** The "Needs ordering" supporting detail, over the same catalogue the count was taken over. */
  orderingFooterText(ordering: DashboardOrderingSummary | UnavailableDashboardSection): string {
    if (isDashboardSectionUnavailable(ordering)) {
      return this.unavailableSectionText(ordering.reason, 'Ordering figures');
    }
    if (ordering.productsEvaluated === 0) return 'No products in the catalogue yet';
    if (ordering.productsNeedingOrdering === 0) return `All ${ordering.productsEvaluated} products adequately stocked`;
    return `of ${ordering.productsEvaluated} products in the catalogue`;
  }

  orderingFooterClass(ordering: DashboardOrderingSummary | UnavailableDashboardSection): string {
    if (isDashboardSectionUnavailable(ordering)) return 'value-muted';
    if (ordering.productsEvaluated === 0) return 'value-muted';
    return ordering.productsNeedingOrdering === 0 ? 'value-positive' : 'value-muted';
  }

  /**
   * The storage-only label the Reorder Alerts section carries, or `null` in machine-aware mode and
   * for a listing that published no mode at all. It is shown for an **empty** storage-only list
   * too: "nothing alerts" means something different when the machines were never read.
   */
  get reorderModeNote(): string | null {
    return this.reorderMode === 'StorageOnly'
      ? storageOnlyReorderLabel(this.reorderMachineNeedUnavailableReason)
      : null;
  }

  /**
   * Whether the Reorder Alerts table may show the machines' replenishment need. In storage-only
   * mode it may not: `machineReplenishmentNeed` is `0` there because nobody read the machines, and
   * a column of zeroes would state that the machines need nothing.
   */
  get isMachineNeedShownInReorderAlerts(): boolean {
    return this.reorderMode !== 'StorageOnly';
  }

  /**
   * Whether the Reorder Alerts table carries the "Last purchase" and "Cheapest purchase" columns at
   * all (issue #789). They are supplier purchasing figures, so they belong to
   * `Dashboard.ViewFinancials` exactly as the endpoint behind them does: a role without it is shown
   * no column rather than a column of refusals, and nothing is requested on its behalf.
   */
  get arePurchaseCostColumnsShown(): boolean {
    return this.canViewFinancials;
  }

  /** Whether to offer Retry: only for a failed read of columns that are actually shown. */
  get isPurchaseCostRetryShown(): boolean {
    return this.arePurchaseCostColumnsShown && this.purchaseCostState === 'failed';
  }

  /** The "Last purchase" cell: who the product was last bought from, and what was paid. */
  lastPurchaseDisplay(product: Product): string {
    return this.purchaseCostDisplay(product, (summary) =>
      purchaseCostText(summary.lastSupplierName, summary.lastUnitCost)
    );
  }

  /** The "Cheapest purchase" cell: the cheapest unit cost ever paid, and who it was paid to. */
  cheapestPurchaseDisplay(product: Product): string {
    return this.purchaseCostDisplay(product, (summary) =>
      purchaseCostText(summary.lowestSupplierName, summary.lowestUnitCost)
    );
  }

  /** Muted presentation for a cell that carries a state rather than a recorded cost. */
  purchaseCostCellClass(product: Product): string {
    return this.purchaseCostState === 'loaded' && this.purchaseCosts.has(product.id) ? '' : 'value-muted';
  }

  /**
   * One purchase-cost cell, from the state of the one request behind both columns. The three
   * substitutes are deliberately distinct: a read that is outstanding or has not been issued says
   * `Loading`, a failed one says `Unavailable`, and `—` is said only after a successful response
   * that carried no entry for this product - the one of the three that means "never bought".
   */
  private purchaseCostDisplay(product: Product, text: (summary: ProductPurchaseCostSummary) => string): string {
    if (this.purchaseCostState === 'failed') return PURCHASE_COST_UNAVAILABLE;
    if (this.purchaseCostState !== 'loaded') return PURCHASE_COST_LOADING;
    const summary = this.purchaseCosts.get(product.id);
    return summary === undefined ? NO_PURCHASE_HISTORY : text(summary);
  }

  /**
   * The sentence for a withheld summary section, by the reason the backend published. The reason is
   * a stable code and never displayed; an unrecognised one reports the feature as unavailable
   * without inventing a cause for it.
   */
  private unavailableSectionText(reason: string, feature: string): string {
    if (reason === SUPPORT_VIEW_REASON) return NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE;
    const unavailability = nayaxUnavailabilityOfReason(reason);
    return unavailability === null ? `${feature} is unavailable.` : nayaxUnavailableMessage(unavailability, feature);
  }

  /** Why this summary's machine figures are missing, as its refill section states it. */
  private get summaryMachineNeedUnavailableReason(): NayaxUnavailability | null {
    const refill = this.summary?.needsRefill;
    return refill !== undefined && isDashboardSectionUnavailable(refill)
      ? nayaxUnavailabilityOfReason(refill.reason)
      : null;
  }

  /**
   * The "Inventory" card's primary value: the authoritative cost-basis inventory value from the
   * Dashboard summary, never calculated from a product's own fields on the frontend.
   * Incomplete costing is shown as unavailable, never as a real `$0.00`.
   */
  inventoryValueDisplay(inventory: DashboardInventorySummary): string {
    if (!inventory.isInventoryValueComplete || inventory.inventoryValueAtCost == null) return 'Unavailable';
    return money(inventory.inventoryValueAtCost);
  }

  inventoryValueHelpText(inventory: DashboardInventorySummary): string {
    if (inventory.isInventoryValueComplete) return 'Business-owned inventory at cost';
    return `${inventory.productsWithUnknownCost} of ${inventory.productCount} products missing cost data`;
  }

  /** Makes explicit that storage quantity is home/storage stock only, not a whole-business count. */
  inventoryScopeText(inventory: DashboardInventorySummary): string {
    return `${inventory.productCount} products · ${inventory.unitsInStorage} units in storage (excludes machines)`;
  }

  trendLabel(current: number | null | undefined, previous: number | null | undefined): string {
    return trendLabel(current, previous);
  }

  trendClass(current: number | null | undefined, previous: number | null | undefined): string {
    return trendClass(current, previous);
  }

  directMargin(directProfit: number, sales: number): number {
    return this.margin(directProfit, sales);
  }

  get totalTodaySales(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayGrossRevenue ?? 0), 0);
  }

  /**
   * Whether every machine's today's gross revenue is known (issue #699): a fleet that includes a
   * withheld/unknown figure must not have the Totals row read its `?? 0` fallback as a real zero
   * and report a fabricated `$0.00`, the same rule {@link isTodayProfitComplete} already follows
   * for direct profit.
   */
  get isTodayRevenueComplete(): boolean {
    return this.machines.every(m => m.todayGrossRevenue != null);
  }

  get totalTodayDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayDirectProfit ?? 0), 0);
  }

  get isTodayProfitComplete(): boolean {
    return this.machines.every(m => m.todayDirectProfit != null);
  }

  get totalCurrentWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekGrossRevenue ?? 0), 0);
  }

  get isCurrentWeekRevenueComplete(): boolean {
    return this.machines.every(m => m.currentWeekGrossRevenue != null);
  }

  get totalCurrentWeekDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekDirectProfit ?? 0), 0);
  }

  get isCurrentWeekProfitComplete(): boolean {
    return this.machines.every(m => m.currentWeekDirectProfit != null);
  }

  get totalPreviousComparableWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.previousComparableWeekGrossRevenue ?? m.lastWeekGrossRevenue ?? 0), 0);
  }

  get totalLastWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekGrossRevenue ?? 0), 0);
  }

  get isLastWeekRevenueComplete(): boolean {
    return this.machines.every(m => m.lastWeekGrossRevenue != null);
  }

  get totalLastWeekDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekDirectProfit ?? 0), 0);
  }

  get isLastWeekProfitComplete(): boolean {
    return this.machines.every(m => m.lastWeekDirectProfit != null);
  }

  get totalMonthToDateSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.monthToDateGrossRevenue ?? 0), 0);
  }

  get isMonthToDateRevenueComplete(): boolean {
    return this.machines.every(m => m.monthToDateGrossRevenue != null);
  }

  get totalMonthToDateDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.monthToDateDirectProfit ?? 0), 0);
  }

  get isMonthToDateProfitComplete(): boolean {
    return this.machines.every(m => m.monthToDateDirectProfit != null);
  }

  get totalDirectMargin(): number {
    return this.margin(this.totalCurrentWeekDirectProfit, this.totalCurrentWeekSales);
  }

  private margin(profit: number, sales: number): number {
    return sales === 0 ? 0 : (profit / sales) * 100;
  }

  siteStockClass(site: Site): string {
    return siteStockClass(site);
  }

  /**
   * Extends the Machines row's real anchor link to the rest of the row (issue #494), the same
   * pattern `SiteListComponent.followRowLink` already uses: the anchor - not the row - owns
   * focus, Enter activation, and ctrl/cmd/middle-click, so this only activates it for a click or
   * Enter elsewhere in the row. A click on a control inside the row - the anchor included, which
   * would otherwise navigate twice - and a modified or non-primary click are left to the browser.
   */
  followMachineRowLink(rowEvent: Event): void {
    const event = rowEvent as MouseEvent | KeyboardEvent;
    const ownControl = (event.target as Element | null)?.closest(
      'a, button, input, select, textarea, label, [role="button"]'
    );
    const browserHandlesIt =
      event.defaultPrevented ||
      (event instanceof MouseEvent && event.button !== 0) ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey ||
      event.altKey;
    if (ownControl || browserHandlesIt) return;

    (event.currentTarget as Element).querySelector<HTMLAnchorElement>('a.table-row-anchor')?.click();
  }
}

/** What a purchase-cost cell says while its one request is outstanding or has not been issued. */
const PURCHASE_COST_LOADING = 'Loading';

/** What it says when that request failed: nothing is known, which is not "never bought". */
const PURCHASE_COST_UNAVAILABLE = 'Unavailable';

/** What it says for a product a successful answer carried no purchase history for. */
const NO_PURCHASE_HISTORY = '—';

/**
 * One purchase-cost cell's text: the supplier that purchase was made from - or `None` when the
 * purchase recorded no supplier, which is a fact about the purchase and never another purchase's
 * supplier - and the cost, formatted to two decimals through the shared `money`. A `0` here is a
 * real recorded cost (a free sample, a replacement delivery) and is shown as `$0.00`; the costs
 * themselves are the backend's, formatted only for display.
 */
function purchaseCostText(supplierName: string | null, unitCost: number): string {
  return `${supplierName ?? 'None'} · ${money(unitCost)}`;
}

/**
 * What one summary says about the provider (issue #727). The refill card is the one section of this
 * response whose every figure comes from the live fleet, so it is the section that states whether
 * the fleet was readable: a real card means it was, and a withheld one carries the reason it was
 * not. A reason this build does not recognise is `unknown` - it keeps the connected behaviour
 * rather than hiding sections because of a code nobody here understands.
 */
function providerAvailabilityOf(summary: DashboardSummary): DashboardProviderAvailability {
  const refill = summary.needsRefill;
  if (!isDashboardSectionUnavailable(refill)) return 'usable';
  if (refill.reason === SUPPORT_VIEW_REASON) return 'supportView';
  switch (nayaxUnavailabilityOfReason(refill.reason)) {
    case 'NayaxNotConnected':
      return 'notConnected';
    case 'PermissionNotGranted':
      return 'permissionNotGranted';
    default:
      return 'unknown';
  }
}

/**
 * What to show in place of a section whose own request failed. A `409` carrying one of the two
 * Nayax availability codes is that feature's own availability answer and gets its per-feature
 * message (decision P9 of issue #329); every other failure is an error and is reported as one - a
 * transport failure or a real Nayax outage must never be presented as a business that is simply
 * not connected.
 */
function sectionUnavailableText(error: unknown, feature: string): string {
  const unavailability = nayaxUnavailabilityOfError(error);
  return unavailability === null
    ? `${feature} could not be loaded. Reload the page to try again.`
    : nayaxUnavailableMessage(unavailability, feature);
}
