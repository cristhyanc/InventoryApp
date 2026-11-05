import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { ConfigService } from './config.service';
import {
  SUPPLIER_PRICE_CONNECTOR_LABELS,
  SUPPLIER_SEARCH_WEEKDAYS,
  SUPPLIER_SERVED_STATES,
  SupplierPriceConnector,
  SupplierSearchFrequency,
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
  searchPrices: true,
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: SupplierPriceConnector.Varnmill,
  priceConnectorKey: 'coles',
  searchFrequency: SupplierSearchFrequency.Daily,
  searchDaysOfWeek: [],
  fortnightStartDate: null,
  scheduleEffectiveFromUtc: '2026-10-23T00:00:00Z',
  priceCheckFreshnessHours: 30,
  createdAtUtc: '2026-10-23T00:00:00Z',
  updatedAtUtc: '2026-10-23T00:00:00Z'
};

/** The supplier-master half of a request, as the one a nationwide GST-inclusive preset sends. */
const nationwideGstInclusive = {
  searchPrices: true,
  servesNationwide: true,
  servedStates: [],
  pricesIncludeGst: true,
  priceConnector: null,
  priceConnectorKey: null,
  searchFrequency: SupplierSearchFrequency.Daily,
  searchDaysOfWeek: [],
  fortnightStartDate: null
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
        searchPrices: true,
        servesNationwide: false,
        servedStates: ['SA', 'NT'],
        pricesIncludeGst: false,
        priceConnector: SupplierPriceConnector.JbMetro,
        priceConnectorKey: null,
        searchFrequency: SupplierSearchFrequency.Daily,
        searchDaysOfWeek: [],
        fortnightStartDate: null
      })
      .subscribe();

    // A thin wrapper: no rule about a market, a GST basis or a connector is decided here, so what
    // the caller submitted is exactly what is sent.
    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', {
      name: 'JB Metro SA & NT',
      url: 'https://www.jbmetro-sa-nt.com.au',
      searchPrices: true,
      servesNationwide: false,
      servedStates: ['SA', 'NT'],
      pricesIncludeGst: false,
      priceConnector: SupplierPriceConnector.JbMetro,
      priceConnectorKey: null,
      searchFrequency: SupplierSearchFrequency.Daily,
      searchDaysOfWeek: [],
      fortnightStartDate: null
    });
  });

  /**
   * The three schedule fields travel exactly as given on both calls (issues #1108 and #1109). This
   * client decides nothing about them - not the weekday order, not whether a start date belongs to
   * the chosen frequency - and it never sends a freshness value, because the server derives the
   * stored limit itself and ignores one a request states.
   */
  it('posts the schedule fields it was given, unchanged, and no freshness value (issue #1109)', () => {
    const { service, http } = createService();

    service
      .create({
        name: 'Kmart',
        url: null,
        ...nationwideGstInclusive,
        searchFrequency: SupplierSearchFrequency.Fortnightly,
        searchDaysOfWeek: ['MON', 'FRI'],
        fortnightStartDate: '2026-11-09'
      })
      .subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', {
      name: 'Kmart',
      url: null,
      ...nationwideGstInclusive,
      searchFrequency: SupplierSearchFrequency.Fortnightly,
      searchDaysOfWeek: ['MON', 'FRI'],
      fortnightStartDate: '2026-11-09'
    });
    expect(http.post.mock.calls[0][1]).not.toHaveProperty('priceCheckFreshnessHours');
  });

  it('puts the schedule fields it was given, unchanged (issue #1109)', () => {
    const { service, http } = createService();

    service
      .update(4, {
        name: 'Kmart',
        url: null,
        isEnabled: true,
        ...nationwideGstInclusive,
        searchFrequency: SupplierSearchFrequency.Weekly,
        searchDaysOfWeek: ['WED', 'SAT'],
        fortnightStartDate: null
      })
      .subscribe();

    expect(http.put).toHaveBeenCalledWith('/api/super-admin/supplier-presets/4', {
      name: 'Kmart',
      url: null,
      isEnabled: true,
      ...nationwideGstInclusive,
      searchFrequency: SupplierSearchFrequency.Weekly,
      searchDaysOfWeek: ['WED', 'SAT'],
      fortnightStartDate: null
    });
    expect(http.put.mock.calls[0][1]).not.toHaveProperty('priceCheckFreshnessHours');
  });

  /**
   * An edit that states no schedule at all sends a body with none of the three fields in it, which
   * is the API's own "keep the stored schedule" path (issue #1108) and what the list's two toggle
   * buttons rely on (issue #1109). The field has to be *absent*, not `null` or an empty list: the
   * server reads any of the three as a whole schedule being stated, so a `searchFrequency: null`
   * would be a schedule edit refused for having no frequency rather than a request that said nothing.
   */
  it('puts no schedule field at all when the edit states none (issue #1109)', () => {
    const { service, http } = createService();

    service
      .update(4, {
        name: 'Kmart',
        url: null,
        isEnabled: false,
        searchPrices: true,
        servesNationwide: true,
        servedStates: [],
        pricesIncludeGst: true,
        priceConnector: SupplierPriceConnector.Kmart,
        priceConnectorKey: null
      })
      .subscribe();

    expect(Object.keys(http.put.mock.calls[0][1])).toEqual([
      'name',
      'url',
      'isEnabled',
      'searchPrices',
      'servesNationwide',
      'servedStates',
      'pricesIncludeGst',
      'priceConnector',
      'priceConnectorKey'
    ]);
  });

  /**
   * The start date is a date-only value and this client is a pass-through for it: the characters the
   * caller gave are the characters that go on the wire, with no `Date` and no zone in between.
   */
  it('sends the fortnight start date as the characters it was given (issue #1109)', () => {
    const { service, http } = createService();

    service
      .create({
        name: 'Kmart',
        url: null,
        ...nationwideGstInclusive,
        searchFrequency: SupplierSearchFrequency.Fortnightly,
        searchDaysOfWeek: ['THU'],
        fortnightStartDate: '2026-11-09'
      })
      .subscribe();

    expect(http.post.mock.calls[0][1]).toEqual(
      expect.objectContaining({ fortnightStartDate: '2026-11-09' })
    );
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
   * The two switches travel on one `PUT` and are sent exactly as given (issue #1077): this client
   * decides nothing about either, which is what lets the page send "no longer suggested, still
   * searched" without a rule here to contradict it.
   */
  it('puts the suggestion and the searching flags independently (issue #1077)', () => {
    const { service, http } = createService();

    service
      .update(4, {
        name: 'Kmart',
        url: null,
        isEnabled: true,
        ...nationwideGstInclusive,
        searchPrices: false
      })
      .subscribe();

    expect(http.put).toHaveBeenCalledWith('/api/super-admin/supplier-presets/4', {
      name: 'Kmart',
      url: null,
      isEnabled: true,
      ...nationwideGstInclusive,
      searchPrices: false
    });
  });

  it('posts a create with the searching flag it was given (issue #1077)', () => {
    const { service, http } = createService();

    service.create({ name: 'Aldi', url: null, ...nationwideGstInclusive, searchPrices: false }).subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/super-admin/supplier-presets', {
      name: 'Aldi',
      url: null,
      ...nationwideGstInclusive,
      searchPrices: false
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

    // The schedule vocabulary is the server's too (issue #1108). The numbers start at 1 on both
    // sides so a stored 0 is never read as the first declared frequency, and the weekday order is
    // the Monday-to-Sunday one the schedule's own weeks run in. `supplier-search-schedule.spec.ts`
    // pins both against the shared test-case file the backend tests read.
    expect(SupplierSearchFrequency.Daily).toBe(1);
    expect(SupplierSearchFrequency.Weekly).toBe(2);
    expect(SupplierSearchFrequency.Fortnightly).toBe(3);
    expect(SUPPLIER_SEARCH_WEEKDAYS).toEqual(['MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT', 'SUN']);

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
