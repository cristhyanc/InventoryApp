import { Component, Input } from '@angular/core';

/** Which prepared logo file to show (issue #1272). */
export type BrandLogoVariant = 'full' | 'symbol';

interface BrandLogoAsset {
  readonly src: string;
  /** The file's own pixel size, so the browser reserves the right box before the image loads. */
  readonly width: number;
  readonly height: number;
}

/**
 * The bundled logo files, made from the owner-approved original in `docs/brand/` by trimming
 * transparent canvas and resizing only (issue #1272). Each file is exported at about three times
 * its largest display height, so it stays sharp on high-density screens.
 */
export const BRAND_LOGO_ASSETS: Readonly<Record<BrandLogoVariant, BrandLogoAsset>> = {
  full: { src: 'assets/brand/restockroo-logo.png', width: 404, height: 108 },
  symbol: { src: 'assets/brand/restockroo-symbol.png', width: 81, height: 96 }
};

/**
 * The Restockroo logo (issue #1272): the full logo (kangaroo-and-box symbol with the navy
 * wordmark) or the symbol alone, as one image.
 *
 * The image is the brand's only name: its `alt` is "Restockroo", and callers render no visible
 * "Restockroo" text beside it, so a screen reader announces the brand once. The image keeps its
 * own proportions: callers set only `height` (CSS pixels) and the width follows from the file's
 * aspect ratio, with `width`/`height` attributes so nothing shifts while it loads.
 *
 * It decides nothing about links: a caller that links the brand wraps it in its own anchor.
 */
@Component({
  selector: 'app-brand-logo',
  standalone: true,
  template: `
    <img
      [src]="asset.src"
      alt="Restockroo"
      [attr.width]="asset.width"
      [attr.height]="asset.height"
      [style.height.px]="height"
      class="block w-auto max-w-none shrink-0"
      decoding="async"
      [attr.data-testid]="'brand-logo-' + variant"
    />
  `,
  host: { class: 'inline-flex shrink-0' }
})
export class BrandLogoComponent {
  @Input() variant: BrandLogoVariant = 'full';
  /** Visible height in CSS pixels. */
  @Input() height = 32;

  get asset(): BrandLogoAsset {
    return BRAND_LOGO_ASSETS[this.variant];
  }
}
