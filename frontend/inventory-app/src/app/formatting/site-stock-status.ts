import { Site } from '../models/models';

/**
 * The Tailwind badge class for a site's `totalStockPercentage`. Shared by the dashboard's sites
 * table and the Sites list page so the red/yellow/green thresholds stay in one place.
 */
export function siteStockClass(site: Site): string {
  if (site.totalStockPercentage < 30) return 'bg-red-100 text-red-700';
  if (site.totalStockPercentage < 80) return 'bg-yellow-100 text-yellow-700';
  return 'bg-green-100 text-green-700';
}
