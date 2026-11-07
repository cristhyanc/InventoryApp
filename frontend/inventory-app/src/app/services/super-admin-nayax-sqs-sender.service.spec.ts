import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ConfigService } from './config.service';
import {
  SuperAdminNayaxSqsSender,
  SuperAdminNayaxSqsSenderService
} from './super-admin-nayax-sqs-sender.service';

const recorded: SuperAdminNayaxSqsSender = {
  businessId: 9,
  awsSenderId: 'AIDAEXAMPLEUSERID123',
  isEnabled: true,
  lastMessageReceivedAtUtc: '2026-11-05T22:10:00Z',
  // The two the endpoints also report since issue #1106: this business's last fully successful
  // reconciliation, and the one reader's own health.
  lastReconciliationSucceededAtUtc: '2026-11-05T21:00:00Z',
  readerHealth: {
    lastSuccessfulPollUtc: '2026-11-05T22:11:40Z',
    consecutivePollFailures: 0,
    deadLetterQueueDepth: 0
  },
  revision: 3,
  createdAtUtc: '2026-11-01T01:00:00Z',
  updatedAtUtc: '2026-11-06T03:45:00Z'
};

describe('SuperAdminNayaxSqsSenderService (issue #1101)', () => {
  let service: SuperAdminNayaxSqsSenderService;
  let http: HttpTestingController;

  const url = '/api/super-admin/businesses/9/nayax-sqs-sender';

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        SuperAdminNayaxSqsSenderService,
        { provide: ConfigService, useValue: { apiBaseUrl: '/api/' } }
      ]
    });

    service = TestBed.inject(SuperAdminNayaxSqsSenderService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads one business’s recorded sender', () => {
    let received: SuperAdminNayaxSqsSender | undefined;
    service.get(9).subscribe((sender) => (received = sender));

    const request = http.expectOne(url);
    expect(request.request.method).toBe('GET');
    request.flush(recorded);

    expect(received).toEqual(recorded);
  });

  it('sends the sender id, the flag and the revision the server reported', () => {
    service.set(9, 'AIDAEXAMPLEUSERID123', true, 3).subscribe();

    const request = http.expectOne(url);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      awsSenderId: 'AIDAEXAMPLEUSERID123',
      isEnabled: true,
      expectedRevision: 3
    });
    request.flush(recorded);
  });

  /**
   * The first write presents a null revision, which is the only way a first row is created - never a
   * zero or an omitted field, either of which the server refuses.
   */
  it('sends a null expected revision to create the first row', () => {
    service.set(9, 'AIDAEXAMPLEUSERID123', true, null).subscribe();

    const request = http.expectOne(url);
    expect(request.request.body).toEqual({
      awsSenderId: 'AIDAEXAMPLEUSERID123',
      isEnabled: true,
      expectedRevision: null
    });
    request.flush(recorded);
  });

  /** Remove is a PUT with a null id and the mapping off, and never a DELETE. */
  it('removes the sender through the same PUT', () => {
    service.set(9, null, false, 3).subscribe();

    const request = http.expectOne(url);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      awsSenderId: null,
      isEnabled: false,
      expectedRevision: 3
    });
    request.flush({ ...recorded, awsSenderId: null, isEnabled: false, revision: 4 });
  });
});
