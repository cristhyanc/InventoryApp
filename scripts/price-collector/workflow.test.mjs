import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { sampleWork, sampleLimits, fullRunLimits, fullRunWork, fullRunRequested, workBatches, collectBatches, FULL_RUN, collectorEnvironment, collectorDeadlineMinutes, collectorTarget, scheduleRequested, runWasIssued, NOTHING_DUE, productionApiOrigin, finaliseSample, launchClaude, toolUseReport, searchDetails, supplierCounts, issuedCountsBySupplier, toolTimeline, claudeResult } from './workflow.mjs';
import { JB_METRO_CAPS, failureCode, jbMetroFlags, permittedCommand, recordDecision, toolDecision, toolLabel, TOOL_LOG_FILE } from './workflow-tool-guard.mjs';
import { LEDGER_FILE, MAX_POLLS_PER_RUN, leadingJsonObject, runFromResponse } from './workflow-run-ledger.mjs';
import { collectorToken } from './workflow-auth.mjs';
import { startStubServer } from './stub-server.mjs';
import { clip, redact, secretsOf, trace } from './trace.mjs';

// A result file submission accepts, for the write hook's tests.
const validResult = (workItemId, extra = {}) => ({ runId: 41, workItemId, supplierPresetId: 3, outcome: 'no_matches', rows: [], ...extra });

const work = { runId: 41, searches: [
  { workItemId: 1, product: { kind: 'waiting', waitingKey: 'a' } },
  { workItemId: 2, product: { kind: 'waiting', waitingKey: 'b' } }
], matching: { waitingProducts: [{ waitingKey: 'a' }, { waitingKey: 'b' }], masterProducts: [] } };

test('the test sample is bounded, keeps authoritative ids and only matching inputs for selected work', () => {
  const sample = sampleWork(work, 1);
  assert.equal(sample.runId, 41);
  assert.deepEqual(sample.searches.map(s => s.workItemId), [1]);
  assert.deepEqual(sample.matching.waitingProducts.map(p => p.waitingKey), ['a']);
  for (const bad of [0, 11, NaN, 1.5]) assert.throws(() => sampleWork(work, bad));
});

test('sample limits retain disabled actors unless explicitly requested, cap calls/items/pages without dollars', () => {
  const source = JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url)));
  const limits = sampleLimits(source, false);
  assert.equal(limits.perRun.maxActorCalls, 12);
  assert.equal(limits.actors['abotapi/kmart-au-scraper'].actorIdConfirmed, false);
  assert.equal(sampleLimits(source, true).actors['abotapi/kmart-au-scraper'].actorIdConfirmed, true);
  assert.equal(source.actors['abotapi/kmart-au-scraper'].actorIdConfirmed, false);
  for (const actor of Object.values(limits.actors)) {
    assert.ok(actor.pageSize <= 5);
    assert.equal(actor.maxPages, 1);
    assert.equal(actor.maxTotalChargeUsd, undefined);
  }
});

