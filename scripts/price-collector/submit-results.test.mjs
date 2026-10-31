// Tests for scripts/price-collector/submit-results.mjs: the submission the **workflow** runs after
// Claude has finished or stopped (issue #926, parent #817), against the stubbed API of
// ./stub-server.mjs and never a live one.
//
// What they hold it to is the submission order and the per-work-item independence the issue asks
// for: match files before result files, every file validated on its own so one bad file loses only
// itself, rows in batches of at most 200, a work item's outcome sent once after **all** of its
// batches succeeded and never after a failed one, the run completed once at the end so unanswered
// searches become `not_searched`, and the token never printed.
import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, it } from 'node:test';
import { API_BASE_URL_ENV, API_TOKEN_ENV } from './collector-api.mjs';
import { startStubServer } from './stub-server.mjs';
import { runSubmission } from './submit-results.mjs';

const MATCHES = 'POST /api/price-collector/matches';
const RESULTS = 'POST /api/price-collector/results';
const COMPLETE = 'POST /api/price-collector/runs/41/complete';
const outcomeRoute = (workItemId) => `POST /api/price-collector/work-items/${workItemId}/outcome`;
const TOKEN = 'collector-token-never-printed';
const RUN_ID = 41;

const rowResult = (workItemId, supplierProductId, reason = null) => ({
  workItemId,
  supplierProductId,
  outcome: 1,
  reason,
  listingId: 90,
  observationId: 500,
});

const resultsAnswer = (request) => ({
  status: 200,
  body: {
    runId: RUN_ID,
    accepted: request.body.rows.map((row) => rowResult(row.workItemId, row.supplierProductId)),
    sentToReview: [],
    pending: [],
    rejected: [],
  },
});

const matchesAnswer = (request) => ({
  status: 200,
  body: {
    runId: RUN_ID,
    autoMatched: request.body.proposals.map((proposal) => ({
      waitingKey: proposal.waitingKey,
      outcome: 1,
      reason: null,
      masterProductId: proposal.masterProductId ?? null,
      suggestionId: 700,
    })),
    tentative: [],
    rejected: [],
  },
});

const outcomeAnswer = (request) => ({
  status: 200,
  body: {
    workItemId: 0,
    outcome: request.body.outcome,
    failureReason: request.body.failureReason ?? null,
    submittedRowCount: request.body.submittedRowCount,
    recordedAtUtc: '2026-10-30T03:00:00Z',
    listingsRecordedNotFound: 0,
  },
});

const completionAnswer = {
  status: 200,
  body: {
    runId: RUN_ID,
    status: 3,
    completedAtUtc: '2026-10-30T03:05:00Z',
    completeSearchCount: 1,
    truncatedSearchCount: 0,
    noMatchesSearchCount: 0,
    failedSearchCount: 0,
    notSearchedCount: 2,
    listingsCheckedCount: 1,
    observationsRecordedCount: 1,
    failureCount: 0,
  },
};

const happyRoutes = {
  [MATCHES]: matchesAnswer,
  [RESULTS]: resultsAnswer,
  'POST *': outcomeAnswer,
  [COMPLETE]: completionAnswer,
};

const row = (supplierProductId, overrides = {}) => ({
  supplierProductId,
  packPrice: 24.5,
  currency: 'AUD',
  priceBasis: 'inclusive_of_gst',
  stockStatus: 'in_stock',
  observedAtUtc: '2026-10-30T02:15:00Z',
  ...overrides,
});

const resultFile = (workItemId, overrides = {}) => ({
  runId: RUN_ID,
  workItemId,
  supplierPresetId: 3,
  outcome: 'complete',
  rows: [row(`coles:${workItemId}`)],
  ...overrides,
});

const matchFileBody = (waitingKey, overrides = {}) => ({
  runId: RUN_ID,
  waitingKey,
  masterProductId: 12,
  confidence: 'high',
  reason: 'Brand, variant and size agree and only one master product fits.',
  ...overrides,
});

let stub = null;
let dir = null;

afterEach(async () => {
  await stub?.close();
  stub = null;
  if (dir !== null) await rm(dir, { recursive: true, force: true });
  dir = null;
});

async function outputDir(files) {
  dir = await mkdtemp(join(tmpdir(), 'price-collector-out-'));
  for (const [name, content] of Object.entries(files)) {
    await writeFile(join(dir, name), typeof content === 'string' ? content : JSON.stringify(content), 'utf8');
  }
  return dir;
}

const submit = (extraArgv = []) =>
  runSubmission({
    argv: ['--dir', dir, ...extraArgv],
    env: { [API_BASE_URL_ENV]: stub.baseUrl, [API_TOKEN_ENV]: TOKEN },
  });

