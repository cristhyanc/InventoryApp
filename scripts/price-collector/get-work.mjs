// The first call of a collector run (issue #926, parent #817): read the work list, print it, and do
// nothing else.
//
//   node scripts/price-collector/get-work.mjs [--raw]
//
// `GET /api/price-collector/work` (#924) issues one run and answers the work it is to do. Reading it
// is a write - the run and one work item per product-and-supplier pair are stored before the list is
// answered - so this script is run **once** per collector run, and a second call would hand out a
// second run and expire the first.
//
// What it prints is one JSON object on stdout: the run, the suppliers due, and **one entry per
// search** with the work-item id a result row must cite (#937), the product as a description, the
// route the search is taken on, and the listings that supplier already has for that product so a
// known listing is recognisable. The matching step's own inputs (#941) are printed beside them. The
// shape is the work list's, flattened: nothing is added, no price exists in it, and nothing about any
// business is in it to print.
//
// `--raw` prints the API's answer verbatim instead, for a human reading a failed run.
//
// The API base URL and the bearer token come from the environment (see ./collector-api.mjs) and are
// never printed. A non-2xx answer, an unreachable API or an answer that is not a work list all exit
// non-zero with nothing on stdout: half a work list read as the day's work is a day of listings
// silently not checked.
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { ApiError, ConfigurationError, redactorFor, requestJson, resolveApi } from './collector-api.mjs';
import { BARCODE_SCOPES, PRICE_CONNECTORS, SIZE_UNITS, nameFor } from './enums.mjs';

export const WORK_PATH = '/api/price-collector/work';

export const USAGE = 'get-work.mjs [--raw]';

function parseArguments(argv) {
  let raw = false;
  for (const argument of argv ?? []) {
    if (String(argument) === '--raw') {
      raw = true;
      continue;
    }
    throw new ConfigurationError(`Unknown option ${String(argument)}.\n${USAGE}`);
  }
  return { raw };
}

const listOf = (value) => (Array.isArray(value) ? value : []);

/** One supplier's route, as a search needs it. */
function supplierView(supplier) {
  return {
    supplierPresetId: supplier.supplierPresetId,
    supplierName: supplier.name ?? null,
    priceConnector: nameFor(PRICE_CONNECTORS, supplier.priceConnector),
    priceConnectorKey: supplier.priceConnectorKey ?? null,
    siteUrl: supplier.siteUrl ?? null,
    pricesIncludeGst: supplier.pricesIncludeGst === true,
  };
}

/** One listing the catalogue already holds. There is no price in a work list, and none here. */
function listingView(listing) {
  return {
    listingId: listing.listingId,
    supplierProductId: listing.supplierProductId,
    url: listing.url ?? null,
    title: listing.title ?? null,
    unitsPerPack: listing.unitsPerPack ?? null,
    barcode: listing.barcode ?? null,
    barcodeScope: nameFor(BARCODE_SCOPES, listing.barcodeScope),
    lastCheckedAtUtc: listing.lastCheckedAtUtc ?? null,
  };
}

function masterProductView(product) {
  return {
    kind: 'master',
    masterProductId: product.masterProductId,
    brand: product.brand,
    name: product.name,
    sizeValue: product.sizeValue,
    sizeUnit: nameFor(SIZE_UNITS, product.sizeUnit),
  };
}

function waitingProductView(product) {
  return {
    kind: 'waiting',
    waitingKey: product.waitingKey,
    searchName: product.searchName,
    unitLabel: product.unitLabel,
    matchingSuppressed: product.matchingSuppressed === true,
  };
}

/** The searches one published product is issued, one per due supplier, each with its own listings. */
function searchesFor({ product, productView, listings, listingsOmitted, suppliers }) {
  return listOf(product.workItems).map((workItem) => {
    const supplier = suppliers.get(workItem.supplierPresetId);
    return {
      workItemId: workItem.workItemId,
      ...(supplier ?? { supplierPresetId: workItem.supplierPresetId }),
      product: productView,
      // A listing of another supplier is another shelf: it would only be noise in this search.
      knownListings: listings
        .filter((listing) => listing.supplierPresetId === workItem.supplierPresetId)
        .map(listingView),
      knownListingsOmitted: listingsOmitted === true,
    };
  });
}

