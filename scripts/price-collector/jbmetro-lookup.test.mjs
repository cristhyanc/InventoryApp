// Unit tests for scripts/price-collector/jbmetro-lookup.mjs: the GraphQL search and its pagination,
// the bounded SKU batches, the host refusal, the retry/backoff behaviour and the fields the collector
// reads off a JB Metro product.
//
// No test makes a live call. The bodies in ./fixtures are the JB Metro Magento GraphQL responses as the
// contract was recorded on issue #927 (owner-verified on 27 October 2026): `price_range.minimum_price`
// holds the ex-GST pack prices, and `custom_attributesV2` holds `pickamntsoldas`, `gst`, `innerbarcode`,
// `outerbarcode`, `special_from_date` and `special_to_date`. The SKUs are the three the issue names.
// They were recorded before the query asked for `fixed_product_taxes` (issue #1082) and are left as
// recorded, so a product read from one reports its levy as `unreadable`: that is the right answer
// for an answer that did not carry the list, and the levy cases below use their own bodies.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  DEFAULT_OPTIONS,
  JB_METRO_SITE_URLS,
  LEVY_STATUS,
  SEARCH_QUERY,
  SKU_QUERY,
  allowedSiteUrls,
  lookupSkus,
  mapProduct,
  parseArguments,
  stockStatus,
  resolveSiteUrl,
  runLookup,
  searchProducts,
} from './jbmetro-lookup.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, 'fixtures');
const fixture = (name) => JSON.parse(readFileSync(join(fixturesDir, `${name}.json`), 'utf8'));

const SITE = 'https://www.jbmetro.com.au';
const SC_ACT = 'https://www.jbmetro-sc-act.com.au';

const ok = (body) => ({ ok: true, status: 200, json: async () => body, text: async () => JSON.stringify(body) });
const serverError = (status = 503) => ({ ok: false, status, json: async () => ({}), text: async () => 'Service Unavailable' });
const timeout = () => Object.assign(new Error('The operation was aborted due to timeout'), { name: 'TimeoutError' });

// A fake fetch over a queue of answers: a function is called, an Error is thrown, anything else is
// returned. The last answer repeats, and every request is recorded so the query can be asserted.
function fakeFetch(answers) {
  const calls = [];
  const fetchImpl = async (url, init) => {
    const body = JSON.parse(init.body);
    calls.push({ url, method: init.method, query: body.query, variables: body.variables });
    const answer = answers[Math.min(calls.length - 1, answers.length - 1)];
    if (answer instanceof Error) throw answer;
    return typeof answer === 'function' ? answer() : answer;
  };
  return { fetchImpl, calls };
}

// Records the backoff delays instead of waiting them out.
function fakeSleep() {
  const waits = [];
  return { sleep: async (ms) => void waits.push(ms), waits };
}

describe('site URLs', () => {
  it('ships the three JB Metro supplier master records', () => {
    assert.deepEqual(JB_METRO_SITE_URLS, [SITE, SC_ACT, 'https://www.jbmetro-sa-nt.com.au']);
  });

  it('accepts a JB Metro site and normalises it to its origin', () => {
    assert.equal(resolveSiteUrl('https://www.jbmetro-sa-nt.com.au/', JB_METRO_SITE_URLS), 'https://www.jbmetro-sa-nt.com.au');
    assert.equal(resolveSiteUrl('HTTPS://WWW.JBMETRO.COM.AU', JB_METRO_SITE_URLS), SITE);
  });

  it('refuses any other host', () => {
    for (const refused of ['https://www.coles.com.au', 'https://jbmetro.com.au.evil.test', 'http://www.jbmetro.com.au', 'not a url', '']) {
      assert.throws(() => resolveSiteUrl(refused, JB_METRO_SITE_URLS), /not a JB Metro site|absolute URL/i, `expected ${refused} to be refused`);
    }
  });

  it('takes the allowed sites from the work list when the collector passes them', () => {
    assert.deepEqual(allowedSiteUrls(''), JB_METRO_SITE_URLS);
    assert.deepEqual(allowedSiteUrls(' https://www.jbmetro-nt.com.au , https://www.jbmetro.com.au '), ['https://www.jbmetro-nt.com.au', SITE]);
    assert.throws(() => allowedSiteUrls('www.jbmetro.com.au'), /absolute URL/i);
  });
});

