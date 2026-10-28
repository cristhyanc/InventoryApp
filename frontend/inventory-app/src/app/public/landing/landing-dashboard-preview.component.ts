import { Component } from '@angular/core';
import { IconComponent } from '../../components/shared/icon.component';
import { SAMPLE_MACHINES, SAMPLE_METRICS, SAMPLE_STOCK_LEVELS, SampleStockLevel, isLowStock } from './landing-content';

/**
 * The landing page's example dashboard (issue #954): a static picture of the application, drawn
 * from the synthetic figures in `landing-content.ts`.
 *
 * It is deliberately not the real dashboard component and reads nothing - no service, no request -
 * so a signed-out visitor sees the same sample a signed-in one does, and no business's data can
 * ever appear in it. The window bar says "Example dashboard · Sample data", and the region is named
 * the same way for assistive technology.
 *
 * The approved mobile design shows the stock levels as a list and leaves out the machines table, so
 * below the `md` breakpoint the stock levels render as a list and the machines table is not shown;
 * from `md` up both render as tables.
 */
@Component({
  selector: 'app-landing-dashboard-preview',
  standalone: true,
  imports: [IconComponent],
  template: `
    <figure
      class="m-0 overflow-hidden rounded-2xl border border-rr-line bg-rr-surface shadow-md-lg"
      role="region"
      aria-labelledby="landing-dashboard-label"
      data-testid="landing-dashboard"
    >
      <div class="flex flex-wrap items-center gap-3 border-b border-rr-line bg-rr-surface-strong px-4 py-2">
        <span class="hidden gap-1.5 sm:flex" aria-hidden="true">
          <span class="h-2.5 w-2.5 rounded-full bg-rr-line-strong"></span>
          <span class="h-2.5 w-2.5 rounded-full bg-rr-line-strong"></span>
          <span class="h-2.5 w-2.5 rounded-full bg-rr-line-strong"></span>
        </span>
        <span class="flex items-center gap-1.5 rounded-md bg-white px-3 py-1 text-xs font-medium text-rr-muted">
          <app-icon name="lock" [size]="14" />
          Restockroo · Dashboard
        </span>
        <figcaption
          id="landing-dashboard-label"
          class="ml-auto rounded-md border border-rr-line bg-white px-2 py-1 font-mono text-xs text-rr-muted"
        >
          Example dashboard · Sample data
        </figcaption>
      </div>

      <div class="flex flex-col gap-4 p-3 sm:p-5">
        <ul class="m-0 grid list-none grid-cols-2 gap-3 p-0 lg:grid-cols-4" aria-label="Sample figures">
          @for (metric of metrics; track metric.label) {
            <li class="flex min-w-0 flex-col gap-2 rounded-xl border border-rr-line bg-white p-3 sm:p-4">
              <div class="flex items-start justify-between gap-2">
                <span class="text-xs font-semibold uppercase tracking-wide text-rr-muted">{{ metric.label }}</span>
                <span [class]="metric.warning ? 'text-rr-warn-text' : 'text-rr-teal'">
                  <app-icon [name]="metric.icon" [size]="18" />
                </span>
              </div>
              <span class="font-mono text-lg font-semibold tabular-nums text-rr-navy sm:text-2xl">{{ metric.value }}</span>
              <span class="text-xs text-rr-muted">{{ metric.caption }}</span>
            </li>
          }
        </ul>

        <div class="grid gap-4 lg:grid-cols-2">
          <section class="hidden overflow-hidden rounded-xl border border-rr-line bg-white md:block" aria-labelledby="landing-sample-machines">
            <header class="flex items-center justify-between border-b border-rr-line px-4 py-3">
              <h3 id="landing-sample-machines" class="m-0 text-sm font-semibold text-rr-navy">Sites &amp; Machines</h3>
              <span class="font-mono text-xs text-rr-muted">{{ machines.length }} configured</span>
            </header>
            <table class="w-full border-collapse text-left text-sm">
              <thead class="bg-rr-canvas text-xs uppercase tracking-wide text-rr-muted">
                <tr>
                  <th scope="col" class="px-4 py-2 font-semibold">Location &amp; machine</th>
                  <th scope="col" class="px-4 py-2 font-semibold">Categories</th>
                  <th scope="col" class="px-4 py-2 text-right font-semibold">Products</th>
                </tr>
              </thead>
              <tbody>
                @for (machine of machines; track machine.machine) {
                  <tr class="border-t border-rr-line">
                    <td class="px-4 py-3">
                      <span class="block font-semibold text-rr-navy">{{ machine.site }}</span>
                      <span class="block font-mono text-xs text-rr-muted">{{ machine.machine }}</span>
                    </td>
                    <td class="px-4 py-3 text-rr-muted">{{ machine.categories }}</td>
                    <td class="px-4 py-3 text-right tabular-nums text-rr-navy">{{ machine.products }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </section>

          <section class="overflow-hidden rounded-xl border border-rr-line bg-white" aria-labelledby="landing-sample-stock">
            <header class="flex items-center justify-between border-b border-rr-line px-4 py-3">
              <h3 id="landing-sample-stock" class="m-0 text-sm font-semibold text-rr-navy">Stock levels</h3>
              <span class="font-mono text-xs text-rr-muted">{{ stockLevels.length }} items</span>
            </header>

            <ul class="m-0 list-none p-0 md:hidden" data-testid="landing-stock-list">
              @for (level of stockLevels; track level.product) {
                <li class="flex items-center justify-between gap-3 border-t border-rr-line px-4 py-3 first:border-t-0">
                  <span class="min-w-0">
                    <span class="block font-semibold text-rr-navy">{{ level.product }}</span>
                    <span class="block text-xs text-rr-muted">{{ level.category }}</span>
                  </span>
                  <span class="shrink-0 text-right">
                    <span [class]="'block text-sm font-semibold tabular-nums ' + stockClass(level)">
                      {{ level.inStock }} in stock
                    </span>
                    @if (lowStock(level)) {
                      <span class="inline-flex items-center gap-1 text-xs font-semibold text-rr-danger-text" data-testid="landing-low-stock">
                        <app-icon name="warning" [size]="14" />
                        Below minimum of {{ level.minimum }}
                      </span>
                    } @else {
                      <span class="block text-xs text-rr-ok-text">Minimum {{ level.minimum }}</span>
                    }
                  </span>
                </li>
              }
            </ul>

            <table class="hidden w-full border-collapse text-left text-sm md:table" data-testid="landing-stock-table">
              <thead class="bg-rr-canvas text-xs uppercase tracking-wide text-rr-muted">
                <tr>
                  <th scope="col" class="px-4 py-2 font-semibold">Product</th>
                  <th scope="col" class="px-4 py-2 text-right font-semibold">In stock</th>
                  <th scope="col" class="px-4 py-2 text-right font-semibold">Minimum</th>
                  <th scope="col" class="px-4 py-2 font-semibold">Supplier · last unit cost</th>
                </tr>
              </thead>
              <tbody>
                @for (level of stockLevels; track level.product) {
                  <tr class="border-t border-rr-line">
                    <th scope="row" class="px-4 py-3 font-semibold text-rr-navy">
                      {{ level.product }}
                      @if (lowStock(level)) {
                        <span class="mt-1 flex items-center gap-1 text-xs font-semibold text-rr-danger-text" data-testid="landing-low-stock">
                          <app-icon name="warning" [size]="14" />
                          Low stock
                        </span>
                      }
                    </th>
                    <td [class]="'px-4 py-3 text-right font-semibold tabular-nums ' + stockClass(level)">{{ level.inStock }}</td>
                    <td class="px-4 py-3 text-right tabular-nums text-rr-muted">{{ level.minimum }}</td>
                    <td class="px-4 py-3 text-xs text-rr-muted">{{ level.supplier }} · {{ level.lastUnitCost }}/unit</td>
                  </tr>
                }
              </tbody>
            </table>
          </section>
        </div>
      </div>
    </figure>
  `
})
export class LandingDashboardPreviewComponent {
  readonly metrics = SAMPLE_METRICS;
  readonly machines = SAMPLE_MACHINES;
  readonly stockLevels = SAMPLE_STOCK_LEVELS;

  lowStock(level: SampleStockLevel): boolean {
    return isLowStock(level);
  }

  stockClass(level: SampleStockLevel): string {
    return isLowStock(level) ? 'text-rr-danger-text' : 'text-rr-navy';
  }
}
