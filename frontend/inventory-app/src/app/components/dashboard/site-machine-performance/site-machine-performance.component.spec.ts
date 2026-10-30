import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Machine, Site } from '../../../models/models';
import {
  MACHINE_FIGURE_UNAVAILABLE,
  NO_PERIOD_COMPARISON,
  OTHER_MACHINES_LABEL,
  SiteMachinePerformanceComponent,
  groupSitesAndMachines
} from './site-machine-performance.component';

/**
 * The combined Site and machine performance card (issue #987): one card whose site summary rows
 * expand to the machines standing at that site. Every figure, destination, capability rule and
 * unavailable state below is the one the two former cards already had - this is a composition and
 * presentation change, and nothing here calculates.
 */

function machine(machineID: number, machineName: string, siteId: number | null): Machine {
  return {
    machineID,
    localMachineId: 500 + machineID,
    machineName,
    machineNumber: `M${machineID}`,
    todayGrossRevenue: 10,
    currentWeekGrossRevenue: 20,
    lastWeekGrossRevenue: 30,
    twoWeeksAgoGrossRevenue: 30,
    todayDirectProfit: 4,
    twoWeeksAgoDirectProfit: 12,
    lastWeekDirectProfit: 12,
    currentWeekDirectProfit: 8,
    previousComparableWeekGrossRevenue: 16,
    previousComparableWeekDirectProfit: 6,
    monthToDateGrossRevenue: 60,
    monthToDateDirectProfit: 24,
    profitabilityStatus: null,
    siteId
  };
}

function site(siteId: number, siteName: string, overrides: Partial<Site> = {}): Site {
  return {
    siteId,
    siteName,
    machineCount: 1,
    totalStockPercentage: 80,
    lowProductCount: 3,
    emptyProductCount: 1,
    todayRevenue: 10,
    currentWeekRevenue: 20,
    previousComparableWeekRevenue: 16,
    lastWeekRevenue: 40,
    monthToDateRevenue: 75,
    ...overrides
  };
}

const HARBOUR = site(1, 'Harbour Point Plaza');
const PAVILLION = site(2, 'Pavillion Shopping Centre', { machineCount: 2, totalStockPercentage: 27 });

const FLEET: Machine[] = [
  machine(1, 'Pavillion Left', 2),
  machine(2, 'Avenue Hotel VM', 1),
  machine(3, 'Warehouse test unit', null),
  machine(4, 'Pavillion Right', 2),
  machine(5, 'Moved machine', 99)
];

interface Options {
  sites?: readonly Site[];
  machines?: readonly Machine[];
  isLoadingSites?: boolean;
  isLoadingMachines?: boolean;
  sitesError?: string | null;
  machinesError?: string | null;
  canViewFinancials?: boolean;
}

type Rendered = { fixture: ComponentFixture<SiteMachinePerformanceComponent>; host: HTMLElement };

function applyInputs(fixture: ComponentFixture<SiteMachinePerformanceComponent>, options: Options): void {
  fixture.componentRef.setInput('sites', options.sites ?? [HARBOUR, PAVILLION]);
  fixture.componentRef.setInput('machines', options.machines ?? FLEET);
  fixture.componentRef.setInput('isLoadingSites', options.isLoadingSites ?? false);
  fixture.componentRef.setInput('isLoadingMachines', options.isLoadingMachines ?? false);
  fixture.componentRef.setInput('sitesError', options.sitesError ?? null);
  fixture.componentRef.setInput('machinesError', options.machinesError ?? null);
  fixture.componentRef.setInput('canViewFinancials', options.canViewFinancials ?? true);
}

