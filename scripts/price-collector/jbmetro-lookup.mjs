// Reads JB Metro's published prices through its open Magento GraphQL endpoint, for the scheduled
// price collector (issue #927, parent #817). Two forms, both read-only and unauthenticated:
//
//   node scripts/price-collector/jbmetro-lookup.mjs --search "<text>" <site-url>
//   node scripts/price-collector/jbmetro-lookup.mjs <site-url> <sku>...
//
// The search is the GraphQL `products(search: ...)` query and never `/catalogsearch/`, which JB
// Metro's robots.txt disallows; the SKU form is `products(filter: { sku: { in: [...] } })` in bounded
// batches and is the fallback for a known listing that is missing from a day's search. Both answer
// one JSON object on stdout: the products with their sku, name, url, stock status, final and regular
// pack price, currency, pack quantity (`pickamntsoldas`), GST rate (`gst`), inner and outer barcodes
// and the special dates, plus what could not be read.
//
// Four things this script deliberately does not do:
//   * It does not decide whether a special is running. `special_from_date`/`special_to_date` are
//     reported exactly as published and nothing is derived from them: the final price *is* the
//     current price, and an old date attribute left on a product must never be read as a live
//     promotion (issue #927) or as a reason to distrust the price beside it.
//   * It does not convert a barcode to a number. They are reported as strings so a leading zero
//     survives, and an empty one is `null` rather than `0`.
//   * It does not convert, discount or GST-adjust a price. JB Metro publishes **ex-GST** prices and
//     the supplier master record is what says so (`SupplierPreset.PricesIncludeGst`, issue #931);
//     what an observed price means is decided where it is stored (#937), not here.
//   * It does not call a host the work list did not name. The site must be one of the JB Metro
//     supplier master URLs (`JBMETRO_SITE_URLS`, defaulting to the three records issue #931 shipped);
//     any other host is refused before a request is made.
//
// A GraphQL error returned with HTTP 200, a timeout, a 5xx and a 429 are retried with bounded
// exponential backoff and then reported against the page or the SKU batch they happened on, without
// stopping the rest of the run; `truncated` says that what was read is not everything there is. A
// SKU is only `notFound` once its batch was read to its end: one whose batch stopped early, whether
// a page failed or the page cap cut it short, is `unresolved` instead, because an unread page may
// still hold it and a prefix must never be published as a confirmed absence.
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

/** The JB Metro supplier master records of issue #931, and the default allowlist. */
export const JB_METRO_SITE_URLS = Object.freeze([
  'https://www.jbmetro.com.au',
  'https://www.jbmetro-sc-act.com.au',
  'https://www.jbmetro-sa-nt.com.au',
]);

export const DEFAULT_OPTIONS = Object.freeze({
  pageSize: 20,
  maxPages: 10,
  batchSize: 20,
  attempts: 3,
  backoffMs: 1000,
  timeoutMs: 30000,
});

export const USAGE = [
  'jbmetro-lookup.mjs --search <text> <site-url> [--page-size n] [--max-pages n] [--attempts n] [--backoff-ms n] [--timeout-ms n]',
  'jbmetro-lookup.mjs <site-url> <sku>... [--batch-size n] [--attempts n] [--backoff-ms n] [--timeout-ms n]',
].join('\n');

const USER_AGENT = 'inventoryapp-price-collector';
const MAX_MESSAGE_LENGTH = 300;

const NUMERIC_OPTIONS = Object.freeze({
  '--page-size': 'pageSize',
  '--max-pages': 'maxPages',
  '--batch-size': 'batchSize',
  '--attempts': 'attempts',
  '--backoff-ms': 'backoffMs',
  '--timeout-ms': 'timeoutMs',
});

// `custom_attributesV2` is read unfiltered: `attribute_codes` is not a valid filter on this endpoint.
const PRODUCT_FIELDS = `
      sku
      name
      url_key
      url_suffix
      stock_status
      price_range {
        minimum_price {
          final_price { value currency }
          regular_price { value currency }
        }
      }
      custom_attributesV2 {
        items {
          code
          ... on AttributeValue { value }
          ... on AttributeSelectedOptions { selected_options { label value } }
        }
      }`;

