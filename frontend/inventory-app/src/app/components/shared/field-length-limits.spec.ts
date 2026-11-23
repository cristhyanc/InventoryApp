import { editableMaxLength, FIELD_LENGTH_LIMITS } from './field-length-limits';

describe('field length limits (issue #1286)', () => {
  it('mirrors the server limits from the issue table', () => {
    expect(FIELD_LENGTH_LIMITS).toEqual({
      product: { name: 200, sku: 100, unit: 100, description: 2000 },
      category: { name: 200, description: 2000 },
      supplier: { name: 200, contactName: 200, phone: 50, email: 254, address: 500 },
      purchase: { title: 200, notes: 2000 },
      expense: { description: 500, notes: 2000 },
      machine: { name: 200 }
    });
  });

  it('applies the limit to a new value and to a value loaded within the limit', () => {
    expect(editableMaxLength('', '', 200)).toBe(200);
    expect(editableMaxLength('abc', undefined, 200)).toBe(200);
    expect(editableMaxLength('x'.repeat(200), 'x'.repeat(200), 200)).toBe(200);
  });

  it('lifts the limit while a loaded over-long value is left unchanged', () => {
    const legacy = 'x'.repeat(201);
    expect(editableMaxLength(legacy, legacy, 200)).toBeNull();
  });

  it('applies the limit again once a loaded over-long value is changed', () => {
    const legacy = 'x'.repeat(250);
    expect(editableMaxLength(legacy.slice(1), legacy, 200)).toBe(200);
    expect(editableMaxLength('short', legacy, 200)).toBe(200);
  });

  it('treats null and undefined as an empty value', () => {
    expect(editableMaxLength(null, null, 50)).toBe(50);
    expect(editableMaxLength(undefined, 'x'.repeat(60), 50)).toBe(50);
  });
});