test('Claude inherits neither collector nor GitHub/OIDC credentials or arbitrary environment configuration', () => {
  const env = collectorEnvironment({ PATH: '/bin', HOME: '/private', ANTHROPIC_API_KEY: 'claude-key', APIFY_TOKEN: 'apify-key',
    PRICE_COLLECTOR_API_TOKEN: 'api-key', ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'oidc', GITHUB_TOKEN: 'github',
    NODE_OPTIONS: '--import malicious.mjs', BASH_ENV: '/evil', PRICE_COLLECTOR_STATE_DIR: '/state' }, '/isolated');
  assert.equal(env.ANTHROPIC_API_KEY, 'claude-key');
  assert.equal(env.APIFY_TOKEN, 'apify-key');
  for (const key of ['PRICE_COLLECTOR_API_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'GITHUB_TOKEN', 'NODE_OPTIONS', 'BASH_ENV']) assert.equal(env[key], undefined);
  assert.equal(env.HOME, '/isolated');
  // The runner's PATH is not inherited: only Node's own directory and the system directories.
  assert.equal(env.PATH, [dirname(process.execPath), '/usr/bin', '/bin'].join(':'));
});

test('Claude is launched only by absolute path', async () => {
  for (const executable of [undefined, 'claude', 'node_modules/.bin/claude']) {
    await assert.rejects(launchClaude({ executable, sandbox: '/tmp', env: {} }), /absolute path/);
  }
});

test('the interruption test stops Claude after a chosen number of minutes, and a normal run gets 14', () => {
  assert.equal(collectorDeadlineMinutes(''), 14);
  assert.equal(collectorDeadlineMinutes(undefined), 14);
  assert.equal(collectorDeadlineMinutes('3'), 3);
  assert.equal(collectorDeadlineMinutes('14'), 14);
  for (const bad of ['0', '15', '1.5', '-1', 'ten', '03']) assert.throws(() => collectorDeadlineMinutes(bad), bad);
});

test('the JB Metro search words prompt.md recommends pass the guard, and the punctuated ones it warns about do not', () => {
  // Production run 41558756584: "Nobby's ..." and "M&M's ..." were refused, and one search was never retried.
  for (const words of ['Nobbys Peanuts', 'M M Peanut', 'Smiths Salt Vinegar', 'Cheezels']) {
    assert.equal(permittedCommand(`node scripts/price-collector/jbmetro-lookup.mjs --search "${words}" https://www.jbmetro.com.au`), true, words);
  }
  for (const words of ["Nobby's Peanuts", "M&M's Peanut"]) {
    assert.equal(permittedCommand(`node scripts/price-collector/jbmetro-lookup.mjs --search "${words}" https://www.jbmetro.com.au`), false, words);
  }
});

test('the prompt states the configured search terms contract (issue #1138)', () => {
  const prompt = readFileSync(new URL('./prompt.md', import.meta.url), 'utf8').replace(/\s+/g, ' ');
  // Retry on an empty search and on a non-empty search with no valid match, in order, never joined.
  assert.match(prompt, /Search the \*\*first phrase as written\*\*/);
  assert.match(prompt, /\*\*Search the next phrase\*\* when the first search returned \*\*no product at all\*\*, \*\*or\*\* returned products but \*\*none of them is a real match\*\*/);
  assert.match(prompt, /Never join two phrases into one search, never search the same phrase twice/);
  assert.match(prompt, /the configured phrases \*\*are\*\* the at-most-two wordings for this work item/);
  assert.match(prompt, /A search whose `searchTerms` is `null` is searched exactly as step 1 says/);
  // Paging outcomes are preserved across both searches.
  assert.match(prompt, /it is `configuredPhrasesOutcome` in `scripts\/price-collector\/pagination\.mjs`/);
  assert.match(prompt, /\*\*`truncated`\*\* when \*\*any\*\* search was cut short, even if a later one found matches/);
  assert.match(prompt, /is `truncated` \*\*and keeps the fallback's rows\*\*/);
  assert.match(prompt, /Its `paging` is the paging of the \*\*first search that was cut short\*\*/);
  assert.match(prompt, /\*\*`no_matches`\*\* only when \*\*every\*\* search was read to the end and none found a real match/);
  assert.match(prompt, /\*\*A failed search ends the work item\*\*: do not search the next phrase after it/);
  // The alias is matching evidence only and every other identity check still applies.
  assert.match(prompt, /They are \*\*never search words\*\*/);
  assert.match(prompt, /the \*\*brand\*\* must agree, the \*\*product type\*\* must agree .* the \*\*individual unit size\*\* must agree, and a title that names any \*\*other\*\* flavour or variant is still a different product/);
  assert.match(prompt, /An alias never applies to another product or another supplier/);
  assert.match(prompt, /Keep the supplier's own `title` and `variant` exactly as published/);
  // The offline Smith's example: the 45 g Plain is eligible, the 27 g and another 45 g flavour are not.
  assert.match(prompt, /`45G SMITH'S PLAIN` — \*\*keep\*\*/);
  assert.match(prompt, /`27G SMITH'S PLAIN` — \*\*reject\*\*: the individual unit size is 27 g/);
  assert.match(prompt, /`45G SMITH'S SALT & VINEGAR`, `45G SMITH'S CHEESE & ONION` — \*\*reject\*\*: a different flavour/);
});

test('the configured Smith\'s phrases pass the JB Metro guard as written', () => {
  for (const words of ['Smiths Plain', 'Smiths']) {
    assert.equal(permittedCommand(`node scripts/price-collector/jbmetro-lookup.mjs --search "${words}" https://www.jbmetro.com.au`), true, words);
  }
  // The configured-phrases outcome helper is a command the run may ask (prompt step 0).
  assert.equal(permittedCommand('node scripts/price-collector/pagination.mjs --configured-phrases truncated,0,1,1,true complete,1,1,1,false'), true);
});

test('tool policy refuses arbitrary shell, command substitution, redirects, extra tools and background commands', async () => {
  assert.equal(permittedCommand('node scripts/price-collector/read-work.mjs'), true);
  assert.equal(permittedCommand('node scripts/price-collector/jbmetro-lookup.mjs --search "Coke Classic" https://www.jbmetro.com.au'), true);
  for (const command of ['env', 'node -e "process.env"', 'node scripts/price-collector/get-work.mjs',
    'node scripts/price-collector/read-work.mjs; env', 'node scripts/price-collector/read-work.mjs > /tmp/out',
    'node scripts/price-collector/jbmetro-lookup.mjs --search "$(env)" https://www.jbmetro.com.au',
    'node scripts/price-collector/read-work.mjs\nenv']) assert.equal(permittedCommand(command), false, command);
  assert.equal((await toolDecision({ tool_name: 'Read', tool_input: { file_path: '/etc/passwd' } }, {})).hookSpecificOutput.permissionDecision, 'deny');
  assert.equal((await toolDecision({ tool_name: 'Bash', tool_input: { command: 'node scripts/price-collector/read-work.mjs', run_in_background: true } }, {})).hookSpecificOutput.permissionDecision, 'deny');
});

test('the run report shows the submission output, its errors and every search in full', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-details-'));
  try {
    const issued = { runId: 41, searches: [
      { workItemId: 1, supplierPresetId: 3, supplierName: 'ALDI', priceConnector: 'varnmill', priceConnectorKey: 'aldi',
        product: { kind: 'master', masterProductId: 7, brand: 'Coca-Cola', name: 'Zero Sugar', sizeValue: 375, sizeUnit: 'ml' } },
      { workItemId: 2, supplierPresetId: 4, supplierName: 'Coles', priceConnector: 'varnmill', product: { kind: 'waiting', searchName: 'CCs Tasty Cheese', unitLabel: '45g' } },
    ], matching: { waitingProducts: [], masterProducts: [] } };
    writeFileSync(join(dir, 'issued-work.json'), JSON.stringify(issued));
    writeFileSync(join(dir, 'collector-status.json'), JSON.stringify({ status: 'completed', costUsd: 0.2, turns: 5, subtype: 'success' }));
    const outputDir = join(dir, 'out');
    mkdirSync(outputDir);
    writeFileSync(join(outputDir, '1.json'), JSON.stringify({ runId: 41, workItemId: 1, outcome: 'complete', rows: [
      { supplierProductId: 'aldi-1', title: 'Coke Zero 18x375ml', packPrice: 20.99, packQuantity: 18, stockStatus: 'in_stock', sourceUrl: 'https://www.aldi.com.au/p/1' }] }));
    const decisions = [{ outcome: 'rejected', workItemId: 1, supplierProductId: 'aldi-1', reason: "price basis differs from the supplier's published basis" }];
    const { report, details } = await finaliseSample({ stateDir: dir, outputDir, env: {}, token: 't',
      submit: async () => ({ exitCode: 1, out: 'rows: accepted 0, sent to review 0, pending 0, rejected 1\n', err: 'HTTP 500 on /results\n', decisions }) });
    assert.match(report, /^rows: accepted 0, sent to review 0, pending 0, rejected 1$/m);
    assert.match(report, /^Submission errors:\nHTTP 500 on \/results$/m);
    assert.match(details, /1\. \*\*Coca-Cola Zero Sugar 375ml \(master product 7\)\*\* at \*\*ALDI\*\* \(varnmill:aldi\), work item 1/);
    assert.match(details, /Coke Zero 18x375ml \[aldi-1\]: \$20\.99 for 18, in_stock, https:\/\/www\.aldi\.com\.au\/p\/1 -> \*\*rejected - price basis differs/);
    assert.match(details, /CCs Tasty Cheese - 45g \(waiting product\)\*\* at \*\*Coles\*\*/);
    assert.match(details, /no result file written \(not_searched\)/);
    const saved = readFileSync(join(dir, 'logs', 'summary.md'), 'utf8');
    assert.ok(saved.includes('Coke Zero 18x375ml'));
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'logs', 'submission.json'), 'utf8')).decisions, decisions);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the tool timeline shows each call, its answer or failure, and each session, with secrets masked', () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-timeline-'));
  try {
    const env = { APIFY_TOKEN: 'apify_api_SECRETSECRET123' };
    trace(dir, { event: 'batch', session: 1, of: 2, workItemIds: [1, 2] });
    trace(dir, { event: 'call', tool: 'mcp__apify__call-actor', decision: 'allow', code: 'ok', input: '{"queries":["coke zero"],"token":"apify_api_SECRETSECRET123"}' }, env);
    trace(dir, { event: 'answer', tool: 'mcp__apify__call-actor', response: '{"runId":"abc"}' }, env);
    trace(dir, { event: 'failed', tool: 'mcp__apify__axlymxp--bigw-product-scraper', code: 'failed-input', error: 'Input is not valid' }, env);
    trace(dir, { event: 'session', session: 1, subtype: 'success', turns: 4, costUsd: 0.1, result: 'Wrote 2 files' }, env);
    const timeline = toolTimeline(dir);
    assert.match(timeline, /SESSION 1 of 2: work items 1, 2/);
    assert.match(timeline, /CALL mcp__apify__call-actor allow \{"queries":\["coke zero"\]/);
    assert.match(timeline, /-> mcp__apify__call-actor answered: \{"runId":"abc"\}/);
    assert.match(timeline, /FAILED \(failed-input\): Input is not valid/);
    assert.match(timeline, /SESSION 1 ended: success, 4 turns, \$0\.1; Claude said: Wrote 2 files/);
    assert.ok(!timeline.includes('SECRETSECRET'));
    assert.equal(toolTimeline(join(dir, 'none')), 'No tool calls were traced.\n');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the session result is read from the last result message of a stream-json transcript', () => {
  const stream = '{"type":"system","subtype":"init"}\n{"type":"assistant"}\n{"type":"result","subtype":"success","num_turns":3}\n';
  assert.equal(claudeResult(stream).num_turns, 3);
  assert.equal(claudeResult('{"type":"result","subtype":"error_max_turns"}').subtype, 'error_max_turns');
  assert.deepEqual(claudeResult('not json'), {});
});

