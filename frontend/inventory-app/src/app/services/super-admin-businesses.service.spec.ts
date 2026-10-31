import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import { SuperAdminBusiness, SuperAdminBusinessMember, SuperAdminBusinessesService } from './super-admin-businesses.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
}

const business: SuperAdminBusiness = {
  businessId: 9,
  name: 'Snack Co',
  status: 'Pending',
  createdAtUtc: '2026-01-01T00:00:00Z',
  appliedAtUtc: '2026-01-02T00:00:00Z',
  decidedAtUtc: null,
  contactEmail: 'owner@example.com',
  abn: '12345678901',
  state: 'NSW',
  activeMemberCount: 1,
  nayaxConfigured: false,
  nayaxConnectionStatus: 'NotConfigured'
};

const member: SuperAdminBusinessMember = {
  membershipId: 3,
  displayName: 'Dana Lee',
  role: 'Manager',
  isActive: true
};

function createService(
  responses: { get?: () => Observable<unknown>; post?: () => Observable<unknown> } = {},
  apiBaseUrl = '/api'
): { service: SuperAdminBusinessesService; http: HttpStub } {
  const http: HttpStub = {
    get: jest.fn(responses.get ?? (() => of([business]))),
    post: jest.fn(responses.post ?? (() => of(business)))
  };
  const service = new SuperAdminBusinessesService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

describe('SuperAdminBusinessesService (issue #547)', () => {
  it('lists every business, with no filter, sort or search parameter', () => {
    const { service, http } = createService();

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/super-admin/businesses');
  });

  it('reads the pending count from its named field', () => {
    const { service, http } = createService({ get: () => of({ pendingCount: 4 }) });
    let count: number | null = null;

    service.pendingCount().subscribe((value) => (count = value));

    expect(http.get).toHaveBeenCalledWith('/api/super-admin/businesses/pending-count');
    expect(count).toBe(4);
  });

  it('approves with an empty body', () => {
    const { service, http } = createService();

    service.approve(9).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/approve', {});
  });

  it('rejects with a trimmed reason', () => {
    const { service, http } = createService();

    service.reject(9, '  Not eligible  ').subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/reject', { reason: '  Not eligible  ' });
  });

  it('rejects with an empty body when no reason is given', () => {
    const { service, http } = createService();

    service.reject(9, null).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/reject', {});
  });

  it('rejects with an empty body for a blank reason', () => {
    const { service, http } = createService();

    service.reject(9, '   ').subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/reject', {});
  });

  it('deactivates with an empty body', () => {
    const { service, http } = createService();

    service.deactivate(9).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/deactivate', {});
  });

  it('reactivates with an empty body', () => {
    const { service, http } = createService();

    service.reactivate(9).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/reactivate', {});
  });

  it('lists the members of one named business', () => {
    const { service, http } = createService({ get: () => of([member]) });

    service.members(9).subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/super-admin/businesses/9/members');
  });

  it('releases a named membership of a named business with an empty body', () => {
    const { service, http } = createService({ post: () => of(member) });

    service.releaseMember(9, 3).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/businesses/9/members/3/release', {});
  });

  it('builds its URL from the configured API base URL without doubling the separator', () => {
    const { service, http } = createService({}, 'https://api.example.com/api/');

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('https://api.example.com/api/super-admin/businesses');
  });

  it('emits on changed$ only when notifyChanged is called', () => {
    const { service } = createService();
    const seen: void[] = [];
    service.changed$.subscribe((value) => seen.push(value));

    expect(seen).toHaveLength(0);

    service.notifyChanged();

    expect(seen).toHaveLength(1);
  });
});
