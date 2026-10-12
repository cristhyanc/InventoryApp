import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { InviteRedemptionService } from './invite-redemption.service';

interface HttpStub {
  post: jest.Mock;
}

function createService(apiBaseUrl = '/api'): { service: InviteRedemptionService; http: HttpStub } {
  const http: HttpStub = { post: jest.fn(() => of({ role: 'Manager' })) };
  const service = new InviteRedemptionService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

/**
 * `InviteRedemptionService` (issue #535): the one call the invite acceptance page makes, against
 * `POST /api/invites/redeem` (issue #532).
 */
describe('InviteRedemptionService (issue #535)', () => {
  it('posts the token in the request body, never in the URL', (done) => {
    const { service, http } = createService();

    service.redeem('a-token').subscribe(() => {
      expect(http.post).toHaveBeenCalledWith('/api/invites/redeem', { token: 'a-token' });
      done();
    });
  });

  it('builds the URL from the configured API base, trimming a trailing slash', (done) => {
    const { service, http } = createService('/api/');

    service.redeem('a-token').subscribe(() => {
      expect(http.post.mock.calls[0][0]).toBe('/api/invites/redeem');
      done();
    });
  });

  it('reports only the granted role', (done) => {
    const { service } = createService();

    service.redeem('a-token').subscribe((redeemed) => {
      expect(redeemed).toEqual({ role: 'Manager' });
      done();
    });
  });
});