test('OIDC exchange uses the API application scope, never a stored API password, and does not retry refusal', async () => {
  const requests = [];
  const env = { PRICE_COLLECTOR_TENANT_ID: '11111111-1111-1111-1111-111111111111', PRICE_COLLECTOR_CLIENT_ID: '22222222-2222-2222-2222-222222222222',
    PRICE_COLLECTOR_API_SCOPE: 'api://test-api/.default', ACTIONS_ID_TOKEN_REQUEST_URL: 'https://token.actions.githubusercontent.com/test?x=1', ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'github-oidc' };
  const token = await collectorToken(env, async (url, options) => {
    requests.push({ url: String(url), options });
    return new Response(JSON.stringify(requests.length === 1 ? { value: 'assertion' } : { access_token: 'api-token', expires_in: 3600 }), { status: 200 });
  });
  assert.equal(token, 'api-token');
  assert.match(requests[0].url, /audience=api%3A%2F%2FAzureADTokenExchange/);
  assert.equal(requests[1].options.body.get('scope'), env.PRICE_COLLECTOR_API_SCOPE);
  assert.equal(requests[1].options.body.get('client_assertion'), 'assertion');
  let calls = 0;
  await assert.rejects(collectorToken(env, async () => { calls++; return new Response('private error body', { status: 401 }); }), /HTTP 401/);
  assert.equal(calls, 1);
});

test('finalisation runs after a collector timeout even with no result files and shows submission errors', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-test-'));
  try {
    writeFileSync(join(dir, 'issued-work.json'), JSON.stringify(work));
    writeFileSync(join(dir, 'collector-status.json'), JSON.stringify({ status: 'timeout', costUsd: null }));
    const calls = [];
    const answer = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 'private-token', submit: async ({ argv }) => {
      calls.push(argv);
      return { exitCode: 0, out: 'run 41: completed; complete 0, truncated 0, no_matches 0, failed 0, not_searched 2\n', err: 'private' };
    } });
    assert.equal(calls.length, 1);
    assert.ok(calls[0].includes('41'));
    assert.ok(answer.report.includes('timeout'));
    assert.ok(answer.report.includes('not_searched 2'));
    // Submission errors are shown in full; the API token never is.
    assert.ok(answer.report.includes('Submission errors:\nprivate'));
    assert.ok(!answer.report.includes('private-token'));
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a real interrupted child leaves finished files for independent submission and completes unsearched work', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-interruption-'));
  const server = await startStubServer({
    'POST /api/price-collector/work-items/1/outcome': { status: 200, body: {} },
    'POST /api/price-collector/runs/41/complete': { status: 200, body: {
      status: 3, completeSearchCount: 0, truncatedSearchCount: 0, noMatchesSearchCount: 1, failedSearchCount: 0, notSearchedCount: 1
    } },
  });
  try {
    const sandbox = join(dir, 'sandbox');
    const outputDir = join(sandbox, 'price-collector-out');
    mkdirSync(join(sandbox, 'scripts/price-collector'), { recursive: true });
    mkdirSync(outputDir);
    writeFileSync(join(sandbox, 'scripts/price-collector/prompt.md'), 'test prompt');
    const fake = join(dir, 'fake-claude');
    writeFileSync(fake, `#!/usr/bin/env node\nconst fs = require('node:fs');
fs.writeFileSync('price-collector-out/1.json', JSON.stringify({runId:41,workItemId:1,supplierPresetId:3,outcome:'no_matches',rows:[]}));
console.error('secret transcript, product, price and token');
setInterval(() => {}, 1000);\n`, { mode: 0o700 });
    const status = await launchClaude({ executable: fake, sandbox, env: { PATH: process.env.PATH, PRICE_COLLECTOR_STATE_DIR: dir }, timeoutMs: 500 });
    assert.equal(status.status, 'timeout');
    saveTestWork(dir, status);
    // A malformed file, another work item, and a stale-run file cannot stop the valid one.
    writeFileSync(join(outputDir, '2.json'), '{');
    writeFileSync(join(outputDir, '99.json'), JSON.stringify({ runId: 41, workItemId: 99 }));
    writeFileSync(join(outputDir, 'match-a.json'), JSON.stringify({ runId: 40, waitingKey: 'a' }));
    const { report } = await finaliseSample({ stateDir: dir, outputDir, env: {}, apiUrl: server.baseUrl, token: 'test-token' });
    assert.deepEqual(server.requests.map(r => r.path), ['/api/price-collector/work-items/1/outcome', '/api/price-collector/runs/41/complete']);
    assert.ok(server.requests.every(r => r.authorization === 'Bearer test-token'));
    assert.ok(report.includes('not_searched 1'));
    assert.ok(report.includes('Files refused before submission: 3'));
    // The session's stderr is kept in the run's log folder for the artifact.
    assert.ok(readFileSync(join(dir, 'logs', 'claude-session-1.stderr.txt'), 'utf8').includes('secret transcript'));
  } finally { await server.close(); rmSync(dir, { recursive: true, force: true }); }
});

function saveTestWork(dir, status) {
  // The run's issued work (finalisation) and the current batch (read-work.mjs and the write guard).
  writeFileSync(join(dir, 'issued-work.json'), JSON.stringify(work));
  writeFileSync(join(dir, 'work.json'), JSON.stringify(work));
  writeFileSync(join(dir, 'collector-status.json'), JSON.stringify(status));
}

