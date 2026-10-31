// The vocabularies the collector scripts and Restockroo share (issue #926, parent #817).
//
// **Every enum travels to the API as the number it declares** (`InventoryApi.DTOs`, issues #937 and
// #941), and every enum travels through a collector output file as the lower-case name a person
// reading that file can understand. This module is the one translation between the two, so a wrong
// number cannot be written into a file by hand and a new value cannot be invented in one place only.
//
// The numbers are the Domain's own, declared from 1: `MasterProductSizeUnit`, `BarcodeScope`,
// `SupplierPriceBasis`, `SupplierStockStatus`, `ProductMatchConfidence`,
// `PriceCollectorWorkItemOutcome`, `PriceCollectorSearchFailureReason`, `SupplierPriceConnector` and
// `PriceCollectorRunStatus`.

/** `MasterProductSizeUnit`: what one individual item's size is measured in. */
export const SIZE_UNITS = Object.freeze({ ml: 1, l: 2, g: 3, kg: 4, each: 5 });

/** `BarcodeScope`: whether a published barcode is the unit's or the carton's. */
export const BARCODE_SCOPES = Object.freeze({ unit: 1, carton: 2 });

/** `SupplierPriceBasis`: whether a published price includes GST. */
export const PRICE_BASES = Object.freeze({ exclusive_of_gst: 1, inclusive_of_gst: 2 });

/** `SupplierStockStatus`: what the supplier said about availability. */
export const STOCK_STATUSES = Object.freeze({ in_stock: 1, out_of_stock: 2, unknown: 3 });

/** `ProductMatchConfidence`: how sure the matcher is. Never sufficient on its own (#941). */
export const MATCH_CONFIDENCES = Object.freeze({ high: 1, medium: 2, low: 3 });

/**
 * `PriceCollectorWorkItemOutcome`, less `NotSearched`: the four answers a collector may give for one
 * search. `NotSearched` is the run completion's own answer and a collector may not claim it (#937).
 */
export const SEARCH_OUTCOME_NUMBERS = Object.freeze({ complete: 1, truncated: 2, no_matches: 3, failed: 4 });

/** `PriceCollectorSearchFailureReason`: why a search could not be completed, and only then. */
export const SEARCH_FAILURE_REASON_NUMBERS = Object.freeze({ actor_error: 1, provider_limit: 2, timeout: 3 });

/** `SupplierPriceConnector`: the collection route a supplier master record names (#931). */
export const PRICE_CONNECTORS = Object.freeze({ varnmill: 1, kmart: 2, big_w: 3, jb_metro: 4 });

/** `PriceCollectorRunStatus`: how a closed run ended, for the submission's own report. */
export const RUN_STATUSES = Object.freeze({
  running: 1,
  succeeded: 2,
  partly_succeeded: 3,
  failed: 4,
  expired: 5,
});

/** The number the API declares for one name, or a thrown error naming what was not understood. */
export function numberFor(vocabulary, name, what) {
  if (typeof name !== 'string' || !Object.hasOwn(vocabulary, name)) {
    throw new Error(`${what} "${String(name)}" is not one of: ${Object.keys(vocabulary).join(', ')}.`);
  }
  return vocabulary[name];
}

/** The name for one number the API published, or `null` for a value this script does not know. */
export function nameFor(vocabulary, value) {
  if (value === null || value === undefined) return null;
  const found = Object.entries(vocabulary).find(([, number]) => number === value);
  return found === undefined ? null : found[0];
}
