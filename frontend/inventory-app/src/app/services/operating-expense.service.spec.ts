import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigService } from './config.service';
import { OperatingExpenseDetail, OperatingExpenseService } from './operating-expense.service';

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

/**
 * The single-expense read the Edit expense page loads from (issue #1167). `GET
 * /api/operating-expenses/{id}` has existed since the attachment work; what these tests pin is that
 * the service reads that exact address and hands back the response's **own** shape - a nested
 * `supplier` object rather than the list row's `supplierName` - so the page takes the supplier's
 * name from `supplier?.name` instead of a field this endpoint never sends.
 */
describe('OperatingExpenseService.getById (issue #1167)', () => {
  it('reads the single expense address and returns the response unchanged', () => {
    const { service, httpMock } = setup();
    let loaded: OperatingExpenseDetail | undefined;

    service.getById(7).subscribe((expense) => (loaded = expense));

    const request = httpMock.expectOne('/api/operating-expenses/7');
    expect(request.request.method).toBe('GET');
    request.flush({
      id: 7,
      expenseDate: '2026-02-03T00:00:00Z',
      category: 1,
      description: 'Annual policy',
      amountExGst: 100,
      gstAmount: 10,
      totalAmount: 110,
      supplierId: 3,
      supplier: { id: 3, name: 'Acme Insurance' },
      siteId: 12,
      machineId: 41,
      servicePeriodStart: '2026-02-01T00:00:00Z',
      servicePeriodEnd: '2027-01-31T00:00:00Z',
      attachmentFileName: 'policy.pdf',
      notes: 'Renewed',
      createdAt: '2026-02-03T01:00:00Z',
      updatedAt: '2026-02-03T01:00:00Z'
    });

    expect(loaded?.supplier?.name).toBe('Acme Insurance');
    expect(loaded?.siteId).toBe(12);
    expect(loaded?.servicePeriodStart).toBe('2026-02-01T00:00:00Z');
    httpMock.verify();
  });

  it('carries a missing expense through as the 404 it is', () => {
    const { service, httpMock } = setup();
    let status: number | undefined;

    service.getById(404).subscribe({ error: (error: HttpErrorResponse) => (status = error.status) });

    httpMock.expectOne('/api/operating-expenses/404').flush('Not found', { status: 404, statusText: 'Not Found' });

    expect(status).toBe(404);
    httpMock.verify();
  });
});

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