test('dedicated -- actor names are normalized, the call cap is enforced and writes outside the sample are refused', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-policy-'));
  try {
    const limits = sampleLimits(JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url))), true);
    limits.perRun.maxActorCalls = 1;
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(limits));
    saveTestWork(dir, {});
    const env = { PRICE_COLLECTOR_STATE_DIR: dir, PRICE_COLLECTOR_OUT_DIR: join(dir, 'out'), PRICE_COLLECTOR_GUARD_STATE: join(dir, 'state.json') };
    const event = { tool_name: 'mcp__apify__abotapi--kmart-au-scraper', tool_input: { searchTerms: ['test'] } };
    const first = await toolDecision(event, env);
    assert.equal(first.hookSpecificOutput.permissionDecision, 'allow');
    assert.equal(first.hookSpecificOutput.updatedInput.maxItems, 5);
    assert.equal(first.hookSpecificOutput.updatedInput.maxPages, 1);
    assert.equal((await toolDecision(event, env)).hookSpecificOutput.permissionDecision, 'deny');
    const write = (path, item) => toolDecision({ tool_name: 'Write', tool_input: { file_path: path,
      content: JSON.stringify(validResult(item)) } }, env);
    assert.equal((await write(join(dir, 'out/99.json'), 99)).hookSpecificOutput.permissionDecision, 'deny');
    assert.equal((await write(join(dir, 'outside/1.json'), 1)).hookSpecificOutput.permissionDecision, 'deny');
    assert.equal((await write(join(dir, 'out/1.json'), 1)).hookSpecificOutput.permissionDecision, 'allow');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a cancelled job (SIGINT) stops the Claude process group just as SIGTERM does', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-sigint-'));
  try {
    mkdirSync(join(dir, 'scripts/price-collector'), { recursive: true });
    writeFileSync(join(dir, 'scripts/price-collector/prompt.md'), 'test prompt');
    const fake = join(dir, 'fake-claude');
    writeFileSync(fake, '#!/usr/bin/env node\nsetInterval(() => {}, 1000);\n', { mode: 0o700 });
    const running = launchClaude({ executable: fake, sandbox: dir, env: { PATH: process.env.PATH }, timeoutMs: 60000 });
    setTimeout(() => process.emit('SIGINT'), 300);
    assert.equal((await running).status, 'interrupted');
    assert.equal(process.listenerCount('SIGINT'), 0);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('result reads are allowed only for runs this collector started, bounded, and not counted as actor calls', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-reads-'));
  try {
    const limits = sampleLimits(JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url))), false);
    limits.perRun.maxActorCalls = 1;
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(limits));
    saveTestWork(dir, {});
    const env = { PRICE_COLLECTOR_STATE_DIR: dir, PRICE_COLLECTOR_OUT_DIR: join(dir, 'out'), PRICE_COLLECTOR_GUARD_STATE: join(dir, 'state.json') };
    const ask = (tool, input) => toolDecision({ tool_name: `mcp__apify__${tool}`, tool_input: input }, env)
      .then(answer => answer.hookSpecificOutput);
    const after = (tool, response) => recordDecision({ hook_event_name: 'PostToolUse', tool_name: `mcp__apify__${tool}`, tool_response: response }, env);
    const runResponse = (runId, datasetId) => ({ structuredContent: { runId, status: 'RUNNING', storages: { datasets: { default: { id: datasetId } } } } });

    // Nothing is readable before this collector has started a run.
    assert.equal((await ask('get-dataset-items', { datasetId: 'Data1' })).permissionDecision, 'deny');
    assert.equal((await ask('get-actor-run', { runId: 'Run1' })).permissionDecision, 'deny');
    // A read tool's answer cannot register a run nobody started, and neither can an unrelated tool.
    await after('get-actor-run', runResponse('Run9', 'Data9'));
    await after('get-dataset-items', runResponse('Run8', 'Data8'));
    assert.equal((await ask('get-dataset-items', { datasetId: 'Data9' })).permissionDecision, 'deny');
    assert.equal((await ask('get-dataset-items', { datasetId: 'Data8' })).permissionDecision, 'deny');

    const call = await ask('call-actor', { actor: 'varnmill/au-supermarket-prices', input: { searchTerms: ['test'] } });
    assert.equal(call.permissionDecision, 'allow');
    // The MCP answer's first text block mirrors structuredContent; either form is recorded.
    await after('call-actor', { content: [{ type: 'text', text: JSON.stringify(runResponse('Run1', 'Data1').structuredContent) }] });
    // Text deeper in an answer (an actor's own output or tip) is never read as a run.
    await after('call-actor', { content: [{ type: 'text', text: 'not json' }, { type: 'text', text: JSON.stringify({ runId: 'Evil' }) }] });
    assert.equal((await ask('get-actor-run', { runId: 'Evil' })).permissionDecision, 'deny');

    const read = await ask('get-dataset-items', { datasetId: 'Data1', limit: 500 });
    assert.equal(read.permissionDecision, 'allow');
    assert.equal(read.updatedInput.limit, 5);
    assert.equal(read.updatedInput.offset, 0);
    assert.equal((await ask('get-dataset-items', { datasetId: 'Data1', limit: 2, offset: 3 })).updatedInput.limit, 2);
    assert.equal((await ask('get-dataset-items', { datasetId: 'Data1' })).permissionDecision, 'allow');
    assert.equal((await ask('get-dataset-items', { datasetId: 'Data1' })).permissionDecision, 'deny', 'reads per dataset are capped');
    for (let poll = 0; poll < MAX_POLLS_PER_RUN; poll += 1) {
      assert.equal((await ask('get-actor-run', { runId: 'Run1', waitSecs: 45 })).permissionDecision, 'allow');
    }
    assert.equal((await ask('get-actor-run', { runId: 'Run1' })).permissionDecision, 'deny', 'polling is bounded');
    assert.equal((await ask('abort-actor-run', { runId: 'Run1' })).permissionDecision, 'allow');
    assert.equal((await ask('abort-actor-run', { runId: 'Run2' })).permissionDecision, 'deny');

    for (const [tool, input] of [
      ['get-dataset-items', { datasetId: 'someone~named-dataset' }],
      ['get-dataset-items', {}],
      ['get-dataset-items', { datasetId: 'Data1', limit: 0 }],
      ['get-dataset-items', { datasetId: 'Data1', offset: -1 }],
      ['get-actor-run', { runId: '../runs' }],
      ['get-dataset-list', {}], ['get-key-value-store-record', { storeId: 'abc', recordKey: 'INPUT' }],
      ['search-actors', { keywords: 'scraper' }], ['create-schedule', {}],
    ]) assert.equal((await ask(tool, input)).permissionDecision, 'deny', tool);
    // None of the reads above used another actor call: the single allowed call is the only one.
    assert.equal((await ask('call-actor', { actor: 'varnmill/au-supermarket-prices', input: { searchTerms: ['x'] } })).permissionDecision, 'deny');
    const state = JSON.parse(readFileSync(join(dir, 'state.json'), 'utf8'));
    assert.equal(state.actorCalls, 1);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a dedicated actor tool records its run, and get-actor-run may add a dataset only to a recorded run', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-ledger-'));
  try {
    const limits = sampleLimits(JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url))), true);
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(limits));
    const env = { PRICE_COLLECTOR_STATE_DIR: dir };
    await recordDecision({ hook_event_name: 'PostToolUse', tool_name: 'mcp__apify__abotapi--kmart-au-scraper',
      tool_response: { structuredContent: { runId: 'KRun', storages: {} } } }, env);
    await recordDecision({ hook_event_name: 'PostToolUse', tool_name: 'mcp__apify__get-actor-run',
      tool_response: [{ type: 'text', text: JSON.stringify({ runId: 'KRun', storages: { datasets: { default: { id: 'KData' } } } }) }] }, env);
    assert.deepEqual(runFromResponse('{"runId":"KRun"}'), { runId: 'KRun', datasetIds: [] });
    const ledger = JSON.parse(readFileSync(join(dir, LEDGER_FILE), 'utf8'));
    assert.deepEqual(Object.keys(ledger.runs), ['KRun']);
    assert.deepEqual(ledger.runs.KRun.datasets, { KData: 0 });
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a test run is issued only from reviewed main or develop, to an HTTPS test API that is not production', () => {
  const production = productionApiOrigin();
  assert.equal(production, new URL(JSON.parse(readFileSync(new URL('../../frontend/inventory-app/src/assets/config.json', import.meta.url), 'utf8')).apiBaseUrl).origin);
  const testApi = 'https://restockroo-testing-api.azurewebsites.net';
  const test = (GITHUB_REF, url) => collectorTarget({ GITHUB_REF, PRICE_COLLECTOR_TARGET: 'test', PRICE_COLLECTOR_TEST_API_URL: url,
    PRICE_COLLECTOR_PRODUCTION_API_URL: production });
  for (const ref of ['refs/heads/main', 'refs/heads/develop']) {
    assert.equal(test(ref, testApi).origin, testApi);
  }
  for (const ref of ['refs/heads/feature', 'refs/heads/developer', 'refs/tags/v1', 'refs/pull/1/merge', undefined]) {
    assert.throws(() => test(ref, testApi), /main or develop/);
  }
  for (const url of [production, `${production}/`, `${production}/api`, production.toUpperCase()]) {
    assert.throws(() => test('refs/heads/develop', url), /not be the production API/);
  }
  for (const url of ['http://restockroo-testing-api.azurewebsites.net', 'https://user:pw@restockroo-testing-api.azurewebsites.net', `${testApi}?x=1`]) {
    assert.throws(() => test('refs/heads/develop', url), /HTTPS test API URL/);
  }
  // No target, or an unknown one, is refused rather than defaulting to either API.
  for (const target of [undefined, '', 'Production', 'prod', 'testing']) {
    assert.throws(() => collectorTarget({ GITHUB_REF: 'refs/heads/main', PRICE_COLLECTOR_TARGET: target, PRICE_COLLECTOR_TEST_API_URL: testApi }), /test or production target/);
  }
});

