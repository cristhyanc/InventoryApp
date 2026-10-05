/**
 * Contrast guard for the Material Dashboard design tokens (issue #410).
 *
 * The visual specification in issue #410 is only accessible if every text/background pair the
 * shared classes in `src/styles.scss` produce stays at or above the WCAG 2.2 AA minimum of
 * 4.5:1 for normal-size text. This spec recomputes those ratios from the token values in
 * `tailwind.config.js`, so changing a token - or re-pointing a shared class at a different
 * token - fails here instead of silently shipping unreadable text.
 *
 * Deliberately out of scope:
 *   - The white icon inside `.icon-tile`. Icon tiles are decorative: the icon is `aria-hidden`
 *     and the meaning is carried by the adjacent text label, so the light tile gradients are
 *     never judged as a text background (WCAG 1.4.3 applies to text).
 *   - The `:disabled` state of buttons and form controls, which WCAG 1.4.3 exempts.
 *   - The `input, select, textarea` element rule, which is not a shared class and which issue
 *     #410 deliberately leaves alone apart from its border, radius and focus styling.
 *
 * https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html
 */
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import tailwindConfig from '../../../tailwind.config.js';

type ColorToken = string | Record<string, string>;

const extend = (
  tailwindConfig as unknown as {
    theme: {
      extend: {
        colors: Record<string, ColorToken>;
        backgroundImage: Record<string, string>;
        boxShadow: Record<string, string>;
        borderRadius: Record<string, string>;
        fontFamily: Record<string, string[]>;
        fontSize: Record<string, string>;
      };
    };
  }
).theme.extend;

const stylesScss = readFileSync(join(__dirname, '..', '..', 'styles.scss'), 'utf8');

// ---- WCAG 2.2 relative luminance and contrast ratio ---------------------------------------

function channels(hex: string): [number, number, number] {
  const value = hex.replace('#', '');
  if (!/^[0-9a-fA-F]{6}$/.test(value)) {
    throw new Error(`Not a 6-digit hex colour: ${hex}`);
  }
  return [0, 2, 4].map((index) => parseInt(value.slice(index, index + 2), 16)) as [
    number,
    number,
    number,
  ];
}