export const SEARCH_QUERY = `query JbMetroSearch($search: String!, $pageSize: Int!, $currentPage: Int!) {
  products(search: $search, pageSize: $pageSize, currentPage: $currentPage) {
    total_count
    page_info { current_page page_size total_pages }
    items {${PRODUCT_FIELDS}
    }
  }
}`;

export const SKU_QUERY = `query JbMetroSkus($skus: [String]!, $pageSize: Int!, $currentPage: Int!) {
  products(filter: { sku: { in: $skus } }, pageSize: $pageSize, currentPage: $currentPage) {
    total_count
    page_info { current_page page_size total_pages }
    items {${PRODUCT_FIELDS}
    }
  }
}`;

/** A failure of one request, with whether repeating it could plausibly answer differently. */
class LookupError extends Error {
  constructor(message, { retryable, attempts = 1 } = {}) {
    super(message);
    this.name = 'LookupError';
    this.retryable = retryable === true;
    this.attempts = attempts;
  }
}

const clean = (value, limit = MAX_MESSAGE_LENGTH) => String(value ?? '').replace(/\s+/g, ' ').trim().slice(0, limit);

const sleepFor = (ms) => new Promise((done) => setTimeout(done, ms));

function text(value) {
  if (value === null || value === undefined) return null;
  const trimmed = String(value).trim();
  return trimmed === '' ? null : trimmed;
}

