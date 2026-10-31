import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import { SupplierService } from './supplier.service';

interface HttpStub {
  get: jest.Mock;
  put: jest.Mock;
  post: jest.Mock;
}

function createService(apiBaseUrl = '/api'): { service: SupplierService; http: HttpStub } {
  const http: HttpStub = {
    get: jest.fn(() => of([])),
    put: jest.fn(() => of(undefined)),
    post: jest.fn(() => of(undefined))
  };
  const service = new SupplierService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

/**
 * Which list each caller asks for (issue #934). This is the whole of how a hidden supplier stays
 * out of the pickers in the client: every picker calls `getAll`, which asks for nothing about
 * hidden suppliers and so gets the list the API leaves them out of, and only the Suppliers page
 * asks for them.
 */
describe('SupplierService hidden suppliers (issue #934)', () => {
  it('asks for the plain list, which the API answers without the suppliers marked "Not used"', () => {
    const { service, http } = createService();

    service.getAll().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/suppliers');
  });

  it('asks for the hidden ones explicitly for the Suppliers page', () => {
    const { service, http } = createService();

    service.getAllIncludingHidden().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/suppliers?includeHidden=true');
  });

  it('puts the "Not used" state to the supplier\'s own resource', () => {
    const { service, http } = createService();

    service.setNotUsed(7, true).subscribe();
    service.setNotUsed(7, false).subscribe();

    expect(http.put).toHaveBeenNthCalledWith(1, '/api/suppliers/7/not-used', { isHidden: true });
    expect(http.put).toHaveBeenNthCalledWith(2, '/api/suppliers/7/not-used', { isHidden: false });
  });

  it('keeps the trailing slash off the base URL', () => {
    const { service, http } = createService('/api/');

    service.getAll().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/suppliers');
  });
});

/**
 * The "Available to add" requests (issue #935). Both carry nothing but the record's id in the URL:
 * the business is the caller's own and the name and website are the platform's, so there is no
 * filter, state or payload for a client to get wrong.
 */
describe('SupplierService available platform suppliers (issue #935)', () => {
  it('asks for what this business may add, with no filter at all', () => {
    const { service, http } = createService();

    service.getAvailablePresets().subscribe();

    expect(http.get).toHaveBeenCalledWith('/api/suppliers/available-presets');
  });

  it('adds one by posting its id with no body', () => {
    const { service, http } = createService();

    service.addFromPreset(12).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/suppliers/available-presets/12', null);
  });
});
