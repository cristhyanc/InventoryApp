import { ICON_PATHS } from './icon-paths';

/**
 * Guards the bundled icon geometry (issue #411).
 *
 * The first implementation shipped square-cornered baseline Material Icons paths while claiming
 * the Rounded set, and nothing in the suite noticed: `IconComponent`'s own spec only asserts that
 * *a* path renders. These assertions encode the three things that went wrong - the wrong glyph
 * geometry, the wrong drawing canvas, and an unreviewed change to the two paths a human
 * explicitly flagged - so the same regression fails here instead of needing a human eye.
 */
describe('ICON_PATHS', () => {
  const expectedNames = [
    'add',
    'edit',
    'delete',
    'close',
    'search',
    'upload',
    'download',
    'refresh',
    'check',
    'warning',
    'error',
    'info',
    'home',
    'settings',
    'location_on',
    'bar_chart',
    'assignment',
    'attach_money',
    'trending_up',
    'trending_down',
    'inventory_2'
  ];

  it('bundles exactly the icons the #409 series needs so far', () => {
    expect(Object.keys(ICON_PATHS).sort()).toEqual([...expectedNames].sort());
  });

  it.each(expectedNames)('%s is a single non-empty path starting with a move command', (name) => {
    const path = ICON_PATHS[name];

    expect(path).toBeTruthy();
    expect(path).toMatch(/^[Mm]/);
  });

  it.each(expectedNames)('%s is drawn on the 24x24 canvas the component renders', (name) => {
    // Material Symbols Rounded draws the same glyphs on a `0 -960 960 960` canvas. Pasting one of
    // those paths into this map renders an unreadable, wildly clipped icon inside
    // `viewBox="0 0 24 24"`. Path data mixes absolute points with relative deltas, so the check
    // is on magnitude: nothing in a 24px glyph, in either form, reaches 25.
    const numbers = (ICON_PATHS[name].match(/-?(?:\d+\.?\d*|\.\d+)/g) ?? []).map(Number);

    expect(numbers.length).toBeGreaterThan(0);
    for (const value of numbers) {
      expect(Math.abs(value)).toBeLessThanOrEqual(25);
    }
  });

  it.each(expectedNames)('%s uses the curved corners of the Rounded variant', (name) => {
    // Every Rounded glyph in this set rounds its corners with arc or curve commands; the baseline
    // (square-cornered) Material Icons geometry for several of them - `add` and `delete` among
    // them - is drawn with straight line commands alone.
    expect(ICON_PATHS[name]).toMatch(/[aAcCqQsStT]/);
  });

  // The two paths a human identified as baseline-instead-of-Rounded, pinned to the exact upstream
  // `round/add.svg` and `round/delete.svg` strings so a silent revert fails the build.
  it('uses the upstream Rounded "add" path', () => {
    expect(ICON_PATHS['add']).toBe(
      'M18 13h-5v5c0 .55-.45 1-1 1s-1-.45-1-1v-5H6c-.55 0-1-.45-1-1s.45-1 1-1h5V6c0-.55.45-1 1-1s1 .45 1 1v5h5c.55 0 1 .45 1 1s-.45 1-1 1z'
    );
  });

  it('uses the upstream Rounded "delete" path', () => {
    expect(ICON_PATHS['delete']).toBe(
      'M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V9c0-1.1-.9-2-2-2H8c-1.1 0-2 .9-2 2v10zM18 4h-2.5l-.71-.71c-.18-.18-.44-.29-.7-.29H9.91c-.26 0-.52.11-.7.29L8.5 4H6c-.55 0-1 .45-1 1s.45 1 1 1h12c.55 0 1-.45 1-1s-.45-1-1-1z'
    );
  });
});
