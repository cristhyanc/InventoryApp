import { Site } from '../models/models';

/**
 * The shared Material Dashboard badge class for a site's `totalStockPercentage`. Shared by the
 * dashboard's sites table and the Sites list page so the danger/warning/success thresholds stay
 * in one place.
 */
export function siteStockClass(site: Site): string {
  if (site.totalStockPercentage < 30) return 'badge-danger';
  if (site.totalStockPercentage < 80) return 'badge-warning';
  return 'badge-success';
}