test('a production run is issued only from reviewed main, and only to the production API origin', () => {
  const production = productionApiOrigin();
  const testApi = 'https://restockroo-testing-api.azurewebsites.net';
  const run = (GITHUB_REF, url) => collectorTarget({ GITHUB_REF, PRICE_COLLECTOR_TARGET: 'production', PRICE_COLLECTOR_PRODUCTION_API_URL: url,
    PRICE_COLLECTOR_TEST_API_URL: testApi });
  assert.equal(run('refs/heads/main', production).origin, production);
  assert.equal(run('refs/heads/main', `${production}/`).origin, production);
  for (const ref of ['refs/heads/develop', 'refs/heads/feature', undefined]) {
    assert.throws(() => run(ref, production), /main/);
  }
  // Never the test API or another host, and never a path: the scripts add /api themselves.
  for (const url of [testApi, 'https://example.com', `${production}/api`]) {
    assert.throws(() => run('refs/heads/main', url), /production API origin/);
  }
  for (const url of [production.replace('https:', 'http:'), `${production}?x=1`]) {
    assert.throws(() => run('refs/heads/main', url), /HTTPS production API URL/);
  }
  assert.throws(() => run('refs/heads/main', undefined));
});

test('a run is read from the leading JSON of an answer even when a summary follows it, and from nowhere else', () => {
  const run = { runId: 'Run7', status: 'SUCCEEDED', storages: { datasets: { default: { id: 'Data7', fields: ['name{x}', 'a"b'] } } } };
  const joined = `${JSON.stringify(run)}\nRun Run7 succeeded with 5 items. Use get-dataset-items with datasetId=Data7.`;
  for (const response of [joined, [{ type: 'text', text: joined }], { content: [{ type: 'text', text: `  ${joined}` }] }]) {
    assert.deepEqual(runFromResponse(response), { runId: 'Run7', datasetIds: ['Data7'] });
  }
  // An object anywhere but the very start, or a second object after the first, is never read.
  assert.equal(runFromResponse('Summary first {"runId":"Evil"}'), null);
  assert.deepEqual(runFromResponse(`{"status":"RUNNING"}\n{"runId":"Evil"}`), null);
  assert.equal(leadingJsonObject('{"a":"}"}trailing').a, '}');
  assert.equal(leadingJsonObject('{"a":"\\"}"}x').a, '"}');
  assert.equal(leadingJsonObject('{unterminated'), undefined);
  assert.equal(leadingJsonObject(42), undefined);
});

test('the hook log becomes counts per tool with fixed codes, never reason text, inputs or invented labels', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-tool-log-'));
  try {
    const limits = sampleLimits(JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url))), false);
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(limits));
    saveTestWork(dir, {});
    const env = { PRICE_COLLECTOR_STATE_DIR: dir, PRICE_COLLECTOR_OUT_DIR: join(dir, 'out'), PRICE_COLLECTOR_GUARD_STATE: join(dir, 'state.json') };
    const hook = event => spawnSync(process.execPath, [new URL('./workflow-tool-guard.mjs', import.meta.url).pathname],
      { input: JSON.stringify(event), env: { ...env, PATH: process.env.PATH }, encoding: 'utf8' });
    const write = (file, content) => ({ hook_event_name: 'PreToolUse', tool_name: 'Write', tool_input: { file_path: join(dir, 'out', file), content } });
    const answers = [
      hook({ hook_event_name: 'PreToolUse', tool_name: 'Bash', tool_input: { command: 'node scripts/price-collector/read-work.mjs' } }),
      hook({ hook_event_name: 'PreToolUse', tool_name: 'Bash', tool_input: { command: 'cat /etc/passwd secret-product' } }),
      hook(write('1.json', JSON.stringify({ runId: 40, workItemId: 1 }))),
      hook(write('1.json', 'not json')),
      hook(write('99.json', JSON.stringify({ runId: 41, workItemId: 99 }))),
      // A field outside the schema is refused before the file exists (live run 42128721044).
      hook(write('1.json', JSON.stringify(validResult(1, { outcome: 'complete', rows: [{ supplierProductId: 'jb-1', title: 'Coke', packQuantity: 1, packPrice: 2, regularPrice: 3, currency: 'AUD', priceBasis: 'inclusive_of_gst' }] })))),
      hook(write('1.json', JSON.stringify(validResult(1)))),
      hook({ hook_event_name: 'PreToolUse', tool_name: 'mcp__apify__get-dataset-items', tool_input: { datasetId: 'Data1' } }),
      hook({ hook_event_name: 'PostToolUse', tool_name: 'mcp__apify__call-actor', tool_input: {}, tool_response: 'no run here' }),
      hook({ hook_event_name: 'PreToolUse', tool_name: 'Read', tool_input: { file_path: '/etc/passwd' } }),
      hook({ hook_event_name: 'PostToolUseFailure', tool_name: 'mcp__apify__axlymxp--bigw-product-scraper', tool_input: { searchQueries: ['secret-product'] }, error: 'Input is not valid: field secret-product' }),
      hook({ hook_event_name: 'PostToolUseFailure', tool_name: 'mcp__apify__axlymxp--bigw-product-scraper', tool_input: {}, error: 'something private happened' }),
    ];
    for (const answer of answers) {
      // Claude Code receives exactly the documented shape: no diagnostic field leaks into it.
      const out = JSON.parse(answer.stdout);
      if (out.hookSpecificOutput) assert.deepEqual(Object.keys(out).sort(), ['hookSpecificOutput']);
    }
    const report = toolUseReport(dir);
    assert.match(report, /^Tool Bash:other: allowed 0, denied 1; not-helper 1$/m);
    assert.match(report, /^Tool Bash:read-work: allowed 1, denied 0$/m);
    assert.match(report, /^Tool Write: allowed 1, denied 4; write-wrong-run 1, write-not-json 1, write-not-in-sample 1, write-invalid 1$/m);
    assert.match(report, /^Tool mcp__apify__get-dataset-items: allowed 0, denied 1; not-own-dataset 1$/m);
    assert.match(report, /^Tool mcp__apify__call-actor: allowed 0, denied 0; not-recorded-string 1$/m);
    assert.match(report, /^Tool Read: allowed 0, denied 1; not-allowlisted 1$/m);
    // A failed Apify call is counted by a fixed label; the error text and input never reach the summary.
    assert.match(report, /^Tool mcp__apify__axlymxp--bigw-product-scraper: allowed 0, denied 0; failed-input 1, failed-other 1$/m);
    assert.equal(JSON.parse(answers.at(-1).stdout).hookSpecificOutput, undefined);
    for (const secret of ['passwd', 'secret-product', 'Data1', 'only', 'sample and its run']) assert.ok(!report.includes(secret), secret);
    // A tampered log line cannot put free text into the summary.
    writeFileSync(join(dir, TOOL_LOG_FILE), `${JSON.stringify({ tool: 'Write <b>price</b>', event: 'before', decision: 'deny', code: 'Coke $4' })}\n`, { flag: 'a' });
    assert.match(toolUseReport(dir), /^Tool other: allowed 0, denied 1; other 1$/m);
    assert.equal(toolLabel({ tool_name: 'Bash', tool_input: { command: 'node scripts/price-collector/pagination.mjs --x' } }), 'Bash:pagination');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a failed call gets one fixed label, most specific cause first', () => {
  const code = error => failureCode({ error });
  assert.equal(code('HTTP 429 Too Many Requests'), 'failed-rate-limit');
  assert.equal(code('Monthly usage limit exceeded (402)'), 'failed-limit');
  assert.equal(code('403 Forbidden'), 'failed-auth');
  assert.equal(code('Actor not found'), 'failed-not-found');
  assert.equal(code('Input is not valid: searchQueries is required'), 'failed-input');
  assert.equal(code('Request timed out'), 'failed-timeout');
  assert.equal(code('boom'), 'failed-other');
  assert.equal(failureCode({ is_interrupt: true, error: '403' }), 'failed-interrupt');
  assert.equal(failureCode(undefined), 'failed-other');
});

