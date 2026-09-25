import { money, moneyOrUnavailable, percentOrUnavailable } from './report-formatting';

describe('report-formatting', () => {
  it('money formats a known amount as AUD currency', () => {
    expect(money(12.5)).toBe('$12.50');
    expect(money(0)).toBe('$0.00');
  });

  it('moneyOrUnavailable keeps a known zero distinct from an unknown value', () => {
    expect(moneyOrUnavailable(null)).toBe('Profit unavailable');
    expect(moneyOrUnavailable(undefined)).toBe('Profit unavailable');
    expect(moneyOrUnavailable(0)).toBe('$0.00');
    expect(moneyOrUnavailable(42.1)).toBe('$42.10');
  });

  it('moneyOrUnavailable accepts a caller-supplied unavailable label', () => {
    expect(moneyOrUnavailable(null, 'Unavailable')).toBe('Unavailable');
  });

  it('percentOrUnavailable keeps a known zero margin distinct from an unknown margin', () => {
    expect(percentOrUnavailable(null)).toBe('—');
    expect(percentOrUnavailable(undefined)).toBe('—');
    expect(percentOrUnavailable(0)).toBe('0.0%');
    expect(percentOrUnavailable(12.34)).toBe('12.3%');
  });

  it('percentOrUnavailable applies a suffix only when the value is known', () => {
    expect(percentOrUnavailable(5, ' margin')).toBe('5.0% margin');
    expect(percentOrUnavailable(null, ' margin')).toBe('—');
  });
});
