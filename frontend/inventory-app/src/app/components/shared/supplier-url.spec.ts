import {
  SUPPLIER_URL_INVALID_MESSAGE,
  SUPPLIER_URL_MAX_LENGTH,
  SUPPLIER_URL_TOO_LONG_MESSAGE,
  normaliseSupplierUrl,
  supplierUrlError
} from './supplier-url';

describe('supplierUrlError (issue #848)', () => {
  it.each([null, undefined, '', '   '])('treats a blank address as no address rather than an error (%p)', (url) => {
    expect(supplierUrlError(url)).toBeNull();
  });

  it.each(['https://www.coles.com.au', 'http://example.test/path?q=1', '  https://example.test  '])(
    'accepts an absolute http(s) address (%s)',
    (url) => {
      expect(supplierUrlError(url)).toBeNull();
    }
  );

  it.each(['www.coles.com.au', 'coles.com.au', '/suppliers', 'coles'])('refuses a relative address (%s)', (url) => {
    expect(supplierUrlError(url)).toBe(SUPPLIER_URL_INVALID_MESSAGE);
  });

  it.each(['javascript:alert(1)', 'data:text/html,<script>', 'file:///etc/passwd', 'ftp://files.example.test'])(
    'refuses any other scheme (%s)',
    (url) => {
      expect(supplierUrlError(url)).toBe(SUPPLIER_URL_INVALID_MESSAGE);
    }
  );

  it('measures the length limit on the trimmed value', () => {
    const atTheLimit = `https://example.test/${'a'.repeat(SUPPLIER_URL_MAX_LENGTH - 'https://example.test/'.length)}`;
    expect(atTheLimit).toHaveLength(SUPPLIER_URL_MAX_LENGTH);
    expect(supplierUrlError(`  ${atTheLimit}  `)).toBeNull();

    expect(supplierUrlError(`${atTheLimit}a`)).toBe(SUPPLIER_URL_TOO_LONG_MESSAGE);
  });
});

describe('normaliseSupplierUrl (issue #848)', () => {
  it.each([null, undefined, '', '  '])('sends null rather than an empty string (%p)', (url) => {
    expect(normaliseSupplierUrl(url)).toBeNull();
  });

  it('trims what it sends', () => {
    expect(normaliseSupplierUrl('  https://example.test  ')).toBe('https://example.test');
  });
});