describe('arguments', () => {
  it('reads the search form', () => {
    const parsed = parseArguments(['--search', 'bundaberg ginger beer', SITE, '--page-size', '50', '--max-pages', '3']);
    assert.equal(parsed.mode, 'search');
    assert.equal(parsed.searchText, 'bundaberg ginger beer');
    assert.equal(parsed.siteUrl, SITE);
    assert.equal(parsed.options.pageSize, 50);
    assert.equal(parsed.options.maxPages, 3);
  });

  it('reads the SKU form', () => {
    const parsed = parseArguments([SITE, 'BGB25', 'KB', 'CCC3']);
    assert.equal(parsed.mode, 'sku');
    assert.equal(parsed.siteUrl, SITE);
    assert.deepEqual(parsed.skus, ['BGB25', 'KB', 'CCC3']);
    assert.equal(parsed.options.batchSize, DEFAULT_OPTIONS.batchSize);
  });

  it('refuses an unusable invocation', () => {
    assert.throws(() => parseArguments([SITE]), /sku/i);
    assert.throws(() => parseArguments(['--search', 'coke']), /site url/i);
    assert.throws(() => parseArguments(['--search', 'coke', SITE, '--page-size', '0']), /--page-size/);
    assert.throws(() => parseArguments(['--search', 'coke', SITE, '--unknown']), /--unknown/);
  });
});

describe('product mapping', () => {
  it('speaks the result file\'s stock words', () => {
    assert.equal(stockStatus('IN_STOCK'), 'in_stock');
    assert.equal(stockStatus('OUT_OF_STOCK'), 'out_of_stock');
    assert.equal(stockStatus('BACKORDER'), 'unknown');
    assert.equal(stockStatus(null), 'unknown');
    assert.equal(stockStatus(undefined), 'unknown');
  });

  const item = fixture('search-bundaberg-page-1').data.products.items[0];

  it('reports the fields the collector needs', () => {
    assert.deepEqual(mapProduct(item, SITE), {
      sku: 'BGB25',
      name: 'Bundaberg Ginger Beer 375ml Bottle',
      url: `${SITE}/bundaberg-ginger-beer-375ml-bottle.html`,
      stockStatus: 'in_stock',
      finalPrice: 31.2,
      regularPrice: 33.6,
      currency: 'AUD',
      // This fixture was recorded before the query asked for `fixed_product_taxes`, so it carries
      // none: an answer without the list is unreadable, never a published absence of levy.
      levy: null,
      levyLabels: [],
      levyStatus: LEVY_STATUS.unreadable,
      packQuantity: 24,
      gstRate: 10,
      innerBarcode: '9311493000012',
      outerBarcode: '19311493000019',
      specialFromDate: '2025-06-02 00:00:00',
      specialToDate: '2025-06-30 00:00:00',
    });
  });

  it('keeps a barcode a string so a leading zero survives', () => {
    const withLeadingZero = fixture('sku-batch').data.products.items[1];
    const mapped = mapProduct(withLeadingZero, SITE);
    assert.equal(mapped.innerBarcode, '09300675024235');
    assert.equal(mapped.outerBarcode, '09300675024242');
    assert.equal(typeof mapped.innerBarcode, 'string');
  });

  it('reports no special flag of its own, because the final price is the current price', () => {
    const mapped = mapProduct(item, SITE);
    assert.equal(mapped.finalPrice, 31.2);
    assert.ok(!('isSpecial' in mapped) && !('isPromotion' in mapped) && !('specialActive' in mapped));
  });

  it('reads an attribute published as a selected option', () => {
    const selectOption = fixture('search-single-page').data.products.items[0];
    assert.equal(mapProduct(selectOption, SITE).gstRate, 10);
  });

  it('leaves an absent or empty value unknown rather than zero', () => {
    const mapped = mapProduct(fixture('search-bundaberg-page-2').data.products.items[0], SITE);
    assert.equal(mapped.outerBarcode, null);
    assert.equal(mapped.specialFromDate, null);
    assert.equal(mapped.specialToDate, null);
    assert.deepEqual(mapProduct({ sku: 'X' }, SITE), {
      sku: 'X',
      name: null,
      url: null,
      stockStatus: 'unknown',
      finalPrice: null,
      regularPrice: null,
      currency: null,
      levy: null,
      levyLabels: [],
      levyStatus: LEVY_STATUS.unreadable,
      packQuantity: null,
      gstRate: null,
      innerBarcode: null,
      outerBarcode: null,
      specialFromDate: null,
      specialToDate: null,
    });
  });
});

