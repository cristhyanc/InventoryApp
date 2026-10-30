import { CommonModule } from '@angular/common';
import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { plural } from '../../../formatting/plural';
import { trendClass, trendLabel } from '../../../formatting/revenue-trend';
import { siteStockClass } from '../../../formatting/site-stock-status';
import { Machine, Site } from '../../../models/models';
import { money } from '../../reports/report-formatting';
import { IconComponent } from '../../shared/icon.component';

/**
 * The four sales periods the Dashboard's figures already carry (issue #972). Since issue #987 one
 * shared control chooses between them for both the site and the machine rows, on every width, so
 * no period is reachable on one layout only.
 */
export type PerformancePeriod = 'today' | 'week' | 'lastWeek' | 'monthToDate';

/**
 * One expandable row of the combined card (issue #987): a site and the machines standing at it, or
 * - as the last group - the machines at no listed site. `site` is `null` for that last group, and
 * `label` is `null` for the one unheaded group a card with no sites at all falls back to, because a
 * lone "Other machines" heading over the whole fleet says nothing.
 */
export interface SiteMachineGroup {
  readonly key: string;
  readonly site: Site | null;
  readonly label: string | null;
  readonly machines: readonly Machine[];
}

export const OTHER_MACHINES_LABEL = 'Other machines';

/**
 * What a machine row says in the stock and low/empty columns. Those two figures are published per
 * **site** and the machine read publishes nothing with the same meaning, so the row says the figure
 * is unavailable and {@link SiteMachinePerformanceComponent.machineStockNote} says why. A site's own
 * percentage is never repeated down its machines, and nothing here derives a machine's stock level.
 */
export const MACHINE_FIGURE_UNAVAILABLE = 'Unavailable';

/** What a row says for a period no comparison is published for; see {@link comparisonHeader}. */
export const NO_PERIOD_COMPARISON = 'No comparison for this period';

/** What a row says for a figure the response carried as `null` - never a fabricated `$0.00`. */
const UNAVAILABLE = 'Unavailable';

/**
 * Places each machine under its site (issue #987, over the `siteId` issue #980 added to the machine
 * read): one group per site, in the order the sites read returned them, each holding the machines
 * whose `siteId` is that site's in the order the machine read returned them; then every remaining
 * machine - at no site, or at a site the sites read did not return - under "Other machines". Each
 * machine lands in exactly one group.
 *
 * A site with no returned machine keeps its own, empty group: it is a site the business has, and
 * hiding it would make the card disagree with the Sites page. When no site is available at all - an
 * empty or failed sites read - the whole fleet is one unheaded, non-collapsible group, so the
 * machines stay readable instead of disappearing with the sites.
 *
 * Nothing is summed, averaged or derived here: the fleet totals are still taken over the whole
 * machine list, and no site figure is copied onto a machine.
 */
export function groupSitesAndMachines(
  sites: readonly Site[],
  machines: readonly Machine[]
): readonly SiteMachineGroup[] {
  const placed = new Set<Machine>();
  const groups: SiteMachineGroup[] = [];
  for (const site of sites) {
    const atSite = machines.filter((machine) => machine.siteId != null && machine.siteId === site.siteId);
    atSite.forEach((machine) => placed.add(machine));
    groups.push({ key: `site-${site.siteId}`, site, label: site.siteName, machines: atSite });
  }
  const others = machines.filter((machine) => !placed.has(machine));
  if (others.length > 0) {
    groups.push({ key: 'other', site: null, label: groups.length > 0 ? OTHER_MACHINES_LABEL : null, machines: others });
  }
  return groups;
}

/**
 * The Dashboard's **Site and machine performance** card (issue #987): the owner-approved Stitch
 * hierarchy, where each site is one summary row that expands to the machines standing at it.
 *
 * It replaces the two separate Site performance and Machine performance cards of issues #972/#980
 * and is a composition and presentation change only. Every figure, destination, capability rule and
 * unavailable state is the one those cards already had, read from the two existing responses:
 * `GET api/sites` for the site rows, `GET api/machines` for the machine rows and the fleet totals,
 * and nothing is calculated here beyond the direct margin the machine rows already displayed, which
 * is the same `profit / sales` presentation the former card did.
 *
 * Since issue #1012 `GET api/sites` publishes all four periods, so a site row shows a sales figure
 * for every one of them on both layouts. The comparison column is unchanged - "vs same time last
 * week" belongs to This week and no other period borrows it - and the fleet totals are still taken
 * over the machine rows alone.
 *
 * It is a dedicated feature component rather than more markup on the page (the page-composition
 * rule, `docs/architecture.md` § Page composition boundary): it owns substantial UI plus its own
 * state - the shared period selection and one expansion state per site - while the page keeps
 * owning the reads, their loading/failed states and the capability answer, which arrive as inputs.
 */
