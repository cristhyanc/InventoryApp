import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BRAND_LOGO_ASSETS, BrandLogoComponent, BrandLogoVariant } from './brand-logo.component';

@Component({
  standalone: true,
  imports: [BrandLogoComponent],
  template: `<app-brand-logo [variant]="variant" [height]="height" />`
})
class HostComponent {
  variant: BrandLogoVariant = 'full';
  height = 36;
}

function render(variant: BrandLogoVariant, height: number): HTMLImageElement {
  TestBed.configureTestingModule({ imports: [HostComponent] });
  const fixture = TestBed.createComponent(HostComponent);
  fixture.componentInstance.variant = variant;
  fixture.componentInstance.height = height;
  fixture.detectChanges();
  return fixture.nativeElement.querySelector('img') as HTMLImageElement;
}

/** The shared Restockroo logo (issue #1272). */
describe('BrandLogoComponent', () => {
  it.each([
    ['full', 'assets/brand/restockroo-logo.png'],
    ['symbol', 'assets/brand/restockroo-symbol.png']
  ] as const)('shows the bundled %s file, named Restockroo', (variant, src) => {
    const image = render(variant, 32);

    expect(image.getAttribute('src')).toBe(src);
    expect(image.getAttribute('alt')).toBe('Restockroo');
  });

  it.each(['full', 'symbol'] as const)(
    'reserves the %s file\'s own proportions and sets only the visible height',
    (variant) => {
      const image = render(variant, 36);

      expect(image.getAttribute('width')).toBe(String(BRAND_LOGO_ASSETS[variant].width));
      expect(image.getAttribute('height')).toBe(String(BRAND_LOGO_ASSETS[variant].height));
      expect(image.style.height).toBe('36px');
      expect(image.style.width).toBe('');
      expect(image.classList).toContain('w-auto');
    }
  );

  it('loads only packaged application assets, never a remote URL', () => {
    for (const asset of Object.values(BRAND_LOGO_ASSETS)) {
      expect(asset.src).toMatch(/^assets\/brand\/[a-z-]+\.png$/);
    }
  });
});