describe('what it submits, and in which order', () => {
  it('posts the match files before the result files, and completes the run last', async () => {
    await outputDir({
      '7001.json': resultFile(7001),
      'match-bundaberg.json': matchFileBody('bundaberg ginger beer 375ml|bottle'),
    });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    assert.equal(answer.exitCode, 0, answer.err);
    assert.deepEqual(
      stub.requests.map((request) => request.key),
      [MATCHES, RESULTS, outcomeRoute(7001), COMPLETE],
    );
  });

  it('stamps every row with the work-item id of the file it came from', async () => {
    await outputDir({ '7001.json': resultFile(7001, { rows: [row('coles:1'), row('coles:2')] }) });
    stub = await startStubServer(happyRoutes);

    await submit();

    const [batch] = stub.bodiesFor(RESULTS);
    assert.equal(batch.runId, RUN_ID);
    assert.deepEqual(
      batch.rows.map((posted) => [posted.workItemId, posted.supplierPresetId, posted.supplierProductId]),
      [
        [7001, 3, 'coles:1'],
        [7001, 3, 'coles:2'],
      ],
    );
  });

  it('sends every enum as the number the API declares', async () => {
    await outputDir({
      '7001.json': resultFile(7001, {
        rows: [
          row('coles:1', {
            priceBasis: 'exclusive_of_gst',
            stockStatus: 'out_of_stock',
            sizeValue: 375,
            sizeUnit: 'ml',
            barcode: '09300675024235',
            barcodeScope: 'carton',
          }),
        ],
      }),
      'match-one.json': matchFileBody('one', { confidence: 'medium' }),
    });
    stub = await startStubServer(happyRoutes);

    await submit();

    const [posted] = stub.bodiesFor(RESULTS)[0].rows;
    assert.equal(posted.priceBasis, 1);
    assert.equal(posted.stockStatus, 2);
    assert.equal(posted.sizeUnit, 1);
    assert.equal(posted.barcodeScope, 2);
    assert.equal(stub.bodiesFor(MATCHES)[0].proposals[0].confidence, 2);
  });

  it('reports each search outcome as its own declared number, with a reason only for a failure', async () => {
    await outputDir({
      '7001.json': resultFile(7001),
      '7002.json': resultFile(7002, { outcome: 'truncated' }),
      '7003.json': resultFile(7003, { outcome: 'no_matches', rows: [] }),
      '7004.json': resultFile(7004, { outcome: 'failed', failureReason: 'timeout', rows: [] }),
    });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    assert.equal(answer.exitCode, 0, answer.err);
    const sent = Object.fromEntries(
      [7001, 7002, 7003, 7004].map((workItemId) => [workItemId, stub.bodiesFor(outcomeRoute(workItemId))[0]]),
    );
    assert.deepEqual(sent[7001], { runId: RUN_ID, outcome: 1, failureReason: null, submittedRowCount: 1 });
    assert.equal(sent[7002].outcome, 2);
    assert.deepEqual(sent[7003], { runId: RUN_ID, outcome: 3, failureReason: null, submittedRowCount: 0 });
    assert.deepEqual(sent[7004], { runId: RUN_ID, outcome: 4, failureReason: 3, submittedRowCount: 0 });
  });

  it('posts a search with no rows as its outcome alone', async () => {
    await outputDir({ '7003.json': resultFile(7003, { outcome: 'no_matches', rows: [] }) });
    stub = await startStubServer(happyRoutes);

    await submit();

    assert.deepEqual(
      stub.requests.map((request) => request.key),
      [outcomeRoute(7003), COMPLETE],
    );
  });
});

describe('batching', () => {
  it('splits a search of 250 rows into batches of 200 and 50, then sends one outcome', async () => {
    const rows = Array.from({ length: 250 }, (_, index) => row(`coles:${index}`));
    await outputDir({ '7001.json': resultFile(7001, { rows }) });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    assert.equal(answer.exitCode, 0, answer.err);
    assert.deepEqual(
      stub.bodiesFor(RESULTS).map((batch) => batch.rows.length),
      [200, 50],
    );
    const outcomes = stub.bodiesFor(outcomeRoute(7001));
    assert.equal(outcomes.length, 1);
    assert.equal(outcomes[0].submittedRowCount, 250);
    assert.equal(stub.requests.at(-1).key, COMPLETE);
    assert.equal(stub.requests.at(-2).key, outcomeRoute(7001));
    // Once, at the end: a second completion would be refused, and a missing one leaves the run open.
    assert.equal(stub.bodiesFor(COMPLETE).length, 1);
  });

  it('splits more than 200 proposals into batches too', async () => {
    const files = Object.fromEntries(
      Array.from({ length: 201 }, (_, index) => [`match-${index}.json`, matchFileBody(`key-${index}`)]),
    );
    await outputDir(files);
    stub = await startStubServer(happyRoutes);

    await submit();

    assert.deepEqual(
      stub.bodiesFor(MATCHES).map((batch) => batch.proposals.length),
      [200, 1],
    );
  });

  it('refuses a batch size above the 200 rows one request may carry', async () => {
    await outputDir({ '7001.json': resultFile(7001) });
    stub = await startStubServer(happyRoutes);

    const answer = await submit(['--batch-size', '500']);

    assert.equal(answer.exitCode, 2);
    assert.equal(stub.requests.length, 0);
  });
});