@Component({
  selector: 'app-site-machine-performance',
  standalone: true,
  imports: [CommonModule, RouterLink, IconComponent],
  templateUrl: './site-machine-performance.component.html'
})
export class SiteMachinePerformanceComponent {
  /** The sites read, in the order `GET api/sites` returned them. */
  @Input({ required: true }) sites: readonly Site[] = [];

  /** The machine read, in the order `GET api/machines` returned it. */
  @Input({ required: true }) machines: readonly Machine[] = [];

  @Input() isLoadingSites = true;
  @Input() isLoadingMachines = true;

  /**
   * What to say instead of a list when one of the two reads failed (issue #727). The two are kept
   * apart on purpose: a failed sites read must not hide machines that were read successfully, and a
   * failed machine read must not hide the sites.
   */
  @Input() sitesError: string | null = null;
  @Input() machinesError: string | null = null;

  /** Whether the signed-in member may see the sales, profit, margin and comparison figures. */
  @Input() canViewFinancials = true;

  /** The period every sales figure in the card is shown for; see {@link PerformancePeriod}. */
  period: PerformancePeriod = 'week';

  /** See {@link MACHINE_FIGURE_UNAVAILABLE}; the template needs it as a member. */
  readonly machineFigureUnavailable = MACHINE_FIGURE_UNAVAILABLE;

  readonly periods: readonly { readonly id: PerformancePeriod; readonly label: string }[] = [
    { id: 'today', label: 'Today' },
    { id: 'week', label: 'This week' },
    { id: 'lastWeek', label: 'Last week' },
    { id: 'monthToDate', label: 'Month to date' }
  ];

  /**
   * Said once in the card: the two stock columns are site figures, so a machine row leaves them
   * unavailable rather than repeating its site's percentage or deriving one of its own.
   */
  readonly machineStockNote =
    'Stock levels and low or empty selection counts are published per site. The machine read publishes no ' +
    'equivalent figure, so machine rows show them as unavailable rather than repeating a site figure.';

  /**
   * The sites whose machines the reader has collapsed, by group key. Collapsed rather than expanded
   * is what is remembered, so a site starts expanded - including a site that arrives in a later
   * read - and a refresh that returns the same site ids keeps exactly the groups the reader closed.
   */
  private readonly collapsedKeys = new Set<string>();

  private groupsOf?: {
    readonly sites: readonly Site[];
    readonly machines: readonly Machine[];
    readonly groups: readonly SiteMachineGroup[];
  };

  /**
   * The card's rows, under their sites. Recomputed only when either list is replaced, so change
   * detection hands the template the same groups and re-renders nothing.
   */
  get groups(): readonly SiteMachineGroup[] {
    const cached = this.groupsOf;
    if (cached?.sites === this.sites && cached.machines === this.machines) {
      return cached.groups;
    }
    const groups = groupSitesAndMachines(this.sites, this.machines);
    this.groupsOf = { sites: this.sites, machines: this.machines, groups };
    return groups;
  }

  /** Whether both reads are still outstanding, which is the card's own loading state. */
  get isLoading(): boolean {
    return this.isLoadingSites && this.isLoadingMachines;
  }

  /** Whether there is a hierarchy to render at all: a site, or a machine, was read. */
  get hasRows(): boolean {
    return this.sites.length > 0 || this.machines.length > 0;
  }

  get isSitesEmpty(): boolean {
    return !this.isLoadingSites && this.sitesError === null && this.sites.length === 0;
  }

  get isMachinesEmpty(): boolean {
    return !this.isLoadingMachines && this.machinesError === null && this.machines.length === 0;
  }

  /** The fleet totals belong to the machine read, so they are shown only once it has rows. */
  get areTotalsShown(): boolean {
    return this.canViewFinancials && this.machines.length > 0;
  }

  get columnCount(): number {
    return this.canViewFinancials ? 6 : 4;
  }

  get periodLabel(): string {
    return this.periods.find((period) => period.id === this.period)?.label ?? '';
  }

  /** The sales column's heading, which names the period its figures are for. */
  get salesHeader(): string {
    return `Sales · ${this.periodLabel}`;
  }

  /**
   * The comparison column's heading. "vs same time last week" is the one comparison these two
   * responses publish, and it belongs to this week's sales, so it is only ever said while This week
   * is the selected period; every other period gets the neutral heading and {@link NO_PERIOD_COMPARISON}
   * cells rather than a comparison borrowed from a period it is not about.
   */
  get comparisonHeader(): string {
    return this.period === 'week' ? 'vs same time last week' : 'Comparison';
  }

  selectPeriod(period: PerformancePeriod): void {
    this.period = period;
  }