function amount(value) {
  const trimmed = text(value);
  if (trimmed === null) return null;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

function wholeNumber(value) {
  const parsed = amount(value);
  return parsed !== null && Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

function count(value) {
  const parsed = amount(value);
  return parsed !== null && Number.isInteger(parsed) && parsed >= 0 ? parsed : null;
}

function originOf(value, refusal) {
  let url;
  try {
    url = new URL(String(value ?? '').trim());
  } catch {
    throw new Error(refusal);
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') throw new Error(refusal);
  return url.origin;
}

/**
 * The sites this run may call: the JB Metro supplier master URLs the collector passes in from the
 * work list (comma-separated), or the three records issue #931 shipped when nothing is configured.
 */
export function allowedSiteUrls(configured) {
  const entries = String(configured ?? '')
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry !== '');
  if (entries.length === 0) return JB_METRO_SITE_URLS;
  return entries.map((entry) => originOf(entry, `JBMETRO_SITE_URLS holds "${clean(entry, 80)}", which is not an absolute URL.`));
}

/** Normalises a site to its origin and refuses any host that is not one of the allowed sites. */
export function resolveSiteUrl(value, allowed = JB_METRO_SITE_URLS) {
  const refusal = `"${clean(value, 80)}" is not a JB Metro site this lookup may call (it must be an absolute URL of one of: ${allowed.join(', ')}).`;
  const origin = originOf(value, refusal);
  if (!allowed.includes(origin)) throw new Error(refusal);
  return origin;
}

/** Reads the text after `--search`, as the index it leaves the loop at. */
function consumeSearchOption(argv, index) {
  const nextIndex = index + 1;
  const searchText = nextIndex < argv.length ? text(argv[nextIndex]) : null;
  if (searchText === null) throw new Error('--search needs the text to search for.');
  return { searchText, nextIndex };
}

/** Reads and stores one `--<option> <n>` pair in place, as the index it leaves the loop at. */
function consumeNumericOption(argv, index, argument, options) {
  const nextIndex = index + 1;
  const raw = nextIndex < argv.length ? String(argv[nextIndex]) : '';
  if (!/^[1-9]\d*$/.test(raw)) throw new Error(`${argument} needs a positive whole number, not "${clean(raw, 40)}".`);
  options[NUMERIC_OPTIONS[argument]] = Number(raw);
  return nextIndex;
}

/** Builds the parsed result once the loop has read every argument, refusing an incomplete form. */
function finishParsedArguments(searchText, positional, options) {
  if (searchText !== null) {
    if (positional.length !== 1) throw new Error(`The search form takes exactly one site URL.\n${USAGE}`);
    return { mode: 'search', searchText, siteUrl: positional[0], skus: [], options };
  }
  if (positional.length < 2) throw new Error(`The SKU form takes a site URL and at least one SKU.\n${USAGE}`);
  return { mode: 'sku', searchText: null, siteUrl: positional[0], skus: positional.slice(1), options };
}

export function parseArguments(argv) {
  const positional = [];
  const options = { ...DEFAULT_OPTIONS };
  let searchText = null;

  for (let index = 0; index < argv.length; index += 1) {
    const argument = String(argv[index]);
    if (argument === '--search') {
      const consumed = consumeSearchOption(argv, index);
      searchText = consumed.searchText;
      index = consumed.nextIndex;
      continue;
    }
    if (Object.hasOwn(NUMERIC_OPTIONS, argument)) {
      index = consumeNumericOption(argv, index, argument, options);
      continue;
    }
    if (argument.startsWith('--')) throw new Error(`Unknown option ${clean(argument, 40)}.\n${USAGE}`);
    positional.push(argument);
  }

  return finishParsedArguments(searchText, positional, options);
}

/** Flattens `custom_attributesV2` into code → value, including attributes published as options. */
export function readCustomAttributes(container) {
  const attributes = Object.create(null);
  for (const entry of Array.isArray(container?.items) ? container.items : []) {
    const code = text(entry?.code);
    if (code === null) continue;
    if (entry.value !== undefined && entry.value !== null) {
      attributes[code] = entry.value;
      continue;
    }
    const labels = (Array.isArray(entry?.selected_options) ? entry.selected_options : [])
      .map((option) => text(option?.label) ?? text(option?.value))
      .filter((label) => label !== null);
    if (labels.length > 0) attributes[code] = labels.join(', ');
  }
  return attributes;
}

/** One product as the collector reads it. A value the site did not publish is `null`, never `0`. */
export function mapProduct(item, siteUrl) {
  const product = item ?? {};
  const attributes = readCustomAttributes(product.custom_attributesV2);
  const minimum = product.price_range?.minimum_price ?? {};
  const urlKey = text(product.url_key);
  return {
    sku: text(product.sku),
    name: text(product.name),
    url: urlKey === null ? null : `${siteUrl}/${urlKey}${text(product.url_suffix) ?? ''}`,
    stockStatus: text(product.stock_status),
    finalPrice: amount(minimum.final_price?.value),
    regularPrice: amount(minimum.regular_price?.value),
    currency: text(minimum.final_price?.currency) ?? text(minimum.regular_price?.currency),
    packQuantity: wholeNumber(attributes.pickamntsoldas),
    gstRate: amount(attributes.gst),
    // Strings, always: a barcode's leading zero is part of it.
    innerBarcode: text(attributes.innerbarcode),
    outerBarcode: text(attributes.outerbarcode),
    specialFromDate: text(attributes.special_from_date),
    specialToDate: text(attributes.special_to_date),
  };
}

/** Posts one GraphQL query and returns the `products` payload, or throws a `LookupError`. */
export async function postGraphql({ fetchImpl, endpoint, query, variables, timeoutMs }) {
  let response;
  try {
    response = await fetchImpl(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'User-Agent': USER_AGENT },
      body: JSON.stringify({ query, variables }),
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (error) {
    const timedOut = error?.name === 'TimeoutError' || error?.name === 'AbortError';
    throw new LookupError(timedOut ? `no answer within ${timeoutMs} ms` : `the request did not complete (${clean(error?.message)})`, { retryable: true });
  }

  if (!response.ok) {
    const status = Number(response.status);
    throw new LookupError(`the site answered HTTP ${status}`, { retryable: status >= 500 || status === 429 });
  }

  let body;
  try {
    body = await response.json();
  } catch (error) {
    throw new LookupError(`the answer was not JSON (${clean(error?.message)})`, { retryable: true });
  }

  // Magento reports a GraphQL failure with HTTP 200 and an `errors` array, so a 200 is not a result.
  const errors = Array.isArray(body?.errors) ? body.errors : [];
  if (errors.length > 0) {
    throw new LookupError(`GraphQL reported: ${clean(errors.map((entry) => clean(entry?.message, 120)).join('; '))}`, { retryable: true });
  }

  const products = body?.data?.products;
  if (products === null || typeof products !== 'object') throw new LookupError('the answer carried no products', { retryable: true });
  return products;
}

async function requestWithRetry({ fetchImpl, sleep, endpoint, query, variables, options }) {
  for (let attempt = 1; ; attempt += 1) {
    try {
      const products = await postGraphql({ fetchImpl, endpoint, query, variables, timeoutMs: options.timeoutMs });
      return { products, attempts: attempt };
    } catch (error) {
      const retryable = error instanceof LookupError ? error.retryable : true;
      if (!retryable || attempt >= options.attempts) {
        throw new LookupError(clean(error?.message), { retryable, attempts: attempt });
      }
      await sleep(options.backoffMs * 2 ** (attempt - 1));
    }
  }
}

function withDefaults(options) {
  const resolved = { ...DEFAULT_OPTIONS };
  for (const [key, fallback] of Object.entries(DEFAULT_OPTIONS)) {
    const supplied = wholeNumber(options?.[key]);
    resolved[key] = supplied ?? fallback;
  }
  return resolved;
}

function totalPagesOf(read, totalCount, pageSize) {
  const declared = count(read?.page_info?.total_pages);
  if (declared !== null) return declared;
  if (totalCount === null) return null;
  return Math.ceil(totalCount / pageSize);
}

/** Maps and appends one page's items, as the count read (0 means the page carried nothing further). */
function collectPageItems(answer, siteUrl, products) {
  const items = Array.isArray(answer.products.items) ? answer.products.items : [];
  for (const item of items) products.push(mapProduct(item, siteUrl));
  return items.length;
}

/** Whether the page just read was the last one: it carried nothing, or the count says so. */
function isLastPage(itemCount, totalPages, page) {
  return itemCount === 0 || (totalPages !== null && page >= totalPages);
}

/**
 * Reads one paged `products` query to its end: `total_count` first, then further pages until every
 * result has been read, a page fails after its retries, or the page cap stops it. `truncated` is
 * true in each of the last two cases, because what was read is then not everything there is.
 */
async function readPages({ fetchImpl, sleep, siteUrl, query, variablesForPage, options, failureContext }) {
  const endpoint = `${siteUrl}/graphql`;
  const products = [];
  const errors = [];
  let totalCount = null;
  let totalPages = null;
  let pagesRead = 0;
  let truncated = false;

  for (let page = 1; page <= options.maxPages; page += 1) {
    let answer;
    try {
      answer = await requestWithRetry({ fetchImpl, sleep, endpoint, query, variables: variablesForPage(page), options });
    } catch (error) {
      errors.push({ ...failureContext(page), attempts: error.attempts ?? options.attempts, message: clean(error?.message) });
      truncated = true;
      break;
    }

    pagesRead += 1;
    if (totalCount === null) totalCount = count(answer.products.total_count);
    if (totalPages === null) totalPages = totalPagesOf(answer.products, totalCount, options.pageSize);

    const itemCount = collectPageItems(answer, siteUrl, products);
    if (isLastPage(itemCount, totalPages, page)) break;
    if (page >= options.maxPages) truncated = true;
  }

  return { totalCount, pagesRead, truncated, products, errors };
}

/** Searches one site by name and reads every page of the result the page cap allows. */
export async function searchProducts({ fetchImpl, sleep = sleepFor, siteUrl, searchText, options = {} }) {
  const resolved = withDefaults(options);
  return readPages({
    fetchImpl,
    sleep,
    siteUrl,
    query: SEARCH_QUERY,
    variablesForPage: (currentPage) => ({ search: searchText, pageSize: resolved.pageSize, currentPage }),
    options: resolved,
    failureContext: (page) => ({ scope: 'search', search: searchText, page }),
  });
}

function batchesOf(values, size) {
  const batches = [];
  for (let index = 0; index < values.length; index += size) batches.push(values.slice(index, index + size));
  return batches;
}

/** De-duplicates the requested SKUs case-insensitively, keeping the first spelling of each. */
function dedupeSkus(skus) {
  const requested = [];
  const seen = new Set();
  for (const sku of skus ?? []) {
    const trimmed = text(sku);
    if (trimmed === null || seen.has(trimmed.toLowerCase())) continue;
    seen.add(trimmed.toLowerCase());
    requested.push(trimmed);
  }
  return requested;
}

/** Reads one SKU batch through the same paged reader the search uses. */
function lookupBatch({ fetchImpl, sleep, siteUrl, batch, resolved }) {
  return readPages({
    fetchImpl,
    sleep,
    siteUrl,
    query: SKU_QUERY,
    variablesForPage: (currentPage) => ({ skus: batch, pageSize: Math.max(resolved.pageSize, batch.length), currentPage }),
    options: resolved,
    failureContext: (pageNumber) => ({ scope: 'sku', skus: batch, page: pageNumber }),
  });
}

/** Appends one batch's products, keeping the first match for a SKU the site answered for twice. */
function mergeBatchProducts(page, found, products) {
  for (const product of page.products) {
    const key = product.sku === null ? null : product.sku.toLowerCase();
    if (key !== null && found.has(key)) continue;
    if (key !== null) found.add(key);
    products.push(product);
  }
}

/**
 * Looks products up by SKU in bounded batches. A batch that fails after its retries is reported and
 * the next one still runs; a SKU the site answered for in full but did not return is `notFound`,
 * while one whose batch was never read to its end — it failed, or the page cap stopped it — is
 * `unresolved`: neither found nor missing, because nothing has answered for it yet.
 */
export async function lookupSkus({ fetchImpl, sleep = sleepFor, siteUrl, skus, options = {} }) {
  const resolved = withDefaults(options);
  const requested = dedupeSkus(skus);

  const products = [];
  const errors = [];
  const found = new Set();
  const unanswered = new Set();
  let truncated = false;

  for (const batch of batchesOf(requested, resolved.batchSize)) {
    // Sequential by design: batches throttle the site rather than racing it concurrently.
    const page = await lookupBatch({ fetchImpl, sleep, siteUrl, batch, resolved });

    mergeBatchProducts(page, found, products);
    errors.push(...page.errors);
    // A batch whose read stopped early answered for only part of itself, so absence is not yet a
    // fact for any SKU in it — a failed page and the page cap are the same silence here.
    if (page.truncated) {
      truncated = true;
      for (const sku of batch) unanswered.add(sku);
    }
  }

  const missing = requested.filter((sku) => !found.has(sku.toLowerCase()));
  return {
    requested,
    batchSize: resolved.batchSize,
    truncated,
    products,
    notFound: missing.filter((sku) => !unanswered.has(sku)),
    unresolved: missing.filter((sku) => unanswered.has(sku)),
    errors,
  };
}

/** Runs one invocation and returns the JSON result with the process exit code it should carry. */
export async function runLookup({ argv, env = {}, fetchImpl = fetch, sleep = sleepFor }) {
  let parsed;
  let siteUrl;
  try {
    parsed = parseArguments(argv ?? []);
    siteUrl = resolveSiteUrl(parsed.siteUrl, allowedSiteUrls(env.JBMETRO_SITE_URLS));
  } catch (error) {
    return { exitCode: 2, result: { error: String(error?.message ?? error), usage: USAGE } };
  }

  if (parsed.mode === 'search') {
    const found = await searchProducts({ fetchImpl, sleep, siteUrl, searchText: parsed.searchText, options: parsed.options });
    return {
      exitCode: found.errors.length > 0 ? 1 : 0,
      result: {
        mode: 'search',
        site: siteUrl,
        search: parsed.searchText,
        pageSize: parsed.options.pageSize,
        maxPages: parsed.options.maxPages,
        totalCount: found.totalCount,
        pagesRead: found.pagesRead,
        truncated: found.truncated,
        products: found.products,
        errors: found.errors,
      },
    };
  }

  const looked = await lookupSkus({ fetchImpl, sleep, siteUrl, skus: parsed.skus, options: parsed.options });
  return {
    exitCode: looked.errors.length > 0 ? 1 : 0,
    result: {
      mode: 'sku',
      site: siteUrl,
      requested: looked.requested,
      batchSize: looked.batchSize,
      truncated: looked.truncated,
      products: looked.products,
      notFound: looked.notFound,
      unresolved: looked.unresolved,
      errors: looked.errors,
    },
  };
}

async function main() {
  const { exitCode, result } = await runLookup({ argv: process.argv.slice(2), env: process.env });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  process.exitCode = exitCode;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