describe('when a batch fails', () => {
  it('leaves that work item without an outcome and still submits the others', async () => {
    await outputDir({ '7001.json': resultFile(7001), '7002.json': resultFile(7002) });
    stub = await startStubServer({
      ...happyRoutes,
      [RESULTS]: (request) =>
        request.body.rows[0].workItemId === 7001 ? { status: 500, body: {} } : resultsAnswer(request),
    });

    const answer = await submit();

    assert.equal(answer.exitCode, 1);
    assert.equal(stub.bodiesFor(outcomeRoute(7001)).length, 0);
    assert.equal(stub.bodiesFor(outcomeRoute(7002)).length, 1);
    assert.equal(stub.requests.at(-1).key, COMPLETE);
  });

  it('leaves the work item without an outcome when only its second batch fails', async () => {
    const rows = Array.from({ length: 250 }, (_, index) => row(`coles:${index}`));
    await outputDir({ '7001.json': resultFile(7001, { rows }) });
    stub = await startStubServer({
      ...happyRoutes,
      [RESULTS]: (request, callIndex) => (callIndex === 1 ? { status: 409, body: {} } : resultsAnswer(request)),
    });

    const answer = await submit();

    assert.equal(answer.exitCode, 1);
    assert.equal(stub.bodiesFor(outcomeRoute(7001)).length, 0);
    assert.match(answer.out, /without an outcome/i);
  });

  it('reports a refused outcome without stopping the run completion', async () => {
    await outputDir({ '7001.json': resultFile(7001) });
    stub = await startStubServer({ ...happyRoutes, 'POST *': { status: 409, body: {} } });

    const answer = await submit();

    assert.equal(answer.exitCode, 1);
    assert.equal(stub.requests.at(-1).key, COMPLETE);
  });

  it('fails closed on 401 and still completes the run', async () => {
    await outputDir({ '7001.json': resultFile(7001) });
    stub = await startStubServer({ 'POST *': { status: 401, body: {} } });

    const answer = await submit();

    assert.equal(answer.exitCode, 1);
    assert.match(answer.err, /401/);
    assert.ok(!`${answer.out}${answer.err}`.includes(TOKEN));
    assert.equal(stub.requests.at(-1).key, COMPLETE);
  });
});

describe('one file at a time', () => {
  it('skips an unreadable, invalid or misnamed file and submits every other one', async () => {
    await outputDir({
      '7001.json': resultFile(7001),
      '7002.json': '{"runId": 41, "workItemId": 7002,',
      '7003.json': resultFile(7003, { outcome: 'complete', rows: [row('coles:3', { currency: 'NZD' })] }),
      '7004.json': resultFile(7004),
      'notes.txt': 'not a collector file',
    });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    assert.equal(answer.exitCode, 1);
    assert.deepEqual(
      stub.bodiesFor(RESULTS).map((batch) => batch.rows[0].workItemId),
      [7001, 7004],
    );
    assert.deepEqual(Object.keys(stub.requests.reduce((keys, request) => ({ ...keys, [request.key]: true }), {})), [
      RESULTS,
      outcomeRoute(7001),
      outcomeRoute(7004),
      COMPLETE,
    ]);
    assert.match(answer.out, /skipped 3/i);
    assert.match(answer.out, /7002\.json/);
    assert.match(answer.out, /7003\.json/);
    assert.match(answer.out, /notes\.txt/);
  });

  it('skips a result file whose run is not the run being submitted', async () => {
    await outputDir({ '7001.json': resultFile(7001), '7002.json': resultFile(7002, { runId: 99 }) });
    stub = await startStubServer(happyRoutes);

    const answer = await submit(['--run-id', String(RUN_ID)]);

    assert.equal(answer.exitCode, 1);
    assert.deepEqual(
      stub.bodiesFor(RESULTS).map((batch) => batch.rows[0].workItemId),
      [7001],
    );
  });

  it('skips an invalid match file and posts the valid ones', async () => {
    await outputDir({
      'match-one.json': matchFileBody('one'),
      'match-two.json': matchFileBody('two', { confidence: 'certain' }),
      'match-three.json': matchFileBody('three'),
    });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    assert.equal(answer.exitCode, 1);
    assert.deepEqual(stub.bodiesFor(MATCHES)[0].proposals.map((proposal) => proposal.waitingKey), ['one', 'three']);
  });

  it('completes the run even when every file was skipped', async () => {
    await outputDir({ '7001.json': '{' });
    stub = await startStubServer(happyRoutes);

    const answer = await submit(['--run-id', String(RUN_ID)]);

    assert.equal(answer.exitCode, 1);
    assert.deepEqual(stub.requests.map((request) => request.key), [COMPLETE]);
  });

  it('refuses to guess a run id when no file states one', async () => {
    await outputDir({ '7001.json': '{' });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    assert.equal(answer.exitCode, 2);
    assert.equal(stub.requests.length, 0);
    assert.match(answer.err, /run id/i);
  });

  it('refuses two run ids in one folder', async () => {
    await outputDir({ '7001.json': resultFile(7001), '7002.json': resultFile(7002, { runId: 42 }) });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    assert.equal(answer.exitCode, 2);
    assert.equal(stub.requests.length, 0);
    assert.match(answer.err, /run id/i);
  });
});

