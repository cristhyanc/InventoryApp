import { TestBed } from '@angular/core/testing';
import { IconComponent } from './icon.component';
import { ICON_PATHS, IconVariant, OUTLINED_ICON_SHAPES } from './icon-paths';

async function render(name: string, label?: string, variant?: IconVariant) {
  await TestBed.configureTestingModule({ imports: [IconComponent] }).compileComponents();

  const fixture = TestBed.createComponent(IconComponent);
  fixture.componentInstance.name = name;
  if (label !== undefined) {
    fixture.componentInstance.label = label;
  }
  if (variant !== undefined) {
    fixture.componentInstance.variant = variant;
  }
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  return { fixture, host, svg: host.querySelector('svg') };
}

afterEach(() => TestBed.resetTestingModule());

describe('IconComponent', () => {
  it('renders an SVG path for a known icon name', async () => {
    const { svg } = await render('close');

    expect(svg).not.toBeNull();
    expect(svg!.querySelector('path')?.getAttribute('d')).toBeTruthy();
  });

  it('renders nothing for an unknown icon name and does not throw', async () => {
    const { svg } = await render('not-a-real-icon');

    expect(svg).toBeNull();
  });

  it('sets the width and height from the size input, defaulting to 24', async () => {
    const { svg } = await render('close');

    expect(svg!.getAttribute('width')).toBe('24');
    expect(svg!.getAttribute('height')).toBe('24');
  });

  it('applies a custom size', async () => {
    const { fixture, svg } = await render('close');
    fixture.componentInstance.size = 16;
    fixture.detectChanges();

    expect(svg!.getAttribute('width')).toBe('16');
    expect(svg!.getAttribute('height')).toBe('16');
  });

  describe('without a label', () => {
    it('is decorative: aria-hidden is true, and there is no role or accessible name', async () => {
      const { svg } = await render('warning');

      expect(svg!.getAttribute('aria-hidden')).toBe('true');
      expect(svg!.hasAttribute('role')).toBe(false);
      expect(svg!.hasAttribute('aria-label')).toBe(false);
    });
  });

  /**
   * The explicit variant (issue #456). The sidebar is the only caller that asks for `outlined`,
   * and the default has to stay `rounded` so no stat card, icon tile or action button changes.
   */
  describe('variant', () => {
    it('defaults to the filled Rounded geometry', async () => {
      const { fixture, svg } = await render('home');

      expect(fixture.componentInstance.variant).toBe('rounded');
      expect(svg!.querySelector('path')?.getAttribute('d')).toBe(ICON_PATHS['home']);
    });

    it('draws the Outlined geometry when it is asked for', async () => {
      const { svg } = await render('home', undefined, 'outlined');

      expect(svg!.querySelector('path')?.getAttribute('d')).toBe(OUTLINED_ICON_SHAPES['home'].paths[0]);
      expect(svg!.querySelector('path')?.getAttribute('d')).not.toBe(ICON_PATHS['home']);
    });

    it('renders every published path of a multi-path outlined glyph', async () => {
      const { svg } = await render('inventory_2', undefined, 'outlined');

      expect(Array.from(svg!.querySelectorAll('path')).map((path) => path.getAttribute('d'))).toEqual(
        OUTLINED_ICON_SHAPES['inventory_2'].paths
      );
    });

    it('renders a circle child with the published cx/cy/r', async () => {
      const { svg } = await render('location_on', undefined, 'outlined');
      const circle = svg!.querySelector('circle');

      expect(svg!.querySelectorAll('path')).toHaveLength(1);
      expect(circle?.getAttribute('cx')).toBe('12');
      expect(circle?.getAttribute('cy')).toBe('9');
      expect(circle?.getAttribute('r')).toBe('2.5');
    });

    it('renders no stray circle for a glyph published without one', async () => {
      const { svg } = await render('home', undefined, 'outlined');

      expect(svg!.querySelectorAll('circle')).toHaveLength(0);
    });

    it('renders nothing, rather than the filled glyph, for a name the outlined set does not bundle', async () => {
      const { svg } = await render('add', undefined, 'outlined');

      expect(svg).toBeNull();
    });

    it('still fills every shape with currentColor so the active item gets a white icon', async () => {
      const { svg } = await render('location_on', undefined, 'outlined');

      expect(svg!.getAttribute('fill')).toBe('currentColor');
    });

    it('keeps an unlabelled outlined icon decorative', async () => {
      const { svg } = await render('home', undefined, 'outlined');

      expect(svg!.getAttribute('aria-hidden')).toBe('true');
      expect(svg!.hasAttribute('role')).toBe(false);
    });

    it('gives a labelled outlined icon the same accessible name treatment as the default variant', async () => {
      const { svg } = await render('menu', 'Collapse navigation', 'outlined');

      expect(svg!.hasAttribute('aria-hidden')).toBe(false);
      expect(svg!.getAttribute('role')).toBe('img');
      expect(svg!.getAttribute('aria-label')).toBe('Collapse navigation');
    });
  });

  describe('with a label', () => {
    it('removes aria-hidden, sets role="img" and exposes the label as the accessible name', async () => {
      const { svg } = await render('warning', 'Low stock');

      expect(svg!.hasAttribute('aria-hidden')).toBe(false);
      expect(svg!.getAttribute('role')).toBe('img');
      expect(svg!.getAttribute('aria-label')).toBe('Low stock');
    });

    it('is still not focusable', async () => {
      const { svg } = await render('warning', 'Low stock');

      expect(svg!.hasAttribute('tabindex')).toBe(false);
    });
  });
});
