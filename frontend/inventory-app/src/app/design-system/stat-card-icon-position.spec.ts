import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { TestBed } from '@angular/core/testing';

import { DesignSystemShowcaseComponent } from './design-system-showcase.component';

/**
 * Regression coverage for issue #453: the stat-card icon tile sits inside the card body at the
 * upper right, and the label/value sit on the left, opposite it.
 *
 * This loads the real compiled `src/styles.css` (not the source `styles.scss`, and not just the
 * template's class names) into jsdom before rendering, so a regression that reintroduces the old
 * `-mt-8` overlap, flips `.stat-card-head` back to centered/reversed, or drops the left-alignment
 * override is caught by the actual resolved computed style, the same cascade a browser applies.
 * `DesignSystemShowcaseComponent` renders one stat card per icon-tile colour variant, so this
 * exercises every shared variant in one pass; `dashboard.component.spec.ts`,
 * `dashboard-report.component.spec.ts` and `bookkeeping-report.component.spec.ts` separately
 * confirm each real production template was updated to the same arrangement.
 */
describe('Stat-card icon tile position (issue #453)', () => {
  let styleElement: HTMLStyleElement;
  let host: HTMLElement;

  beforeAll(() => {
    const compiledCss = readFileSync(join(__dirname, '..', '..', 'styles.css'), 'utf8');
    styleElement = document.createElement('style');
    styleElement.textContent = compiledCss;
    document.head.appendChild(styleElement);
  });

  afterAll(() => {
    styleElement.remove();
  });

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [DesignSystemShowcaseComponent] }).compileComponents();
    const fixture = TestBed.createComponent(DesignSystemShowcaseComponent);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  });

  it('positions the icon tile as the last child of the stat-card head, after the label/value content', () => {
    const heads = Array.from(host.querySelectorAll('.stat-card-head'));
    expect(heads.length).toBeGreaterThan(0);

    for (const head of heads) {
      const children = Array.from(head.children);
      const contentIndex = children.findIndex((child) => child.classList.contains('stat-card-content'));
      const iconIndex = children.findIndex((child) => child.classList.contains('icon-tile'));

      expect(contentIndex).toBe(0);
      expect(iconIndex).toBe(children.length - 1);
    }
  });

  it('never gives the icon tile a negative margin that would pull it outside the card border', () => {
    const tiles = Array.from(host.querySelectorAll('.stat-card-head .icon-tile'));
    expect(tiles.length).toBeGreaterThan(0);

    const isNotNegative = (value: string): boolean => value === '' || parseFloat(value) >= 0;

    for (const tile of tiles) {
      const computed = getComputedStyle(tile);
      expect(isNotNegative(computed.marginTop)).toBe(true);
      expect(isNotNegative(computed.marginRight)).toBe(true);
      expect(isNotNegative(computed.marginBottom)).toBe(true);
      expect(isNotNegative(computed.marginLeft)).toBe(true);
    }
  });

  it('aligns the stat-card head to the top edge, so the icon sits at the upper corner instead of vertically centered', () => {
    const heads = Array.from(host.querySelectorAll('.stat-card-head'));
    expect(heads.length).toBeGreaterThan(0);

    for (const head of heads) {
      expect(getComputedStyle(head).alignItems).toBe('flex-start');
    }
  });

  it('left-aligns the label and value, opposite the icon, inside the stat-card head', () => {
    const labels = Array.from(host.querySelectorAll('.stat-card-head .stat-card-label'));
    const values = Array.from(host.querySelectorAll('.stat-card-head .stat-card-value'));
    expect(labels.length).toBeGreaterThan(0);
    expect(values.length).toBeGreaterThan(0);

    for (const element of [...labels, ...values]) {
      expect(getComputedStyle(element).textAlign).toBe('left');
    }
  });

  it('keeps a plain stat-card-label right-aligned outside an icon stat-card head, so other report summary tiles are unaffected', () => {
    const standalone = document.createElement('p');
    standalone.className = 'stat-card-label';
    document.body.appendChild(standalone);

    try {
      expect(getComputedStyle(standalone).textAlign).toBe('right');
    } finally {
      standalone.remove();
    }
  });

  it('lets the content wrapper shrink below its text width and wrap, so a long label or large value cannot overflow the card or push the icon out', () => {
    const contents = Array.from(host.querySelectorAll('.stat-card-head .stat-card-content'));
    expect(contents.length).toBeGreaterThan(0);

    for (const content of contents) {
      const computed = getComputedStyle(content);
      expect(computed.minWidth).toBe('0px');
      expect(computed.overflowWrap).toBe('break-word');
    }

    const tiles = Array.from(host.querySelectorAll('.stat-card-head .icon-tile'));
    expect(tiles.length).toBeGreaterThan(0);
    for (const tile of tiles) {
      expect(getComputedStyle(tile).flexShrink).toBe('0');
    }
  });
});
