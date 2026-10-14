import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import { BusinessApplicationRequest } from '../access/business-application-rules';
import { BusinessSettings, BusinessSettingsService, isLegacyBusinessProfile } from './business-settings.service';

const SETTINGS: BusinessSettings = {
  name: 'Corner Store',
  contactEmail: 'owner@example.com',
  abn: '51824753556',
  gstRegistered: true,
  timeZoneId: 'Australia/Sydney'
};

const SAVE_REQUEST: BusinessApplicationRequest = {
  name: 'Corner Store',
  contactEmail: 'owner@example.com',
  abn: null,
  gstRegistered: true,
  timeZoneId: 'Australia/Brisbane'
};

function createService(apiBaseUrl = '/api'): {
  service: BusinessSettingsService;
  http: { get: jest.Mock; put: jest.Mock };
} {
  const http = { get: jest.fn(() => of(SETTINGS)), put: jest.fn(() => of(SETTINGS)) };
  const service = new BusinessSettingsService(http as unknown as HttpClient, { apiBaseUrl } as ConfigService);
  return { service, http };
}

/** `BusinessSettingsService` (issue #549): the Owner-only `GET`/`PUT /api/business/settings`. */
describe('BusinessSettingsService (issue #549)', () => {
  it('reads the settings from the business settings endpoint', (done) => {
    const { service, http } = createService();

    service.get().subscribe((settings) => {
      expect(http.get).toHaveBeenCalledWith('/api/business/settings');
      expect(settings).toEqual(SETTINGS);
      done();
    });
  });

  it('saves the whole settings body to the same endpoint', (done) => {
    const { service, http } = createService();

    service.save(SAVE_REQUEST).subscribe(() => {
      expect(http.put).toHaveBeenCalledWith('/api/business/settings', SAVE_REQUEST);
      done();
    });
  });

  it('builds both URLs from the configured API base, trimming a trailing slash', (done) => {
    const { service, http } = createService('/api/');

    service.get().subscribe(() => {
      expect(http.get.mock.calls[0][0]).toBe('/api/business/settings');
      service.save(SAVE_REQUEST).subscribe(() => {
        expect(http.put.mock.calls[0][0]).toBe('/api/business/settings');
        done();
      });
    });
  });
});

describe('isLegacyBusinessProfile (issue #549)', () => {
  it('is false once both the contact email and the GST registration are recorded', () => {
    expect(isLegacyBusinessProfile(SETTINGS)).toBe(false);
  });

  it('is true for a missing contact email', () => {
    expect(isLegacyBusinessProfile({ ...SETTINGS, contactEmail: null })).toBe(true);
    expect(isLegacyBusinessProfile({ ...SETTINGS, contactEmail: '   ' })).toBe(true);
  });

  it('is true for an unrecorded GST registration, regardless of its value once stated', () => {
    expect(isLegacyBusinessProfile({ ...SETTINGS, gstRegistered: null })).toBe(true);
    expect(isLegacyBusinessProfile({ ...SETTINGS, gstRegistered: false })).toBe(false);
  });
});
