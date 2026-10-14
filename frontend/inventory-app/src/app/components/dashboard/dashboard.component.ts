import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { DashboardService } from '../../services/dashboard.service';
import {
  DashboardInventorySummary,
  DashboardOrderingSummary,
  DashboardRefillSummary,
  DashboardSalesThisWeek,
  DashboardSummary,
  Product,
  Machine,
  Site
} from '../../models/models';
import { trendLabel, trendClass } from '../../formatting/revenue-trend';
import { siteStockClass } from '../../formatting/site-stock-status';
import { money } from '../reports/report-formatting';
import { IconComponent } from '../shared/icon.component';
import { LegacyBusinessSettingsPromptComponent } from '../shared/legacy-business-settings-prompt/legacy-business-settings-prompt.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink, IconComponent, LegacyBusinessSettingsPromptComponent],
  templateUrl: './dashboard.component.html'
})
export class DashboardComponent implements OnInit {
  lowStock: Product[] = [];
  machines: Machine[] = [];
  sites: Site[] = [];
  isLoadingSites = false;
  isLoadingMachines = false;
  isLoadingLowStock = false;
  isSalesSyncFailed = false;
  summary: DashboardSummary | null = null;
  isLoadingSummary = false;
  isSummaryFailed = false;

  constructor(
    private productService: ProductService,
    private machineService: MachineService,
    private siteService: SiteService,
    private nayaxSalesSyncService: NayaxSalesSyncService,
    private dashboardService: DashboardService
  ) {}

  ngOnInit(): void {
    this.refreshLowStock();
    this.refreshSalesDashboard();
    this.refreshSummary();
  }

  /**
   * The four headline cards' authoritative source (issue #459/#460): one request, so "Sales this
   * week", "Needs refill", "Needs ordering" and "Inventory" always describe the same backend read.
   * A request failure clears `summary` rather than leaving a stale one, so a failed metric is never
   * presented as a real zero.
   */
  refreshSummary(): void {
    this.isLoadingSummary = true;
    this.isSummaryFailed = false;
    this.dashboardService.getSummary().subscribe({
      next: (summary) => {
        this.summary = summary;
        this.isLoadingSummary = false;
      },
      error: () => {
        this.summary = null;
        this.isSummaryFailed = true;
        this.isLoadingSummary = false;
      }
    });
  }

  /**
   * Coordinates the home dashboard's Sites and Machines figures so both read the same freshness
   * boundary (issue #187): the latest Nayax sales are synchronized once, then Sites and Machines
   * are loaded from the resulting persisted `NayaxSales` data. If synchronization fails, existing
   * persisted data is still loaded rather than fabricated as zero, but `isSalesSyncFailed` flags
   * that the figures may be stale so the UI never presents them as freshly synchronized.
   */
  refreshSalesDashboard(): void {
    this.isSalesSyncFailed = false;
    this.isLoadingSites = true;
    this.isLoadingMachines = true;
    this.nayaxSalesSyncService.syncLatest().subscribe({
      next: () => {
        this.refreshMachines();
        this.refreshSites();
      },
      error: () => {
        this.isSalesSyncFailed = true;
        this.refreshMachines();
        this.refreshSites();
      }
    });
  }

  refreshSites(): void {
    this.isLoadingSites = true;
    this.siteService.getAll().subscribe({
      next: (sites) => {
        this.sites = sites;
      },
      error: () => {
        this.sites = [];
      },
      complete: () => {
        this.isLoadingSites = false;
      }
    });
  }

  refreshLowStock(): void {
    this.isLoadingLowStock = true;
    this.productService.getLowStock().subscribe({
      next: (p) => {
        this.lowStock = p.filter((product) => product.isActive !== false);
      },
      error: () => {
        this.lowStock = [];
      },
      complete: () => {
        this.isLoadingLowStock = false;
      }
    });
  }

  refreshMachines(): void {
    this.isLoadingMachines = true;
    this.machineService.getAll().subscribe({
      next: (m) => {
        this.machines = m;
      },
      error: () => {
        this.machines = [];
      },
      complete: () => {
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
   * Whether to render the "Sales this week" card at all (issue #528).
   *
   * It is hidden only when a summary that *was* loaded does not carry the figure, which is what
   * the API's redaction looks like. A summary that is still loading or that failed keeps the card,
   * reporting "Unavailable" exactly as before: not knowing the figure yet, or at all, is a
   * different thing from having been told it is not this member's to see.
   */
  get isSalesCardShown(): boolean {
    return this.summary === null || this.summary.salesThisWeek !== undefined;
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
   * The "Needs refill" supporting detail. `machinesEvaluated` is what makes a zero honest: zero of
   * zero machines is nothing to evaluate yet, zero of many is every evaluated machine adequately
   * stocked.
   */
  refillFooterText(refill: DashboardRefillSummary): string {
    if (refill.machinesEvaluated === 0) return 'No machines to evaluate yet';
    if (refill.machinesNeedingRefill === 0) return `All ${refill.machinesEvaluated} machines adequately stocked`;
    return `${refill.lowSelectionCount} low · ${refill.emptySelectionCount} empty selections across ${refill.machinesEvaluated} machines evaluated`;
  }

  refillFooterClass(refill: DashboardRefillSummary): string {
    if (refill.machinesEvaluated === 0) return 'value-muted';
    return refill.machinesNeedingRefill === 0 ? 'value-positive' : 'value-muted';
  }

  /** The "Needs ordering" supporting detail, over the same catalogue the count was taken over. */
  orderingFooterText(ordering: DashboardOrderingSummary): string {
    if (ordering.productsEvaluated === 0) return 'No products in the catalogue yet';
    if (ordering.productsNeedingOrdering === 0) return `All ${ordering.productsEvaluated} products adequately stocked`;
    return `of ${ordering.productsEvaluated} products in the catalogue`;
  }

  orderingFooterClass(ordering: DashboardOrderingSummary): string {
    if (ordering.productsEvaluated === 0) return 'value-muted';
    return ordering.productsNeedingOrdering === 0 ? 'value-positive' : 'value-muted';
  }

  /**
   * The "Inventory" card's primary value: the authoritative cost-basis inventory value from the
   * Dashboard summary, never calculated from `Product.unitPrice`/`quantityInStock` on the frontend.
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

  trendLabel(current: number, previous: number): string {
    return trendLabel(current, previous);
  }

  trendClass(current: number, previous: number): string {
    return trendClass(current, previous);
  }

  directMargin(directProfit: number, sales: number): number {
    return this.margin(directProfit, sales);
  }

  get totalTodaySales(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayGrossRevenue ?? 0), 0);
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

  get totalLastWeekDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekDirectProfit ?? 0), 0);
  }

  get isLastWeekProfitComplete(): boolean {
    return this.machines.every(m => m.lastWeekDirectProfit != null);
  }

  get totalMonthToDateSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.monthToDateGrossRevenue ?? 0), 0);
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
   * "After I fill the machines to their current required level, how much storage stock will I
   * have left?" (issue #243). A pure display derivation of the already-authoritative
   * `quantityInStock`/`machineReplenishmentNeed` the Reorder Alerts row already carries; it never
   * feeds back into `needToOrder` or any other reorder calculation.
   */
  stockAfterMachineNeed(product: Product): number {
    return product.quantityInStock - product.machineReplenishmentNeed;
  }

  stockAfterMachineNeedClass(product: Product): string {
    return this.stockAfterMachineNeed(product) < 0 ? 'value-negative font-semibold' : '';
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