test('the summary carries Claude turns and result subtype, and says when no tool was called', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-turns-'));
  try {
    saveTestWork(dir, { status: 'completed', costUsd: 0.12, turns: 9, subtype: 'success' });
    const { report } = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 't',
      submit: async () => ({ exitCode: 0, out: '' }) });
    assert.match(report, /^Claude turns: 9; result: success$/m);
    assert.match(report, /^Tool calls: none$/m);
    rmSync(join(dir, 'submission'), { recursive: true, force: true });
    saveTestWork(dir, { status: 'completed', costUsd: null, turns: 'many', subtype: 'Error: secret' });
    const { report: unsafe } = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 't',
      submit: async () => ({ exitCode: 0, out: '' }) });
    assert.match(unsafe, /^Claude turns: unknown; result: unknown$/m);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

const search = (workItemId, product) => ({ workItemId, product });
const master = id => ({ kind: 'master', masterProductId: id });
const waiting = key => ({ kind: 'waiting', waitingKey: key });

test('a full run is split into batches that never separate one product\'s searches', () => {
  const issued = { runId: 7, searches: [
    search(1, master(10)), search(2, master(10)), search(3, waiting('w')), search(4, master(11)),
    search(5, waiting('w')), search(6, master(12)), search(7, master(12)), search(8, master(12)),
  ], matching: { waitingProducts: [{ waitingKey: 'w' }, { waitingKey: 'x' }], masterProducts: [] } };
  const batches = workBatches(issued, 3);
  assert.deepEqual(batches.map(b => b.searches.map(s => s.workItemId)), [[1, 2], [3, 5, 4], [6, 7, 8]]);
  assert.deepEqual(batches.map(b => b.matching.waitingProducts.map(p => p.waitingKey)), [[], ['w'], []]);
  assert.deepEqual(batches.map(b => b.matching.required), [false, true, false]);
  assert.ok(batches.every(b => b.runId === 7));
  // A product with more searches than a batch holds still goes whole into one batch.
  assert.deepEqual(workBatches(issued, 2).map(b => b.searches.length), [2, 2, 1, 3]);
  assert.deepEqual(workBatches({ ...issued, searches: [] }), []);
});

test('a full run keeps at most maxBatches batches; the rest is left for not_searched', () => {
  const searches = Array.from({ length: FULL_RUN.batchSize * (FULL_RUN.maxBatches + 2) }, (_, i) => search(i + 1, master(i + 1)));
  const full = fullRunWork({ runId: 1, searches, matching: { waitingProducts: [], masterProducts: [] } });
  assert.equal(full.searches.length, FULL_RUN.batchSize * FULL_RUN.maxBatches);
  assert.equal(full.searchCount, full.searches.length);
  assert.equal(workBatches(full).length, FULL_RUN.maxBatches);
});

test('a full run reaches a 71-product catalogue of 8 searches each, 3 products per session', () => {
  // Run 41303824929 was issued 568 searches (71 products x 8 suppliers) and reached only 120.
  const searches = Array.from({ length: 71 * 8 }, (_, i) => search(i + 1, master(Math.floor(i / 8) + 1)));
  const work = { runId: 1, searches, matching: { waitingProducts: [], masterProducts: [] } };
  assert.equal(fullRunWork(work).searches.length, 568);
  assert.deepEqual(new Set(workBatches(work).map(b => b.searches.length)), new Set([24, 16]));
  assert.equal(workBatches(work).length, 24);
  assert.ok(FULL_RUN.maxBatches * FULL_RUN.maxBudgetUsdPerBatch <= 24);
});

test('full-run limits are the production limits, with the reviewed retailers only on request', () => {
  const source = JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url)));
  const limits = fullRunLimits(source, false);
  const { jbMetro, ...apify } = limits;
  assert.deepEqual(apify, source);
  assert.deepEqual(jbMetro, JB_METRO_CAPS.full);
  assert.deepEqual(sampleLimits(source, false).jbMetro, JB_METRO_CAPS.sample);
  assert.notEqual(limits, source);
  const enabled = fullRunLimits(source, true);
  assert.equal(enabled.actors['abotapi/kmart-au-scraper'].actorIdConfirmed, true);
  assert.equal(enabled.actors['axlymxp/bigw-product-scraper'].actorIdConfirmed, true);
  assert.equal(enabled.perRun.maxActorCalls, source.perRun.maxActorCalls);
  assert.equal(enabled.actors['varnmill/au-supermarket-prices'].pageSize, source.actors['varnmill/au-supermarket-prices'].pageSize);
  assert.equal(source.actors['abotapi/kmart-au-scraper'].actorIdConfirmed, false);
});

test('the full-run switch and its deadline are explicit', () => {
  assert.equal(fullRunRequested('true'), true);
  assert.equal(fullRunRequested('false'), false);
  for (const bad of ['', undefined, 'yes', 'TRUE']) assert.throws(() => fullRunRequested(bad));
  assert.equal(collectorDeadlineMinutes('', true), FULL_RUN.deadlineMinutes);
  assert.equal(collectorDeadlineMinutes('3', true), 3);
  assert.throws(() => collectorDeadlineMinutes('90', true));
});

