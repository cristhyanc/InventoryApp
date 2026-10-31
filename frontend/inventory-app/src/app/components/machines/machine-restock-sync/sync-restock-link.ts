/**
 * The query parameter, and its one value, with which the Dashboard's restock-sync reminder asks the
 * machine page to open Sync Restock (issue #1011). An explicit intent, never inferred from
 * `restockFrom`, which a link may or may not carry. Kept in a file of its own so the Dashboard can
 * build the link without importing the lazily loaded machine page.
 */
export const SYNC_RESTOCK_QUERY_PARAM = 'syncRestock';
export const SYNC_RESTOCK_OPEN = 'open';