describe('container deposit levy', () => {
  // One drink priced in AUD, with whatever `fixed_product_taxes` the case is about.
  const priced = (fixedProductTaxes, final = { value: 22, currency: 'AUD' }) => mapProduct({
    sku: 'YOOSH',
    price_range: { minimum_price: { final_price: final, ...fixedProductTaxes } },
  }, SITE);

  it('requests fixed_product_taxes under minimum_price in both the search and SKU queries', () => {
    const shape = /fixed_product_taxes\s*\{\s*label\s*amount\s*\{\s*value\s*currency\s*\}\s*\}/;
    assert.match(SEARCH_QUERY, shape);
    assert.match(SKU_QUERY, shape);
  });

  it('reports one fixed product tax as the levy and its label', () => {
    const mapped = priced({ fixed_product_taxes: [{ label: 'NSW Levy', amount: { value: 1.1, currency: 'AUD' } }] });
    assert.equal(mapped.levy, 1.1);
    assert.deepEqual(mapped.levyLabels, ['NSW Levy']);
    assert.equal(mapped.levyStatus, LEVY_STATUS.published);
  });

  it('sums two fixed product tax entries into the levy', () => {
    const mapped = priced({
      fixed_product_taxes: [
        { label: 'NSW Levy', amount: { value: 1.65, currency: 'AUD' } },
        { label: 'NSW Levy', amount: { value: 1.64, currency: 'AUD' } },
      ],
    }, { value: 68.8, currency: 'AUD' });
    assert.equal(mapped.levy, 3.29);
    assert.deepEqual(mapped.levyLabels, ['NSW Levy', 'NSW Levy']);
    assert.equal(mapped.levyStatus, LEVY_STATUS.published);
  });

  it('keeps a published empty list apart from a levy it could not read', () => {
    // The whole point of the status: both carry `levy: null`, and prompt.md prices them oppositely.
    const empty = priced({ fixed_product_taxes: [] });
    assert.equal(empty.levy, null);
    assert.deepEqual(empty.levyLabels, []);
    assert.equal(empty.levyStatus, LEVY_STATUS.none, 'an empty published list is the site saying there is no levy');

    const unreadable = priced({ fixed_product_taxes: [{ label: 'NSW Levy', amount: { value: 'not-a-number', currency: 'AUD' } }] });
    assert.equal(unreadable.levy, null);
    assert.equal(unreadable.levyStatus, LEVY_STATUS.unreadable);
    assert.notEqual(empty.levyStatus, unreadable.levyStatus);
  });

  it('never guesses a levy from a malformed, missing, negative or part-readable list', () => {
    const malformed = priced({ fixed_product_taxes: [{ label: 'NSW Levy', amount: { value: 'not-a-number', currency: 'AUD' } }] });
    assert.equal(malformed.levy, null);
    assert.equal(malformed.levyStatus, LEVY_STATUS.unreadable);
    assert.deepEqual(malformed.levyLabels, ['NSW Levy'], 'the label it did read is still reported');

    const missingAmount = priced({ fixed_product_taxes: [{ label: 'NSW Levy' }] });
    assert.equal(missingAmount.levy, null);
    assert.equal(missingAmount.levyStatus, LEVY_STATUS.unreadable);
    assert.deepEqual(missingAmount.levyLabels, ['NSW Levy']);

    const negative = priced({ fixed_product_taxes: [{ label: 'NSW Levy', amount: { value: -1.1, currency: 'AUD' } }] });
    assert.equal(negative.levy, null);
    assert.equal(negative.levyStatus, LEVY_STATUS.unreadable);

    // A mixed list must not publish the part that read: 1.10 is not the levy, it is half of it.
    const mixed = priced({
      fixed_product_taxes: [
        { label: 'NSW Levy', amount: { value: 1.1, currency: 'AUD' } },
        { label: 'NSW Levy', amount: { value: null, currency: 'AUD' } },
      ],
    });
    assert.equal(mixed.levy, null);
    assert.equal(mixed.levyStatus, LEVY_STATUS.unreadable);
    assert.deepEqual(mixed.levyLabels, ['NSW Levy', 'NSW Levy']);
  });

  it('treats a tax list the answer did not carry as unreadable, never as no levy', () => {
    for (const missing of [{}, { fixed_product_taxes: null }, { fixed_product_taxes: 'NSW Levy' }]) {
      const mapped = priced(missing);
      assert.equal(mapped.levy, null);
      assert.deepEqual(mapped.levyLabels, []);
      assert.equal(mapped.levyStatus, LEVY_STATUS.unreadable, JSON.stringify(missing));
    }

    // The recorded fixtures were captured before the query asked for the field, so they are the
    // same silence: a product read from one is unpriceable, not levy-free.
    const recorded = mapProduct(fixture('search-bundaberg-page-1').data.products.items[0], SITE);
    assert.equal(recorded.levy, null);
    assert.equal(recorded.levyStatus, LEVY_STATUS.unreadable);
  });

  it('refuses a levy that is not in the price\'s own currency', () => {
    const otherCurrency = priced({ fixed_product_taxes: [{ label: 'NSW Levy', amount: { value: 1.1, currency: 'USD' } }] });
    assert.equal(otherCurrency.levy, null);
    assert.equal(otherCurrency.levyStatus, LEVY_STATUS.unreadable);

    const noTaxCurrency = priced({ fixed_product_taxes: [{ label: 'NSW Levy', amount: { value: 1.1 } }] });
    assert.equal(noTaxCurrency.levy, null);
    assert.equal(noTaxCurrency.levyStatus, LEVY_STATUS.unreadable);

    const noPriceCurrency = priced({ fixed_product_taxes: [{ label: 'NSW Levy', amount: { value: 1.1, currency: 'AUD' } }] }, { value: 22 });
    assert.equal(noPriceCurrency.currency, null);
    assert.equal(noPriceCurrency.levy, null);
    assert.equal(noPriceCurrency.levyStatus, LEVY_STATUS.unreadable);
  });

  // The script only reports the levy; prompt.md is what prices a row with it, so the rule that
  // keeps a base-only price out of a file lives there and is checked here against the same words.
  describe('the pricing rule prompt.md states', () => {
    const prompt = readFileSync(join(here, 'prompt.md'), 'utf8');
    const anchor = "A JB Metro row's price includes JB Metro's container deposit levy";
    const section = (() => {
      const start = prompt.indexOf(anchor);
      assert.ok(start > 0, 'prompt.md no longer has a JB Metro container deposit levy rule');
      const rest = prompt.slice(start);
      const end = rest.indexOf('\n- ');
      return end === -1 ? rest : rest.slice(0, end);
    })();

    /** The sub-bullet that tells the collector what to do with one `levyStatus`. */
    const ruleFor = (status) => {
      const marker = `\`"${status}"\``;
      const at = section.indexOf(marker);
      assert.ok(at >= 0, `prompt.md states no rule for a ${status} levy`);
      const rest = section.slice(at + marker.length);
      const end = rest.search(/\n {2}- /);
      return end === -1 ? rest : rest.slice(0, end);
    };

    it('states a rule for every levy status the script can report', () => {
      for (const status of Object.values(LEVY_STATUS)) assert.ok(ruleFor(status).length > 0, status);
    });

    it('adds the levy into both prices, and says so in the row, only when it was published', () => {
      const published = ruleFor(LEVY_STATUS.published).replace(/\s+/g, ' ');
      assert.match(published, /`packPrice` as `finalPrice` plus that `levy`/);
      assert.match(published, /`regularPackPrice` as `regularPrice` plus the same `levy`/);
      assert.match(published, /sourceReference/);
    });

    it('keeps today\'s price for a product the site publishes no levy for', () => {
      assert.match(ruleFor(LEVY_STATUS.none).replace(/\s+/g, ' '), /row exactly as today, with nothing added/);
    });

    it('keeps a base-only price out of the file when the levy could not be read', () => {
      // One line of prose per rule, reflowed freely, so every assertion tolerates a line break.
      const unreadable = ruleFor(LEVY_STATUS.unreadable).replace(/\s+/g, ' ');
      assert.match(unreadable, /do not write a row for it/);
      assert.match(unreadable, /`truncated` rather than `complete`/);
      assert.match(unreadable, /[Nn]ever fall back to `finalPrice`/);
      assert.match(unreadable, /never treat the levy as `0`/);
      assert.match(unreadable, /never `no_matches`/);
    });
  });
});

