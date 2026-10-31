// Tests for scripts/price-collector/get-work.mjs: the one call a collector run starts with
// (issue #926, parent #817), against the stubbed API of ./stub-server.mjs and never a live one.
//
// What they hold it to: the work-item id of every product-and-supplier search is printed, the base
// URL and token come from the environment and are never printed, and a non-2xx answer fails closed
// with nothing printed as if it were work.
import assert from 'node:assert/strict';
import { afterEach, describe, it } from 'node:test';
import { API_BASE_URL_ENV, API_TOKEN_ENV } from './collector-api.mjs';
import { runGetWork, summariseWorkList } from './get-work.mjs';
import { startStubServer } from './stub-server.mjs';

const WORK_ROUTE = 'GET /api/price-collector/work';
const TOKEN = 'collector-token-never-printed';

const workList = (overrides = {}) => ({
  runId: 41,
  issuedAtUtc: '2026-10-30T02:00:00Z',
  expiresAtUtc: '2026-10-30T08:00:00Z',
  suppliers: [
    {
      supplierPresetId: 3,
      name: 'Coles',
      priceConnector: 1,
      priceConnectorKey: 'coles',
      siteUrl: 'https://www.coles.com.au',
      pricesIncludeGst: true,
    },
    {
      supplierPresetId: 4,
      name: 'Kmart',
      priceConnector: 2,
      priceConnectorKey: null,
      siteUrl: 'https://www.kmart.com.au',
      pricesIncludeGst: true,
    },
  ],
  matchedProducts: [
    {
      masterProductId: 12,
      brand: 'Coca-Cola',
      name: 'Classic',
      sizeValue: 375,
      sizeUnit: 1,
      knownListings: [
        {
          listingId: 90,
          supplierPresetId: 3,
          supplierProductId: 'coles:5216969',
          url: 'https://www.coles.com.au/product/5216969',
          title: 'Coca-Cola Classic Cans 24x375mL',
          unitsPerPack: 24,
          barcode: '09300675024235',
          barcodeScope: 2,
          lastCheckedAtUtc: '2026-10-29T02:10:00Z',
        },
        {
          listingId: 91,
          supplierPresetId: 4,
          supplierProductId: 'kmart:42991',
          url: null,
          title: null,
          unitsPerPack: null,
          barcode: null,
          barcodeScope: null,
          lastCheckedAtUtc: null,
        },
      ],
      knownListingsOmitted: false,
      workItems: [
        { workItemId: 7001, supplierPresetId: 3 },
        { workItemId: 7002, supplierPresetId: 4 },
      ],
    },
  ],
  waitingProducts: [
    {
      waitingKey: 'bundaberg ginger beer 375ml|bottle',
      searchName: 'Bundaberg Ginger Beer 375mL',
      unitLabel: 'bottle',
      proposedListings: [],
      proposedListingsOmitted: false,
      refusedMasterProductIds: [5],
      matchingSuppressed: false,
      workItems: [{ workItemId: 7003, supplierPresetId: 3 }],
    },
  ],
  masterProductsForMatching: [{ id: 12, brand: 'Coca-Cola', name: 'Classic', sizeValue: 375, sizeUnit: 1 }],
  workItemLimitReached: false,
  ...overrides,
});

let stub = null;

afterEach(async () => {
  await stub?.close();
  stub = null;
});

const envFor = (baseUrl, token = TOKEN) => ({ [API_BASE_URL_ENV]: baseUrl, [API_TOKEN_ENV]: token });