async function render(options: Options = {}): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [SiteMachinePerformanceComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(SiteMachinePerformanceComponent);
  applyInputs(fixture, options);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function table(host: HTMLElement): HTMLElement {
  return host.querySelector('[data-testid="performance-table"]') as HTMLElement;
}

function headers(host: HTMLElement): (string | undefined)[] {
  return Array.from(table(host).querySelectorAll('thead th')).map((th) => th.textContent?.replace(/\s+/g, ' ').trim());
}

function expander(host: HTMLElement, key: string): HTMLButtonElement {
  return host.querySelector(`[data-testid="performance-expander-${key}"]`) as HTMLButtonElement;
}

/** The desktop row group holding one site's machines; the expander's `aria-controls` names it. */
function machineRegion(host: HTMLElement, key: string): HTMLElement {
  return host.querySelector(`#site-machines-${key}`) as HTMLElement;
}

/** The machine names rendered in the desktop table under each site, in rendered order. */
function machineNamesInTable(host: HTMLElement, key: string): string[] {
  return Array.from(machineRegion(host, key).querySelectorAll('a.table-row-anchor')).map(
    (anchor) => anchor.textContent?.trim() ?? ''
  );
}

function periodButton(host: HTMLElement, label: string): HTMLButtonElement {
  return Array.from(
    host.querySelectorAll<HTMLButtonElement>('[data-testid="performance-period"] button')
  ).find((button) => button.textContent?.trim() === label) as HTMLButtonElement;
}

function totals(host: HTMLElement): string {
  return table(host).querySelector('tfoot')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('groupSitesAndMachines (issue #987)', () => {
  it('lists every site in the site order with its own machines, then the rest under Other machines', () => {
    const groups = groupSitesAndMachines([HARBOUR, PAVILLION], FLEET);

    expect(groups.map((group) => [group.label, group.machines.map((m) => m.machineID)])).toEqual([
      ['Harbour Point Plaza', [2]],
      ['Pavillion Shopping Centre', [1, 4]],
      [OTHER_MACHINES_LABEL, [3, 5]]
    ]);
  });

  it('places every machine exactly once', () => {
    const placed = groupSitesAndMachines([HARBOUR, PAVILLION], FLEET).flatMap((group) => group.machines);

    expect(placed).toHaveLength(FLEET.length);
    expect(new Set(placed).size).toBe(FLEET.length);
  });

  it('keeps a site with no returned machine visible as its own empty group (issue #987)', () => {
    const groups = groupSitesAndMachines([HARBOUR, PAVILLION], [machine(2, 'Avenue Hotel VM', 1)]);

    expect(groups.map((group) => [group.label, group.machines.length])).toEqual([
      ['Harbour Point Plaza', 1],
      ['Pavillion Shopping Centre', 0]
    ]);
  });

  it('adds no Other machines group when every machine is at a listed site', () => {
    const groups = groupSitesAndMachines([HARBOUR], [machine(2, 'Avenue Hotel VM', 1)]);

    expect(groups.map((group) => group.label)).toEqual(['Harbour Point Plaza']);
  });

  it('leaves the machines in one unheaded group when no site is available at all', () => {
    const groups = groupSitesAndMachines([], FLEET);

    expect(groups).toHaveLength(1);
    expect(groups[0].label).toBeNull();
    expect(groups[0].site).toBeNull();
    expect(groups[0].machines).toHaveLength(FLEET.length);
  });
});

describe('Site and machine performance card (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('is one card holding both the sites and their machines', async () => {
    const { host } = await render();

    expect(host.querySelectorAll('section.card')).toHaveLength(1);
    expect(host.querySelector('.card-title')?.textContent?.trim()).toBe('Site and machine performance');
  });

  it('aligns site and machine rows under one set of columns', async () => {
    const { host } = await render();

    expect(headers(host)).toEqual([
      'Site / machine',
      'Stock',
      'Low / empty',
      'Sales · This week',
      'vs same time last week',
      'Actions'
    ]);
  });

  it('starts every site expanded, with its machines in the API order', async () => {
    const { host } = await render();

    expect(expander(host, 'site-1').getAttribute('aria-expanded')).toBe('true');
    expect(expander(host, 'site-2').getAttribute('aria-expanded')).toBe('true');
    expect(machineNamesInTable(host, 'site-1')).toEqual(['Avenue Hotel VM']);
    expect(machineNamesInTable(host, 'site-2')).toEqual(['Pavillion Left', 'Pavillion Right']);
    expect(machineNamesInTable(host, 'other')).toEqual(['Warehouse test unit', 'Moved machine']);
  });

  it('collapses one site without touching another, and re-expands it', async () => {
    const { fixture, host } = await render();

    expander(host, 'site-2').click();
    fixture.detectChanges();

    expect(expander(host, 'site-2').getAttribute('aria-expanded')).toBe('false');
    expect(machineNamesInTable(host, 'site-2')).toEqual([]);
    expect(expander(host, 'site-1').getAttribute('aria-expanded')).toBe('true');
    expect(machineNamesInTable(host, 'site-1')).toEqual(['Avenue Hotel VM']);

    expander(host, 'site-2').click();
    fixture.detectChanges();

    expect(machineNamesInTable(host, 'site-2')).toEqual(['Pavillion Left', 'Pavillion Right']);
  });

  it('exposes no focusable descendant inside a collapsed group', async () => {
    const { fixture, host } = await render();

    expander(host, 'site-2').click();
    fixture.detectChanges();

    const focusable = machineRegion(host, 'site-2').querySelectorAll('a[href], button, input, [tabindex]');
    expect(focusable).toHaveLength(0);
  });

  it('gives each expander an accessible name, aria-expanded and an aria-controls that resolves', async () => {
    const { host } = await render();
    const button = expander(host, 'site-1');

    expect(button.tagName).toBe('BUTTON');
    expect(button.getAttribute('type')).toBe('button');
    expect(button.getAttribute('aria-label')).toBe('Harbour Point Plaza machines');
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.getAttribute('aria-controls')).toBe('site-machines-site-1');
    expect(machineRegion(host, 'site-1')).not.toBeNull();
  });

  it('keeps the expansion of surviving sites when the data is refreshed', async () => {
    const { fixture, host } = await render();

    expander(host, 'site-1').click();
    fixture.detectChanges();
    expect(expander(host, 'site-1').getAttribute('aria-expanded')).toBe('false');

    // A refresh replaces both lists with equal-but-new arrays, exactly as the reads do.
    applyInputs(fixture, { sites: [site(1, 'Harbour Point Plaza'), PAVILLION], machines: [...FLEET] });
    fixture.detectChanges();

    expect(expander(host, 'site-1').getAttribute('aria-expanded')).toBe('false');
    expect(expander(host, 'site-2').getAttribute('aria-expanded')).toBe('true');
  });

  it('does not toggle the group when a nested Products or Details link is activated', async () => {
    const { fixture, host } = await render();
    jest.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const productsLink = host.querySelector('[data-testid="site-products-1"]') as HTMLAnchorElement;
    const detailsLink = machineRegion(host, 'site-1').querySelector('[data-testid^="machine-details-"]') as HTMLAnchorElement;

    productsLink.click();
    detailsLink.click();
    fixture.detectChanges();

    expect(expander(host, 'site-1').getAttribute('aria-expanded')).toBe('true');
  });
});