describe('search', () => {
  it('reads a single page and searches with the GraphQL products query', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('search-single-page'))]);
    const result = await searchProducts({ fetchImpl, sleep: async () => {}, siteUrl: SITE, searchText: 'bundaberg ginger beer' });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, `${SITE}/graphql`);
    assert.equal(calls[0].method, 'POST');
    assert.match(calls[0].query, /products\(\s*search:/);
    assert.doesNotMatch(calls[0].query, /catalogsearch/i);
    assert.equal(calls[0].variables.search, 'bundaberg ginger beer');
    assert.equal(calls[0].variables.currentPage, 1);
    assert.equal(result.totalCount, 1);
    assert.equal(result.pagesRead, 1);
    assert.equal(result.truncated, false);
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25']);
  });

  it('reports a search nothing matched without inventing a result', async () => {
    const { fetchImpl } = fakeFetch([ok(fixture('search-no-results'))]);
    const result = await searchProducts({ fetchImpl, sleep: async () => {}, siteUrl: SITE, searchText: 'no such thing' });

    assert.equal(result.totalCount, 0);
    assert.equal(result.products.length, 0);
    assert.equal(result.truncated, false);
  });

  it('reads every page of a multi-page search', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('search-bundaberg-page-1')), ok(fixture('search-bundaberg-page-2'))]);
    const result = await searchProducts({ fetchImpl, sleep: async () => {}, siteUrl: SITE, searchText: 'bundaberg', options: { pageSize: 2 } });

    assert.deepEqual(calls.map((call) => call.variables.currentPage), [1, 2]);
    assert.deepEqual(calls.map((call) => call.variables.pageSize), [2, 2]);
    assert.equal(result.totalCount, 3);
    assert.equal(result.pagesRead, 2);
    assert.equal(result.truncated, false);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25', 'CCC3', 'KB']);
  });

  it('stops at the page cap and says the results are truncated', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('search-bundaberg-page-1')), ok(fixture('search-bundaberg-page-2'))]);
    const result = await searchProducts({ fetchImpl, sleep: async () => {}, siteUrl: SITE, searchText: 'bundaberg', options: { pageSize: 2, maxPages: 1 } });

    assert.equal(calls.length, 1);
    assert.equal(result.totalCount, 3);
    assert.equal(result.pagesRead, 1);
    assert.equal(result.truncated, true);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25', 'CCC3']);
  });

  it('retries a GraphQL error returned with HTTP 200 and then reports it', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('graphql-errors-200'))]);
    const { sleep, waits } = fakeSleep();
    const result = await searchProducts({ fetchImpl, sleep, siteUrl: SITE, searchText: 'bundaberg', options: { attempts: 3, backoffMs: 100 } });

    assert.equal(calls.length, 3);
    assert.deepEqual(waits, [100, 200]);
    assert.equal(result.products.length, 0);
    assert.equal(result.truncated, true);
    assert.equal(result.errors.length, 1);
    assert.equal(result.errors[0].page, 1);
    assert.equal(result.errors[0].attempts, 3);
    assert.match(result.errors[0].message, /attribute_codes/);
  });

  it('retries a timeout and a 5xx, and succeeds when the next attempt answers', async () => {
    const { fetchImpl, calls } = fakeFetch([timeout(), serverError(503), ok(fixture('search-single-page'))]);
    const { sleep, waits } = fakeSleep();
    const result = await searchProducts({ fetchImpl, sleep, siteUrl: SITE, searchText: 'bundaberg', options: { attempts: 3, backoffMs: 100 } });

    assert.equal(calls.length, 3);
    assert.deepEqual(waits, [100, 200]);
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25']);
  });

  it('does not retry a refusal the site will repeat', async () => {
    const { fetchImpl, calls } = fakeFetch([{ ok: false, status: 403, json: async () => ({}), text: async () => 'Forbidden' }]);
    const result = await searchProducts({ fetchImpl, sleep: async () => {}, siteUrl: SITE, searchText: 'bundaberg', options: { attempts: 3 } });

    assert.equal(calls.length, 1);
    assert.equal(result.errors.length, 1);
    assert.match(result.errors[0].message, /403/);
  });

  it('keeps the pages it read when a later page fails', async () => {
    const { fetchImpl } = fakeFetch([ok(fixture('search-bundaberg-page-1')), serverError(500)]);
    const result = await searchProducts({ fetchImpl, sleep: async () => {}, siteUrl: SITE, searchText: 'bundaberg', options: { pageSize: 2, attempts: 2 } });

    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25', 'CCC3']);
    assert.equal(result.pagesRead, 1);
    assert.equal(result.truncated, true);
    assert.equal(result.errors[0].page, 2);
  });
});

