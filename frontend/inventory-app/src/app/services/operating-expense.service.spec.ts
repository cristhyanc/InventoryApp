import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigService } from './config.service';
import { OperatingExpenseService } from './operating-expense.service';

/**
 * The expense list's machine filter (issue #746). `GET /api/operating-expenses` has accepted a
 * **local** `machineId` since issue #780; what these tests pin is that the service sends it as that
 * query parameter when one is chosen, and sends no machine parameter at all when none is - so "all
 * machines" stays a request that narrows nothing rather than one that asks for machine `null`.
 */
function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      OperatingExpenseService,
      { provide: ConfigService, useValue: { apiBaseUrl: '/api' } }
    ]
  });
  return {
    service: TestBed.inject(OperatingExpenseService),
    httpMock: TestBed.inject(HttpTestingController)
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('OperatingExpenseService machine filter (issue #746)', () => {
  it('sends the chosen local machine id as the machineId query parameter', () => {
    const { service, httpMock } = setup();

    service.getAll({ machineId: 42 }).subscribe();

    const request = httpMock.expectOne((candidate) => candidate.url === '/api/operating-expenses');
    expect(request.request.params.get('machineId')).toBe('42');
    request.flush([]);
    httpMock.verify();
  });

  it('sends no machine parameter when no machine is chosen', () => {
    const { service, httpMock } = setup();

    service.getAll({ from: '2026-01-01' }).subscribe();

    const request = httpMock.expectOne((candidate) => candidate.url === '/api/operating-expenses');
    expect(request.request.params.has('machineId')).toBe(false);
    expect(request.request.params.get('from')).toBe('2026-01-01');
    request.flush([]);
    httpMock.verify();
  });
});
