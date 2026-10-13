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
  timeZoneId: 'Australia/Sydney'
};

const SUBMITTED = { name: 'Corner Store', status: 'PendingApproval', appliedAtUtc: '2026-10-13T00:00:00Z' };

function createService(apiBaseUrl = '/api'): { service: BusinessApplicationService; http: { post: jest.Mock } } {
  const http = { post: jest.fn(() => of(SUBMITTED)) };
  const service = new BusinessApplicationService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

/** `BusinessApplicationService` (issue #543): the form's one call, `POST /api/onboarding/business-applications`. */
describe('BusinessApplicationService (issue #543)', () => {
  it('posts the five settings to the onboarding endpoint', (done) => {
    const { service, http } = createService();

    service.submit(REQUEST).subscribe((submitted) => {
      expect(http.post).toHaveBeenCalledWith('/api/onboarding/business-applications', REQUEST);
      expect(submitted).toEqual(SUBMITTED);
      done();
    });
  });

  it('builds the URL from the configured API base, trimming a trailing slash', (done) => {
    const { service, http } = createService('/api/');

    service.submit(REQUEST).subscribe(() => {
      expect(http.post.mock.calls[0][0]).toBe('/api/onboarding/business-applications');
      done();
    });
  });
});
