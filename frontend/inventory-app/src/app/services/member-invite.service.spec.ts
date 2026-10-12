import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import { CreatedMemberInvite, MemberInvite, MemberInviteService } from './member-invite.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
}

const invite: MemberInvite = {
  inviteId: 3,
  role: 'Manager',
  note: 'New site manager',
  status: 'Pending',
  createdAtUtc: '2026-01-01T00:00:00Z',
  expiresAtUtc: '2026-01-08T00:00:00Z',
  usedAtUtc: null,
  cancelledAtUtc: null,
  redeemedByMembershipId: null
};

const created: CreatedMemberInvite = { token: 'abc123', invite };

function createService(
  responses: { get?: () => Observable<unknown>; post?: () => Observable<unknown> } = {},
  apiBaseUrl = '/api'
): { service: MemberInviteService; http: HttpStub } {
  const http: HttpStub = {
    get: jest.fn(responses.get ?? (() => of([invite]))),
    post: jest.fn(responses.post ?? (() => of(created)))
  };
  const service = new MemberInviteService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

describe('MemberInviteService (issue #531)', () => {
  it('lists every invite of the caller\'s business', () => {
    const { service, http } = createService();

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/members/invites');
  });

  it('creates an invite with the role and note and nothing else', () => {
    const { service, http } = createService();

    service.create('Manager', 'New site manager').subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/members/invites', { role: 'Manager', note: 'New site manager' });
  });

  it('creates an invite with a null note when none was entered', () => {
    const { service, http } = createService();

    service.create('Operator', null).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/members/invites', { role: 'Operator', note: null });
  });

  it('cancels an invite by id with an empty body', () => {
    const { service, http } = createService({ post: () => of(invite) });

    service.cancel(3).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/members/invites/3/cancel', {});
  });

  it('builds its URL from the configured API base URL without doubling the separator', () => {
    const { service, http } = createService({}, 'https://api.example.com/api/');

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('https://api.example.com/api/members/invites');
  });

  it('returns the creation response, token included, unchanged', () => {
    const { service } = createService({ post: () => of(created) });
    let result: CreatedMemberInvite | null = null;

    service.create('Manager', null).subscribe((value) => (result = value));

    expect(result).toEqual(created);
  });
});