describe('Site and machine performance shared period controls (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('offers every existing period and starts on This week', async () => {
    const { host } = await render();
    const labels = Array.from(
      host.querySelectorAll('[data-testid="performance-period"] button')
    ).map((button) => button.textContent?.trim());

    expect(labels).toEqual(['Today', 'This week', 'Last week', 'Month to date']);
    expect(periodButton(host, 'This week').getAttribute('aria-pressed')).toBe('true');
  });

  it('labels the sales column with the selected period and moves every row and the total to it', async () => {
    const { fixture, host } = await render();

    periodButton(host, 'Last week').click();
    fixture.detectChanges();

    expect(headers(host)).toContain('Sales · Last week');
    expect(machineRegion(host, 'site-1').textContent).toContain('$30.00 sales');
    expect(totals(host)).toContain('$150.00 sales');
  });

  it('shows a site its own published sales for every one of the four periods', async () => {
    const { fixture, host } = await render();
    const siteRow = () => host.querySelector('[data-testid="site-row-1"]') as HTMLElement;
    const siteCard = () =>
      host.querySelector('[data-testid="performance-card-group-site-1"]') as HTMLElement;

    expect(siteRow().textContent).toContain('$20.00');
    expect(siteCard().textContent).toContain('$20.00');

    for (const [label, amount] of [
      ['Today', '$10.00'],
      ['Last week', '$40.00'],
      ['Month to date', '$75.00']
    ]) {
      periodButton(host, label).click();
      fixture.detectChanges();

      expect(headers(host)).toContain(`Sales · ${label}`);
      expect(siteRow().textContent).toContain(amount);
      expect(siteCard().textContent).toContain(amount);
    }
  });

  it('shows $0.00 for a period with no sales and Unavailable for a withheld figure', async () => {
    const quiet = site(1, 'Harbour Point Plaza', { monthToDateRevenue: 0, lastWeekRevenue: null });
    const { fixture, host } = await render({ sites: [quiet, PAVILLION] });
    const siteRow = () => host.querySelector('[data-testid="site-row-1"]') as HTMLElement;

    periodButton(host, 'Month to date').click();
    fixture.detectChanges();
    expect(siteRow().textContent).toContain('$0.00');

    periodButton(host, 'Last week').click();
    fixture.detectChanges();
    expect(siteRow().textContent).toContain('Unavailable');
    expect(siteRow().textContent).not.toContain('$0.00');
  });

  it('no longer says a site publishes only some of the periods', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="site-period-note"]')).toBeNull();
    expect(host.textContent).not.toContain('Not published for sites');
  });

  it('keeps "vs same time last week" to the week comparison the API actually publishes', async () => {
    const { fixture, host } = await render();

    expect(headers(host)).toContain('vs same time last week');
    expect((host.querySelector('[data-testid="site-row-1"]') as HTMLElement).textContent).toContain('↑ 25.0%');

    periodButton(host, 'Today').click();
    fixture.detectChanges();

    expect(headers(host)).not.toContain('vs same time last week');
    expect(headers(host)).toContain('Comparison');
    expect((host.querySelector('[data-testid="site-row-1"]') as HTMLElement).textContent).toContain(NO_PERIOD_COMPARISON);
  });

  it('shows each machine its own direct profit and margin for the selected period', async () => {
    const { host } = await render();

    expect(machineRegion(host, 'site-1').textContent).toContain('$8.00 direct profit · 40.0% direct margin');
  });
});

