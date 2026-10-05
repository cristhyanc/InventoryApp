import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { SiteListComponent } from './site-list.component';
import { SiteService } from '../../services/site.service';
import { Site } from '../../models/models';

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

async function renderWith(getAll: () => ReturnType<SiteService['getAll']>) {
  await TestBed.configureTestingModule({
    imports: [SiteListComponent],
    providers: [provideRouter([]), { provide: SiteService, useValue: { getAll } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SiteListComponent);
  return { fixture, host: fixture.nativeElement as HTMLElement };
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

  it('links each site to its existing site-products route', async () => {
    const { fixture, host } = await renderWith(() => of([site({ siteId: 42, siteName: 'North Mall' })]));

    fixture.detectChanges();

    const link = host.querySelector('a[href="/sites/42/products"]');
    expect(link).not.toBeNull();
  });
});