test('a full run starts one capped Claude session per batch, each seeing only its batch', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-batches-'));
  try {
    const issued = { runId: 7, fullRun: true, searches: Array.from({ length: 2 * FULL_RUN.batchSize + 2 }, (_, i) => search(i + 1, master(i + 1))),
      matching: { waitingProducts: [], masterProducts: [] } };
    const seen = [];
    const status = await collectBatches({ stateDir: dir, issued, deadlineMs: 60 * 60 * 1000, launch: async options => {
      seen.push({ ids: JSON.parse(readFileSync(join(dir, 'work.json'), 'utf8')).searches.map(s => s.workItemId), options });
      return { status: 'completed', costUsd: 0.25, turns: 10, subtype: 'success' };
    } });
    assert.deepEqual(seen.map(s => s.ids.length), [FULL_RUN.batchSize, FULL_RUN.batchSize, 2]);
    assert.ok(seen.every(s => s.options.maxTurns === FULL_RUN.maxTurnsPerBatch && s.options.maxBudgetUsd === FULL_RUN.maxBudgetUsdPerBatch));
    assert.ok(seen.every(s => s.options.timeoutMs <= FULL_RUN.batchMinutes * 60 * 1000));
    assert.deepEqual(status, { status: 'completed', costUsd: 0.75, turns: 30, subtype: 'success', batchesRun: 3, batchCount: 3 });
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'collector-status.json'), 'utf8')), status);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a full run stops at the first session that does not complete, and at the deadline', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-batches-stop-'));
  try {
    const issued = { runId: 7, fullRun: true, searches: Array.from({ length: 3 * FULL_RUN.batchSize }, (_, i) => search(i + 1, master(i + 1))),
      matching: { waitingProducts: [], masterProducts: [] } };
    let launches = 0;
    const limited = await collectBatches({ stateDir: dir, issued, deadlineMs: 60 * 60 * 1000, launch: async () => {
      launches += 1;
      return launches === 2 ? { status: 'claude_limit', costUsd: null, turns: 60, subtype: 'error_max_turns' } : { status: 'completed', costUsd: 0.5, turns: 5, subtype: 'success' };
    } });
    assert.equal(launches, 2);
    assert.deepEqual(limited, { status: 'claude_limit', costUsd: null, turns: 65, subtype: 'error_max_turns', batchesRun: 2, batchCount: 3 });

    let clock = 0;
    const timed = await collectBatches({ stateDir: dir, issued, deadlineMs: 10 * 60 * 1000, now: () => clock, launch: async ({ timeoutMs }) => {
      assert.ok(timeoutMs <= 10 * 60 * 1000);
      clock += 9.5 * 60 * 1000;
      return { status: 'completed', costUsd: 0.1, turns: 3, subtype: 'success' };
    } });
    assert.equal(timed.status, 'timeout');
    assert.equal(timed.batchesRun, 1);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a sample run is still one session with the sample caps and no batch line', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-sample-'));
  try {
    const calls = [];
    const status = await collectBatches({ stateDir: dir, issued: { ...work, fullRun: false }, deadlineMs: 14 * 60 * 1000,
      launch: async options => { calls.push(options); return { status: 'timeout', costUsd: null, turns: null, subtype: null }; } });
    assert.equal(calls.length, 1);
    assert.deepEqual(Object.keys(calls[0]), ['timeoutMs', 'session']);
    assert.deepEqual(status, { status: 'timeout', costUsd: null, turns: null, subtype: null });
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'work.json'), 'utf8')).searches, work.searches);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the summary counts the batches that ran and the searches given to Claude', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-batch-report-'));
  try {
    saveTestWork(dir, { status: 'completed', costUsd: 1.5, turns: 90, subtype: 'success', batchesRun: 3, batchCount: 5 });
    const { report: answer } = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 't', submit: async () => ({ exitCode: 0, out: '' }) });
    assert.match(answer, /^Claude batches: 3 of 5$/m);
    assert.match(answer, /^Searches given to Claude: 2$/m);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('JB Metro calls get the run\'s own page caps, appended last, and the sample caps by default', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-jbmetro-'));
  try {
    const sample = '--page-size 5 --max-pages 1 --batch-size 5 --attempts 1 --timeout-ms 15000';
    assert.equal(jbMetroFlags(dir), sample);
    const source = JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url)));
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(sampleLimits(source, false)));
    assert.equal(jbMetroFlags(dir), sample);
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(fullRunLimits(source, false)));
    assert.equal(jbMetroFlags(dir), '--page-size 20 --max-pages 5 --batch-size 20 --attempts 3 --timeout-ms 25000');
    // A limits file above the hard ceiling, or with a missing or non-integer cap, falls back to the sample.
    for (const jbMetro of [{ ...JB_METRO_CAPS.full, maxPages: 50 }, { ...JB_METRO_CAPS.full, attempts: undefined }, { ...JB_METRO_CAPS.full, pageSize: 2.5 }, null]) {
      writeFileSync(join(dir, 'limits.json'), JSON.stringify({ ...fullRunLimits(source, false), jbMetro }));
      assert.equal(jbMetroFlags(dir), sample);
    }
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(fullRunLimits(source, false)));
    const answer = await toolDecision({ tool_name: 'Bash', tool_input: { command: 'node scripts/price-collector/jbmetro-lookup.mjs --search "Coke Classic" https://www.jbmetro.com.au --max-pages 99' } },
      { PRICE_COLLECTOR_STATE_DIR: dir });
    assert.equal(answer.hookSpecificOutput.permissionDecision, 'allow');
    assert.match(answer.hookSpecificOutput.updatedInput.command, /--max-pages 99 --page-size 20 --max-pages 5 --batch-size 20 --attempts 3 --timeout-ms 25000$/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the run log masks every credential and keeps long answers to their head and tail', () => {
  const secrets = secretsOf({ APIFY_TOKEN: 'abcdefgh12345678', ANTHROPIC_API_KEY: 'short', PRICE_COLLECTOR_API_TOKEN: 'api-token-value-1' });
  assert.deepEqual(secrets, ['api-token-value-1', 'abcdefgh12345678']);
  const masked = redact('token abcdefgh12345678 and api-token-value-1; Authorization: Bearer xyz123456789 apify_api_ABCDEFGHIJ sk-ant-api03-ABCDEFGHIJ eyJhbGciOiJIUzI1.eyJzdWIiOiIxMjM0.c2lnbmF0dXJlMTIz', secrets);
  for (const leaked of ['abcdefgh12345678', 'api-token-value-1', 'xyz123456789', 'ABCDEFGHIJ', 'eyJhbGciOiJIUzI1']) assert.ok(!masked.includes(leaked), leaked);
  assert.match(masked, /Bearer \*\*\*/);
  assert.equal(clip('short', 10), 'short');
  const long = clip('a'.repeat(30) + 'b'.repeat(30), 20);
  assert.match(long, /^a{10} … \[40 characters left out\] … b{10}$/);
});

