import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { SiteCommissionAgreementsComponent } from './site-commission-agreements.component';
import { ReportingService, SiteCommissionAgreement } from '../../../services/reporting.service';
import { ToastService } from '../../../services/toast.service';
import { Site } from '../../../models/models';
import { SiteService } from '../../../services/site.service';

function agreement(overrides: Partial<SiteCommissionAgreement> = {}): SiteCommissionAgreement {
  return { id: 1, siteId: 1, effectiveFrom: '2026-01-01', commissionRate: 0.1, frequency: 1, basis: 0, ...overrides };
}

function site(overrides: Partial<Site> = {}): Site {
  return { siteId: 1, siteName: 'Main Office', ...overrides } as Site;
}

async function render(
  reporting: Partial<ReportingService>,
  siteService: Partial<SiteService> = { getAll: jest.fn(() => of([site()])) },
  toast?: Partial<ToastService>
) {
  await TestBed.configureTestingModule({
    imports: [SiteCommissionAgreementsComponent],
    providers: [
      provideRouter([]),
      { provide: ReportingService, useValue: reporting },
      { provide: SiteService, useValue: siteService },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SiteCommissionAgreementsComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('SiteCommissionAgreementsComponent (issue #388)', () => {
  it('loads the sites and current agreements on initialization', async () => {
    const siteCommissionAgreements = jest.fn(() => of([agreement()]));
    const { host } = await render({ siteCommissionAgreements, saveSiteCommissionAgreement: jest.fn() });

    expect(siteCommissionAgreements).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('Main Office');
    expect(host.textContent).toContain('10.00%');
  });

  it('shows an empty state and reports a toast error when loading agreements fails', async () => {
    const error = jest.fn();
    const { host } = await render(
      { siteCommissionAgreements: () => throwError(() => new Error('boom')), saveSiteCommissionAgreement: jest.fn() },
      undefined,
      { error }
    );

    expect(error).toHaveBeenCalledWith('Unable to load site commission agreements.');
    expect(host.textContent).toContain('No site commission agreements have been configured.');
  });

  it('rejects saving without a selected site without calling the API', async () => {
    const saveSiteCommissionAgreement = jest.fn();
    const error = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements: jest.fn(() => of([])), saveSiteCommissionAgreement },
      undefined,
      { error }
    );

    fixture.componentInstance.commissionSiteId = null;
    fixture.componentInstance.saveCommissionAgreement();

    expect(saveSiteCommissionAgreement).not.toHaveBeenCalled();
    expect(error).toHaveBeenCalledWith('Enter a site, valid commission rate, and effective date.');
  });

  it('saves the agreement converting the percentage rate, shows a success toast, and reloads', async () => {
    const saveSiteCommissionAgreement = jest.fn(() => of(agreement()));
    const siteCommissionAgreements = jest.fn(() => of([]));
    const success = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements, saveSiteCommissionAgreement },
      undefined,
      { success }
    );

    fixture.componentInstance.commissionSiteId = 1;
    fixture.componentInstance.commissionRate = 12.5;
    fixture.componentInstance.commissionEffectiveFrom = '2026-02-01';
    fixture.componentInstance.commissionFrequency = 1;
    fixture.componentInstance.commissionBasis = 0;
    fixture.componentInstance.commissionDueDays = 14;
    fixture.componentInstance.saveCommissionAgreement();

    expect(saveSiteCommissionAgreement).toHaveBeenCalledWith({
      siteId: 1, commissionRate: 0.125, effectiveFrom: '2026-02-01', frequency: 1, basis: 0, paymentDueDaysAfterPeriodEnd: 14
    });
    expect(success).toHaveBeenCalledWith('Site commission agreement saved.');
    expect(siteCommissionAgreements).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.loading).toBe(false);
  });

  it('reports an API error on save failure without reloading', async () => {
    const siteCommissionAgreements = jest.fn(() => of([]));
    const saveSiteCommissionAgreement = jest.fn(() => throwError(() => ({ error: 'Overlapping agreement.' })));
    const error = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements, saveSiteCommissionAgreement },
      undefined,
      { error }
    );

    fixture.componentInstance.commissionSiteId = 1;
    fixture.componentInstance.commissionRate = 10;
    fixture.componentInstance.commissionEffectiveFrom = '2026-02-01';
    fixture.componentInstance.saveCommissionAgreement();

    expect(error).toHaveBeenCalledWith('Overlapping agreement.');
    expect(siteCommissionAgreements).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.loading).toBe(false);
  });
});
