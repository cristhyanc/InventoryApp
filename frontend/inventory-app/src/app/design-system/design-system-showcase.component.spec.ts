import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DesignSystemShowcaseComponent } from './design-system-showcase.component';

/**
 * The showcase is the rendering fixture for the shared Material Dashboard classes (issue #410).
 * It is not routed and not linked from navigation: it exists so that every shared class has one
 * place that renders it, which is what this spec asserts and what the pull request screenshots
 * capture.
 *
 * It is also what makes the shared classes reach `src/styles.css`: Tailwind only emits an
 * `@layer components` rule when it finds the class name in a scanned template, so a class that
 * nothing renders here would silently never be generated.
 */
describe('DesignSystemShowcaseComponent', () => {
  let fixture: ComponentFixture<DesignSystemShowcaseComponent>;
  let host: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DesignSystemShowcaseComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(DesignSystemShowcaseComponent);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  });

  const SHARED_CLASSES = [
    'page',
    'page-header',
    'page-title',
    'page-subtitle',
    'page-actions',
    'card',
    'card-header',
    'card-title',
    'card-body',
    'card-footer',
    'stat-card',
    'stat-card-head',
    'stat-card-label',
    'stat-card-value',
    'stat-card-footer',
    'icon-tile',
    'icon-tile-dark',
    'icon-tile-info',
    'icon-tile-success',
    'icon-tile-warning',
    'icon-tile-danger',
    'btn',
    'btn-primary',
    'btn-secondary',
    'btn-danger',
    'btn-link',
    'btn-sm',
    'table',
    'table-head',
    'table-row',
    'table-cell',
    'table-num',
    'badge',
    'badge-success',
    'badge-warning',
    'badge-danger',
    'badge-info',
    'badge-neutral',
    'alert',
    'alert-title',
    'alert-success',
    'alert-warning',
    'alert-danger',
    'alert-info',
    'field',
    'field-label',
    'field-hint',
    'field-error',
    'value-positive',
    'value-negative',
    'value-muted',
  ];

  it.each(SHARED_CLASSES)('renders at least one .%s element', (sharedClass) => {
    expect(host.querySelectorAll(`.${sharedClass}`).length).toBeGreaterThan(0);
  });

  it('renders every button variant, including a disabled one of each', () => {
    const buttons = Array.from(host.querySelectorAll('button.btn'));
    expect(buttons.length).toBeGreaterThan(0);

    for (const variant of ['btn-primary', 'btn-secondary', 'btn-danger', 'btn-link']) {
      const enabled = buttons.filter(
        (button) => button.classList.contains(variant) && !(button as HTMLButtonElement).disabled,
      );
      const disabled = buttons.filter(
        (button) => button.classList.contains(variant) && (button as HTMLButtonElement).disabled,
      );
      expect(enabled.length).toBeGreaterThan(0);
      expect(disabled.length).toBeGreaterThan(0);
    }
  });

  it('renders a disabled form control alongside the enabled ones', () => {
    expect(host.querySelectorAll('.field input:disabled, .field select:disabled').length)
      .toBeGreaterThan(0);
    expect(host.querySelectorAll('.field input:not(:disabled)').length).toBeGreaterThan(0);
  });

  it('associates every field label with its control', () => {
    const labels = Array.from(host.querySelectorAll('.field-label')) as HTMLLabelElement[];
    expect(labels.length).toBeGreaterThan(0);

    for (const label of labels) {
      const target = label.getAttribute('for');
      expect(target).toBeTruthy();
      expect(host.querySelector(`#${target}`)).not.toBeNull();
    }
  });

  it('hides every icon tile glyph from assistive technology', () => {
    const glyphs = Array.from(host.querySelectorAll('.icon-tile > svg'));
    expect(glyphs.length).toBeGreaterThan(0);

    for (const glyph of glyphs) {
      expect(glyph.getAttribute('aria-hidden')).toBe('true');
    }
  });

  it('gives every icon tile an adjacent text label, because the tile itself is decorative', () => {
    const tiles = Array.from(host.querySelectorAll('.icon-tile'));
    expect(tiles.length).toBeGreaterThan(0);

    for (const tile of tiles) {
      const card = tile.closest('.stat-card');
      expect(card).not.toBeNull();
      expect(card?.querySelector('.stat-card-label')?.textContent?.trim()).toBeTruthy();
    }
  });

  it('marks the field error as a live validation message for its control', () => {
    const error = host.querySelector('.field-error');
    expect(error).not.toBeNull();

    const describedBy = host.querySelector('[aria-describedby]');
    expect(describedBy?.getAttribute('aria-describedby')).toContain(error?.id ?? '');
  });
});
