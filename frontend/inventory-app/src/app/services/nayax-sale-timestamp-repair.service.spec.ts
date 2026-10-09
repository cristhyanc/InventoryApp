import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { ConfigService } from './config.service';
import {
  EVIDENCE_EXPORT_MAX_BYTES,
  EVIDENCE_EXPORT_SUPPORTED_EXTENSIONS,
  NayaxSaleTimestampRepairService
} from './nayax-sale-timestamp-repair.service';

interface HttpStub {
  post: jest.Mock;
}

/**
 * Every field name the preview endpoint accepts (`NayaxSaleTimestampRepairsController.Preview`).
 * The assertions below check each one individually rather than iterating the body, because the
 * frontend's TypeScript `lib` has no `dom.iterable` and `FormData` is therefore not enumerable
 * here.
 */
const PREVIEW_FIELDS = [
  'includeLatestSalesApiEvidence',
  'reconciliationCutoffUtc',
  'reconciliationFromBusinessDate',
  'reconciliationToBusinessDate',
  'evidenceExport'
] as const;

function createService(apiBaseUrl = '/api'): { service: NayaxSaleTimestampRepairService; http: HttpStub } {
  const http: HttpStub = { post: jest.fn(() => of({})) };
  const service = new NayaxSaleTimestampRepairService(
    http as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, http };
}

function exportFile(name = 'transactions.xlsx'): File {
  return new File(['TransactionID,AuthorizationDateTimeGMT'], name, { type: 'text/csv' });
}

function sentForm(http: HttpStub): FormData {
  return http.post.mock.calls[0][1] as FormData;
}

/**
 * The uploaded part's own file name. `FormData.append(name, blob, fileName)` re-wraps the blob, so
 * the entry is compared by name rather than by identity.
 */
function sentExportName(http: HttpStub): string | null {
  const part = sentForm(http).get('evidenceExport');
  return part instanceof File ? part.name : null;
}

/** The field names the multipart body actually carried, in the contract's own order. */
function presentFields(http: HttpStub): string[] {
  const form = sentForm(http);
  return PREVIEW_FIELDS.filter((field) => form.has(field));
}

/**
 * The preview endpoint's multipart contract and the apply endpoint's body are the whole request
 * surface of a maintenance operation that rewrites historical financial instants (issue #487 over
 * #472's API). A renamed field, an added field or a hand-set `Content-Type` would be a contract
 * break rather than a refactor, so each is pinned here.
 */
