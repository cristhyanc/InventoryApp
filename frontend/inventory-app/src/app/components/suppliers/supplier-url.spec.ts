import {
  SUPPLIER_URL_INVALID_MESSAGE,
  SUPPLIER_URL_MAX_LENGTH,
  SUPPLIER_URL_TOO_LONG_MESSAGE,
  isSafeSupplierUrl,
  normalizeSupplierUrl
} from './supplier-url';

/**
 * The browser-side half of the supplier website rule (issue #847). It mirrors
 * `Inventory.Application.Suppliers.SupplierUrlPolicy` case for case, so an operator sees the same
 * refusal before the round trip as after it - and so a value that somehow reached the database
 * without passing the server rule is still never rendered as a link.
 */
describe('supplier URL rule (issue #847)', () => {
  describe('normalizeSupplierUrl', () => {
    it.each([null, undefined, '', '   ', '\t\n'])('accepts %p as no website', (value) => {
      expect(normalizeSupplierUrl(value)).toEqual({ url: null, error: null });
    });

    it.each([
      ['http://coles.com.au', 'http://coles.com.au'],
      ['https://coles.com.au', 'https://coles.com.au'],
      ['  https://coles.com.au/trade?ref=1#top  ', 'https://coles.com.au/trade?ref=1#top'],
      ['HTTPS://COLES.COM.AU', 'HTTPS://COLES.COM.AU'],
      ['https://sub.domain.example.com:8443/path', 'https://sub.domain.example.com:8443/path']
    ])('accepts %p and stores the trimmed text as typed', (value, expected) => {
      expect(normalizeSupplierUrl(value)).toEqual({ url: expected, error: null });
    });

    it.each([
      'javascript:alert(1)',
      'JavaScript:alert(1)',
      'data:text/html;base64,PHNjcmlwdD4=',
      'file:///etc/passwd',
      'ftp://files.example.com',
      'mailto:buyer@example.test',
      '//coles.com.au',
      'coles.com.au',
      'www.coles.com.au/trade',
      '/suppliers/1',
      '../suppliers',
      'https:///no-host',
      'http://',
      'not a url at all'
    ])('rejects %p as not an absolute http(s) address', (value) => {
      expect(normalizeSupplierUrl(value)).toEqual({ url: null, error: SUPPLIER_URL_INVALID_MESSAGE });
    });

    it('accepts a URL of exactly the maximum length and rejects one character more', () => {
      const atLimit = urlOfLength(SUPPLIER_URL_MAX_LENGTH);

      expect(normalizeSupplierUrl(atLimit)).toEqual({ url: atLimit, error: null });
      expect(normalizeSupplierUrl(`  ${atLimit}  `)).toEqual({ url: atLimit, error: null });
      expect(normalizeSupplierUrl(urlOfLength(SUPPLIER_URL_MAX_LENGTH + 1))).toEqual({
        url: null,
        error: SUPPLIER_URL_TOO_LONG_MESSAGE
      });
    });

    it('reports an over-long value as too long before considering the scheme', () => {
      const value = `javascript:${'a'.repeat(SUPPLIER_URL_MAX_LENGTH)}`;

      expect(normalizeSupplierUrl(value).error).toBe(SUPPLIER_URL_TOO_LONG_MESSAGE);
    });

    it('publishes the same 2048 limit the column and the server rule use', () => {
      expect(SUPPLIER_URL_MAX_LENGTH).toBe(2048);
    });
  });

  describe('isSafeSupplierUrl', () => {
    it.each(['https://coles.com.au', 'http://coles.com.au/trade', 'HTTPS://COLES.COM.AU'])(
      'treats %p as safe to render as a link',
      (value) => {
        expect(isSafeSupplierUrl(value)).toBe(true);
      }
    );

    it.each([null, undefined, '', '   ', 'javascript:alert(1)', 'data:text/html,x', 'coles.com.au', '/suppliers/1'])(
      'refuses to treat %p as a link',
      (value) => {
        expect(isSafeSupplierUrl(value)).toBe(false);
      }
    );
  });
});

/** An `https` URL padded out to exactly `length` characters. */
function urlOfLength(length: number): string {
  const prefix = 'https://coles.com.au/';
  return prefix + 'a'.repeat(length - prefix.length);
}
