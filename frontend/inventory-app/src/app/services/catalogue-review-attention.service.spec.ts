import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { CatalogueReviewAttentionService } from './catalogue-review-attention.service';
import { ConfigService } from './config.service';

function createService(apiBaseUrl = '/api'): { service: CatalogueReviewAttentionService; get: jest.Mock } {
  const get = jest.fn(() => of({ unimportedProductCount: 0, examples: [] }));
  const service = new CatalogueReviewAttentionService(
    { get } as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, get };
}

describe('CatalogueReviewAttentionService (issue #1352)', () => {
  it('reads the attention resource once, with no parameter of its own', () => {
    const { service, get } = createService();

    service.read().subscribe();

    expect(get).toHaveBeenCalledWith('/api/catalogue-review/attention');
    expect(get).toHaveBeenCalledTimes(1);
  });

  /** A configured base URL with a trailing slash must not produce a double slash in the route. */
  it('tolerates a trailing slash on the configured base URL', () => {
    const { service, get } = createService('https://api.example.test/api/');

    service.read().subscribe();

    expect(get).toHaveBeenCalledWith('https://api.example.test/api/catalogue-review/attention');
  });
});
