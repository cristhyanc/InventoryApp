import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import { SuperAdminSupplierPreset, SuperAdminSupplierPresetsService } from './super-admin-supplier-presets.service';

interface HttpStub {
  get: jest.Mock;
  post: jest.Mock;
  put: jest.Mock;
}

const preset: SuperAdminSupplierPreset = {
  id: 4,
  name: 'Coles',
  url: 'https://www.coles.com.au',
  isEnabled: true,
  createdAtUtc: '2026-10-23T00:00:00Z',
  updatedAtUtc: '2026-10-23T00:00:00Z'
};

function createService(
  responses: { get?: () => Observable<unknown>; post?: () => Observable<unknown>; put?: () => Observable<unknown> } = {},
  apiBaseUrl = '/api'
): { service: SuperAdminSupplierPresetsService; http: HttpStub } {
  const http: HttpStub = {
    get: jest.fn(responses.get ?? (() => of([preset]))),
    post: jest.fn(responses.post ?? (() => of(preset))),
    put: jest.fn(responses.put ?? (() => of(preset)))
  };
  const service = new SuperAdminSupplierPresetsService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

describe('SuperAdminSupplierPresetsService (issue #848)', () => {
  it('lists every preset, with no filter, sort or search parameter', () => {
    const { service, http } = createService();

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/super-admin/supplier-presets');
    expect(http.get).toHaveBeenCalledTimes(1);
  });

  it('posts a create with the name and the website', () => {
    const { service, http } = createService();

    service.create({ name: 'Aldi', url: 'https://www.aldi.com.au' }).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', {
      name: 'Aldi',
      url: 'https://www.aldi.com.au'
    });
  });

  it('posts a create with a null website when none was given', () => {
    const { service, http } = createService();

    service.create({ name: 'Aldi', url: null }).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', { name: 'Aldi', url: null });
  });

  it('puts an edit of the whole resource, enabled state included', () => {
    const { service, http } = createService();

    service.update(4, { name: 'Coles Supermarkets', url: null, isEnabled: false }).subscribe();

    expect(http.put).toHaveBeenCalledWith('/api/super-admin/supplier-presets/4', {
      name: 'Coles Supermarkets',
      url: null,
      isEnabled: false
    });
  });

  it('reads the API base URL from ConfigService and tolerates a trailing slash', () => {
    const { service, http } = createService({}, 'https://api.example.test/inventory/');

    service.list().subscribe();

    expect(http.get).toHaveBeenCalledWith('https://api.example.test/inventory/super-admin/supplier-presets');
  });

  it('exposes no delete, because the API has none', () => {
    const { service } = createService();

    expect((service as unknown as Record<string, unknown>)['delete']).toBeUndefined();
  });
});