describe('Site and machine performance totals (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('reports one fleet total over every machine', async () => {
    const { host } = await render();

    expect(totals(host)).toContain('Totals (5 machines)');
    expect(totals(host)).toContain('$100.00 sales');
  });

  it('leaves the totals unchanged when a group is collapsed', async () => {
    const { fixture, host } = await render();
    const before = totals(host);

    expander(host, 'site-2').click();
    expander(host, 'other').click();
    fixture.detectChanges();

    expect(totals(host)).toBe(before);
  });

  it('reports a period total nobody has every figure for as unavailable, never as zero', async () => {
    const { host } = await render({
      machines: [machine(1, 'Pavillion Left', 2), { ...machine(2, 'Avenue Hotel VM', 1), currentWeekGrossRevenue: null }]
    });

    expect(totals(host)).toContain('Unavailable');
    expect(totals(host)).not.toContain('$0.00');
    expect(machineRegion(host, 'site-1').textContent).toContain('Unavailable');
  });
});

describe('Site and machine performance unsupported machine stock (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('never reuses the site percentage for a machine and says why the machine figure is absent', async () => {
    const { host } = await render();
    const machineRow = machineRegion(host, 'site-2').querySelector('tr') as HTMLElement;

    expect(machineRow.querySelector('[data-testid="machine-stock"]')?.textContent?.trim()).toBe(MACHINE_FIGURE_UNAVAILABLE);
    expect(machineRow.querySelector('[data-testid="machine-low-empty"]')?.textContent?.trim()).toBe(MACHINE_FIGURE_UNAVAILABLE);
    expect(machineRow.textContent).not.toContain('27%');
    expect(host.querySelector('[data-testid="machine-stock-note"]')?.textContent).toContain('per site');
  });

  it('states a site stock level in words as well as in colour', async () => {
    const { host } = await render();
    const stock = host.querySelector('[data-testid="site-stock-1"]') as HTMLElement;

    expect(stock.textContent).toContain('80%');
    expect(stock.textContent).toContain('Well stocked');
  });
});

describe('Site and machine performance actions (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps the site Products destination', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="site-products-2"]')?.getAttribute('href')).toBe('/sites/2/products');
  });

  it('keeps the machine Details destination, on the local machine id', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="machine-details-502"]')?.getAttribute('href')).toBe('/machines/502');
  });

  it('fabricates no Details URL for a machine this business has no local record of', async () => {
    const { host } = await render({ machines: [{ ...machine(2, 'Avenue Hotel VM', 1), localMachineId: null }] });
    const region = machineRegion(host, 'site-1');

    expect(region.querySelector('a[href^="/machines"]')).toBeNull();
    expect(region.textContent).toContain('Avenue Hotel VM');
    expect(region.querySelector('[data-testid="machine-details-unavailable"]')).not.toBeNull();
  });
});

