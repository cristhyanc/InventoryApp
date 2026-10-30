import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { PlanCatalogueEntry, PlanCatalogueService } from './plan-catalogue.service';

const ENTRIES: readonly PlanCatalogueEntry[] = [
  {
    plan: 'Free',
    monthlyPriceMinorUnits: 0,
    currencyCode: 'AUD',
    attachmentStorageLimitBytes: 1_000_000_000,
    includedConnectedMachines: 0,
    additionalConnectedMachinePriceMinorUnits: null
  },
  {
    plan: 'Pro',
    monthlyPriceMinorUnits: 1500,
    currencyCode: 'AUD',
    attachmentStorageLimitBytes: 20_000_000_000,
    includedConnectedMachines: 5,
    additionalConnectedMachinePriceMinorUnits: 200
  }
];

function createService(apiBaseUrl = '/api'): { service: PlanCatalogueService; http: { get: jest.Mock } } {
  const http = { get: jest.fn(() => of({ plans: ENTRIES })) };
  const service = new PlanCatalogueService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

/** `PlanCatalogueService` (issue #970): the plan page's one call, `GET /api/me/plan-catalogue`. */
describe('PlanCatalogueService (issue #970)', () => {
  it('reads the catalogue from the plan-catalogue endpoint', (done) => {
    const { service, http } = createService();

    service.get().subscribe((entries) => {
      expect(http.get).toHaveBeenCalledWith('/api/me/plan-catalogue');
      expect(entries).toEqual(ENTRIES);
      done();
    });
  });

  it('builds the URL from the configured API base, trimming a trailing slash', (done) => {
    const { service, http } = createService('/api/');

    service.get().subscribe(() => {
      expect(http.get.mock.calls[0][0]).toBe('/api/me/plan-catalogue');
      done();
    });
  });
});
