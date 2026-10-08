import { ICON_PATHS, OUTLINED_ICON_SHAPES, iconShapes } from './icon-paths';
import { primaryNavigation } from '../../layout/navigation';

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

/**
 * Guards the Outlined navigation geometry (issue #456).
 *
 * The regression #411 already suffered — paths reconstructed from memory, shipped under the wrong
 * set's label — is exactly as easy to repeat with a second variant, and this time there is a
 * further way to get it wrong: approximating an outlined glyph from the filled Rounded path, or
 * faking one with a CSS stroke. These assertions encode what "official Outlined variant" means,
 * so a reconstructed, re-canvassed or quietly refilled glyph fails here rather than needing a
 * human eye on a screenshot.
 */
describe('OUTLINED_ICON_SHAPES', () => {
  const sidebarControlIcons = ['menu', 'close'];
  const navigationIcons = [
    ...new Set(
      primaryNavigation.flatMap((item) => (item.kind === 'group' ? [item.icon] : item.icon ? [item.icon] : []))
    )
  ];
  const expectedNames = [...sidebarControlIcons, ...navigationIcons];
  const bundledNames = Object.keys(OUTLINED_ICON_SHAPES);

  it('covers every icon the sidebar renders, and nothing else', () => {
    expect(bundledNames.sort()).toEqual([...expectedNames].sort());
  });

  it.each(navigationIcons)('%s is bundled in the outlined variant the sidebar asks for', (name) => {
    // A navigation icon missing from this map renders nothing at all in the sidebar, which is a
    // blank rail rather than a wrong glyph - still a bug, and this is where it is caught.
    expect(iconShapes(name, 'outlined')).toBeDefined();
  });

  it.each(bundledNames)('%s is drawn from at least one non-empty path', (name) => {
    const { paths } = OUTLINED_ICON_SHAPES[name];

    expect(paths.length).toBeGreaterThan(0);
    for (const path of paths) {
      expect(path).toBeTruthy();
      expect(path).toMatch(/^[Mm]/);
    }
  });

  it.each(bundledNames)('%s is drawn on the 24x24 canvas the component renders', (name) => {
    // The same canvas trap as the Rounded set: Material Symbols draws these glyphs on a
    // `0 -960 960 960` canvas, and such a path renders wildly clipped inside `viewBox="0 0 24 24"`.
    const { paths, circles } = OUTLINED_ICON_SHAPES[name];
    const numbers = paths.flatMap((path) => (path.match(/-?(?:\d+\.?\d*|\.\d+)/g) ?? []).map(Number));
    for (const circle of circles ?? []) {
      numbers.push(circle.cx, circle.cy, circle.r);
    }

    expect(numbers.length).toBeGreaterThan(0);
    for (const value of numbers) {
      expect(Math.abs(value)).toBeLessThanOrEqual(25);
    }
  });

  it.each(bundledNames)('%s is the Outlined glyph, not the filled Rounded one', (name) => {
    // Every name here also exists in the Rounded map. Equal geometry would mean the outlined
    // variant is the filled silhouette under a new label, which is the bug this issue fixed.
    const rounded = ICON_PATHS[name];
    if (rounded !== undefined) {
      expect(OUTLINED_ICON_SHAPES[name].paths.join(' ')).not.toBe(rounded);
    }
  });

  // Pinned upstream strings for the two shapes the single-path Rounded map could not have
  // expressed, so a well-meant "simplification" that merges or drops one fails the build.
  it('keeps both published paths of the two-path "inventory_2" glyph', () => {
    expect(OUTLINED_ICON_SHAPES['inventory_2'].paths).toEqual([
      'M20 2H4c-1 0-2 .9-2 2v3.01c0 .72.43 1.34 1 1.69V20c0 1.1 1.1 2 2 2h14c.9 0 2-.9 2-2V8.7c.57-.35 1-.97 1-1.69V4c0-1.1-1-2-2-2zm-1 18H5V9h14v11zm1-13H4V4h16v3z',
      'M9 12h6v2H9z'
    ]);
  });

  it('keeps the published circle of the path-plus-circle "location_on" glyph', () => {
    expect(OUTLINED_ICON_SHAPES['location_on'].circles).toEqual([{ cx: 12, cy: 9, r: 2.5 }]);
  });

  it('leaves the Rounded variant as the default, so no other caller changes', () => {
    for (const name of bundledNames) {
      if (ICON_PATHS[name] !== undefined) {
        expect(iconShapes(name, 'rounded')?.paths).toEqual([ICON_PATHS[name]]);
      }
    }

    // An icon only the Rounded set bundles renders nothing in the outlined variant rather than
    // silently falling back to the filled geometry the variant exists to avoid.
    expect(iconShapes('add', 'outlined')).toBeUndefined();
    expect(iconShapes('add', 'rounded')).toBeDefined();
  });

  it('returns the same object for repeated lookups, so change detection re-renders nothing', () => {
    expect(iconShapes('home', 'rounded')).toBe(iconShapes('home', 'rounded'));
    expect(iconShapes('home', 'outlined')).toBe(iconShapes('home', 'outlined'));
  });

  it('renders nothing for an unknown name in either variant', () => {
    expect(iconShapes('not-a-real-icon', 'rounded')).toBeUndefined();
    expect(iconShapes('not-a-real-icon', 'outlined')).toBeUndefined();
  });
});