describe('SKU lookup', () => {
  it('queries a batch by SKU filter and reports the same fields', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('sku-batch'))]);
    const result = await lookupSkus({ fetchImpl, sleep: async () => {}, siteUrl: SITE, skus: ['BGB25', 'KB'] });

    assert.equal(calls.length, 1);
    assert.match(calls[0].query, /filter:\s*\{\s*sku:\s*\{\s*in:\s*\$skus/);
    assert.doesNotMatch(calls[0].query, /catalogsearch/i);
    assert.deepEqual(calls[0].variables.skus, ['BGB25', 'KB']);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25', 'KB']);
    assert.equal(result.products[0].packQuantity, 24);
    assert.equal(result.products[1].packQuantity, 30);
    assert.deepEqual(result.notFound, []);
    assert.deepEqual(result.errors, []);
  });

  it('reports a SKU the site did not return as not found', async () => {
    const { fetchImpl } = fakeFetch([ok(fixture('sku-batch'))]);
    const result = await lookupSkus({ fetchImpl, sleep: async () => {}, siteUrl: SITE, skus: ['BGB25', 'KB', 'CCC3'] });

    // The batch was read to its end, so the site did answer for CCC3: that is a confirmed absence.
    assert.equal(result.truncated, false);
    assert.deepEqual(result.notFound, ['CCC3']);
    assert.deepEqual(result.unresolved, []);
    assert.deepEqual(result.errors, []);
  });

  it('reads every page of a batch the site answered over two pages', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('sku-batch-page-1')), ok(fixture('sku-batch-page-2'))]);
    const result = await lookupSkus({ fetchImpl, sleep: async () => {}, siteUrl: SITE, skus: ['BGB25', 'KB'] });

    assert.deepEqual(calls.map((call) => call.variables.currentPage), [1, 2]);
    assert.equal(result.truncated, false);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25', 'KB']);
    assert.deepEqual(result.notFound, []);
    assert.deepEqual(result.unresolved, []);
  });

  it('does not call a SKU missing when the page cap stopped its batch before the site had answered', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('sku-batch-page-1')), ok(fixture('sku-batch-page-2'))]);
    const result = await lookupSkus({
      fetchImpl,
      sleep: async () => {},
      siteUrl: SITE,
      skus: ['BGB25', 'KB'],
      options: { maxPages: 1 },
    });

    assert.equal(calls.length, 1);
    assert.equal(result.truncated, true);
    // What was read is kept; KB is unresolved, because it may be on the page the cap stopped.
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25']);
    assert.deepEqual(result.unresolved, ['KB']);
    assert.deepEqual(result.notFound, []);
    assert.deepEqual(result.errors, []);
  });

  it('splits the SKUs into bounded batches and keeps going after one fails', async () => {
    const { fetchImpl, calls } = fakeFetch([serverError(500), serverError(500), ok(fixture('sku-batch'))]);
    const result = await lookupSkus({
      fetchImpl,
      sleep: async () => {},
      siteUrl: SITE,
      skus: ['CCC3', 'BGB25', 'KB'],
      options: { batchSize: 1, attempts: 2 },
    });

    assert.deepEqual(calls.map((call) => call.variables.skus), [['CCC3'], ['CCC3'], ['BGB25'], ['KB']]);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25', 'KB']);
    assert.equal(result.errors.length, 1);
    assert.deepEqual(result.errors[0].skus, ['CCC3']);
    // A SKU whose batch failed was never answered for, so it is unresolved and not a "not found".
    assert.deepEqual(result.notFound, []);
    assert.deepEqual(result.unresolved, ['CCC3']);
  });
});

