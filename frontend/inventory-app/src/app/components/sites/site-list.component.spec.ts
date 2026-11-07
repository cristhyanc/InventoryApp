import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { SiteListComponent } from './site-list.component';
import { SiteService } from '../../services/site.service';
import { Site } from '../../models/models';
import { AccessService } from '../../access/access.service';
import { NayaxConnectionService, NayaxConnectionStatusName } from '../../services/nayax-connection.service';

function site(overrides: Partial<Site> = {}): Site {
  return {
    siteId: 1,
    siteName: 'North Mall',
    machineCount: 3,
    totalStockPercentage: 50,
    lowProductCount: 1,
    emptyProductCount: 0,
    todayRevenue: 10,
    currentWeekRevenue: 100,
    previousComparableWeekRevenue: 80,
    ...overrides
  };
}

async function renderWith(
  getAll: () => ReturnType<SiteService['getAll']>,
  nayaxStatus: NayaxConnectionStatusName = 'Ready',
  canViewFinancials = true
) {
  await TestBed.configureTestingModule({
    imports: [SiteListComponent],
    providers: [
      provideRouter([]),
      { provide: SiteService, useValue: { getAll } },
      { provide: NayaxConnectionService, useValue: { status: () => of({ status: nayaxStatus, machineChecksPending: false }) } },
      { provide: AccessService, useValue: { allows: () => of(canViewFinancials) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SiteListComponent);
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

/** `routerLink` navigates through `navigateByUrl`, so that is where a followed link shows up. */
function spyOnNavigateByUrl() {
  return jest.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
}

function navigatedUrls(spy: ReturnType<typeof spyOnNavigateByUrl>): string[] {
  const router = TestBed.inject(Router);
  return spy.mock.calls.map(([url]) => (typeof url === 'string' ? url : router.serializeUrl(url)));
}

describe('SiteListComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows a loading state before the sites arrive', async () => {
    const pending = new Subject<Site[]>();
    const { fixture, host } = await renderWith(() => pending.asObservable());

    fixture.detectChanges();

    expect(host.textContent).not.toContain('No sites available yet.');
    expect(host.querySelectorAll('table')).toHaveLength(0);
  });

  it('renders the site summary fields supplied by the Site model', async () => {
    const { fixture, host } = await renderWith(() =>
      of([site({ siteName: 'North Mall', machineCount: 4, totalStockPercentage: 65, lowProductCount: 2, emptyProductCount: 1, todayRevenue: 12.5, currentWeekRevenue: 150, previousComparableWeekRevenue: 100 })])
    );

    fixture.detectChanges();

    expect(host.textContent).toContain('North Mall');
    expect(host.textContent).toContain('4');
    expect(host.textContent).toContain('65%');
    expect(host.textContent).toContain('2 low');
    expect(host.textContent).toContain('1 empty');
    expect(host.textContent).toContain('$12.50');
    expect(host.textContent).toContain('$150.00');
    expect(host.textContent).toContain('50.0%');
  });

  it('filters sites client-side by name as the operator types', async () => {
    const { fixture, host } = await renderWith(() =>
      of([site({ siteId: 1, siteName: 'North Mall' }), site({ siteId: 2, siteName: 'South Plaza' })])
    );
    fixture.detectChanges();

    fixture.componentInstance.search = 'south';
    fixture.detectChanges();

    expect(host.textContent).toContain('South Plaza');
    expect(host.textContent).not.toContain('North Mall');
  });

  it('shows a clear no-match state when the search filters out every site', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteName: 'North Mall' })]));
    fixture.detectChanges();

    fixture.componentInstance.search = 'nonexistent';
    fixture.detectChanges();

    expect(host.textContent).toContain('No sites match');
    expect(host.querySelectorAll('tbody tr')).toHaveLength(0);
  });

  it('shows a clear empty state when there are no sites at all', async () => {
    const { fixture, host } = await renderWith(() => of([]));

    fixture.detectChanges();

    expect(host.textContent).toContain('No sites available yet.');
  });

  it('shows an explicit error state when the API call fails', async () => {
    const { fixture, host } = await renderWith(() => throwError(() => new Error('network error')));

    fixture.detectChanges();

    expect(host.textContent).toContain('Failed to load sites');
    expect(host.querySelectorAll('table')).toHaveLength(0);
  });

  /*
   * Row navigation is a real `<a href>` in the site cell, not an ARIA `role="link"` on the row:
   * keyboard activation (Enter), focus, ctrl/cmd/middle-click and "copy link address" are then
   * the browser's own link behaviour rather than something this component reimplements. jsdom
   * does not run an anchor's activation behaviour for Enter, so these tests assert the native
   * anchor and its href - the things that make that behaviour exist - plus the row-click
   * convenience this component does own.
   */
  it('renders the site name as a native link to the existing site-products route', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteId: 42, siteName: 'North Mall' })]));

    fixture.detectChanges();

    const link = host.querySelector('tbody tr td a') as HTMLAnchorElement;
    expect(link).not.toBeNull();
    expect(link.getAttribute('href')).toBe('/sites/42/products');
    expect(link.textContent?.trim()).toBe('North Mall');
    expect(link.getAttribute('aria-label')).toBe('View products for North Mall');
  });

  it('leaves the row itself without an emulated link role and without a Details button', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteId: 42, siteName: 'North Mall' })]));

    fixture.detectChanges();

    const row = host.querySelector('tbody tr') as HTMLElement;
    expect(row.getAttribute('role')).toBeNull();
    expect(row.getAttribute('tabindex')).toBeNull();
    expect(row.classList.contains('table-row-link')).toBe(true);
    expect(host.querySelector('.btn')).toBeNull();
  });

  it('follows the row link when the row itself is clicked', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteId: 42, siteName: 'North Mall' })]));
    fixture.detectChanges();
    const navigateByUrl = spyOnNavigateByUrl();

    const row = host.querySelector('tbody tr') as HTMLElement;
    row.click();

    expect(navigatedUrls(navigateByUrl)).toEqual(['/sites/42/products']);
  });

  it('navigates once, through the link, when the link itself is clicked', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteId: 42, siteName: 'North Mall' })]));
    fixture.detectChanges();
    const navigateByUrl = spyOnNavigateByUrl();

    const link = host.querySelector('tbody tr td a') as HTMLAnchorElement;
    link.click();

    expect(navigatedUrls(navigateByUrl)).toEqual(['/sites/42/products']);
  });

  it('leaves a ctrl-click on the row to the browser so the link can open in a new tab', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteId: 42, siteName: 'North Mall' })]));
    fixture.detectChanges();
    const navigateByUrl = spyOnNavigateByUrl();

    const row = host.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));

    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('navigates once when Enter is pressed on the focused link', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteId: 42, siteName: 'North Mall' })]));
    fixture.detectChanges();
    const navigateByUrl = spyOnNavigateByUrl();

    const link = host.querySelector('tbody tr td a') as HTMLAnchorElement;
    link.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    link.click(); // the browser's own activation of a focused anchor on Enter

    expect(navigatedUrls(navigateByUrl)).toEqual(['/sites/42/products']);
  });

  it('shows the Nayax status banner when the connection is not Ready (issue #539)', async () => {
    const { fixture, host } = await renderWith(() => of([site()]), 'NotConfigured');
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).not.toBeNull();
  });

  it('hides the Nayax status banner once the connection is Ready (issue #539)', async () => {
    const { fixture, host } = await renderWith(() => of([site()]), 'Ready');
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="nayax-status-banner"]')).toBeNull();
  });
});

