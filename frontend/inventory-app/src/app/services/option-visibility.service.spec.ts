import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import { OptionVisibilityService } from './option-visibility.service';

function createService(apiBaseUrl = '/api'): { service: OptionVisibilityService; put: jest.Mock } {
  const put = jest.fn(() => of({ historicalGstClassificationVisible: false }));
  const service = new OptionVisibilityService(
    { put } as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, put };
}

describe('OptionVisibilityService (issue #873)', () => {
  it('puts the requested state to the Super Admin option-visibility resource', () => {
    const { service, put } = createService();

    service.setHistoricalGstClassificationVisible(false).subscribe();

    expect(put).toHaveBeenCalledWith('/api/super-admin/option-visibility', {
      historicalGstClassificationVisible: false
    });
    expect(put).toHaveBeenCalledTimes(1);
  });

  it('puts the on state the same way, so switching back is the same one call', () => {
    const { service, put } = createService();

    service.setHistoricalGstClassificationVisible(true).subscribe();

    expect(put).toHaveBeenCalledWith('/api/super-admin/option-visibility', {
      historicalGstClassificationVisible: true
    });
  });

  /**
   * The base URL comes from `ConfigService` like every other service's (AGENTS.md § Frontend
   * rules), and a trailing slash on it does not produce a doubled one.
   */
  it('builds the URL from the configured API base, with no doubled separator', () => {
    const { service, put } = createService('https://api.example.com/');

    service.setHistoricalGstClassificationVisible(true).subscribe();

    expect(put).toHaveBeenCalledWith(
      'https://api.example.com/super-admin/option-visibility',
      expect.anything()
    );
  });
});
