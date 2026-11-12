import { Component } from '@angular/core';
import { IconComponent } from '../../components/shared/icon.component';
import {
  LANDING_REFILL_ITEMS,
  LANDING_REFILL_LOADED_NOTE,
  LANDING_REFILL_MACHINE,
  LANDING_REFILL_SITE,
  LandingRefillItem,
  refillItemIsPacked
} from './landing-content';

/**
 * The landing page's refill example (issue #1192, replacing issue #954's fleet-metrics dashboard
 * preview): one fictional site and machine, with a short Pick List of products to pack.
 *
 * It is deliberately not the real Pick List component and reads nothing - no service, no request -
 * so a signed-out visitor sees the same static figures every time. "Loaded" is how many of each
 * "To pack" quantity have been packed so far in this one Pick List run; it is not a second quantity
 * the application records separately from the Pick List (see `LANDING_REFILL_LOADED_NOTE`).
 */
@Component({
  selector: 'app-landing-refill-example',
  standalone: true,
  imports: [IconComponent],
  template: `
    <figure
      class="m-0 overflow-hidden rounded-2xl border border-rr-line bg-rr-surface shadow-md-lg"
      role="region"
      aria-labelledby="landing-refill-label"
      data-testid="landing-refill-example"
    >
      <div class="flex flex-wrap items-center gap-3 border-b border-rr-line bg-rr-surface-strong px-4 py-2">
        <span class="flex items-center gap-1.5 rounded-md bg-white px-3 py-1 text-xs font-medium text-rr-muted">
          <app-icon name="sync_alt" [size]="14" />
          {{ site }} · {{ machine }}
        </span>
        <figcaption
          id="landing-refill-label"
          class="ml-auto rounded-md border border-rr-line bg-white px-2 py-1 font-mono text-xs text-rr-muted"
        >
          Example refill · Fictional data
        </figcaption>
      </div>

      <div class="flex flex-col gap-4 p-3 sm:p-5">
        <table class="w-full border-collapse text-left text-sm" data-testid="landing-refill-table">
          <thead class="bg-rr-canvas text-xs uppercase tracking-wide text-rr-muted">
            <tr>
              <th scope="col" class="px-4 py-2 font-semibold">Product</th>
              <th scope="col" class="px-4 py-2 text-right font-semibold">To pack</th>
              <th scope="col" class="px-4 py-2 text-right font-semibold">Loaded</th>
            </tr>
          </thead>
          <tbody>
            @for (item of items; track item.product) {
              <tr class="border-t border-rr-line" data-testid="landing-refill-row">
                <th scope="row" class="px-4 py-3 font-semibold text-rr-navy">{{ item.product }}</th>
                <td class="px-4 py-3 text-right tabular-nums text-rr-navy">{{ item.toPack }}</td>
                <td class="px-4 py-3 text-right tabular-nums text-rr-navy">
                  {{ item.loaded }}
                  @if (isPacked(item)) {
                    <span class="ml-1 inline-flex rounded-md bg-rr-ok-bg px-1.5 py-0.5 text-xs font-semibold text-rr-ok-text">Packed</span>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>

        <p class="m-0 flex items-start gap-2 text-sm text-rr-muted" data-testid="landing-refill-loaded-note">
          <span class="shrink-0 text-rr-teal" aria-hidden="true"><app-icon name="info" [size]="16" /></span>
          <span>{{ loadedNote }}</span>
        </p>
      </div>
    </figure>
  `
})
export class LandingRefillExampleComponent {
  readonly site = LANDING_REFILL_SITE;
  readonly machine = LANDING_REFILL_MACHINE;
  readonly items = LANDING_REFILL_ITEMS;
  readonly loadedNote = LANDING_REFILL_LOADED_NOTE;

  isPacked(item: LandingRefillItem): boolean {
    return refillItemIsPacked(item);
  }
}
