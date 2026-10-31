import { Site } from '../models/models';

/**
 * The shared Material Dashboard badge class for a stock percentage. The danger/warning/success
 * thresholds live here alone, so every surface that shows a stock level reads the same ones.
 *
 * Since issue #1013 a **machine's** own percentage is classified by this too, which is what keeps a
 * machine row's colours and words on the same thresholds as the site row above it. It takes the
 * percentage rather than a `Site`, because a machine figure is not a site.
 */
export function stockPercentageClass(percentage: number): string {
  if (percentage < 30) return 'badge-danger';
  if (percentage < 80) return 'badge-warning';
  return 'badge-success';
}

/**
 * The same classification for a site's `totalStockPercentage`. Shared by the dashboard's site rows
 * and the Sites list page.
 */
export function siteStockClass(site: Site): string {
  return stockPercentageClass(site.totalStockPercentage);
}