describe('runLookup', () => {
  it('answers the search result and exits zero', async () => {
    const { fetchImpl } = fakeFetch([ok(fixture('search-single-page'))]);
    const { exitCode, result } = await runLookup({
      argv: ['--search', 'bundaberg ginger beer', SITE],
      env: {},
      fetchImpl,
      sleep: async () => {},
    });

    assert.equal(exitCode, 0);
    assert.equal(result.mode, 'search');
    assert.equal(result.site, SITE);
    assert.equal(result.search, 'bundaberg ginger beer');
    assert.equal(result.truncated, false);
    assert.equal(result.products.length, 1);
  });

  it('exits non-zero when something was reported but still answers what it read', async () => {
    const { fetchImpl } = fakeFetch([ok(fixture('search-bundaberg-page-1')), serverError(500)]);
    const { exitCode, result } = await runLookup({
      argv: ['--search', 'bundaberg', SITE, '--page-size', '2', '--attempts', '1'],
      env: {},
      fetchImpl,
      sleep: async () => {},
    });

    assert.equal(exitCode, 1);
    assert.equal(result.truncated, true);
    assert.equal(result.products.length, 2);
    assert.equal(result.errors.length, 1);
  });

  it('refuses a host that is not one of the configured JB Metro sites, without calling anything', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('search-single-page'))]);
    const { exitCode, result } = await runLookup({
      argv: ['--search', 'bundaberg', 'https://www.coles.com.au'],
      env: {},
      fetchImpl,
      sleep: async () => {},
    });

    assert.equal(exitCode, 2);
    assert.equal(calls.length, 0);
    assert.match(result.error, /not a JB Metro site/i);
  });

  it('accepts a site the work list passed in', async () => {
    const { fetchImpl, calls } = fakeFetch([ok(fixture('sku-batch'))]);
    const { exitCode, result } = await runLookup({
      argv: ['https://www.jbmetro-nt.com.au', 'BGB25', 'KB'],
      env: { JBMETRO_SITE_URLS: 'https://www.jbmetro-nt.com.au' },
      fetchImpl,
      sleep: async () => {},
    });

    assert.equal(exitCode, 0);
    assert.equal(calls[0].url, 'https://www.jbmetro-nt.com.au/graphql');
    assert.deepEqual(result.notFound, []);
    assert.deepEqual(result.unresolved, []);
  });

  it('answers a SKU the page cap left unresolved separately from the ones it confirmed missing', async () => {
    const { fetchImpl } = fakeFetch([ok(fixture('sku-batch-page-1')), ok(fixture('sku-batch-page-2'))]);
    const { exitCode, result } = await runLookup({
      argv: [SITE, 'BGB25', 'KB', '--max-pages', '1'],
      env: {},
      fetchImpl,
      sleep: async () => {},
    });

    assert.equal(exitCode, 0);
    assert.equal(result.mode, 'sku');
    assert.equal(result.truncated, true);
    assert.deepEqual(result.products.map((p) => p.sku), ['BGB25']);
    assert.deepEqual(result.unresolved, ['KB']);
    assert.deepEqual(result.notFound, []);
  });
});
