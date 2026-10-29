import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import { SuperAdminPlanGrant, SuperAdminPlanGrantsService } from './super-admin-plan-grants.service';

interface HttpStub {
  get: jest.Mock;
  put: jest.Mock;
  post: jest.Mock;
}

const grant: SuperAdminPlanGrant = {
  businessId: 9,
  name: 'Snack Co',
  hasGrant: true,
  plan: 'Pro',
  startsAtUtc: '2026-10-29T13:45:00Z',
  endsAtUtc: null,
  endsOnUtc: null,
  reason: 'Complimentary pilot',
  revision: 3,
  grantedAtUtc: '2026-10-29T13:45:00Z',
  active: true
};

function createService(apiBaseUrl = '/api'): { service: SuperAdminPlanGrantsService; http: HttpStub } {
  const http: HttpStub = {
    get: jest.fn(() => of(grant)),
    put: jest.fn(() => of(grant)),
    post: jest.fn(() => of(grant))
  };
  const service = new SuperAdminPlanGrantsService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

describe('SuperAdminPlanGrantsService (issue #982)', () => {
  it('reads one business’s grant from the route that names it', () => {
    const { service, http } = createService();

    service.get(9).subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/super-admin/businesses/9/plan-grant');
  });

  it('sends the plan, the end date, the reason and the revision it was given', () => {
    const { service, http } = createService();

    service.set(9, 'Starter', '2026-12-31', 'Trial', 3).subscribe();

    expect(http.put).toHaveBeenCalledWith('/api/super-admin/businesses/9/plan-grant', {
      plan: 'Starter',
      endsOnUtc: '2026-12-31',
      reason: 'Trial',
      revision: 3
    });
  });

  /**
   * The revision is sent exactly as the server reported it: a client that incremented or omitted it
   * would defeat the concurrency check it exists for.
   */
  it('never changes the revision it was given', () => {
    const { service, http } = createService();

    service.set(9, 'Pro', null, 'Upgrade', 0).subscribe();
    service.end(9, 'Finished', 7).subscribe();

    expect(http.put.mock.calls[0][1]).toMatchObject({ revision: 0 });
    expect(http.post.mock.calls[0][1]).toEqual({ reason: 'Finished', revision: 7 });
  });

  it('ends a grant through its own route, with no plan and no date', () => {
    const { service, http } = createService();

    service.end(9, 'Pilot finished', 2).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/plan-grant/end', {
      reason: 'Pilot finished',
      revision: 2
    });
  });

  it('tolerates a configured base URL with a trailing slash', () => {
    const { service, http } = createService('https://api.example.com/');

    service.get(4).subscribe();

    expect(http.get).toHaveBeenCalledWith('https://api.example.com/super-admin/businesses/4/plan-grant');
  });
});
