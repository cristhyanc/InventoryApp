import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { BusinessTimeZoneService } from '../../../../formatting/business-time-zone.service';
import { ImportService } from '../../../../services/import.service';
import { ToastService } from '../../../../services/toast.service';
import { SupportSessionService } from '../../../../support-session/support-session.service';
import { ReimbursementImportJobsComponent } from './reimbursement-import-jobs.component';
import { ReimbursementReportPageComponent } from './reimbursement-report-page.component';
import { ReimbursementReportUploadComponent } from './reimbursement-report-upload.component';

async function render(
  imports: Partial<ImportService> = {},
  toast?: Partial<ToastService>,
  sessionActive: Observable<boolean> = of(false)
) {
  await TestBed.configureTestingModule({
    imports: [ReimbursementReportPageComponent],
    providers: [
      provideRouter([]),
      { provide: ImportService, useValue: { reimbursementReportJobs: () => of([]), ...imports } },
      {
        provide: ToastService,
        useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast }
      },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
    ]
  }).compileComponents();

  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(ReimbursementReportPageComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function button(host: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes(label));
}

afterEach(() => TestBed.resetTestingModule());

/**
 * The dedicated Reimbursement Report page (issue #1152): the ZIP upload, the recent uploads list and
 * the pending-XML action that moved here from Admin → Imports.
 */
describe('ReimbursementReportPageComponent (issue #1152)', () => {
  it('names itself and links back to the Imports page it is reached from', async () => {
    const { host } = await render();

    expect(host.querySelector('.page-title')?.textContent).toContain('Reimbursement Report');
    const back = Array.from(host.querySelectorAll('a')).find((link) => link.textContent?.includes('Imports'));
    expect(back?.getAttribute('href')).toBe('/admin/imports');
  });

  /**
   * The page is a composition boundary (docs/architecture.md § Page composition boundary): the
   * upload and the job list each own their own state, actions and lifecycle, so each is its own
   * feature component rather than markup grown into this one.
   */
  it('composes the upload card and the recent uploads list rather than owning either workflow', async () => {
    const { fixture } = await render();

    expect(fixture.debugElement.query(By.directive(ReimbursementReportUploadComponent))).not.toBeNull();
    expect(fixture.debugElement.query(By.directive(ReimbursementImportJobsComponent))).not.toBeNull();
  });

  it('asks the list to read again once an upload has been accepted', async () => {
    const { fixture } = await render();
    const list = fixture.debugElement.query(By.directive(ReimbursementImportJobsComponent));
    const upload = fixture.debugElement.query(By.directive(ReimbursementReportUploadComponent));
    const before = (list.componentInstance as ReimbursementImportJobsComponent).reloadToken;

    (upload.componentInstance as ReimbursementReportUploadComponent).uploaded.emit(11);
    fixture.detectChanges();

    expect((list.componentInstance as ReimbursementImportJobsComponent).reloadToken).not.toBe(before);
  });

  it('runs the pending reimbursement XML import and reports the counts it imported', async () => {
    const importPendingXmlFiles = jest.fn(() =>
      of({ importedFiles: 2, importedReimbursements: 4, skippedFiles: 1, failedFiles: 0 })
    );
    const success = jest.fn();
    const { fixture, host } = await render({ importPendingXmlFiles }, { success });

    button(host, 'Import XML files')?.click();

    expect(importPendingXmlFiles).toHaveBeenCalledTimes(1);
    expect(success).toHaveBeenCalledWith('Imported 2 file(s) and 4 reimbursement(s).');
    expect(fixture.componentInstance.xmlLoading).toBe(false);
  });

  it('reports the API error and clears loading when the pending XML import fails', async () => {
    const importPendingXmlFiles = jest.fn(() => throwError(() => ({ error: 'No pending XML files found.' })));
    const error = jest.fn();
    const { fixture } = await render({ importPendingXmlFiles }, { error });

    fixture.componentInstance.importXml();

    expect(error).toHaveBeenCalledWith('No pending XML files found.');
    expect(fixture.componentInstance.xmlLoading).toBe(false);
  });

  it('falls back to the generic XML failure message when the API returns no body', async () => {
    const importPendingXmlFiles = jest.fn(() => throwError(() => ({})));
    const error = jest.fn();
    const { fixture } = await render({ importPendingXmlFiles }, { error });

    fixture.componentInstance.importXml();

    expect(error).toHaveBeenCalledWith('The XML import could not be started.');
  });

  /**
   * A read-only support session (issue #661) refuses every `POST` centrally, so both writes on this
   * page are withheld - and the handler checks it too, so the action is unavailable rather than
   * merely inert. The job list is a `GET`, which a session may make, so it is left alone.
   */
  describe('in a support session', () => {
    it('says so and withholds both of the page\'s writes', async () => {
      const importPendingXmlFiles = jest.fn();
      const { fixture, host } = await render({ importPendingXmlFiles }, undefined, of(true));
      const upload = fixture.debugElement.query(By.directive(ReimbursementReportUploadComponent));

      expect(host.querySelector('[data-testid="reimbursement-support-session-notice"]')).not.toBeNull();
      expect(button(host, 'Import XML files')?.disabled).toBe(true);
      expect((upload.componentInstance as ReimbursementReportUploadComponent).disabled).toBe(true);

      fixture.componentInstance.importXml();
      expect(importPendingXmlFiles).not.toHaveBeenCalled();
    });

    it('still reads the recent uploads, because the status endpoint is a GET', async () => {
      const reimbursementReportJobs = jest.fn(() => of([]));
      await render({ reimbursementReportJobs }, undefined, of(true));

      expect(reimbursementReportJobs).toHaveBeenCalled();
    });

    it('leaves both controls available while no session is active', async () => {
      const { fixture, host } = await render({}, undefined, of(false));
      const upload = fixture.debugElement.query(By.directive(ReimbursementReportUploadComponent));

      expect(host.querySelector('[data-testid="reimbursement-support-session-notice"]')).toBeNull();
      expect(button(host, 'Import XML files')?.disabled).toBe(false);
      expect((upload.componentInstance as ReimbursementReportUploadComponent).disabled).toBe(false);
    });
  });

  /** Nothing financial is on this page: it reports what an import did, never what it was worth. */
  it('renders no figure, no payment detail and no XML of its own', async () => {
    const { host } = await render();

    expect(host.textContent).not.toContain('$');
    expect(host.querySelector('table')).toBeNull();
  });
});