test('the work schedule is all or due and nothing else, and no run id means nothing was issued', () => {
  assert.equal(scheduleRequested('all'), 'all');
  assert.equal(scheduleRequested('due'), 'due');
  // A typo read as all would re-search, and re-pay for, the whole catalogue.
  for (const value of [undefined, '', 'Due', 'weekly', 'all ']) assert.throws(() => scheduleRequested(value), /all or due/);
  assert.equal(runWasIssued({ runId: 41 }), true);
  for (const issued of [{ runId: null }, { runId: 0 }, { runId: '41' }, {}, null]) assert.equal(runWasIssued(issued), false);
  assert.match(NOTHING_DUE, /^Nothing due today/);
});

test('the summary counts each supplier\'s issued, given, searched and unsearched work, with no product, price or link', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-suppliers-'));
  try {
    const coke = { kind: 'master', masterProductId: 7, brand: 'Coca-Cola', name: 'Zero Sugar', sizeValue: 375, sizeUnit: 'ml' };
    const issuedSearches = [
      { workItemId: 1, supplierPresetId: 3, supplierName: 'ALDI', product: coke },
      { workItemId: 2, supplierPresetId: 4, supplierName: 'Coles', product: coke },
      { workItemId: 3, supplierPresetId: 4, supplierName: 'Coles', product: { kind: 'waiting', searchName: 'Tasty Cheese', unitLabel: '45g' } },
      { workItemId: 5, supplierPresetId: 6, supplierName: 'JB Metro', product: coke },
      { workItemId: 6, supplierPresetId: 6, supplierName: 'JB Metro', product: { kind: 'waiting', searchName: 'Tasty Cheese', unitLabel: '45g' } },
      { workItemId: 7, supplierPresetId: 6, supplierName: 'JB Metro', product: { kind: 'waiting', searchName: 'Mints', unitLabel: '30g' } },
      // Issued but left out of the sample: it must count as not searched, never as searched.
      { workItemId: 4, supplierPresetId: 5, supplierName: 'Woolworths', product: coke },
    ];
    const work = { runId: 41, searches: issuedSearches.filter(search => search.workItemId !== 4), matching: { waitingProducts: [], masterProducts: [] },
      issuedBySupplier: issuedCountsBySupplier(issuedSearches) };
    writeFileSync(join(dir, 'issued-work.json'), JSON.stringify(work));
    writeFileSync(join(dir, 'collector-status.json'), JSON.stringify({ status: 'timeout', costUsd: 0.1 }));
    const outputDir = join(dir, 'out');
    mkdirSync(outputDir);
    writeFileSync(join(outputDir, '1.json'), JSON.stringify({ runId: 41, workItemId: 1, supplierPresetId: 3, outcome: 'complete', rows: [
      { supplierProductId: 'aldi-1', title: 'Coke Zero 18x375ml', packPrice: 20.99, packQuantity: 18, stockStatus: 'in_stock', sourceUrl: 'https://www.aldi.com.au/p/1', currency: 'AUD', priceBasis: 'inclusive_of_gst' }] }));
    writeFileSync(join(outputDir, '2.json'), JSON.stringify({ runId: 41, workItemId: 2, supplierPresetId: 4, outcome: 'no_matches', rows: [] }));
    // A file that says not_searched, a malformed file, and a file of another run (refused before
    // submission): none of them is evidence that a search happened.
    writeFileSync(join(outputDir, '5.json'), JSON.stringify({ runId: 41, workItemId: 5, supplierPresetId: 6, outcome: 'not_searched', rows: [] }));
    writeFileSync(join(outputDir, '6.json'), JSON.stringify({ runId: 41, workItemId: 6, supplierPresetId: 6, outcome: 'complete', rows: 'not a list' }));
    writeFileSync(join(outputDir, '7.json'), JSON.stringify({ runId: 99, workItemId: 7, supplierPresetId: 6, outcome: 'no_matches', rows: [] }));
    const { suppliers } = await finaliseSample({ stateDir: dir, outputDir, env: {}, token: 't', submit: async () => ({ exitCode: 0, out: '' }) });
    assert.match(suppliers, /^\| ALDI \| 1 \| 1 \| 1 \| 0 \| complete 1 \| 0 \|$/m);
    assert.match(suppliers, /^\| Coles \| 2 \| 2 \| 1 \| 1 \| no_matches 1 \| 0 \|$/m);
    // The not_searched file and the refused file are not searched; the malformed one is also unusable.
    assert.match(suppliers, /^\| JB Metro \| 3 \| 3 \| 0 \| 3 \| - \| 1 \|$/m);
    assert.match(suppliers, /^\| Woolworths \| 1 \| 0 \| 0 \| 1 \| - \| 0 \|$/m);
    // A supplier with no issued work is not listed at all.
    assert.ok(!/Kmart|BIG W/.test(suppliers));
    for (const detail of ['Coke Zero', 'aldi-1', '20.99', 'https://', 'Tasty Cheese', 'Zero Sugar']) assert.ok(!suppliers.includes(detail), detail);
    assert.ok(readFileSync(join(dir, 'logs', 'summary.md'), 'utf8').includes('### Suppliers'));
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the supplier counts say when nothing was issued and fall back to the searches given', () => {
  assert.match(supplierCounts({ searches: [] }, '/nonexistent'), /No searches were issued/);
  assert.match(supplierCounts({ searches: [{ workItemId: 9, supplierPresetId: 2, supplierName: 'JB Metro' }] }, '/nonexistent'), /^\| JB Metro \| 1 \| 1 \| 0 \| 1 \| - \| 0 \|$/m);
});

test('a result file submission would skip is refused with the reason, and the corrected file is then allowed once', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-write-check-'));
  try {
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(sampleLimits(JSON.parse(readFileSync(new URL('./apify-limits.json', import.meta.url))), false)));
    saveTestWork(dir, {});
    const env = { PRICE_COLLECTOR_STATE_DIR: dir, PRICE_COLLECTOR_OUT_DIR: join(dir, 'out'), PRICE_COLLECTOR_GUARD_STATE: join(dir, 'state.json') };
    const row = { supplierProductId: 'jb-1', title: 'Coke Zero 375ml', packQuantity: 24, packPrice: 30, currency: 'AUD', priceBasis: 'exclusive_of_gst', stockStatus: 'in_stock' };
    const write = file => toolDecision({ tool_name: 'Write', tool_input: { file_path: join(dir, 'out/1.json'), content: JSON.stringify(file) } }, env);
    const refused = await write(validResult(1, { outcome: 'complete', rows: [{ ...row, regularPrice: 33 }] }));
    assert.equal(refused.hookSpecificOutput.permissionDecision, 'deny');
    assert.equal(refused.code, 'write-invalid');
    assert.match(refused.hookSpecificOutput.permissionDecisionReason, /regularPrice is not a field of this file/);
    // Tidying that submission does itself (a stock word, a barcode without a scope) is not a refusal.
    const allowed = await write(validResult(1, { outcome: 'complete', rows: [{ ...row, stockStatus: 'maybe', barcode: '9300675024235' }] }));
    assert.equal(allowed.hookSpecificOutput.permissionDecision, 'allow');
    // A match file is checked against its own schema.
    const match = await toolDecision({ tool_name: 'Write', tool_input: { file_path: join(dir, 'out/match-a.json'), content: JSON.stringify({ runId: 41, waitingKey: 'a', extra: 1 }) } }, env);
    assert.equal(match.code, 'write-invalid');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
