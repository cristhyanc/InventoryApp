import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { PurchaseScanDraft } from '../models/purchase-scan';
import { ConfigService } from './config.service';
import { PurchaseScanService } from './purchase-scan.service';

const DRAFT = {
  lines: [],
  usage: { month: '2026-11', used: 1, remaining: 99, limit: 100 }
} as unknown as PurchaseScanDraft;

function createService(apiBaseUrl = '/api'): { service: PurchaseScanService; post: jest.Mock } {
  const post = jest.fn(() => of(DRAFT));
  const service = new PurchaseScanService(
    { post } as unknown as HttpClient,
    { apiBaseUrl } as ConfigService
  );
  return { service, post };
}

/** `PurchaseScanService` (issue #1166): the Scan receipt workflow's one call. */
describe('PurchaseScanService', () => {
  it('posts the chosen file as the one `file` part of the scan endpoint', (done) => {
    const { service, post } = createService();
    const file = new File(['receipt'], 'receipt.jpg', { type: 'image/jpeg' });

    service.scan(file).subscribe((draft) => {
      expect(post).toHaveBeenCalledTimes(1);
      expect(post.mock.calls[0][0]).toBe('/api/purchases/scan');

      const body = post.mock.calls[0][1] as FormData;
      expect(body.get('file')).toBe(file);
      // One part and no other: a scan sends the receipt and nothing about the business.
      const parts: string[] = [];
      body.forEach((_value, key) => parts.push(key));
      expect(parts).toEqual(['file']);
      expect(draft).toBe(DRAFT);
      done();
    });
  });

  it('builds the URL from the configured API base, trimming a trailing slash', (done) => {
    const { service, post } = createService('/api/');

    service.scan(new File(['receipt'], 'receipt.pdf', { type: 'application/pdf' })).subscribe(() => {
      expect(post.mock.calls[0][0]).toBe('/api/purchases/scan');
      done();
    });
  });
});
