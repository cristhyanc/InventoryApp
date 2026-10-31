import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { sampleWork, sampleLimits, collectorEnvironment, collectorDeadlineMinutes, collectorTarget, productionApiOrigin, publicSubmissionReport, finaliseSample, launchClaude } from './workflow.mjs';
import { permittedCommand, recordDecision, toolDecision } from './workflow-tool-guard.mjs';
import { LEDGER_FILE, MAX_POLLS_PER_RUN, runFromResponse } from './workflow-run-ledger.mjs';
import { collectorToken } from './workflow-auth.mjs';
import { startStubServer } from './stub-server.mjs';

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

test('public report permits only numeric counters, not supplier text, error bodies or filenames', () => {
  const raw = 'rows: accepted 1, sent to review 2, pending 0, rejected 0\n  rejected: private product\nfailed requests: 1\nrun 41: completed; complete 1, truncated 0, no_matches 0, failed 0, not_searched 7\nsecret token';
  const safe = publicSubmissionReport(raw);
  assert.ok(safe.includes('not_searched 7'));
  assert.ok(safe.includes('accepted 1'));
  assert.ok(!safe.includes('private'));
  assert.ok(!safe.includes('secret'));
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

test('finalisation runs after a collector timeout even with no result files and hides submission errors', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-test-'));
  try {
    writeFileSync(join(dir, 'work.json'), JSON.stringify(work));
    writeFileSync(join(dir, 'collector-status.json'), JSON.stringify({ status: 'timeout', costUsd: null }));
    const calls = [];
    const answer = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 'private-token', submit: async ({ argv }) => {
      calls.push(argv);
      return { exitCode: 0, out: 'run 41: completed; complete 0, truncated 0, no_matches 0, failed 0, not_searched 2\n', err: 'private' };
    } });
    assert.equal(calls.length, 1);
    assert.ok(calls[0].includes('41'));
    assert.ok(answer.includes('timeout'));
    assert.ok(answer.includes('not_searched 2'));
    assert.ok(!answer.includes('private'));
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
    const status = await launchClaude({ executable: fake, sandbox, env: { PATH: process.env.PATH }, timeoutMs: 500 });
    assert.equal(status.status, 'timeout');
    saveTestWork(dir, status);
    // A malformed file, another work item, and a stale-run file cannot stop the valid one.
    writeFileSync(join(outputDir, '2.json'), '{');
    writeFileSync(join(outputDir, '99.json'), JSON.stringify({ runId: 41, workItemId: 99 }));
    writeFileSync(join(outputDir, 'match-a.json'), JSON.stringify({ runId: 40, waitingKey: 'a' }));
    const report = await finaliseSample({ stateDir: dir, outputDir, env: { PRICE_COLLECTOR_TEST_API_URL: server.baseUrl }, token: 'test-token' });
    assert.deepEqual(server.requests.map(r => r.path), ['/api/price-collector/work-items/1/outcome', '/api/price-collector/runs/41/complete']);
    assert.ok(server.requests.every(r => r.authorization === 'Bearer test-token'));
    assert.ok(report.includes('not_searched 1'));
    assert.ok(report.includes('Files refused before submission: 3'));
    assert.ok(!report.includes('secret'));
  } finally { await server.close(); rmSync(dir, { recursive: true, force: true }); }
});

function saveTestWork(dir, status) {
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
      content: JSON.stringify({ runId: 41, workItemId: item }) } }, env);
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

test('a run is issued only from reviewed main or develop, to an HTTPS test API that is not production', () => {
  const production = productionApiOrigin();
  assert.equal(production, new URL(JSON.parse(readFileSync(new URL('../../frontend/inventory-app/src/assets/config.json', import.meta.url), 'utf8')).apiBaseUrl).origin);
  const testApi = 'https://restockroo-testing-api.azurewebsites.net';
  for (const ref of ['refs/heads/main', 'refs/heads/develop']) {
    assert.equal(collectorTarget({ GITHUB_REF: ref, PRICE_COLLECTOR_TEST_API_URL: testApi }).origin, testApi);
  }
  for (const ref of ['refs/heads/feature', 'refs/heads/developer', 'refs/tags/v1', 'refs/pull/1/merge', undefined]) {
    assert.throws(() => collectorTarget({ GITHUB_REF: ref, PRICE_COLLECTOR_TEST_API_URL: testApi }), /main or develop/);
  }
  for (const url of [production, `${production}/`, production.toUpperCase()]) {
    assert.throws(() => collectorTarget({ GITHUB_REF: 'refs/heads/develop', PRICE_COLLECTOR_TEST_API_URL: url }), /not be the production API/);
  }
  for (const url of ['http://restockroo-testing-api.azurewebsites.net', 'https://user:pw@restockroo-testing-api.azurewebsites.net', `${testApi}?x=1`]) {
    assert.throws(() => collectorTarget({ GITHUB_REF: 'refs/heads/develop', PRICE_COLLECTOR_TEST_API_URL: url }), /HTTPS test API URL/);
  }
});