describe('the work list it prints', () => {
  it('prints every product-and-supplier search with its work-item id', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: workList() } });

    const answer = await runGetWork({ env: envFor(stub.baseUrl), argv: [] });

    assert.equal(answer.exitCode, 0, answer.err);
    const printed = JSON.parse(answer.out);
    assert.equal(printed.runId, 41);
    assert.deepEqual(
      printed.searches.map((search) => [search.workItemId, search.supplierName]),
      [
        [7001, 'Coles'],
        [7002, 'Kmart'],
        [7003, 'Coles'],
      ],
    );
    assert.equal(printed.searchCount, 3);
  });

  it('describes a matched search by its master product and a waiting one by its own name', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: workList() } });

    const printed = JSON.parse((await runGetWork({ env: envFor(stub.baseUrl), argv: [] })).out);

    const matched = printed.searches.find((search) => search.workItemId === 7001);
    assert.deepEqual(matched.product, {
      kind: 'master',
      masterProductId: 12,
      brand: 'Coca-Cola',
      name: 'Classic',
      sizeValue: 375,
      sizeUnit: 'ml',
    });
    const waiting = printed.searches.find((search) => search.workItemId === 7003);
    assert.equal(waiting.product.kind, 'waiting');
    assert.equal(waiting.product.waitingKey, 'bundaberg ginger beer 375ml|bottle');
    assert.equal(waiting.product.searchName, 'Bundaberg Ginger Beer 375mL');
    assert.equal(waiting.product.unitLabel, 'bottle');
  });

  it('names the route each search is taken on', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: workList() } });

    const printed = JSON.parse((await runGetWork({ env: envFor(stub.baseUrl), argv: [] })).out);

    const coles = printed.searches.find((search) => search.workItemId === 7001);
    assert.equal(coles.priceConnector, 'varnmill');
    assert.equal(coles.priceConnectorKey, 'coles');
    assert.equal(coles.pricesIncludeGst, true);
    assert.equal(printed.searches.find((search) => search.workItemId === 7002).priceConnector, 'kmart');
  });

  it('gives a search only the known listings of its own supplier', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: workList() } });

    const printed = JSON.parse((await runGetWork({ env: envFor(stub.baseUrl), argv: [] })).out);

    assert.deepEqual(
      printed.searches.find((search) => search.workItemId === 7001).knownListings.map((listing) => listing.supplierProductId),
      ['coles:5216969'],
    );
    assert.deepEqual(
      printed.searches.find((search) => search.workItemId === 7002).knownListings.map((listing) => listing.supplierProductId),
      ['kmart:42991'],
    );
  });

  it('keeps the omission and suppression flags a bound states itself with', () => {
    const summary = summariseWorkList(
      workList({
        workItemLimitReached: true,
        matchedProducts: [
          {
            ...workList().matchedProducts[0],
            knownListings: [],
            knownListingsOmitted: true,
          },
        ],
        waitingProducts: [
          {
            ...workList().waitingProducts[0],
            refusedMasterProductIds: [],
            matchingSuppressed: true,
          },
        ],
      }),
    );

    assert.equal(summary.workItemLimitReached, true);
    assert.equal(summary.searches.find((search) => search.workItemId === 7001).knownListingsOmitted, true);
    assert.equal(summary.matching.waitingProducts[0].matchingSuppressed, true);
  });

  it('answers the matching step with the waiting products and the master products to match against', () => {
    const summary = summariseWorkList(workList());

    assert.equal(summary.matching.required, true);
    assert.deepEqual(summary.matching.waitingProducts.map((product) => product.refusedMasterProductIds), [[5]]);
    assert.deepEqual(summary.matching.masterProducts, [
      { masterProductId: 12, brand: 'Coca-Cola', name: 'Classic', sizeValue: 375, sizeUnit: 'ml' },
    ]);
  });

  it('says no matching is needed when nothing is waiting', () => {
    const summary = summariseWorkList(workList({ waitingProducts: [], masterProductsForMatching: [] }));

    assert.equal(summary.matching.required, false);
    assert.deepEqual(summary.matching.masterProducts, []);
  });

  it('prints the answer verbatim with --raw', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: workList() } });

    const answer = await runGetWork({ env: envFor(stub.baseUrl), argv: ['--raw'] });

    assert.deepEqual(JSON.parse(answer.out), workList());
  });
});

describe('how it authenticates', () => {
  it('sends the configured token as a bearer header and nothing else of itself', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: workList() } });

    await runGetWork({ env: envFor(stub.baseUrl), argv: [] });

    assert.equal(stub.requests.length, 1);
    assert.equal(stub.requests[0].authorization, `Bearer ${TOKEN}`);
    assert.equal(stub.requests[0].method, 'GET');
    assert.equal(stub.requests[0].path, '/api/price-collector/work');
  });

  it('never prints the token or the base URL on success', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: workList() } });

    const answer = await runGetWork({ env: envFor(stub.baseUrl), argv: [] });

    assert.ok(!`${answer.out}${answer.err}`.includes(TOKEN));
    assert.ok(!`${answer.out}${answer.err}`.includes(stub.baseUrl));
  });

  it('refuses to run with no base URL or no token, naming the variable and not its value', async () => {
    const missingUrl = await runGetWork({ env: { [API_TOKEN_ENV]: TOKEN }, argv: [] });
    assert.equal(missingUrl.exitCode, 2);
    assert.equal(missingUrl.out, '');
    assert.match(missingUrl.err, new RegExp(API_BASE_URL_ENV));

    const missingToken = await runGetWork({ env: { [API_BASE_URL_ENV]: 'https://api.example.test' }, argv: [] });
    assert.equal(missingToken.exitCode, 2);
    assert.match(missingToken.err, new RegExp(API_TOKEN_ENV));
    assert.ok(!missingToken.err.includes(TOKEN));
  });

  it('refuses a base URL that is not an absolute http(s) URL', async () => {
    const answer = await runGetWork({ env: envFor('api.example.test'), argv: [] });

    assert.equal(answer.exitCode, 2);
    assert.match(answer.err, new RegExp(API_BASE_URL_ENV));
  });
});

describe('when the API refuses', () => {
  it('fails closed on 401 and prints no work', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 401, body: {} } });

    const answer = await runGetWork({ env: envFor(stub.baseUrl), argv: [] });

    assert.equal(answer.exitCode, 1);
    assert.equal(answer.out, '');
    assert.match(answer.err, /401/);
    assert.ok(!answer.err.includes(TOKEN));
  });

  it('fails closed on a 4xx and on a 5xx', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 403, body: { detail: 'no role' } } });
    const refused = await runGetWork({ env: envFor(stub.baseUrl), argv: [] });
    assert.equal(refused.exitCode, 1);
    assert.equal(refused.out, '');
    await stub.close();

    stub = await startStubServer({ [WORK_ROUTE]: { status: 503, body: {} } });
    const unavailable = await runGetWork({ env: envFor(stub.baseUrl), argv: [] });
    assert.equal(unavailable.exitCode, 1);
    assert.equal(unavailable.out, '');
  });

  it('fails closed when nothing answers at all', async () => {
    const answer = await runGetWork({ env: envFor('http://127.0.0.1:1'), argv: [] });

    assert.equal(answer.exitCode, 1);
    assert.equal(answer.out, '');
    assert.ok(!answer.err.includes(TOKEN));
  });

  it('fails closed when the answer is not a work list', async () => {
    stub = await startStubServer({ [WORK_ROUTE]: { status: 200, body: { runId: 0 } } });

    const answer = await runGetWork({ env: envFor(stub.baseUrl), argv: [] });

    assert.equal(answer.exitCode, 1);
    assert.equal(answer.out, '');
  });
});
