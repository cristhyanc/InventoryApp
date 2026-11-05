import { reorderGuidance } from './product-reorder-guidance';

describe('reorderGuidance (issue #1111)', () => {
  it('words the inclusive projected-stock rule with the values on screen', () => {
    expect(reorderGuidance(4, 12)).toBe(
      'A reorder is suggested when warehouse stock plus stock on order, less what machines need, is at or ' +
        'below 4 units. The suggested quantity brings warehouse stock back to 12 units once machines are ' +
        'refilled and the order arrives.'
    );
  });

  it('never says an order is placed automatically or that it triggers only below the threshold', () => {
    const text = reorderGuidance(12, 48)!;

    expect(text).toContain('at or below 12 units');
    expect(text).not.toMatch(/automatic/i);
    expect(text).not.toMatch(/Reorder below/);
  });

  it('uses the singular for one unit and accepts equal values and zero', () => {
    expect(reorderGuidance(1, 1)).toContain('at or below 1 unit.');
    expect(reorderGuidance(0, 0)).toContain('at or below 0 units.');
  });

  it('states nothing for values the form would refuse', () => {
    expect(reorderGuidance(null, 5)).toBeNull();
    expect(reorderGuidance(5, null)).toBeNull();
    expect(reorderGuidance(-1, 5)).toBeNull();
    expect(reorderGuidance(5, 4)).toBeNull();
    expect(reorderGuidance(2.5, 5)).toBeNull();
  });
});
