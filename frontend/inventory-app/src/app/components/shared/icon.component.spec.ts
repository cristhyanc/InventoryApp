import { TestBed } from '@angular/core/testing';
import { IconComponent } from './icon.component';

async function render(name: string, label?: string) {
  await TestBed.configureTestingModule({ imports: [IconComponent] }).compileComponents();

  const fixture = TestBed.createComponent(IconComponent);
  fixture.componentInstance.name = name;
  if (label !== undefined) {
    fixture.componentInstance.label = label;
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