  /** Whether a group can be collapsed at all: the one unheaded fallback group cannot. */
  isCollapsible(group: SiteMachineGroup): boolean {
    return group.label !== null;
  }

  isExpanded(group: SiteMachineGroup): boolean {
    return !this.isCollapsible(group) || !this.collapsedKeys.has(group.key);
  }

  toggle(group: SiteMachineGroup): void {
    if (this.collapsedKeys.has(group.key)) {
      this.collapsedKeys.delete(group.key);
    } else {
      this.collapsedKeys.add(group.key);
    }
  }

  /** The expander's accessible name; its state is `aria-expanded`, never part of the name. */
  expanderLabel(group: SiteMachineGroup): string {
    return `${group.label} machines`;
  }

  /** The id of the row group one site's machines are in, which its expander controls. */
  rowsRegionId(group: SiteMachineGroup): string {
    return `site-machines-${group.key}`;
  }

  /** The same, for the narrow layout's machine-card list. */
  cardsRegionId(group: SiteMachineGroup): string {
    return `site-machine-cards-${group.key}`;
  }

  machineCountLabel(count: number): string {
    return plural(count, 'machine');
  }

  /**
   * What an expanded group with no machine row says. A read that failed is never reported as a site
   * with nothing at it: the two are different answers, and only the successful read's emptiness is
   * a fact about the site.
   */
  get emptyGroupText(): string {
    if (this.machinesError !== null) return 'Machines could not be loaded for this site.';
    if (this.isLoadingMachines) return 'Loading machines…';
    return 'No machines at this site.';
  }

  siteStockClass(site: Site): string {
    return siteStockClass(site);
  }

  /** The stock bar's fill, on the same thresholds as {@link siteStockClass}'s badge beside it. */
  siteStockBarClass(site: Site): string {
    switch (siteStockClass(site)) {
      case 'badge-danger':
        return 'bg-rr-danger-text';
      case 'badge-warning':
        return 'bg-rr-warn-text';
      default:
        return 'bg-rr-ok-text';
    }
  }

  /**
   * The same classification in words, so the stock level is never conveyed by the badge's colour
   * alone. It reads the shared {@link siteStockClass} thresholds rather than restating them.
   */
  siteStockStatusLabel(site: Site): string {
    switch (siteStockClass(site)) {
      case 'badge-danger':
        return 'Critically low';
      case 'badge-warning':
        return 'Low';
      default:
        return 'Well stocked';
    }
  }

  /** The bar's width, held to 0-100 so a stray figure can never overflow its track. */
  stockBarWidth(site: Site): number {
    return Math.min(100, Math.max(0, site.totalStockPercentage));
  }

  /** One site's sales for the selected period, exactly as `GET api/sites` published them. */
  siteSalesText(site: Site): string {
    const sales = siteSales(site, this.period);
    return sales == null ? UNAVAILABLE : money(sales);
  }

  siteSalesClass(site: Site): string {
    return siteSales(site, this.period) == null ? 'value-muted' : '';
  }

  siteComparisonText(site: Site): string {
    if (this.period !== 'week') return NO_PERIOD_COMPARISON;
    return trendLabel(site.currentWeekRevenue, site.previousComparableWeekRevenue);
  }

  siteComparisonClass(site: Site): string {
    if (this.period !== 'week') return 'value-muted';
    return trendClass(site.currentWeekRevenue, site.previousComparableWeekRevenue);
  }

  /** One machine's sales for the selected period, or `Unavailable` for a withheld/unknown figure. */
  machineSalesText(machine: Machine): string {
    const { sales } = machinePeriodFigures(machine, this.period);
    return sales == null ? UNAVAILABLE : `${money(sales)} sales`;
  }

  /** One machine's direct profit and direct margin, never a `0` for an unknown one. */
  machineProfitText(machine: Machine): string {
    const { sales, profit } = machinePeriodFigures(machine, this.period);
    if (sales == null || profit == null) return 'Profit unavailable';
    return `${money(profit)} direct profit · ${margin(profit, sales).toFixed(1)}% direct margin`;
  }

  machineComparisonText(machine: Machine): string {
    if (this.period !== 'week') return NO_PERIOD_COMPARISON;
    return trendLabel(machine.currentWeekGrossRevenue, machine.previousComparableWeekGrossRevenue);
  }

  machineComparisonClass(machine: Machine): string {
    if (this.period !== 'week') return 'value-muted';
    return trendClass(machine.currentWeekGrossRevenue, machine.previousComparableWeekGrossRevenue);
  }

  machineName(machine: Machine): string {
    return machine.machineName || 'Unnamed machine';
  }

  get totalsLabel(): string {
    return `Totals (${this.machineCountLabel(this.machines.length)})`;
  }