function relativeLuminance(hex: string): number {
  const [red, green, blue] = channels(hex).map((channel) => {
    const srgb = channel / 255;
    return srgb <= 0.04045 ? srgb / 12.92 : Math.pow((srgb + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

function contrastRatio(foreground: string, background: string): number {
  const first = relativeLuminance(foreground);
  const second = relativeLuminance(background);
  const lighter = Math.max(first, second);
  const darker = Math.min(first, second);
  return (lighter + 0.05) / (darker + 0.05);
}

/** Flattens a translucent colour onto an opaque surface, the way a browser composites it. */
function composite(foreground: string, alpha: number, surface: string): string {
  const source = channels(foreground);
  const target = channels(surface);
  return `#${[0, 1, 2]
    .map((index) => Math.round(source[index] * alpha + target[index] * (1 - alpha)))
    .map((channel) => channel.toString(16).padStart(2, '0'))
    .join('')
    .toUpperCase()}`;
}

// ---- Token lookup -------------------------------------------------------------------------

const colors: Record<string, ColorToken> = extend.colors ?? {};
const backgroundImages: Record<string, string> = extend.backgroundImage ?? {};

function color(token: string): string {
  const value = colors[token];
  if (typeof value !== 'string') {
    throw new Error(`Colour token not defined as a single value: ${token}`);
  }
  return value;
}

function gray(shade: 100 | 200 | 300 | 500 | 600 | 800): string {
  const scale = colors['md-gray'];
  if (typeof scale !== 'object') {
    throw new Error('Colour token md-gray is not defined as a scale');
  }
  const value = scale[String(shade)];
  if (typeof value !== 'string') {
    throw new Error(`Colour token md-gray-${shade} is not defined`);
  }
  return value;
}

/** The two stops of a `linear-gradient(195deg, from, to)` background-image token. */
function gradientStops(token: string): [string, string] {
  const value = backgroundImages[token];
  const stops = value?.match(/#[0-9a-fA-F]{6}/g);
  if (!stops || stops.length !== 2) {
    throw new Error(`Gradient token ${token} does not declare two hex stops: ${value}`);
  }
  return [stops[0], stops[1]];
}

const WHITE = '#FFFFFF';
/** Cards, dialogs and table bodies. */
const SURFACE_WHITE = WHITE;
/** The page canvas, the table header, and a hovered table row. */
const SURFACE_CANVAS = gray(100);
/**
 * The `.badge-neutral` surface. The visual specification states this value inside the shared
 * class list rather than in the token table, so it is asserted against `styles.scss` below.
 */
const SURFACE_NEUTRAL_BADGE = '#EAEAEA';

/** The 15% tint of a decorative solid colour, as `bg-md-<status>/15` composites it. */
const tint = (status: string, surface: string): string =>
  composite(color(`md-${status}`), 0.15, surface);

const STATUSES = ['success', 'warning', 'danger', 'info'] as const;

// ---- Token definitions --------------------------------------------------------------------

describe('design tokens', () => {
  it('defines exactly the colour tokens of the #410 visual specification', () => {
    expect(Object.keys(colors).sort()).toEqual(
      [
        'md-danger',
        'md-danger-text',
        'md-dark',
        'md-dark-text',
        'md-gray',
        'md-info',
        'md-info-text',
        'md-input-border',
        'md-success',
        'md-success-text',
        'md-warning',
        'md-warning-text',
      ].sort(),
    );
  });

  it('uses the specified solid, text and gray values', () => {
    expect(color('md-dark')).toBe('#262626');
    expect(color('md-info')).toBe('#1A73E8');
    expect(color('md-success')).toBe('#4CAF50');
    expect(color('md-warning')).toBe('#FB8C00');
    expect(color('md-danger')).toBe('#F44335');

    expect(color('md-dark-text')).toBe('#262626');
    expect(color('md-info-text')).toBe('#1557B0');
    expect(color('md-success-text')).toBe('#1B5E20');
    expect(color('md-warning-text')).toBe('#8A4B00');
    expect(color('md-danger-text')).toBe('#B71C1C');

    expect(gray(100)).toBe('#F5F5F5');
    expect(gray(200)).toBe('#E5E5E5');
    expect(gray(300)).toBe('#D4D4D4');
    expect(gray(500)).toBe('#737373');
    expect(gray(600)).toBe('#525252');
    expect(gray(800)).toBe('#262626');

    expect(color('md-input-border')).toBe('#D2D6DA');
  });

  it('defines every gradient as a 195deg background image', () => {
    expect(backgroundImages).toEqual({
      'md-dark-gradient': 'linear-gradient(195deg, #42424a, #191919)',
      'md-info-gradient': 'linear-gradient(195deg, #49a3f1, #1A73E8)',
      'md-success-gradient': 'linear-gradient(195deg, #66BB6A, #43A047)',
      'md-warning-gradient': 'linear-gradient(195deg, #FFA726, #FB8C00)',
      'md-danger-gradient': 'linear-gradient(195deg, #EF5350, #E53935)',
      'md-danger-button-gradient': 'linear-gradient(195deg, #D32F2F, #B71C1C)',
    });
  });

  it('defines the card, dropdown, dialog and icon tile shadows', () => {
    expect(Object.keys(extend.boxShadow).sort()).toEqual(
      [
        'md',
        'md-card',
        'md-lg',
        'md-tile-danger',
        'md-tile-dark',
        'md-tile-info',
        'md-tile-success',
        'md-tile-warning',
      ].sort(),
    );
    expect(extend.boxShadow['md-card']).toBe('0 1px 2px 0 rgb(0 0 0 / 0.05)');
    expect(extend.boxShadow['md-tile-success']).toContain('rgba(76, 175, 80, 0.4)');
  });

  it('defines the control, card, badge and dialog radii', () => {
    expect(extend.borderRadius).toEqual({
      'md-control': '0.375rem',
      'md-card': '0.5rem',
      'md-badge': '0.45rem',
      'md-dialog': '0.75rem',
    });
  });

  it('puts Inter in front of the previous system font stack', () => {
    expect(extend.fontFamily['sans']).toEqual([
      'Inter',
      "'Segoe UI'",
      'Roboto',
      'Helvetica',
      'Arial',
      'sans-serif',
    ]);
  });

  it('defines the typographic sizes the shared classes use', () => {
    expect(extend.fontSize).toEqual({
      'md-page-title': '1.25rem',
      'md-card-title': '1rem',
      'md-stat-value': '1.5rem',
      'md-body': '0.875rem',
      'md-badge': '0.75rem',
      'md-table-head': '0.65rem',
    });
  });
});

// ---- Contrast -----------------------------------------------------------------------------

describe('shared class text contrast', () => {
  const pairs: Array<[string, string, string]> = [
    // Page layout, on the md-gray-100 canvas.
    ['.page-title', gray(800), SURFACE_CANVAS],
    ['.page-subtitle', gray(600), SURFACE_CANVAS],
    ['body copy on the page canvas', gray(600), SURFACE_CANVAS],

    // Cards, on white.
    ['.card-title', gray(800), SURFACE_WHITE],
    ['.card-body', gray(600), SURFACE_WHITE],
    ['.card-footer', gray(600), SURFACE_WHITE],
    ['.stat-card-label', gray(500), SURFACE_WHITE],
    ['.stat-card-value', gray(800), SURFACE_WHITE],
    ['.stat-card-footer', gray(600), SURFACE_WHITE],

    // Buttons. Only md-dark and the danger button gradient carry white text.
    ['.btn-primary label (gradient start)', WHITE, gradientStops('md-dark-gradient')[0]],
    ['.btn-primary label (gradient end)', WHITE, gradientStops('md-dark-gradient')[1]],
    ['.btn-secondary label', gray(800), SURFACE_WHITE],
    [
      '.btn-danger label (gradient start)',
      WHITE,
      gradientStops('md-danger-button-gradient')[0],
    ],
    ['.btn-danger label (gradient end)', WHITE, gradientStops('md-danger-button-gradient')[1]],
    ['.btn-link label on white', color('md-info-text'), SURFACE_WHITE],

    // Tables. The header sits on md-gray-100 and a hovered row turns md-gray-100.
    ['.table-head', gray(600), SURFACE_CANVAS],
    ['.table-cell', gray(600), SURFACE_WHITE],
    ['.table-cell in a hovered .table-row', gray(600), SURFACE_CANVAS],

    // Badges.
    ['.badge-neutral', gray(600), SURFACE_NEUTRAL_BADGE],

    // Form fields, on white.
    ['.field-label', gray(600), SURFACE_WHITE],
    ['.field-hint', gray(500), SURFACE_WHITE],
    ['.field-error', color('md-danger-text'), SURFACE_WHITE],

    // Value classes.
    ['.value-positive on white', color('md-success-text'), SURFACE_WHITE],
    ['.value-positive on the canvas', color('md-success-text'), SURFACE_CANVAS],
    ['.value-negative on white', color('md-danger-text'), SURFACE_WHITE],
    ['.value-negative on the canvas', color('md-danger-text'), SURFACE_CANVAS],
    ['.value-muted on white', gray(500), SURFACE_WHITE],
    ['.value-muted on a gray surface', gray(600), SURFACE_CANVAS],
  ];

  // Every badge and alert, on both the white card surface and the gray page canvas, because a
  // 15% tint is translucent and composites differently on each.
  for (const status of STATUSES) {
    for (const [surfaceName, surface] of [
      ['white', SURFACE_WHITE],
      ['the canvas', SURFACE_CANVAS],
    ] as const) {
      pairs.push([
        `.badge-${status} on ${surfaceName}`,
        color(`md-${status}-text`),
        tint(status, surface),
      ]);
      pairs.push([
        `.alert-${status} title on ${surfaceName}`,
        color(`md-${status}-text`),
        tint(status, surface),
      ]);
      pairs.push([
        `.alert-${status} body text on ${surfaceName}`,
        gray(800),
        tint(status, surface),
      ]);
    }
  }

  it.each(pairs)('%s reaches WCAG AA 4.5:1', (_label, foreground, background) => {
    expect(contrastRatio(foreground, background)).toBeGreaterThanOrEqual(4.5);
  });

  /*
   * The visual specification quotes its own measurements for the tightest pairs. Asserting them
   * exactly pins the derivation of the 15% tints, not just that they clear the minimum.
   */
  it('reproduces the measured ratios quoted in the #410 visual specification', () => {
    const measured = (status: string): number =>
      contrastRatio(color(`md-${status}-text`), tint(status, SURFACE_WHITE));

    expect(measured('success')).toBeCloseTo(6.84, 2);
    expect(measured('warning')).toBeCloseTo(5.98, 2);
    expect(measured('danger')).toBeCloseTo(5.4, 2);
    expect(measured('info')).toBeCloseTo(5.71, 2);

    expect(contrastRatio(gray(500), SURFACE_WHITE)).toBeCloseTo(4.74, 2);
    expect(contrastRatio(gray(500), SURFACE_CANVAS)).toBeCloseTo(4.35, 2);
    expect(contrastRatio(gray(600), SURFACE_CANVAS)).toBeCloseTo(7.17, 2);
    expect(contrastRatio(gray(600), SURFACE_NEUTRAL_BADGE)).toBeCloseTo(6.5, 2);
    expect(contrastRatio(color('md-info'), SURFACE_WHITE)).toBeCloseTo(4.51, 2);
  });
});

describe('non-text contrast', () => {
  // A focus indicator is a non-text contrast requirement (WCAG 1.4.11), minimum 3:1.
  it.each([
    ['focus-visible outline on white', SURFACE_WHITE],
    ['focus-visible outline on the page canvas', SURFACE_CANVAS],
  ])('%s reaches 3:1', (_label, surface) => {
    expect(contrastRatio(color('md-info'), surface)).toBeGreaterThanOrEqual(3);
  });
});

// ---- styles.scss guards -------------------------------------------------------------------

describe('styles.scss', () => {
  it.each(STATUSES)('never uses the decorative md-%s solid colour as a text colour', (status) => {
    const decorativeAsText = new RegExp(`text-md-${status}(?!-text)\\b`, 'g');
    expect(stylesScss.match(decorativeAsText)).toBeNull();
  });

  it('uses the specified neutral badge surface, which this spec measures', () => {
    expect(stylesScss).toContain(`bg-[${SURFACE_NEUTRAL_BADGE}]`);
  });

  it('replaces focus outlines with the 2px md-info outline at 2px offset', () => {
    expect(stylesScss).toMatch(/:focus-visible/);
    expect(stylesScss).toContain('outline outline-2 outline-offset-2 outline-md-info');
  });

  it('builds the status badges and alerts from a 15% tint of the solid colour', () => {
    for (const status of STATUSES) {
      expect(stylesScss).toContain(`bg-md-${status}/15`);
    }
  });

  it('paints the page canvas and base text from tokens', () => {
    expect(stylesScss).toMatch(
      /html,\s*body\s*\{[^}]*@apply bg-md-gray-100 font-sans text-md-gray-600;/,
    );
  });

  /*
   * The global `input, select, textarea` rule carries layout, not just looks: page and form
   * layouts rely on its full width and padding (docs/architecture.md § Interaction and
   * presentation). Issue #410 changes only its border colour, radius and focus styling, so this
   * guards the parts that must not move.
   */
  it('keeps the global form control width and padding, and tokenises its border and radius', () => {
    const rule = /input,\s*select,\s*textarea\s*\{\s*@apply([^;]*);/.exec(stylesScss);
    expect(rule).not.toBeNull();

    const applied = rule?.[1] ?? '';
    expect(applied).toContain('w-full');
    expect(applied).toContain('px-3');
    expect(applied).toContain('py-2');
    expect(applied).toContain('rounded-md-control');
    expect(applied).toContain('border-md-input-border');
    expect(applied).toContain('focus:border-md-info');
  });
});
