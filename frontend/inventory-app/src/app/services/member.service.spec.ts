import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import { BusinessMember, MemberService } from './member.service';

interface HttpStub {
  get: jest.Mock;
  put: jest.Mock;
  post: jest.Mock;
}

const member: BusinessMember = {
  membershipId: 7,
  displayName: 'Dana Lee',
  email: 'dana@example.com',
  role: 'Manager',
  isActive: true,
  joinedAtUtc: '2026-01-01T00:00:00Z'
};

function createService(
  responses: { get?: () => Observable<unknown>; put?: () => Observable<unknown>; post?: () => Observable<unknown> } = {},
  apiBaseUrl = '/api'
): { service: MemberService; http: HttpStub } {
  const http: HttpStub = {
    get: jest.fn(responses.get ?? (() => of([member]))),
    put: jest.fn(responses.put ?? (() => of(member))),
    post: jest.fn(responses.post ?? (() => of(member)))
  };
  const service = new MemberService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

describe('MemberService (issue #533)', () => {
  it('lists every member of the caller\'s business', () => {
    const { service, http } = createService();

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/members');
  });

  it('changes a role by membership id and the published role name', () => {
    const { service, http } = createService();

    service.changeRole(7, 'Owner').subscribe();

    expect(http.put).toHaveBeenCalledWith('/api/members/7/role', { role: 'Owner' });
  });

  it('revokes a member with an empty body', () => {
    const { service, http } = createService();

    service.revoke(7).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/members/7/revoke', {});
  });

  it('reinstates a member with an empty body', () => {
    const { service, http } = createService();

    service.reinstate(7).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/members/7/reinstate', {});
  });

  it('builds its URL from the configured API base URL without doubling the separator', () => {
    const { service, http } = createService({}, 'https://api.example.com/api/');

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('https://api.example.com/api/members');
  });

  it('returns the API payload unchanged', () => {
    const { service } = createService({ get: () => of([member]) });
    let members: BusinessMember[] | null = null;

    service.list().subscribe((value) => (members = value));

    expect(members).toEqual([member]);
  });
});