/**
 * The work list as the collector reads it: the run, the suppliers, one entry per search, and the
 * matching step's inputs. A transformation of the response and never an addition to it.
 */
export function summariseWorkList(response) {
  const suppliers = new Map(listOf(response.suppliers).map((supplier) => [supplier.supplierPresetId, supplierView(supplier)]));

  const matchedSearches = listOf(response.matchedProducts).flatMap((product) =>
    searchesFor({
      product,
      productView: masterProductView(product),
      listings: listOf(product.knownListings),
      listingsOmitted: product.knownListingsOmitted,
      suppliers,
    }),
  );

  const waitingSearches = listOf(response.waitingProducts).flatMap((product) =>
    searchesFor({
      product,
      productView: waitingProductView(product),
      listings: listOf(product.proposedListings),
      listingsOmitted: product.proposedListingsOmitted,
      suppliers,
    }),
  );

  const waitingProducts = listOf(response.waitingProducts).map((product) => ({
    ...waitingProductView(product),
    refusedMasterProductIds: listOf(product.refusedMasterProductIds),
    proposedListings: listOf(product.proposedListings).map(listingView),
    proposedListingsOmitted: product.proposedListingsOmitted === true,
    workItemIds: listOf(product.workItems).map((workItem) => workItem.workItemId),
  }));

  const searches = [...matchedSearches, ...waitingSearches];

  return {
    runId: response.runId,
    issuedAtUtc: response.issuedAtUtc ?? null,
    expiresAtUtc: response.expiresAtUtc ?? null,
    workItemLimitReached: response.workItemLimitReached === true,
    suppliers: [...suppliers.values()],
    searchCount: searches.length,
    searches,
    matching: {
      required: waitingProducts.length > 0,
      waitingProducts,
      masterProducts: listOf(response.masterProductsForMatching).map((master) => ({
        masterProductId: master.id,
        brand: master.brand,
        name: master.name,
        sizeValue: master.sizeValue,
        sizeUnit: nameFor(SIZE_UNITS, master.sizeUnit),
      })),
    },
  };
}

/** Refuses an answer that is not a work list, so a run is never started from half a list. */
function requireWorkList(response) {
  const looksRight =
    response !== null &&
    typeof response === 'object' &&
    Number.isInteger(response.runId) &&
    response.runId > 0 &&
    Array.isArray(response.suppliers) &&
    Array.isArray(response.matchedProducts) &&
    Array.isArray(response.waitingProducts);

  if (!looksRight) throw new ApiError(`GET ${WORK_PATH}: the answer is not a work list.`);
  return response;
}

/** One invocation. `out` is printed only when a whole work list was read. */
export async function runGetWork({ env = {}, argv = [], fetchImpl = fetch } = {}) {
  let api;
  let options;
  try {
    options = parseArguments(argv);
    api = resolveApi(env);
  } catch (error) {
    return { exitCode: 2, out: '', err: `${String(error?.message ?? error)}\n` };
  }

  const redact = redactorFor(api);
  try {
    const { body } = await requestJson({ api, method: 'GET', path: WORK_PATH, fetchImpl });
    const workList = requireWorkList(body);
    const answer = options.raw ? workList : summariseWorkList(workList);
    return { exitCode: 0, out: `${JSON.stringify(answer, null, 2)}\n`, err: '' };
  } catch (error) {
    return { exitCode: 1, out: '', err: `${redact(String(error?.message ?? error))}\n` };
  }
}

async function main() {
  const { exitCode, out, err } = await runGetWork({ env: process.env, argv: process.argv.slice(2) });
  if (out !== '') process.stdout.write(out);
  if (err !== '') process.stderr.write(err);
  process.exitCode = exitCode;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
