import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { SiteCommissionAgreementsComponent } from './site-commission-agreements.component';
import { ReportingService, SiteCommissionAgreement } from '../../../services/reporting.service';
import { ToastService } from '../../../services/toast.service';
import { SiteListItem } from '../../../models/models';
import { SiteService } from '../../../services/site.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

function agreement(overrides: Partial<SiteCommissionAgreement> = {}): SiteCommissionAgreement {
  return { id: 1, siteId: 1, effectiveFrom: '2026-01-01', commissionRate: 0.1, frequency: 1, basis: 0, ...overrides };
}

/**
 * One row of the light site list this page's picker fills from (issue #841). The picker shows the
 * name only; `machineCount` is here to keep the fixture the shape the endpoint publishes.
 */
function site(overrides: Partial<SiteListItem> = {}): SiteListItem {
  return { siteId: 1, name: 'Main Office', machineCount: 3, ...overrides };
}

async function render(
  reporting: Partial<ReportingService>,
  siteService: Partial<SiteService> = { getList: jest.fn(() => of([site()])) },
  toast?: Partial<ToastService>,
  sessionActive: Observable<boolean> = of(false)
) {
  await TestBed.configureTestingModule({
    imports: [SiteCommissionAgreementsComponent],
    providers: [
      provideRouter([]),
      { provide: ReportingService, useValue: reporting },
      { provide: SiteService, useValue: siteService },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
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

  /**
   * Issue #841: the picker and the agreement table's site labels come from the light site list, and
   * the site summaries - 1 + M Nayax reads for figures this page never shows - are not asked for.
   */
  it('fills the site picker from the light site list and never from the site summaries', async () => {
    const siteService = { getList: jest.fn(() => of([site()])), getAll: jest.fn(() => of([])) };
    const { fixture, host } = await render(
      { siteCommissionAgreements: jest.fn(() => of([agreement()])), saveSiteCommissionAgreement: jest.fn() },
      siteService
    );

    expect(siteService.getList).toHaveBeenCalledTimes(1);
    expect(siteService.getAll).not.toHaveBeenCalled();
    expect(fixture.componentInstance.sites).toEqual([site()]);
    expect(Array.from(host.querySelectorAll('#commission-site option')).map((option) => option.textContent?.trim()))
      .toEqual(['Select a site', 'Main Office']);
  });

  it('labels an agreement whose site the list does not carry by its id', async () => {
    const { fixture } = await render(
      { siteCommissionAgreements: jest.fn(() => of([agreement({ siteId: 7 })])), saveSiteCommissionAgreement: jest.fn() }
    );

    expect(fixture.componentInstance.siteName(7)).toBe('Site 7');
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

/**
 * Hiding the Save agreement write control and guarding its handler during a support session
 * (issue #659, extending #554). A session grants `SiteCommissionsManage`, so this control would
 * otherwise still render enabled.
 */
describe('SiteCommissionAgreementsComponent write control during a support session (issue #659)', () => {
  it('disables Save agreement while a session is active', async () => {
    const { host } = await render(
      { siteCommissionAgreements: jest.fn(() => of([])), saveSiteCommissionAgreement: jest.fn() },
      undefined,
      undefined,
      of(true)
    );

    const saveButton = host.querySelector('button.btn-primary') as HTMLButtonElement;
    expect(saveButton.disabled).toBe(true);
  });

  it('leaves Save agreement enabled while no session is active', async () => {
    const { host } = await render(
      { siteCommissionAgreements: jest.fn(() => of([])), saveSiteCommissionAgreement: jest.fn() },
      undefined,
      undefined,
      of(false)
    );

    const saveButton = host.querySelector('button.btn-primary') as HTMLButtonElement;
    expect(saveButton.disabled).toBe(false);
  });

  it('does not save while a session is active, even if the handler is invoked directly', async () => {
    const saveSiteCommissionAgreement = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements: jest.fn(() => of([])), saveSiteCommissionAgreement },
      undefined,
      undefined,
      of(true)
    );

    fixture.componentInstance.commissionSiteId = 1;
    fixture.componentInstance.commissionRate = 10;
    fixture.componentInstance.commissionEffectiveFrom = '2026-02-01';
    fixture.componentInstance.saveCommissionAgreement();

    expect(saveSiteCommissionAgreement).not.toHaveBeenCalled();
  });

  /**
   * Issue #1254. The action is hidden during a session rather than disabled, the way Record Payment
   * was: there is no mid-edit state in a date picker nobody may submit. A session grants
   * `SiteCommissionsManage`, so the control would otherwise still render.
   */
  it('hides End agreement while a session is active', async () => {
    const { host } = await render(
      { siteCommissionAgreements: jest.fn(() => of([agreement()])), saveSiteCommissionAgreement: jest.fn() },
      undefined,
      undefined,
      of(true)
    );

    expect(host.querySelector('app-end-commission-agreement')).toBeNull();
    expect(host.textContent).not.toContain('End agreement');
  });

  it('does not end an agreement while a session is active, even if the handler is invoked directly', async () => {
    const endSiteCommissionAgreement = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements: jest.fn(() => of([agreement()])), saveSiteCommissionAgreement: jest.fn(), endSiteCommissionAgreement },
      undefined,
      undefined,
      of(true)
    );

    fixture.componentInstance.endAgreement(agreement(), '2026-06-30');

    expect(endSiteCommissionAgreement).not.toHaveBeenCalled();
  });
});

/**
 * Ending an agreement from the current-agreements table (issue #1254), which is how a site's rate
 * changes: the add refuses any overlap, so an open-ended agreement blocked every later one for ever.
 */
describe('SiteCommissionAgreementsComponent End agreement (issue #1254)', () => {
  it('offers End agreement on every stored agreement while no session is active', async () => {
    const { host } = await render(
      { siteCommissionAgreements: jest.fn(() => of([agreement(), agreement({ id: 2, siteId: 1 })])), saveSiteCommissionAgreement: jest.fn() }
    );

    expect(host.querySelectorAll('app-end-commission-agreement')).toHaveLength(2);
    expect(host.textContent).toContain('End agreement');
  });

  it('ends the agreement, shows a success toast, and reloads', async () => {
    const siteCommissionAgreements = jest.fn(() => of([agreement()]));
    const endSiteCommissionAgreement = jest.fn(() => of(agreement({ effectiveTo: '2026-06-30' })));
    const success = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements, saveSiteCommissionAgreement: jest.fn(), endSiteCommissionAgreement },
      undefined,
      { success }
    );

    fixture.componentInstance.endAgreement(agreement(), '2026-06-30');

    expect(endSiteCommissionAgreement).toHaveBeenCalledWith(1, '2026-06-30');
    expect(success).toHaveBeenCalledWith('Site commission agreement ended.');
    expect(siteCommissionAgreements).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.loading).toBe(false);
  });

  it('reports the API refusal verbatim and does not reload', async () => {
    const siteCommissionAgreements = jest.fn(() => of([agreement()]));
    const endSiteCommissionAgreement = jest.fn(() =>
      throwError(() => ({ error: { message: "The end date must be on or after the agreement's start date." } }))
    );
    const error = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements, saveSiteCommissionAgreement: jest.fn(), endSiteCommissionAgreement },
      undefined,
      { error }
    );

    fixture.componentInstance.endAgreement(agreement(), '2025-12-31');

    expect(error).toHaveBeenCalledWith("The end date must be on or after the agreement's start date.");
    expect(siteCommissionAgreements).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.loading).toBe(false);
  });

  it('calls nothing for an agreement the listing carries no key for', async () => {
    const endSiteCommissionAgreement = jest.fn();
    const { fixture } = await render(
      { siteCommissionAgreements: jest.fn(() => of([])), saveSiteCommissionAgreement: jest.fn(), endSiteCommissionAgreement }
    );

    fixture.componentInstance.endAgreement(agreement({ id: undefined }), '2026-06-30');

    expect(endSiteCommissionAgreement).not.toHaveBeenCalled();
  });
});
