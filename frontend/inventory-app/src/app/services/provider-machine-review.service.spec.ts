import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import {
  ProviderMachineLinkAction,
  ProviderMachineReviewService
} from './provider-machine-review.service';

interface HttpStub {
  post: jest.Mock;
}

function createService(apiBaseUrl = '/api'): { service: ProviderMachineReviewService; http: HttpStub } {
  const http: HttpStub = { post: jest.fn(() => of({})) };
  const service = new ProviderMachineReviewService(
    http as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, http };
}

/**
 * The provider machine review API as the browser calls it (issue #742): the two routes, the verb,
 * and a request body that carries decisions and nothing else.
 */
describe('ProviderMachineReviewService (issue #742)', () => {
  it('reviews the fleet with a POST, because reviewing may link machines and must not be cached', () => {
    const { service, http } = createService();

    service.review().subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/machine-link-review', {});
  });

  it('applies decisions to the apply route, sending no provider name or number', () => {
    const { service, http } = createService();

    service
      .apply({
        decisions: [
          { providerMachineId: 7, action: ProviderMachineLinkAction.LinkExisting, localMachineId: 4 },
          { providerMachineId: 8, action: ProviderMachineLinkAction.CreateNew }
        ]
      })
      .subscribe();

    expect(http.post).toHaveBeenCalledWith('/api/machine-link-review/apply', {
      decisions: [
        { providerMachineId: 7, action: ProviderMachineLinkAction.LinkExisting, localMachineId: 4 },
        { providerMachineId: 8, action: ProviderMachineLinkAction.CreateNew }
      ]
    });
  });

  it('does not double the slash when the configured base url ends with one', () => {
    const { service, http } = createService('https://api.example.com/api/');

    service.review().subscribe();

    expect(http.post).toHaveBeenCalledWith('https://api.example.com/api/machine-link-review', {});
  });
});