/**
 * `Dashboard.ViewFinancials` hides the Today/This Week/vs Last Week revenue columns (issue #699):
 * a role without it - an Operator - sees none of the three, while a Manager or an Owner sees them
 * exactly as before.
 */
describe('SiteListComponent revenue columns gated on Dashboard.ViewFinancials (issue #699)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows no sales column, figure or period note to an Operator, keeping stock and navigation', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteName: 'North Mall', todayRevenue: 12.5 })]), 'Ready', false);
    fixture.detectChanges();

    const headers = Array.from(host.querySelectorAll('th')).map((th) => th.textContent?.trim());
    expect(headers).toEqual(['Site', 'Machines', 'Stock level', 'Refill status']);
    expect(host.textContent).not.toContain('$12.50');
    expect(host.textContent).not.toContain('Sales today');
    expect(host.querySelector('[data-testid="sites-period-note"]')).toBeNull();
    expect(host.textContent).toContain('North Mall');
    expect(host.querySelector('tbody tr td a')).not.toBeNull();
  });

  it('keeps the revenue columns exactly as today for a Manager or an Owner', async () => {
    const { fixture, host } = await renderWith(() => of([site({ todayRevenue: 12.5, currentWeekRevenue: 150 })]), 'Ready', true);
    fixture.detectChanges();

    const headers = Array.from(host.querySelectorAll('th')).map((th) => th.textContent?.trim());
    expect(headers).toEqual(['Site', 'Machines', 'Stock level', 'Refill status', 'Sales today', 'Sales this week', 'vs last week']);
    expect(host.textContent).toContain('$12.50');
    expect(host.textContent).toContain('$150.00');
  });

  it('renders without error when a granted role is sent a site with the figures absent', async () => {
    const { fixture, host } = await renderWith(
      () => of([site({ todayRevenue: null, currentWeekRevenue: null, previousComparableWeekRevenue: null })]),
      'Ready',
      true
    );
    fixture.detectChanges();

    expect(host.textContent).toContain('Unavailable');
    expect(host.textContent).not.toContain('NaN');
    expect(host.textContent).not.toContain('$0.00');
  });
});

