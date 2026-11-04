import { clampPageIndex, pageCount, pageRange, pageSlice } from './paging';

describe('paging (issue #1093)', () => {
  const items = Array.from({ length: 23 }, (_, index) => index + 1);

  it('counts pages, with an empty list still one page', () => {
    expect(pageCount(0, 10)).toBe(1);
    expect(pageCount(10, 10)).toBe(1);
    expect(pageCount(11, 10)).toBe(2);
    expect(pageCount(71, 5)).toBe(15);
  });

  it('clamps a page index into the pages the list now has', () => {
    expect(clampPageIndex(5, 23, 10)).toBe(2);
    expect(clampPageIndex(-1, 23, 10)).toBe(0);
    expect(clampPageIndex(1, 23, 10)).toBe(1);
    expect(clampPageIndex(3, 0, 10)).toBe(0);
  });

  it('slices every item exactly once across the pages, whatever the size', () => {
    for (const size of [5, 10, 20, 50, 100]) {
      const seen: number[] = [];
      for (let page = 0; page < pageCount(items.length, size); page += 1) {
        seen.push(...pageSlice(items, page, size));
      }
      expect(seen).toEqual(items);
    }
  });

  it('never shows an empty page for an index past the end', () => {
    expect(pageSlice(items, 9, 10)).toEqual([21, 22, 23]);
  });

  it('states the range a page shows', () => {
    expect(pageRange(0, 23, 10)).toEqual({ first: 1, last: 10 });
    expect(pageRange(2, 23, 10)).toEqual({ first: 21, last: 23 });
    expect(pageRange(0, 0, 10)).toEqual({ first: 0, last: 0 });
  });
});
