import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { sampleWork, sampleLimits, fullRunLimits, fullRunWork, fullRunRequested, workBatches, collectBatches, publicSkipLine, FULL_RUN, collectorEnvironment, collectorDeadlineMinutes, collectorTarget, productionApiOrigin, publicSubmissionReport, finaliseSample, launchClaude, toolUseReport } from './workflow.mjs';
import { JB_METRO_CAPS, jbMetroFlags, permittedCommand, recordDecision, toolDecision, toolLabel, TOOL_LOG_FILE } from './workflow-tool-guard.mjs';
import { LEDGER_FILE, MAX_POLLS_PER_RUN, leadingJsonObject, runFromResponse } from './workflow-run-ledger.mjs';
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
    writeFileSync(join(dir, 'issued-work.json'), JSON.stringify(work));
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
      hook(write('1.json', JSON.stringify({ runId: 41, workItemId: 1 }))),
      hook({ hook_event_name: 'PreToolUse', tool_name: 'mcp__apify__get-dataset-items', tool_input: { datasetId: 'Data1' } }),
      hook({ hook_event_name: 'PostToolUse', tool_name: 'mcp__apify__call-actor', tool_input: {}, tool_response: 'no run here' }),
      hook({ hook_event_name: 'PreToolUse', tool_name: 'Read', tool_input: { file_path: '/etc/passwd' } }),
    ];
    for (const answer of answers) {
      // Claude Code receives exactly the documented shape: no diagnostic field leaks into it.
      const out = JSON.parse(answer.stdout);
      if (out.hookSpecificOutput) assert.deepEqual(Object.keys(out).sort(), ['hookSpecificOutput']);
    }
    const report = toolUseReport(dir);
    assert.match(report, /^Tool Bash:other: allowed 0, denied 1; not-helper 1$/m);
    assert.match(report, /^Tool Bash:read-work: allowed 1, denied 0$/m);
    assert.match(report, /^Tool Write: allowed 1, denied 3; write-wrong-run 1, write-not-json 1, write-not-in-sample 1$/m);
    assert.match(report, /^Tool mcp__apify__get-dataset-items: allowed 0, denied 1; not-own-dataset 1$/m);
    assert.match(report, /^Tool mcp__apify__call-actor: allowed 0, denied 0; not-recorded-string 1$/m);
    assert.match(report, /^Tool Read: allowed 0, denied 1; not-allowlisted 1$/m);
    for (const secret of ['passwd', 'secret-product', 'Data1', 'only', 'sample and its run']) assert.ok(!report.includes(secret), secret);
    // A tampered log line cannot put free text into the summary.
    writeFileSync(join(dir, TOOL_LOG_FILE), `${JSON.stringify({ tool: 'Write <b>price</b>', event: 'before', decision: 'deny', code: 'Coke $4' })}\n`, { flag: 'a' });
    assert.match(toolUseReport(dir), /^Tool other: allowed 0, denied 1; other 1$/m);
    assert.equal(toolLabel({ tool_name: 'Bash', tool_input: { command: 'node scripts/price-collector/pagination.mjs --x' } }), 'Bash:pagination');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the summary carries Claude turns and result subtype, and says when no tool was called', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-turns-'));
  try {
    saveTestWork(dir, { status: 'completed', costUsd: 0.12, turns: 9, subtype: 'success' });
    const report = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 't',
      submit: async () => ({ exitCode: 0, out: '' }) });
    assert.match(report, /^Claude turns: 9; result: success$/m);
    assert.match(report, /^Tool calls: none$/m);
    rmSync(join(dir, 'submission'), { recursive: true, force: true });
    saveTestWork(dir, { status: 'completed', costUsd: null, turns: 'many', subtype: 'Error: secret' });
    const unsafe = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 't',
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
    const issued = { runId: 7, fullRun: true, searches: Array.from({ length: 14 }, (_, i) => search(i + 1, master(i + 1))),
      matching: { waitingProducts: [], masterProducts: [] } };
    const seen = [];
    const status = await collectBatches({ stateDir: dir, issued, deadlineMs: 60 * 60 * 1000, launch: async options => {
      seen.push({ ids: JSON.parse(readFileSync(join(dir, 'work.json'), 'utf8')).searches.map(s => s.workItemId), options });
      return { status: 'completed', costUsd: 0.25, turns: 10, subtype: 'success' };
    } });
    assert.deepEqual(seen.map(s => s.ids.length), [6, 6, 2]);
    assert.ok(seen.every(s => s.options.maxTurns === FULL_RUN.maxTurnsPerBatch && s.options.maxBudgetUsd === FULL_RUN.maxBudgetUsdPerBatch));
    assert.ok(seen.every(s => s.options.timeoutMs <= FULL_RUN.batchMinutes * 60 * 1000));
    assert.deepEqual(status, { status: 'completed', costUsd: 0.75, turns: 30, subtype: 'success', batchesRun: 3, batchCount: 3 });
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'collector-status.json'), 'utf8')), status);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('a full run stops at the first session that does not complete, and at the deadline', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'collector-batches-stop-'));
  try {
    const issued = { runId: 7, fullRun: true, searches: Array.from({ length: 18 }, (_, i) => search(i + 1, master(i + 1))),
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
    assert.deepEqual(Object.keys(calls[0]), ['timeoutMs']);
    assert.deepEqual(status, { status: 'timeout', costUsd: null, turns: null, subtype: null });
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'work.json'), 'utf8')).searches, work.searches);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the summary names a skipped file\'s work item and field, never the validator\'s text', async () => {
  assert.equal(publicSkipLine('  skipped 12.json: file.rows[2].packQuantity must be 1 or more.'), 'skipped 12.json: field file.rows[2].packQuantity');
  assert.equal(publicSkipLine('  skipped 12.json: file.rows[1] reports supplier product id "SKU-secret", which another row already reports.'), 'skipped 12.json: field file.rows[1]');
  assert.equal(publicSkipLine('  skipped match-coke-zero.json: a match file proposes exactly one thing.'), 'skipped a match file: a file-level rule');
  assert.equal(publicSkipLine('  skipped notes.txt: it is not a result file'), 'skipped a match file: a file-level rule');
  assert.equal(publicSkipLine('  skipped 3.json: a search the page cap stopped must be reported as truncated, not complete.'), 'skipped 3.json: a file-level rule');
  assert.equal(publicSkipLine('run 7: completed'), null);
  const report = publicSubmissionReport('files: results 9, matches 0, skipped 1\n  skipped 4.json: file.rows[0].unitPrice must be above 0 "Coke $4".\nfailed requests: 0\n');
  assert.equal(report, 'files: results 9, matches 0, skipped 1\nskipped 4.json: field file.rows[0].unitPrice\nfailed requests: 0');

  const dir = mkdtempSync(join(tmpdir(), 'collector-batch-report-'));
  try {
    saveTestWork(dir, { status: 'completed', costUsd: 1.5, turns: 90, subtype: 'success', batchesRun: 3, batchCount: 5 });
    const answer = await finaliseSample({ stateDir: dir, outputDir: join(dir, 'missing'), env: {}, token: 't', submit: async () => ({ exitCode: 0, out: '' }) });
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
    assert.equal(jbMetroFlags(dir), '--page-size 20 --max-pages 5 --batch-size 20 --attempts 2 --timeout-ms 15000');
    // A limits file above the hard ceiling, or with a missing or non-integer cap, falls back to the sample.
    for (const jbMetro of [{ ...JB_METRO_CAPS.full, maxPages: 50 }, { ...JB_METRO_CAPS.full, attempts: undefined }, { ...JB_METRO_CAPS.full, pageSize: 2.5 }, null]) {
      writeFileSync(join(dir, 'limits.json'), JSON.stringify({ ...fullRunLimits(source, false), jbMetro }));
      assert.equal(jbMetroFlags(dir), sample);
    }
    writeFileSync(join(dir, 'limits.json'), JSON.stringify(fullRunLimits(source, false)));
    const answer = await toolDecision({ tool_name: 'Bash', tool_input: { command: 'node scripts/price-collector/jbmetro-lookup.mjs --search "Coke Classic" https://www.jbmetro.com.au --max-pages 99' } },
      { PRICE_COLLECTOR_STATE_DIR: dir });
    assert.equal(answer.hookSpecificOutput.permissionDecision, 'allow');
    assert.match(answer.hookSpecificOutput.updatedInput.command, /--max-pages 99 --page-size 20 --max-pages 5 --batch-size 20 --attempts 2 --timeout-ms 15000$/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
