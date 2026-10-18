import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { Machine, Site } from '../../models/models';
import {
  SpecConnectionStatus,
  provideNayaxConnectionStatus
} from '../../provider/provider-availability.spec-helpers';
import { MachineService } from '../../services/machine.service';
import { ReportingService } from '../../services/reporting.service';
import { SiteService } from '../../services/site.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { MachineReportComponent } from './machine-report.component';
import { SiteCommissionsReportComponent } from './site-commissions-report.component';

function machine(machineID: number, machineName: string): Machine {
  return { machineID, machineName } as Machine;
}

function site(): Site {
  return { siteId: 1, siteName: 'Lobby' } as Site;
}

function notConnected(): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: { code: 'nayax_not_connected', nayaxConnectionStatus: 'NotConfigured' }
  });
}

async function renderMachineReport(options: {
  status?: SpecConnectionStatus;
  machines?: Observable<Machine[]>;
} = {}) {
  await TestBed.configureTestingModule({
    imports: [MachineReportComponent],
    providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } },
      { provide: ReportingService, useValue: { machines: jest.fn(() => of({ rows: [] })) } },
      {
        provide: MachineService,
        useValue: { getAll: jest.fn(() => options.machines ?? of([machine(9, 'Lobby')])) }
      },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(MachineReportComponent);
  fixture.detectChanges();

  return {
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    machineService: TestBed.inject(MachineService) as unknown as { getAll: jest.Mock }
  };
}

async function renderSiteCommissions(options: {
  status?: SpecConnectionStatus;
  sites?: Observable<Site[]>;
} = {}) {
  await TestBed.configureTestingModule({
    imports: [SiteCommissionsReportComponent],
    providers: [
      { provide: ReportingService, useValue: { siteCommissions: jest.fn(() => of({ rows: [] })) } },
      { provide: SiteService, useValue: { getAll: jest.fn(() => options.sites ?? of([site()])) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      provideNayaxConnectionStatus(options.status ?? 'Ready')
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SiteCommissionsReportComponent);
  fixture.detectChanges();

  return {
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    siteService: TestBed.inject(SiteService) as unknown as { getAll: jest.Mock }
  };
}

const machineSelect = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('label')).find((label) => label.textContent?.trim().startsWith('Machine'));
const machineFilterError = (host: HTMLElement) => host.querySelector('[data-testid="report-machine-filter-error"]');
const siteSelect = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('label')).find((label) => label.textContent?.trim().startsWith('Site'));
const siteFilterError = (host: HTMLElement) => host.querySelector('[data-testid="site-filter-error"]');

afterEach(() => TestBed.resetTestingModule());

/**
 * The report pages' machine and site filters without a usable provider (issue #728). Both lists
 * come from a live fleet read, so a business without one has no such filter - which is a different
 * answer from a read that failed, and neither may be presented as "All machines"/"All Sites"
 * narrowing nothing.
 *
 * `MachineReportComponent` stands for every page built on `ReportPageBase`: the filter, the load
 * and the error sentence all live there, so one page proves the shared behaviour and the others
 * differ only in the report they request.
 */
describe('Report machine filter without a provider (issue #728)', () => {
  it('offers the machine filter for a connected business', async () => {
    const { host } = await renderMachineReport();

    expect(machineSelect(host)).toBeDefined();
    expect(machineFilterError(host)).toBeNull();
  });

  it('hides the machine filter without a provider, and asks for no machines', async () => {
    const { host, machineService } = await renderMachineReport({ status: 'NotConfigured' });

    expect(machineService.getAll).not.toHaveBeenCalled();
    expect(machineSelect(host)).toBeUndefined();
    expect(machineFilterError(host)).toBeNull();
  });

  it('hides it when the machine read itself is refused', async () => {
    const { host, component } = await renderMachineReport({
      status: 'unreadable',
      machines: throwError(() => notConnected())
    });

    expect(machineSelect(host)).toBeUndefined();
    expect(component.machinesError).toBe('');
  });

  it('shows an error in place of the filter when the machine read failed', async () => {
    const { host } = await renderMachineReport({
      machines: throwError(() => new HttpErrorResponse({ status: 0 }))
    });

    expect(machineSelect(host)).toBeUndefined();
    expect(machineFilterError(host)?.textContent?.trim()).toBe(
      'The machine list could not be loaded, so this report cannot be narrowed to one machine.'
    );
  });

  it('keeps Apply available whether or not the machine filter is there', async () => {
    const connected = await renderMachineReport();
    expect(
      Array.from(connected.host.querySelectorAll('button')).some((button) => button.textContent?.trim() === 'Apply')
    ).toBe(true);
  });
});

describe('Site Commissions site filter without a provider (issue #728)', () => {
  it('offers the site filter for a connected business', async () => {
    const { host } = await renderSiteCommissions();

    expect(siteSelect(host)).toBeDefined();
    expect(siteFilterError(host)).toBeNull();
  });

  it('hides the site filter without a provider, and asks for no sites', async () => {
    const { host, siteService } = await renderSiteCommissions({ status: 'NotConfigured' });

    expect(siteService.getAll).not.toHaveBeenCalled();
    expect(siteSelect(host)).toBeUndefined();
    expect(siteFilterError(host)).toBeNull();
  });

  it('shows an error in place of the site filter when the site read failed', async () => {
    const { host } = await renderSiteCommissions({
      sites: throwError(() => new HttpErrorResponse({ status: 500 }))
    });

    expect(siteSelect(host)).toBeUndefined();
    expect(siteFilterError(host)?.textContent?.trim()).toBe(
      'The site list could not be loaded, so this report cannot be narrowed to one site.'
    );
  });
});