  /** The fleet's sales for the selected period, only while every machine's figure for it is known. */
  get totalSalesText(): string {
    const totals = this.periodTotals();
    return totals.isRevenueComplete ? `${money(totals.sales)} sales` : UNAVAILABLE;
  }

  get totalProfitText(): string {
    const totals = this.periodTotals();
    if (!totals.isRevenueComplete || !totals.isProfitComplete) return 'Profit unavailable';
    return `${money(totals.profit)} direct profit · ${margin(totals.profit, totals.sales).toFixed(1)}% direct margin`;
  }

  get totalComparisonText(): string {
    if (this.period !== 'week') return NO_PERIOD_COMPARISON;
    return trendLabel(this.totalCurrentWeekSales, this.totalPreviousComparableWeekSales);
  }

  get totalComparisonClass(): string {
    if (this.period !== 'week') return 'value-muted';
    return trendClass(this.totalCurrentWeekSales, this.totalPreviousComparableWeekSales);
  }

  /**
   * Extends a machine row's real anchor link to the rest of the row (issue #494), the same pattern
   * `SiteListComponent.followRowLink` already uses: the anchor - not the row - owns focus, Enter
   * activation, and ctrl/cmd/middle-click, so this only activates it for a click or Enter elsewhere
   * in the row. A click on a control inside the row - an anchor or the group's own expander
   * included - and a modified or non-primary click are left to the browser.
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

  /**
   * The fleet totals for the selected period, over the whole machine list. They are deliberately
   * taken from `machines` and not from the groups, so collapsing a group - or a machine landing
   * under "Other machines" - can never change a total, and a site total is never added to a machine
   * total nor a percentage averaged into a new aggregate.
   */
  private periodTotals(): {
    sales: number;
    profit: number;
    isRevenueComplete: boolean;
    isProfitComplete: boolean;
  } {
    const figures = this.machines.map((machine) => machinePeriodFigures(machine, this.period));
    return {
      sales: figures.reduce((sum, figure) => sum + (figure.sales ?? 0), 0),
      profit: figures.reduce((sum, figure) => sum + (figure.profit ?? 0), 0),
      isRevenueComplete: figures.every((figure) => figure.sales != null),
      isProfitComplete: figures.every((figure) => figure.profit != null)
    };
  }

  private get totalCurrentWeekSales(): number {
    return this.machines.reduce((sum, machine) => sum + (machine.currentWeekGrossRevenue ?? 0), 0);
  }

  /**
   * The baseline the fleet's week comparison uses. It falls back to a machine's full
   * `lastWeekGrossRevenue` when its `previousComparableWeekGrossRevenue` is `null`, which mixes a
   * full week into a part-week comparison. That is pre-existing behaviour, reported rather than
   * changed here: changing a comparison is explicitly out of this issue's scope.
   */
  private get totalPreviousComparableWeekSales(): number {
    return this.machines.reduce(
      (sum, machine) => sum + (machine.previousComparableWeekGrossRevenue ?? machine.lastWeekGrossRevenue ?? 0),
      0
    );
  }
}

/**
 * One site's published sales for a period (issue #1012): the figure, or `null` for one the response
 * carried as unknown or withheld. `GET api/sites` publishes all four periods, so no period is
 * silent for a site any more and nothing here falls back to a figure a different period published.
 * Nothing is summed: each value is the server's own sum for that period.
 */
function siteSales(site: Site, period: PerformancePeriod): number | null {
  switch (period) {
    case 'today':
      return site.todayRevenue ?? null;
    case 'week':
      return site.currentWeekRevenue ?? null;
    case 'lastWeek':
      return site.lastWeekRevenue ?? null;
    case 'monthToDate':
      return site.monthToDateRevenue ?? null;
  }
}

/** One machine's published sales and direct profit for a period, exactly as the API sent them. */
function machinePeriodFigures(
  machine: Machine,
  period: PerformancePeriod
): { sales: number | null | undefined; profit: number | null | undefined } {
  switch (period) {
    case 'today':
      return { sales: machine.todayGrossRevenue, profit: machine.todayDirectProfit };
    case 'week':
      return { sales: machine.currentWeekGrossRevenue, profit: machine.currentWeekDirectProfit };
    case 'lastWeek':
      return { sales: machine.lastWeekGrossRevenue, profit: machine.lastWeekDirectProfit };
    case 'monthToDate':
      return { sales: machine.monthToDateGrossRevenue, profit: machine.monthToDateDirectProfit };
  }
}

/** The direct margin the machine rows and the totals row already displayed, unchanged. */
function margin(profit: number, sales: number): number {
  return sales === 0 ? 0 : (profit / sales) * 100;
}
