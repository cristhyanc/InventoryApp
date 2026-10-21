import { generateIdempotencyKey } from './idempotency-key';

describe('generateIdempotencyKey', () => {
  it('returns a non-empty key', () => {
    expect(generateIdempotencyKey().length).toBeGreaterThan(0);
  });

  it('returns a different key on every call', () => {
    const keys = new Set(Array.from({ length: 20 }, () => generateIdempotencyKey()));

    expect(keys.size).toBe(20);
  });

  it("stays within the backend contract's maximum idempotency key length (128)", () => {
    expect(generateIdempotencyKey().length).toBeLessThanOrEqual(128);
  });
});
