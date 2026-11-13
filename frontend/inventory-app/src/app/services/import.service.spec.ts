import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import { ImportService } from './import.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
}

function createService(apiBaseUrl = '/api'): { service: ImportService; http: HttpStub } {
  const http: HttpStub = { get: jest.fn(() => of({})), post: jest.fn(() => of({})) };
  const service = new ImportService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

/**
 * The three Reimbursement Report routes of issue #1149, as issue #1152's page calls them. They are
 * asserted here rather than only through the components because they are an API contract: a
 * mistyped path is a page that silently reports nothing, and the server's own tests pin the same
 * three routes (`ImportsControllerReimbursementReportTests`).
 */
describe('ImportService Reimbursement Report calls (issue #1152)', () => {
  it('uploads the ZIP as multipart to the reimbursement-report endpoint, under its own name', () => {
    const { service, http } = createService();
    const file = new File(['zip bytes'], 'Reports_09-11-26.zip', { type: 'application/zip' });

    service.uploadReimbursementReport(file).subscribe();

    expect(http.post).toHaveBeenCalledTimes(1);
    expect(http.post.mock.calls[0][0]).toBe('/api/imports/reimbursement-report');
    const form = http.post.mock.calls[0][1] as FormData;
    const part = form.get('file');
    expect(part instanceof File ? part.name : null).toBe('Reports_09-11-26.zip');
  });

  it('reads the job list from the jobs endpoint', () => {
    const { service, http } = createService();

    service.reimbursementReportJobs().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/imports/reimbursement-report/jobs');
  });

  it('reads one job from the job endpoint, addressed by its id', () => {
    const { service, http } = createService();

    service.reimbursementReportJob(42).subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/imports/reimbursement-report/jobs/42');
  });

  /** The configured base URL is the only authority on where the API is (AGENTS.md § Frontend rules). */
  it('builds every call from the configured API base URL, trailing slash and all', () => {
    const { service, http } = createService('https://api.example.com/');

    service.reimbursementReportJobs().subscribe();
    service.reimbursementReportJob(7).subscribe();
    service.uploadReimbursementReport(new File([], 'r.zip')).subscribe();

    expect(http.get.mock.calls[0][0]).toBe('https://api.example.com/imports/reimbursement-report/jobs');
    expect(http.get.mock.calls[1][0]).toBe('https://api.example.com/imports/reimbursement-report/jobs/7');
    expect(http.post.mock.calls[0][0]).toBe('https://api.example.com/imports/reimbursement-report');
  });

  /**
   * The Nayax sales call is untouched by the page that moved (issue #1152) and by the server-folder
   * import's retirement (issue #1237): it is the only `POST` left here besides the ZIP upload.
   */
  it('leaves the Nayax sales call exactly as it was', () => {
    const { service, http } = createService();

    service.importNayaxSales(new File([], 'sales.csv')).subscribe();

    expect(http.post.mock.calls[0][0]).toBe('/api/imports/nayax-sales');
  });
});