/** Issue #1128: the redesigned overview's summary, search + filter, refill order and stock states. */
describe('SiteListComponent overview (issue #1128)', () => {
  afterEach(() => TestBed.resetTestingModule());

  const rowNames = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('[data-testid="site-row"] a')).map((a) => a.textContent?.trim());
  const cardNames = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('[data-testid="site-card"] a')).map((a) => a.querySelector('span')?.textContent?.trim());

  function click(host: HTMLElement, testId: string) {
    (host.querySelector(`[data-testid="${testId}"]`) as HTMLElement).click();
  }

  const fleet = [
    site({ siteId: 1, siteName: 'Avenue', machineCount: 1, lowProductCount: 4, emptyProductCount: 0, totalStockPercentage: 92 }),
    site({ siteId: 2, siteName: 'Pavilion', machineCount: 2, lowProductCount: 1, emptyProductCount: 2, totalStockPercentage: 94 }),
    site({ siteId: 3, siteName: 'Central', machineCount: 1, lowProductCount: 0, emptyProductCount: 0, totalStockPercentage: 98 }),
    site({ siteId: 4, siteName: 'Beach', machineCount: 3, lowProductCount: 6, emptyProductCount: 0, totalStockPercentage: 60 })
  ];

  it('states the site and machine count from the loaded rows', async () => {
    const { fixture, host } = await renderWith(() => of(fleet));
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="sites-summary"]')?.textContent?.trim()).toBe('4 sites · 7 machines');
    expect(host.querySelector('[data-testid="sites-filter-all"]')?.textContent?.trim()).toBe('All sites (4)');
    expect(host.querySelector('[data-testid="sites-filter-needs-refill"]')?.textContent?.trim()).toBe('Needs refill (3)');
  });

  it('shows no count line while loading or after a failed read', async () => {
    const { fixture, host } = await renderWith(() => throwError(() => new Error('down')));
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="sites-summary"]')).toBeNull();
  });

  it('orders rows by refill priority by default and by name when chosen, in the table and the cards alike', async () => {
    const { fixture, host } = await renderWith(() => of(fleet));
    fixture.detectChanges();

    expect(rowNames(host)).toEqual(['Pavilion', 'Beach', 'Avenue', 'Central']);
    expect(cardNames(host)).toEqual(['Pavilion', 'Beach', 'Avenue', 'Central']);
    await fixture.whenStable();
    expect((host.querySelector('[data-testid="sites-sort"]') as HTMLSelectElement).value).toBe('refillPriority');

    fixture.componentInstance.sort = 'name';
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Avenue', 'Beach', 'Central', 'Pavilion']);
    expect(cardNames(host)).toEqual(['Avenue', 'Beach', 'Central', 'Pavilion']);
  });

  it('marks the selected filter and keeps only sites with a known low or empty slot under Needs refill', async () => {
    const { fixture, host } = await renderWith(() => of(fleet));
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="sites-filter-all"]')?.getAttribute('aria-pressed')).toBe('true');

    click(host, 'sites-filter-needs-refill');
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="sites-filter-needs-refill"]')?.getAttribute('aria-pressed')).toBe('true');
    expect(host.querySelector('[data-testid="sites-filter-all"]')?.getAttribute('aria-pressed')).toBe('false');
    expect(rowNames(host)).toEqual(['Pavilion', 'Beach', 'Avenue']);
  });

  it('applies the search and the Needs refill filter together', async () => {
    const { fixture, host } = await renderWith(() => of(fleet));
    fixture.detectChanges();

    fixture.componentInstance.search = '  a  ';
    click(host, 'sites-filter-needs-refill');
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Pavilion', 'Beach', 'Avenue']);

    fixture.componentInstance.search = 'cent';
    fixture.detectChanges();
    expect(rowNames(host)).toEqual([]);
    expect(host.querySelector('[data-testid="sites-no-results"]')?.textContent).toContain('No sites match "cent" with a known low or empty slot.');

    click(host, 'sites-filter-all');
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Central']);
  });

  it('says plainly when no site needs a refill and resets search and filter from the no-results state', async () => {
    const { fixture, host } = await renderWith(() => of([fleet[2]]));
    fixture.detectChanges();

    click(host, 'sites-filter-needs-refill');
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="sites-no-results"]')?.textContent).toContain('No site has a known low or empty slot.');

    click(host, 'sites-reset');
    fixture.detectChanges();
    expect(fixture.componentInstance.filter).toBe('all');
    expect(fixture.componentInstance.search).toBe('');
    expect(rowNames(host)).toEqual(['Central']);
  });

  it('clears the search from its own button', async () => {
    const { fixture, host } = await renderWith(() => of(fleet));
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="sites-search-clear"]')).toBeNull();

    fixture.componentInstance.search = 'zzz';
    fixture.detectChanges();
    click(host, 'sites-search-clear');
    fixture.detectChanges();

    expect(fixture.componentInstance.search).toBe('');
    expect(rowNames(host)).toHaveLength(4);
  });

  it('shows low and empty counts as coloured text chips, separate from the neutral stock percentage', async () => {
    const { fixture, host } = await renderWith(() => of([fleet[1]]));
    fixture.detectChanges();

    const row = host.querySelector('[data-testid="site-row"]') as HTMLElement;
    const status = row.querySelector('[data-testid="site-refill-status"]') as HTMLElement;
    expect(status.getAttribute('data-tone')).toBe('empty');
    const chips = Array.from(status.querySelectorAll('span.rounded-full'));
    expect(chips.map((c) => c.textContent?.trim())).toEqual(['2 empty', '1 low']);
    expect(chips[0].className).toContain('text-rr-danger-text');
    expect(chips[1].className).toContain('text-rr-warn-text');
    expect(row.querySelector('[data-testid="site-stock-level"]')?.textContent).toContain('94%');
    expect((row.querySelector('[data-testid="site-stock-fill"]') as HTMLElement).style.width).toBe('94%');
  });

  it('keeps several low slots more prominent than a high overall stock level', async () => {
    const { fixture, host } = await renderWith(() =>
      of([site({ siteName: 'Busy', totalStockPercentage: 96, lowProductCount: 5, emptyProductCount: 0 })])
    );
    fixture.detectChanges();

    const row = host.querySelector('[data-testid="site-row"]') as HTMLElement;
    const chip = row.querySelector('[data-testid="site-refill-status"] span.rounded-full') as HTMLElement;
    const percentage = row.querySelector('[data-testid="site-stock-level"] span.tabular-nums') as HTMLElement;
    expect(chip.textContent?.trim()).toBe('5 low');
    expect(chip.className).toContain('font-bold');
    expect(chip.className).toContain('bg-rr-warn-bg');
    expect(percentage.textContent?.trim()).toBe('96%');
    expect(percentage.className).toContain('text-rr-muted');
    expect(percentage.className).not.toContain('font-bold');
    expect(row.querySelector('[data-testid="site-stock-level"] .badge')).toBeNull();
  });

  it('shows Stock OK only for known zero counts, and Stock unavailable with no bar fill for missing data', async () => {
    const missing = {
      ...site({ siteId: 8, siteName: 'Missing' }),
      totalStockPercentage: null as unknown as number,
      lowProductCount: null as unknown as number,
      emptyProductCount: 0
    };
    const partial = { ...site({ siteId: 9, siteName: 'Partial' }), lowProductCount: 3, emptyProductCount: undefined as unknown as number };
    const { fixture, host } = await renderWith(() => of([fleet[2], missing, partial]));
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('[data-testid="site-row"]')) as HTMLElement[];
    const byName = (name: string) => rows.find((r) => r.querySelector('a')?.textContent?.trim() === name)!;

    expect(byName('Central').querySelector('[data-testid="site-refill-status"]')?.textContent?.trim()).toBe('Stock OK');
    expect(byName('Missing').querySelector('[data-testid="site-refill-status"]')?.textContent?.trim()).toBe('Stock unavailable');
    expect(byName('Missing').querySelector('[data-testid="site-stock-level"]')?.textContent).toContain('Unavailable');
    expect(byName('Missing').querySelector('[data-testid="site-stock-fill"]')).toBeNull();
    expect(byName('Partial').querySelector('[data-testid="site-refill-status"]')?.textContent).toContain('3 low');
    expect(byName('Partial').querySelector('[data-testid="site-refill-status"]')?.textContent).toContain('Empty count unavailable');
    expect(host.textContent).not.toContain('NaN');
  });

  it('gives every mobile card one link to the existing site route', async () => {
    const { fixture, host } = await renderWith(() => of([fleet[0]]));
    fixture.detectChanges();

    const card = host.querySelector('[data-testid="site-card"]') as HTMLElement;
    const links = card.querySelectorAll('a');
    expect(links).toHaveLength(1);
    expect(links[0].getAttribute('href')).toBe('/sites/1/products');
    expect(links[0].className).toContain('min-h-11');
    expect(card.textContent).toContain('1 machine');
    expect(card.textContent).toContain('Sales today');
    expect(card.textContent).toContain('Sales this week');
    expect(card.textContent).toContain('↑ 25.0%');
  });

  it('keeps one site link per desktop row', async () => {
    const { fixture, host } = await renderWith(() => of(fleet));
    fixture.detectChanges();

    for (const row of Array.from(host.querySelectorAll('[data-testid="site-row"]'))) {
      expect(row.querySelectorAll('a')).toHaveLength(1);
    }
  });

  it('hides the mobile sales tiles from an Operator', async () => {
    const { fixture, host } = await renderWith(() => of([fleet[0]]), 'Ready', false);
    fixture.detectChanges();

    const card = host.querySelector('[data-testid="site-card"]') as HTMLElement;
    expect(card.textContent).not.toContain('Sales today');
    expect(card.textContent).toContain('4 low');
  });
});