describe('Site and machine performance independent read states (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps the machines in the card, with a site-unavailable notice, when the sites failed', async () => {
    const { host } = await render({ sites: [], sitesError: 'The Sites section could not be loaded.' });

    expect(host.querySelector('[data-testid="sites-unavailable"]')?.textContent).toContain(
      'The Sites section could not be loaded.'
    );
    expect(host.textContent).not.toContain('No sites available yet');
    expect(machineNamesInTable(host, 'other')).toEqual([
      'Pavillion Left',
      'Avenue Hotel VM',
      'Warehouse test unit',
      'Pavillion Right',
      'Moved machine'
    ]);
  });

  it('keeps the site rows in the card, with a machine-unavailable notice, when the machines failed', async () => {
    const { host } = await render({ machines: [], machinesError: 'The Machines section could not be loaded.' });

    expect(host.querySelector('[data-testid="machines-unavailable"]')?.textContent).toContain(
      'The Machines section could not be loaded.'
    );
    expect(host.textContent).not.toContain('No machines available yet');
    expect(host.querySelector('[data-testid="site-row-1"]')).not.toBeNull();
    expect(machineRegion(host, 'site-1').textContent).toContain('Machines could not be loaded for this site.');
  });

  it('shows a site with no returned machine as empty rather than hiding it', async () => {
    const { host } = await render({ machines: [machine(2, 'Avenue Hotel VM', 1)] });

    expect(machineRegion(host, 'site-2').textContent).toContain('No machines at this site.');
  });

  it('still tells a genuinely empty read apart from a failed one', async () => {
    const { host } = await render({ sites: [], machines: [] });

    expect(host.textContent).toContain('No sites available yet');
    expect(host.textContent).toContain('No machines available yet');
  });

  it('shows the card as loading rather than as empty while both reads are outstanding', async () => {
    const { host } = await render({ sites: [], machines: [], isLoadingSites: true, isLoadingMachines: true });

    expect(host.querySelector('[data-testid="performance-loading"]')).not.toBeNull();
    expect(host.textContent).not.toContain('No sites available yet');
    expect(host.textContent).not.toContain('No machines available yet');
  });

  it('keeps the arrived half visible while the other read is still outstanding', async () => {
    const { host } = await render({ machines: [], isLoadingMachines: true });

    expect(host.querySelector('[data-testid="site-row-1"]')).not.toBeNull();
    expect(host.textContent).not.toContain('No machines available yet');
    expect(host.querySelector('[data-testid="machines-loading"]')).not.toBeNull();
  });
});

describe('Site and machine performance without Dashboard.ViewFinancials (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows no period control, sales, profit, comparison or total', async () => {
    const { host } = await render({ canViewFinancials: false });

    expect(host.querySelector('[data-testid="performance-period"]')).toBeNull();
    expect(headers(host)).toEqual(['Site / machine', 'Stock', 'Low / empty', 'Actions']);
    expect(table(host).querySelector('tfoot')).toBeNull();
    expect(host.textContent).not.toContain('$');
  });

  it('keeps the hierarchy, the stock figures and both actions', async () => {
    const { host } = await render({ canViewFinancials: false });

    expect(machineNamesInTable(host, 'site-2')).toEqual(['Pavillion Left', 'Pavillion Right']);
    expect(host.querySelector('[data-testid="site-stock-2"]')?.textContent).toContain('27%');
    expect(host.querySelector('[data-testid="site-products-2"]')?.getAttribute('href')).toBe('/sites/2/products');
    expect(host.querySelector('[data-testid="machine-details-501"]')?.getAttribute('href')).toBe('/machines/501');
  });
});

describe('Site and machine performance narrow layout (issue #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('repeats the same hierarchy as expandable site cards holding machine cards', async () => {
    const { host } = await render();
    const group = host.querySelector('[data-testid="performance-card-group-site-2"]') as HTMLElement;
    const names = Array.from(group.querySelectorAll('[data-testid^="performance-machine-card-"]')).map((card) =>
      (card.querySelector('[data-testid^="machine-name-"]') as HTMLElement)?.textContent?.trim()
    );

    expect(names).toEqual(['Pavillion Left', 'Pavillion Right']);
  });

  it('collapses a card group with its own labelled expander and keeps the other open', async () => {
    const { fixture, host } = await render();
    const cardExpander = host.querySelector('[data-testid="performance-card-expander-site-2"]') as HTMLButtonElement;

    expect(cardExpander.getAttribute('aria-label')).toBe('Pavillion Shopping Centre machines');
    cardExpander.click();
    fixture.detectChanges();

    const group = host.querySelector('[data-testid="performance-card-group-site-2"]') as HTMLElement;
    expect(cardExpander.getAttribute('aria-expanded')).toBe('false');
    expect(group.querySelectorAll('[data-testid^="performance-machine-card-"]')).toHaveLength(0);
    expect(
      (host.querySelector('[data-testid="performance-card-expander-site-1"]') as HTMLElement).getAttribute('aria-expanded')
    ).toBe('true');
  });

  it('carries the same values and actions as the table', async () => {
    const { host } = await render();
    const group = host.querySelector('[data-testid="performance-card-group-site-1"]') as HTMLElement;

    expect(group.textContent).toContain('$20.00');
    expect(group.textContent).toContain('↑ 25.0%');
    expect(group.querySelector('[data-testid="site-card-products-1"]')?.getAttribute('href')).toBe('/sites/1/products');
    expect(group.textContent).toContain('$8.00 direct profit');
  });
});
