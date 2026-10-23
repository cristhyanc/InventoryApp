import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SiteListItem } from '../models/models';
import { ConfigService } from './config.service';
import { SiteService } from './site.service';

/**
 * The requests this client makes. `getList()` is issue #841's light site list: the site pickers
 * fill from it instead of from `getAll()`, whose per-site stock and revenue figures cost one Nayax
 * read per machine in the fleet. The two are separate endpoints, and the heavy one is still what
 * the Dashboard and the Sites page read.
 */
describe('SiteService (issue #841)', () => {
  let service: SiteService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfigService, useValue: { apiBaseUrl: '/api' } as ConfigService },
        SiteService
      ]
    });

    service = TestBed.inject(SiteService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks the light site list endpoint for the picker list', () => {
    const sites: SiteListItem[] = [{ siteId: 42, name: 'Lobby', machineCount: 2 }];
    let received: SiteListItem[] | undefined;

    service.getList().subscribe((items) => (received = items));

    const request = http.expectOne('/api/sites/list');
    expect(request.request.method).toBe('GET');

    request.flush(sites);
    expect(received).toEqual(sites);
  });

  it('still asks the site summaries endpoint for the full rows', () => {
    service.getAll().subscribe();

    expect(http.expectOne('/api/sites').request.method).toBe('GET');
  });
});
