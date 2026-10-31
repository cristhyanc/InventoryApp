import { purchaseTitleSuggestion } from './purchase-title-suggestion';

describe('purchaseTitleSuggestion (issue #1029)', () => {
  it('names the supplier and the purchase date', () => {
    expect(purchaseTitleSuggestion('Costco Wholesale', '2026-10-24')).toBe('Costco Wholesale — 24 Oct 2026');
  });

  it('calls a purchase with no supplier a Purchase', () => {
    expect(purchaseTitleSuggestion(null, '2026-01-05')).toBe('Purchase — 5 Jan 2026');
    expect(purchaseTitleSuggestion('   ', '2026-01-05')).toBe('Purchase — 5 Jan 2026');
  });

  it('reads the date as the calendar day it names, never as an instant a time zone could move', () => {
    expect(purchaseTitleSuggestion('Acme', '2026-12-31')).toBe('Acme — 31 Dec 2026');
    expect(purchaseTitleSuggestion('Acme', '2026-01-01')).toBe('Acme — 1 Jan 2026');
  });

  it('leaves out a blank or unreadable date', () => {
    expect(purchaseTitleSuggestion('Acme', '')).toBe('Acme');
    expect(purchaseTitleSuggestion('Acme', '2026-13-01')).toBe('Acme');
    expect(purchaseTitleSuggestion('Acme', '24/10/2026')).toBe('Acme');
  });
});
