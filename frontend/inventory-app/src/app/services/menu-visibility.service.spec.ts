import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import { MenuVisibilityService } from './menu-visibility.service';

function createService(apiBaseUrl = '/api'): {
  service: MenuVisibilityService;
  get: jest.Mock;
  put: jest.Mock;
} {
  const get = jest.fn(() => of({ options: [] }));
  const put = jest.fn(() => of({ key: 'admin.imports', label: 'Imports', visible: false }));
  const service = new MenuVisibilityService(
    { get, put } as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, get, put };
}

describe('MenuVisibilityService (issues #1148 and #1151)', () => {
  it('reads the whole catalogue from the Super Admin menu-visibility resource', () => {
    const { service, get } = createService();

    service.list().subscribe();

    expect(get).toHaveBeenCalledWith('/api/super-admin/menu-visibility');
    expect(get).toHaveBeenCalledTimes(1);
  });

  /** The key is in the route and the state is the whole body: one write names one destination. */
  it('puts one key on its own resource, with the requested state as the body', () => {
    const { service, put } = createService();

    service.set('admin.imports', false).subscribe();

    expect(put).toHaveBeenCalledWith('/api/super-admin/menu-visibility/admin.imports', { visible: false });
    expect(put).toHaveBeenCalledTimes(1);
  });

  it('puts the on state the same way, so switching back is the same one call', () => {
    const { service, put } = createService();

    service.set('admin.costing-repair', true).subscribe();

    expect(put).toHaveBeenCalledWith('/api/super-admin/menu-visibility/admin.costing-repair', { visible: true });
  });

  /** A key is server-owned, so it is sent back as given - encoded, never rewritten. */
  it('encodes the key it was given rather than interpreting it', () => {
    const { service, put } = createService();

    service.set('admin.a b/c', true).subscribe();

    expect(put).toHaveBeenCalledWith('/api/super-admin/menu-visibility/admin.a%20b%2Fc', { visible: true });
  });

  /**
   * The base URL comes from `ConfigService` like every other service's (AGENTS.md § Frontend
   * rules), and a trailing slash on it does not produce a doubled one.
   */
  it('builds the URL from the configured API base, with no doubled separator', () => {
    const { service, get, put } = createService('https://api.example.com/');

    service.list().subscribe();
    service.set('admin.imports', true).subscribe();

    expect(get).toHaveBeenCalledWith('https://api.example.com/super-admin/menu-visibility');
    expect(put).toHaveBeenCalledWith(
      'https://api.example.com/super-admin/menu-visibility/admin.imports',
      expect.anything()
    );
  });
});
