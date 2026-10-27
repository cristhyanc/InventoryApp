import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import {
  SUPPLIER_PRICE_CONNECTOR_LABELS,
  SUPPLIER_SERVED_STATES,
  SupplierPriceConnector,
  SuperAdminSupplierPreset,
  SuperAdminSupplierPresetsService
} from './super-admin-supplier-presets.service';

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
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: SupplierPriceConnector.Varnmill,
  priceConnectorKey: 'coles',
  createdAtUtc: '2026-10-23T00:00:00Z',
  updatedAtUtc: '2026-10-23T00:00:00Z'
};

/** The supplier-master half of a request, as the one a nationwide GST-inclusive preset sends. */
const nationwideGstInclusive = {
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: null,
  priceConnectorKey: null
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

    service
      .create({ name: 'Aldi', url: 'https://www.aldi.com.au', ...nationwideGstInclusive })
      .subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', {
      name: 'Aldi',
      url: 'https://www.aldi.com.au',
      ...nationwideGstInclusive
    });
  });

  it('posts a create with a null website when none was given', () => {
    const { service, http } = createService();

    service.create({ name: 'Aldi', url: null, ...nationwideGstInclusive }).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', {
      name: 'Aldi',
      url: null,
      ...nationwideGstInclusive
    });
  });

  it('posts the supplier-master fields it was given, unchanged (issue #931)', () => {
    const { service, http } = createService();

    service
      .create({
        name: 'JB Metro SA & NT',
        url: 'https://www.jbmetro-sa-nt.com.au',
        servesNationwide: false,
        servedStates: ['SA', 'NT'],
        pricesIncludeGst: false,
        priceConnector: SupplierPriceConnector.JbMetro,
        priceConnectorKey: null
      })
      .subscribe();

    // A thin wrapper: no rule about a market, a GST basis or a connector is decided here, so what
    // the caller submitted is exactly what is sent.
    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', {
      name: 'JB Metro SA & NT',
      url: 'https://www.jbmetro-sa-nt.com.au',
      servesNationwide: false,
      servedStates: ['SA', 'NT'],
      pricesIncludeGst: false,
      priceConnector: SupplierPriceConnector.JbMetro,
      priceConnectorKey: null
    });
  });

  it('puts an edit of the whole resource, enabled state included', () => {
    const { service, http } = createService();

    service
      .update(4, { name: 'Coles Supermarkets', url: null, isEnabled: false, ...nationwideGstInclusive })
      .subscribe();

    expect(http.put).toHaveBeenCalledWith('/api/super-admin/supplier-presets/4', {
      name: 'Coles Supermarkets',
      url: null,
      isEnabled: false,
      ...nationwideGstInclusive
    });
  });

  /**
   * The vocabularies are the server's, mirrored here as the numbers and codes the API serializes
   * (backend `SupplierPriceConnector` and `SupplierPresetMasterPolicy.ServedStateCodes`). They are
   * asserted rather than assumed because they are the contract: a renumbered connector or a renamed
   * state code would make every stored preset read as a different one.
   */
  it('mirrors the server’s connector numbers and state codes (issue #931)', () => {
    expect(SupplierPriceConnector.Varnmill).toBe(1);
    expect(SupplierPriceConnector.Kmart).toBe(2);
    expect(SupplierPriceConnector.BigW).toBe(3);
    expect(SupplierPriceConnector.JbMetro).toBe(4);

    expect(SUPPLIER_SERVED_STATES).toEqual(['NSW', 'VIC', 'QLD', 'SA', 'WA', 'TAS', 'ACT', 'NT']);

    // Every declared route has a label, so the page can never show a blank price source for a
    // preset that has one.
    expect(Object.values(SUPPLIER_PRICE_CONNECTOR_LABELS)).toEqual([
      'Varnmill',
      'Kmart',
      'BIG W',
      'JB Metro'
    ]);
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