describe('NayaxSaleTimestampRepairService preview request (issues #487, #472)', () => {
  it('posts an API-only preview to the preview endpoint as multipart form data', () => {
    const { service, http } = createService();

    service
      .preview({ includeLatestSalesApiEvidence: true, evidenceExport: null, reconciliation: null })
      .subscribe();

    expect(http.post.mock.calls[0][0]).toBe('/api/admin/nayax-sale-timestamp-repair/preview');
    expect(sentForm(http)).toBeInstanceOf(FormData);
    expect(presentFields(http)).toEqual(['includeLatestSalesApiEvidence']);
    expect(sentForm(http).get('includeLatestSalesApiEvidence')).toBe('true');
  });

  /**
   * The browser has to set the multipart boundary itself: a hand-written `Content-Type` header
   * produces a body the server's multipart reader cannot parse.
   */
  it('lets the browser set the multipart boundary by sending no request options at all', () => {
    const { service, http } = createService();

    service
      .preview({ includeLatestSalesApiEvidence: true, evidenceExport: null, reconciliation: null })
      .subscribe();

    expect(http.post.mock.calls[0][2]).toBeUndefined();
  });

  it('sends an export-only preview with the evidenceExport file and no API evidence', () => {
    const { service, http } = createService();
    const file = exportFile();

    service
      .preview({ includeLatestSalesApiEvidence: false, evidenceExport: file, reconciliation: null })
      .subscribe();

    expect(presentFields(http)).toEqual(['includeLatestSalesApiEvidence', 'evidenceExport']);
    expect(sentForm(http).get('includeLatestSalesApiEvidence')).toBe('false');
    expect(sentExportName(http)).toBe(file.name);
  });

  it('sends both sources and the complete reconciliation window when all are named', () => {
    const { service, http } = createService();

    service
      .preview({
        includeLatestSalesApiEvidence: true,
        evidenceExport: exportFile('week.csv'),
        reconciliation: {
          cutoffUtc: '2026-10-08T04:00:00Z',
          fromBusinessDate: '2026-10-05',
          toBusinessDate: '2026-10-08'
        }
      })
      .subscribe();

    expect(presentFields(http)).toEqual([...PREVIEW_FIELDS]);
    expect(sentExportName(http)).toBe('week.csv');
  });

  /**
   * The cutoff is carried as the operator's own explicitly labelled UTC instant and the two
   * business dates as plain calendar dates. Nothing here constructs a `Date`, so no browser-local
   * offset can shift the window the server reconciles.
   */
  it('carries the reconciliation values verbatim, without reinterpreting them as instants', () => {
    const { service, http } = createService();

    service
      .preview({
        includeLatestSalesApiEvidence: true,
        evidenceExport: null,
        reconciliation: {
          cutoffUtc: '2026-04-05T15:30:00Z',
          fromBusinessDate: '2026-04-05',
          toBusinessDate: '2026-04-05'
        }
      })
      .subscribe();

    const form = sentForm(http);
    expect(form.get('reconciliationCutoffUtc')).toBe('2026-04-05T15:30:00Z');
    expect(form.get('reconciliationFromBusinessDate')).toBe('2026-04-05');
    expect(form.get('reconciliationToBusinessDate')).toBe('2026-04-05');
  });

  /**
   * The server refuses a partial window, because a daily total is only comparable when both sides
   * cover the same days and the same cutoff. Sending none of the three is the other valid shape.
   */
  it('omits every reconciliation field when no window was requested', () => {
    const { service, http } = createService();

    service
      .preview({ includeLatestSalesApiEvidence: true, evidenceExport: null, reconciliation: null })
      .subscribe();

    expect(presentFields(http)).toEqual(['includeLatestSalesApiEvidence']);
  });

  it('never sends a business identifier or any other field the endpoint does not accept', () => {
    const { service, http } = createService();

    service
      .preview({
        includeLatestSalesApiEvidence: true,
        evidenceExport: exportFile(),
        reconciliation: {
          cutoffUtc: '2026-10-08T04:00:00Z',
          fromBusinessDate: '2026-10-05',
          toBusinessDate: '2026-10-08'
        }
      })
      .subscribe();

    const form = sentForm(http);
    expect(form.has('businessId')).toBe(false);
    expect(form.has('business')).toBe(false);
    expect(form.has('previewId')).toBe(false);
    expect(form.has('file')).toBe(false);
  });
});

describe('NayaxSaleTimestampRepairService apply request (issues #487, #472)', () => {
  /**
   * The apply carries the server's own preview id and an explicit confirmation, and nothing else:
   * every instant, business date, outcome and provenance comes from the stored plan. A
   * client-supplied repair has to be impossible to express through this client.
   */
  it('posts only the server preview id and the confirmed flag', () => {
    const { service, http } = createService();

    service.apply('6f1b2c4e-0000-4000-8000-000000000001').subscribe();

    expect(http.post.mock.calls[0][0]).toBe('/api/admin/nayax-sale-timestamp-repair/apply');
    expect(http.post.mock.calls[0][1]).toEqual({
      previewId: '6f1b2c4e-0000-4000-8000-000000000001',
      confirmed: true
    });
    expect(Object.keys(http.post.mock.calls[0][1])).toEqual(['previewId', 'confirmed']);
  });
});

describe('NayaxSaleTimestampRepairService configuration (issue #487)', () => {
  it('resolves both endpoints from the configured API base URL, trailing slash or not', () => {
    const { service, http } = createService('https://api.example.test/api/');

    service
      .preview({ includeLatestSalesApiEvidence: true, evidenceExport: null, reconciliation: null })
      .subscribe();
    service.apply('6f1b2c4e-0000-4000-8000-000000000001').subscribe();

    expect(http.post.mock.calls[0][0]).toBe(
      'https://api.example.test/api/admin/nayax-sale-timestamp-repair/preview'
    );
    expect(http.post.mock.calls[1][0]).toBe(
      'https://api.example.test/api/admin/nayax-sale-timestamp-repair/apply'
    );
  });

  /**
   * These two mirror the server's own limits so the page can refuse an obviously unusable upload
   * before spending it. They are help, never authority: the server enforces both independently.
   */
  it("mirrors the server's upload cap and the export formats its reader supports", () => {
    expect(EVIDENCE_EXPORT_MAX_BYTES).toBe(8_000_000);
    expect(EVIDENCE_EXPORT_SUPPORTED_EXTENSIONS).toEqual(['.xlsx', '.xls', '.csv']);
  });
});
