import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
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
 * The dedicated Reimbursement Report page (issue #1152): the ZIP upload and the recent uploads
 * list. The pending-XML action that used to stand beside them went with the server-folder import
 * issue #1237 retired.
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

  /**
   * The server-folder import is retired (issue #1237): the page offers the upload and nothing else,
   * so there is no button here that imports a file nobody on this page chose.
   */
  it('offers no XML import button of its own', async () => {
    const { host } = await render();

    expect(button(host, 'Import XML files')).toBeUndefined();
  });

  /**
   * A read-only support session (issue #661) refuses every `POST` centrally, so the upload on this
   * page is withheld. The job list is a `GET`, which a session may make, so it is left alone.
   */
  describe('in a support session', () => {
    it('says so and withholds the page\'s upload', async () => {
      const { fixture, host } = await render({}, undefined, of(true));
      const upload = fixture.debugElement.query(By.directive(ReimbursementReportUploadComponent));

      expect(host.querySelector('[data-testid="reimbursement-support-session-notice"]')).not.toBeNull();
      expect((upload.componentInstance as ReimbursementReportUploadComponent).disabled).toBe(true);
    });

    it('still reads the recent uploads, because the status endpoint is a GET', async () => {
      const reimbursementReportJobs = jest.fn(() => of([]));
      await render({ reimbursementReportJobs }, undefined, of(true));

      expect(reimbursementReportJobs).toHaveBeenCalled();
    });

    it('leaves the upload available while no session is active', async () => {
      const { fixture, host } = await render({}, undefined, of(false));
      const upload = fixture.debugElement.query(By.directive(ReimbursementReportUploadComponent));

      expect(host.querySelector('[data-testid="reimbursement-support-session-notice"]')).toBeNull();
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
