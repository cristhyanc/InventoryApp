import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { BusinessApplicationRequest } from './business-application-rules';
import { BusinessApplicationService } from './business-application.service';

const REQUEST: BusinessApplicationRequest = {
  name: 'Corner Store',
  contactEmail: 'owner@example.com',
  abn: null,
  gstRegistered: true,
  timeZoneId: 'Australia/Sydney',
  state: 'NSW'
};

const SUBMITTED = { name: 'Corner Store', status: 'PendingApproval', appliedAtUtc: '2026-10-13T00:00:00Z' };

function createService(apiBaseUrl = '/api'): { service: BusinessApplicationService; http: { post: jest.Mock } } {
  const http = { post: jest.fn(() => of(SUBMITTED)) };
  const service = new BusinessApplicationService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

/** `BusinessApplicationService` (issue #543): the form's one call, `POST /api/onboarding/business-applications`. */
describe('BusinessApplicationService (issue #543)', () => {
  it('posts the six settings and the accepted legal version to the onboarding endpoint', (done) => {
    const { service, http } = createService();

    service.submit(REQUEST, '2026-11-13').subscribe((submitted) => {
      expect(http.post).toHaveBeenCalledWith('/api/onboarding/business-applications', {
        ...REQUEST,
        acceptedLegalVersion: '2026-11-13'
      });
      expect(submitted).toEqual(SUBMITTED);
      done();
    });
  });

  /**
   * Issue #1259: the page sends whatever version it read, including none. The API refuses a request
   * with no version, which is the right answer for a page that could not read one - and much better
   * than this service inventing a version somebody never agreed to.
   */
  it('sends an unread version as null rather than inventing one', (done) => {
    const { service, http } = createService();

    service.submit(REQUEST, null).subscribe(() => {
      expect(http.post).toHaveBeenCalledWith('/api/onboarding/business-applications', {
        ...REQUEST,
        acceptedLegalVersion: null
      });
      done();
    });
  });

  /**
   * The accepted version is not a business setting: the same six settings go to
   * `PUT /api/business/settings`, which must not carry it - so it is added here rather than put on
   * `BusinessApplicationRequest`.
   */
  it('leaves the request shape itself free of the legal version', () => {
    expect(Object.keys(REQUEST)).not.toContain('acceptedLegalVersion');
  });

  it('builds the URL from the configured API base, trimming a trailing slash', (done) => {
    const { service, http } = createService('/api/');

    service.submit(REQUEST, '2026-11-13').subscribe(() => {
      expect(http.post.mock.calls[0][0]).toBe('/api/onboarding/business-applications');
      done();
    });
  });
});