describe('what it prints', () => {
  it('prints the per-outcome counts and the four row counts with their reasons', async () => {
    await outputDir({
      '7001.json': resultFile(7001, { rows: [row('coles:1'), row('coles:2'), row('coles:3'), row('coles:4')] }),
      'match-one.json': matchFileBody('one'),
    });
    stub = await startStubServer({
      ...happyRoutes,
      [MATCHES]: () => ({
        status: 200,
        body: {
          runId: RUN_ID,
          autoMatched: [],
          tentative: [{ waitingKey: 'one', outcome: 2, reason: 'two suitable candidates', masterProductId: 12, suggestionId: 1 }],
          rejected: [],
        },
      }),
      [RESULTS]: () => ({
        status: 200,
        body: {
          runId: RUN_ID,
          accepted: [rowResult(7001, 'coles:1')],
          sentToReview: [rowResult(7001, 'coles:2', 'price outside the band')],
          pending: [rowResult(7001, 'coles:3', 'listing is not confirmed')],
          rejected: [rowResult(7001, 'coles:4', 'listing belongs to another product')],
        },
      }),
    });

    const answer = await submit();

    assert.equal(answer.exitCode, 0, answer.err);
    assert.match(answer.out, /accepted 1/);
    assert.match(answer.out, /sent to review 1/);
    assert.match(answer.out, /pending 1/);
    assert.match(answer.out, /rejected 1/);
    assert.match(answer.out, /price outside the band/);
    assert.match(answer.out, /listing belongs to another product/);
    assert.match(answer.out, /auto-matched 0/);
    assert.match(answer.out, /tentative 1/);
    assert.match(answer.out, /two suitable candidates/);
    assert.match(answer.out, /complete 1/);
    assert.match(answer.out, /not_searched 2/);
  });

  it('prints no price, product name or supplier product id', async () => {
    await outputDir({
      '7001.json': resultFile(7001, {
        rows: [row('coles:5216969', { title: 'Coca-Cola Classic Cans 24x375mL', packPrice: 24.5 })],
      }),
    });
    stub = await startStubServer(happyRoutes);

    const answer = await submit();

    const printed = `${answer.out}${answer.err}`;
    assert.ok(!printed.includes('24.5'));
    assert.ok(!printed.includes('Coca-Cola'));
    assert.ok(!printed.includes('coles:5216969'));
    assert.ok(!printed.includes(TOKEN));
    assert.ok(!printed.includes(stub.baseUrl));
  });
});

describe('the invocation itself', () => {
  it('refuses to run with no base URL or token, and calls nothing', async () => {
    await outputDir({ '7001.json': resultFile(7001) });
    stub = await startStubServer(happyRoutes);

    const answer = await runSubmission({ argv: ['--dir', dir], env: { [API_TOKEN_ENV]: TOKEN } });

    assert.equal(answer.exitCode, 2);
    assert.equal(stub.requests.length, 0);
    assert.match(answer.err, new RegExp(API_BASE_URL_ENV));
  });

  it('refuses a folder that does not exist', async () => {
    stub = await startStubServer(happyRoutes);

    const answer = await runSubmission({
      argv: ['--dir', join(tmpdir(), 'price-collector-out-missing-folder')],
      env: { [API_BASE_URL_ENV]: stub.baseUrl, [API_TOKEN_ENV]: TOKEN },
    });

    assert.equal(answer.exitCode, 2);
    assert.equal(stub.requests.length, 0);
  });
});
