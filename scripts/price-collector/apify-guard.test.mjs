// Tests for scripts/price-collector/apify-guard.mjs and apify-limits.json: the `PreToolUse` hook
// that enforces the collector's actor, input and call-count rules and its write-once output folder
// (issue #926, parent #817).
//
// The point of the hook is that **the caps are not Claude's to obey**: an unlisted or unconfirmed
// actor, a call past the run's own call cap and a rewrite of a finished result file are all
// refused by code, in a separate process, before the tool runs. No test makes a
// live call: the hook never calls anything itself.
//
// The call accounting is tested as the loop control it is, because its failure modes are what a cap
// is worth: an accounting that cannot be read, is corrupt, cannot be locked or cannot be written
// refuses the paid call rather than starting the count again, and four real node processes
// released together may not take one remaining reservation twice. The guard sets and counts no
// dollar amount: the Apify account usage limit caps spending (owner decision, 31 October 2026).
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, it } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import {
  GUARD_STATE_ENV,
  OUTPUT_DIR_ENV,
  STATE_SCHEMA_VERSION,
  decide,
  emptyState,
  fileStateStorage,
  loadLimits,
  loadMcpConfig,
  runGuard,
} from './apify-guard.mjs';

const limits = loadLimits();
const VARNMILL = 'varnmill/au-supermarket-prices';
const UNCONFIRMED = Object.freeze(['abotapi/kmart-au-scraper', 'axlymxp/bigw-product-scraper', 'dromb/big-w-au-product-scraper']);

/**
 * The rules with one actor's configuration marked confirmed. The actors the owner has not verified
 * yet stay `actorIdConfirmed: false` in `apify-limits.json` - nothing is marked confirmed to make a
 * test pass - so a test about what the guard does to an *allowed* call says so here instead.
 */
const confirming = (actorId, rules = limits) => ({
  ...rules,
  actors: { ...rules.actors, [actorId]: { ...rules.actors[actorId], actorIdConfirmed: true } },
});

const apifyEvent = (toolInput, toolName = 'mcp__apify__call-actor') => ({
  hook_event_name: 'PreToolUse',
  tool_name: toolName,
  tool_input: toolInput,
});

const writeEvent = (filePath, toolName = 'Write') => ({
  hook_event_name: 'PreToolUse',
  tool_name: toolName,
  tool_input: { file_path: filePath, content: '{}' },
});

const decideApify = (toolInput, { state = emptyState(), toolName, rules = limits } = {}) =>
  decide({ event: apifyEvent(toolInput, toolName), limits: rules, state, outputDir: '/work/price-collector-out', exists: () => false });

const inputOf = (answer) => answer.updatedInput?.input ?? answer.updatedInput ?? {};

let dir = null;

afterEach(async () => {
  if (dir !== null) await rm(dir, { recursive: true, force: true });
  dir = null;
});

describe('the limits file', () => {
  it('names the actors the owner chose in testing, and only those', () => {
    assert.deepEqual(Object.keys(limits.actors).sort(), [
      'abotapi/kmart-au-scraper',
      'axlymxp/bigw-product-scraper',
      'dromb/big-w-au-product-scraper',
      VARNMILL,
    ]);
  });

  it('gives every actor a page size, a page cap and an item-limit field, and no dollar ceiling', () => {
    for (const [actorId, actor] of Object.entries(limits.actors)) {
      assert.ok(Number.isInteger(actor.pageSize) && actor.pageSize > 0, `${actorId} pageSize`);
      assert.ok(Number.isInteger(actor.maxPages) && actor.maxPages > 0, `${actorId} maxPages`);
      assert.equal(Object.hasOwn(actor, 'maxTotalChargeUsd'), false, `${actorId} maxTotalChargeUsd`);
      assert.ok(typeof actor.itemLimitField === 'string' && actor.itemLimitField !== '', `${actorId} itemLimitField`);
      assert.ok(Array.isArray(actor.serves) && actor.serves.length > 0, `${actorId} serves`);
    }
  });

  it('bounds one collector run by a call count, and sets no dollar amount (the Apify account limit does)', () => {
    assert.ok(Number.isInteger(limits.perRun.maxActorCalls) && limits.perRun.maxActorCalls > 0);
    assert.equal(Object.hasOwn(limits.perRun, 'maxTotalChargeUsd'), false);
    assert.equal(Object.hasOwn(limits, 'chargeField'), false);
  });

  it('forces the enrichments and the watcher off, which is where the money goes', () => {
    assert.equal(limits.actors[VARNMILL].forcedInput.onlyChanged, false);
    assert.equal(limits.actors[VARNMILL].forcedInput.fetchPaidDetails, false);
    assert.equal(limits.actors['abotapi/kmart-au-scraper'].forcedInput.fetchReviews, false);
    assert.equal(limits.actors['abotapi/kmart-au-scraper'].forcedInput.includeClickAndCollect, false);
  });

  it('says which actor ids are still to be confirmed in the dry run', () => {
    assert.equal(limits.actors[VARNMILL].actorIdConfirmed, true);
    for (const actorId of ['abotapi/kmart-au-scraper', 'axlymxp/bigw-product-scraper', 'dromb/big-w-au-product-scraper']) {
      assert.equal(limits.actors[actorId].actorIdConfirmed, false, actorId);
    }
  });

  it('offers the MCP server exactly the actors the guard allows, and no token', () => {
    const { config, actors } = loadMcpConfig();

    assert.deepEqual([...actors].sort(), Object.keys(limits.actors).sort());
    const serialized = JSON.stringify(config);
    assert.match(serialized, /\$\{APIFY_TOKEN\}/);
    assert.ok(!/apify_api_[A-Za-z0-9]/.test(serialized), 'no Apify token may be committed');
  });
});

describe('which actor may be called', () => {
  it('blocks an actor the limits file does not list', () => {
    const answer = decideApify({ actor: 'someone/cheap-scraper' });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /someone\/cheap-scraper/);
    assert.equal(answer.state.actorCalls, 0);
  });

  it('blocks a call no actor can be read from', () => {
    const answer = decideApify({ query: 'coca cola 375ml' });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /actor/i);
  });

  it('reads the actor from a per-actor tool name', () => {
    const answer = decideApify({ query: 'coca cola' }, { toolName: 'mcp__apify__varnmill-slash-au-supermarket-prices' });

    assert.equal(answer.decision, 'allow');
  });

  it('reads the actor case-insensitively and ignores surrounding spaces', () => {
    assert.equal(decideApify({ actor: ` ${VARNMILL.toUpperCase()} ` }).decision, 'allow');
  });

  it('refuses an actor whose id and input fields are not confirmed, without consuming any budget', () => {
    for (const actorId of UNCONFIRMED) {
      const answer = decideApify({ actor: actorId, input: { query: 'coca cola' } });

      assert.equal(answer.decision, 'deny', actorId);
      assert.match(answer.reason, /not confirmed/i);
      assert.equal(answer.state.actorCalls, 0);
    }
  });

  it('refuses an actor whose configuration says nothing about confirmation, or says it loosely', () => {
    for (const confirmation of [undefined, false, 'true', 1, null]) {
      const rules = {
        ...limits,
        actors: { ...limits.actors, [VARNMILL]: { ...limits.actors[VARNMILL], actorIdConfirmed: confirmation } },
      };

      const answer = decideApify({ actor: VARNMILL }, { rules });

      assert.equal(answer.decision, 'deny', String(confirmation));
      assert.match(answer.reason, /not confirmed/i);
    }
  });

  it('allows the actor whose configuration the owner has confirmed', () => {
    assert.equal(limits.actors[VARNMILL].actorIdConfirmed, true);
    assert.equal(decideApify({ actor: VARNMILL, input: { query: 'coca cola' } }).decision, 'allow');
  });

  it('refuses a paid call when the limits file states no per-run call cap', () => {
    for (const perRun of [undefined, {}, { maxActorCalls: 0 }, { maxActorCalls: 1.5 }]) {
      const answer = decideApify({ actor: VARNMILL }, { rules: { ...limits, perRun } });

      assert.equal(answer.decision, 'deny', JSON.stringify(perRun));
      assert.match(answer.reason, /call cap/i);
    }
  });
});

describe('the limits it injects or verifies', () => {
  it('injects the item limit when the call carries none, and adds no charge field to the actor input', () => {
    const answer = decideApify({ actor: VARNMILL, input: { query: 'coca cola 375ml' } });

    assert.equal(answer.decision, 'allow');
    assert.equal(inputOf(answer).maxItems, limits.actors[VARNMILL].pageSize);
    assert.equal(Object.hasOwn(inputOf(answer), 'maxTotalChargeUsd'), false);
  });

  it('adds no charge field to an actor whose closed input schema would reject it', () => {
    // Dromb declares additionalProperties: false; an injected maxTotalChargeUsd would fail every call.
    const actor = 'dromb/big-w-au-product-scraper';
    const answer = decideApify({ actor, input: { item_id: '123' } }, { rules: confirming(actor) });

    assert.equal(answer.decision, 'allow');
    assert.equal(Object.hasOwn(inputOf(answer), 'maxTotalChargeUsd'), false);
  });

  it('keeps a call that asks for less than the limits allow', () => {
    const answer = decideApify({ actor: VARNMILL, input: { maxItems: 10 } });

    assert.equal(answer.decision, 'allow');
    assert.equal(inputOf(answer).maxItems, 10);
  });

  it('blocks an item limit above the actor\'s page size', () => {
    const answer = decideApify({ actor: VARNMILL, input: { maxItems: limits.actors[VARNMILL].pageSize + 1 } });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /maxItems/);
  });

  it('blocks an item limit that is not a positive number', () => {
    assert.equal(decideApify({ actor: VARNMILL, input: { maxItems: 0 } }).decision, 'deny');
    assert.equal(decideApify({ actor: VARNMILL, input: { maxItems: 'all' } }).decision, 'deny');
  });

  it('forces the enrichments off however the call asked for them', () => {
    const answer = decideApify({ actor: VARNMILL, input: { onlyChanged: true, fetchPaidDetails: true } });

    assert.equal(answer.decision, 'allow');
    assert.equal(inputOf(answer).onlyChanged, false);
    assert.equal(inputOf(answer).fetchPaidDetails, false);

    const kmart = decideApify(
      { actor: 'abotapi/kmart-au-scraper', input: { fetchReviews: true, includeClickAndCollect: true } },
      { rules: confirming('abotapi/kmart-au-scraper') },
    );
    assert.equal(kmart.decision, 'allow');
    assert.equal(inputOf(kmart).fetchReviews, false);
    assert.equal(inputOf(kmart).includeClickAndCollect, false);
  });

  it('fixes an input the call carries flat rather than under input', () => {
    const answer = decideApify({ actor: VARNMILL, query: 'coca cola' });

    assert.equal(answer.decision, 'allow');
    assert.equal(answer.updatedInput.maxItems, limits.actors[VARNMILL].pageSize);
    assert.equal(answer.updatedInput.actor, VARNMILL);
    assert.equal(answer.updatedInput.query, 'coca cola');
  });
});

describe('the per-run call cap', () => {
  it('counts every allowed call, and no charge', () => {
    const first = decideApify({ actor: VARNMILL, input: { query: 'coca cola' } });

    assert.equal(first.state.actorCalls, 1);
    assert.equal(Object.hasOwn(first.state, 'chargeUsd'), false);

    const second = decide({
      event: apifyEvent({ actor: VARNMILL, input: { query: 'tim tams' } }),
      limits,
      state: first.state,
      outputDir: '/work/price-collector-out',
      exists: () => false,
    });

    assert.equal(second.decision, 'allow', second.reason);
    assert.equal(second.state.actorCalls, 2);
  });

  it('blocks the call that would pass the run\'s call cap', () => {
    const state = { ...emptyState(), actorCalls: limits.perRun.maxActorCalls };

    const answer = decideApify({ actor: VARNMILL }, { state });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /calls/i);
    assert.equal(answer.state.actorCalls, limits.perRun.maxActorCalls);
  });
});

describe('what may be written', () => {
  const inFolder = (name) => `/work/price-collector-out/${name}`;

  it('allows one new file in the output folder', () => {
    const answer = decide({
      event: writeEvent(inFolder('7001.json')),
      limits,
      state: emptyState(),
      outputDir: '/work/price-collector-out',
      exists: () => false,
    });

    assert.equal(answer.decision, 'allow');
  });

  it('refuses a write outside the output folder', () => {
    for (const path of ['/work/backend/Program.cs', '/work/price-collector-out/../7001.json', '/etc/hosts']) {
      const answer = decide({
        event: writeEvent(path),
        limits,
        state: emptyState(),
        outputDir: '/work/price-collector-out',
        exists: () => false,
      });

      assert.equal(answer.decision, 'deny', path);
      assert.match(answer.reason, /price-collector-out/);
    }
  });

  it('refuses a rewrite of a result file that already exists', () => {
    const answer = decide({
      event: writeEvent(inFolder('7001.json')),
      limits,
      state: emptyState(),
      outputDir: '/work/price-collector-out',
      exists: () => true,
    });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /already/i);
  });

  it('refuses an edit, which is always a rewrite', () => {
    for (const toolName of ['Edit', 'MultiEdit', 'NotebookEdit']) {
      const answer = decide({
        event: writeEvent(inFolder('7001.json'), toolName),
        limits,
        state: emptyState(),
        outputDir: '/work/price-collector-out',
        exists: () => true,
      });

      assert.equal(answer.decision, 'deny', toolName);
    }
  });

  it('refuses a write to the guard\'s own accounting', () => {
    const answer = decide({
      event: writeEvent(inFolder('.apify-guard-state.json')),
      limits,
      state: emptyState(),
      outputDir: '/work/price-collector-out',
      exists: () => false,
    });

    assert.equal(answer.decision, 'deny');
  });

  it('refuses a write with no path at all', () => {
    const answer = decide({
      event: { hook_event_name: 'PreToolUse', tool_name: 'Write', tool_input: { content: '{}' } },
      limits,
      state: emptyState(),
      outputDir: '/work/price-collector-out',
      exists: () => false,
    });

    assert.equal(answer.decision, 'deny');
  });
});

describe('everything else', () => {
  it('has no opinion about a tool that is neither an Apify call nor a write', () => {
    const answer = decide({
      event: { hook_event_name: 'PreToolUse', tool_name: 'Read', tool_input: { file_path: '/work/AGENTS.md' } },
      limits,
      state: emptyState(),
      outputDir: '/work/price-collector-out',
      exists: () => false,
    });

    assert.equal(answer.decision, 'pass');
  });
});

describe('the hook as the process runs it', () => {
  it('keeps the run\'s call count in its state file and blocks once the cap is reached', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const env = { [GUARD_STATE_ENV]: join(dir, 'state.json'), [OUTPUT_DIR_ENV]: dir };
    const call = () => runGuard({ event: apifyEvent({ actor: VARNMILL }), env, limits: { ...limits, perRun: { maxActorCalls: 2 } } });

    const first = await call();
    const second = await call();
    const third = await call();

    assert.equal(JSON.parse(first.out).hookSpecificOutput.permissionDecision, 'allow');
    assert.equal(JSON.parse(second.out).hookSpecificOutput.permissionDecision, 'allow');
    assert.equal(JSON.parse(third.out).hookSpecificOutput.permissionDecision, 'deny');
    assert.equal(first.exitCode, 0);
    assert.equal(third.exitCode, 0);
  });

  it('refuses a rewrite of a file the output folder really holds', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    await writeFile(join(dir, '7001.json'), '{}', 'utf8');
    const env = { [GUARD_STATE_ENV]: join(dir, 'state.json'), [OUTPUT_DIR_ENV]: dir };

    const existing = await runGuard({ event: writeEvent(join(dir, '7001.json')), env });
    const fresh = await runGuard({ event: writeEvent(join(dir, '7002.json')), env });

    assert.equal(JSON.parse(existing.out).hookSpecificOutput.permissionDecision, 'deny');
    assert.equal(JSON.parse(fresh.out).hookSpecificOutput.permissionDecision, 'allow');
  });

  it('answers a malformed event by blocking rather than by failing open', async () => {
    const answer = await runGuard({ event: null, env: {} });

    assert.equal(JSON.parse(answer.out).hookSpecificOutput.permissionDecision, 'deny');
  });
});

const decisionOf = (answer) => JSON.parse(answer.out).hookSpecificOutput;
const capped = (maxActorCalls) => ({ ...limits, perRun: { maxActorCalls } });
const varnmillCall = () => apifyEvent({ actor: VARNMILL, input: { query: 'coca cola' } });
const accounting = (fields) => `${JSON.stringify({ schemaVersion: STATE_SCHEMA_VERSION, actorCalls: 3, updatedAtUtc: '2026-10-30T02:00:00.000Z', ...fields }, null, 2)}\n`;

describe('the accounting the caps are counted in', () => {
  it('starts a run that has made no call from zero, and reserves the call before allowing it', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const statePath = join(dir, 'accounting', 'state.json');

    const answer = await runGuard({
      event: varnmillCall(),
      env: { [GUARD_STATE_ENV]: statePath },
      limits: capped(2),
      now: () => new Date('2026-10-30T02:00:00Z'),
    });

    assert.equal(decisionOf(answer).permissionDecision, 'allow', decisionOf(answer).permissionDecisionReason);
    assert.deepEqual(JSON.parse(await readFile(statePath, 'utf8')), {
      schemaVersion: STATE_SCHEMA_VERSION,
      actorCalls: 1,
      updatedAtUtc: '2026-10-30T02:00:00.000Z',
    });
  });

  it('refuses every paid call when the accounting can be neither read nor written', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    await writeFile(join(dir, 'occupied.txt'), 'not a folder', 'utf8');
    const env = { [GUARD_STATE_ENV]: join(dir, 'occupied.txt', 'state.json') };
    const call = () => runGuard({ event: varnmillCall(), env, limits: capped(1) });

    const first = decisionOf(await call());
    const second = decisionOf(await call());

    assert.equal(first.permissionDecision, 'deny', first.permissionDecisionReason);
    assert.equal(second.permissionDecision, 'deny');
    assert.match(first.permissionDecisionReason, /accounting/i);
  });

  it('refuses a paid call when the stored accounting is corrupt, and leaves the file exactly as it is', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const statePath = join(dir, 'state.json');
    const corruptions = [
      'not json at all',
      '[]',
      JSON.stringify({ actorCalls: 3 }),
      accounting({ schemaVersion: 1, chargeUsd: 0.5 }),
      accounting({ actorCalls: -1 }),
      accounting({ actorCalls: 2.5 }),
      accounting({ actorCalls: '3' }),
      accounting({ actorCalls: null }),
    ];

    for (const corrupt of corruptions) {
      await writeFile(statePath, corrupt, 'utf8');

      const answer = decisionOf(await runGuard({ event: varnmillCall(), env: { [GUARD_STATE_ENV]: statePath }, limits: capped(600) }));

      assert.equal(answer.permissionDecision, 'deny', corrupt);
      assert.match(answer.permissionDecisionReason, /accounting/i);
      assert.equal(await readFile(statePath, 'utf8'), corrupt, 'a corrupt accounting is never written over with an empty count');
    }
  });

  it('refuses a paid call it cannot reserve durably', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const statePath = join(dir, 'state.json');
    const storage = {
      ...fileStateStorage,
      writeState: () => {
        const error = new Error('read-only file system');
        error.code = 'EROFS';
        throw error;
      },
    };

    const answer = decisionOf(
      await runGuard({ event: varnmillCall(), env: { [GUARD_STATE_ENV]: statePath }, limits: capped(600), storage }),
    );

    assert.equal(answer.permissionDecision, 'deny');
    assert.match(answer.permissionDecisionReason, /reserved/i);
    assert.equal(existsSync(statePath), false);
  });

  it('refuses a paid call when the accounting cannot be locked, and does not read it without the lock', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const storage = {
      ...fileStateStorage,
      acquireLock: () => Promise.resolve({ acquired: false, problem: 'EACCES' }),
      readState: () => assert.fail('the accounting must not be read outside the lock'),
      releaseLock: () => {},
    };

    const answer = decisionOf(
      await runGuard({ event: varnmillCall(), env: { [GUARD_STATE_ENV]: join(dir, 'state.json') }, limits: capped(600), storage }),
    );

    assert.equal(answer.permissionDecision, 'deny');
    assert.match(answer.permissionDecisionReason, /locked/i);
  });

  it('releases the lock it took, so the next call is decided rather than refused', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const statePath = join(dir, 'state.json');
    const call = () => runGuard({ event: varnmillCall(), env: { [GUARD_STATE_ENV]: statePath }, limits: capped(600) });

    const first = decisionOf(await call());
    const second = decisionOf(await call());

    assert.equal(first.permissionDecision, 'allow', first.permissionDecisionReason);
    assert.equal(second.permissionDecision, 'allow', second.permissionDecisionReason);
    assert.equal(existsSync(`${statePath}.lock`), false);
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).actorCalls, 2);
  });

  it('writes nothing for a call it refuses, because a call that was never made spends nothing', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const statePath = join(dir, 'state.json');
    const stored = accounting({ actorCalls: 3 });
    await writeFile(statePath, stored, 'utf8');

    const answer = decisionOf(
      await runGuard({
        event: apifyEvent({ actor: UNCONFIRMED[1], input: { query: 'coca cola' } }),
        env: { [GUARD_STATE_ENV]: statePath },
        limits: capped(600),
      }),
    );

    assert.equal(answer.permissionDecision, 'deny');
    assert.match(answer.permissionDecisionReason, /not confirmed/i);
    assert.equal(await readFile(statePath, 'utf8'), stored);
  });

  it('still guards the output folder when the accounting is unusable, because a write costs nothing', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    await writeFile(join(dir, '7001.json'), '{}', 'utf8');
    const env = { [GUARD_STATE_ENV]: join(dir, 'occupied.txt', 'state.json'), [OUTPUT_DIR_ENV]: dir };
    await writeFile(join(dir, 'occupied.txt'), 'not a folder', 'utf8');

    const fresh = decisionOf(await runGuard({ event: writeEvent(join(dir, '7002.json')), env }));
    const rewrite = decisionOf(await runGuard({ event: writeEvent(join(dir, '7001.json')), env }));

    assert.equal(fresh.permissionDecision, 'allow', fresh.permissionDecisionReason);
    assert.equal(rewrite.permissionDecision, 'deny');
  });
});

// Two Apify calls can be in flight at once, each in its own hook process, so the reservation is
// taken across processes or it is not taken at all. These tests are the real thing: several node
// processes, released together by a shared start instant, against one accounting file.
const guardUrl = pathToFileURL(fileURLToPath(new URL('./apify-guard.mjs', import.meta.url))).href;
const execFileAsync = promisify(execFile);

const runnerSource = () =>
  [
    `import { loadLimits, runGuard } from ${JSON.stringify(guardUrl)};`,
    '',
    'const [statePath, limitsPath, startAtMs] = process.argv.slice(2);',
    'const waitMs = Number(startAtMs) - Date.now();',
    'if (waitMs > 0) await new Promise((done) => { setTimeout(done, waitMs); });',
    '',
    'const answer = await runGuard({',
    '  event: {',
    "    hook_event_name: 'PreToolUse',",
    "    tool_name: 'mcp__apify__call-actor',",
    `    tool_input: { actor: ${JSON.stringify(VARNMILL)}, input: { query: 'coca cola' } },`,
    '  },',
    `  env: { ${JSON.stringify(GUARD_STATE_ENV)}: statePath },`,
    '  limits: loadLimits(limitsPath),',
    '});',
    '',
    "process.stdout.write(JSON.parse(answer.out).hookSpecificOutput.permissionDecision);",
    '',
  ].join('\n');

async function race(perRun, processes) {
  dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
  const statePath = join(dir, 'state.json');
  const limitsPath = join(dir, 'limits.json');
  const runnerPath = join(dir, 'runner.mjs');
  await writeFile(limitsPath, JSON.stringify({ ...limits, perRun }), 'utf8');
  await writeFile(runnerPath, runnerSource(), 'utf8');

  const startAt = Date.now() + 500;
  const answers = await Promise.all(
    Array.from({ length: processes }, () => execFileAsync(process.execPath, [runnerPath, statePath, limitsPath, String(startAt)])),
  );

  return {
    decisions: answers.map(({ stdout }) => stdout.trim()),
    stored: JSON.parse(await readFile(statePath, 'utf8')),
  };
}

describe('parallel hook invocations', () => {
  it('lets exactly one of four parallel calls take the last call reservation', async () => {
    const { decisions, stored } = await race({ maxActorCalls: 1 }, 4);

    assert.equal(decisions.filter((decision) => decision === 'allow').length, 1, decisions.join(','));
    assert.equal(decisions.filter((decision) => decision === 'deny').length, 3);
    assert.equal(stored.actorCalls, 1);
  });

  it('accounts for every one of four parallel calls it allows, losing no increment', async () => {
    const { decisions, stored } = await race({ maxActorCalls: 4 }, 4);

    assert.deepEqual(decisions, ['allow', 'allow', 'allow', 'allow']);
    assert.equal(stored.actorCalls, 4);
    assert.equal(Object.hasOwn(stored, 'chargeUsd'), false);
  });
});


describe('verified retailer input contracts', () => {
  it('forces off Kmart reviews, stores, delivery and incremental output', () => {
    const actor = 'abotapi/kmart-au-scraper';
    const answer = decideApify({ actor, input: { searchTerms: ['chocolate'], fetchReviews: true, maxReviews: 20,
      storePostcodes: ['2600'], storeNames: ['Canberra'], includeClickAndCollect: true,
      verifyOnlineDelivery: true, incrementalMode: true } }, { rules: confirming(actor) });
    assert.equal(answer.decision, 'allow');
    const input = inputOf(answer);
    assert.equal(input.fetchReviews, false);
    assert.equal(input.maxReviews, 0);
    assert.deepEqual(input.storePostcodes, []);
    assert.deepEqual(input.storeNames, []);
    assert.equal(input.includeClickAndCollect, false);
    assert.equal(input.verifyOnlineDelivery, false);
    assert.equal(input.incrementalMode, false);
    assert.equal(input.maxPages, 3);
    assert.deepEqual(input.searchTerms, ['chocolate']);
  });

  it('bounds BIG W internal paging and keeps pricing without reviews', () => {
    const actor = 'axlymxp/bigw-product-scraper';
    const answer = decideApify({ actor, input: { searchQueries: ['chocolate'],
      includePricing: false, includeReviewStats: true, includeReviews: true } }, { rules: confirming(actor) });
    assert.equal(answer.decision, 'allow');
    const input = inputOf(answer);
    assert.equal(input.maxPagesPerQuery, 3);
    assert.equal(input.includePricing, true);
    assert.equal(input.includeReviews, false);
    assert.equal(input.includeReviewStats, false);
    assert.deepEqual(input.searchQueries, ['chocolate']);
  });

  it('uses Dromb item lookup, not a keyword search or invented maxItems', () => {
    const actor = 'dromb/big-w-au-product-scraper';
    const answer = decideApify({ actor, input: { operation: 'search', item_id: '123' } }, { rules: confirming(actor) });
    assert.equal(answer.decision, 'allow');
    assert.equal(inputOf(answer).operation, 'item');
    assert.equal(inputOf(answer).item_id, '123');
    assert.equal(inputOf(answer).maxItems, undefined);
    assert.equal(inputOf(answer).limit, 1);
  });

  for (const [actor, field, query] of [
    ['abotapi/kmart-au-scraper', 'maxPages', { searchTerms: ['chocolate'] }],
    ['axlymxp/bigw-product-scraper', 'maxPagesPerQuery', { searchQueries: ['chocolate'] }],
  ]) {
    it(`keeps a smaller ${field} for ${actor} rather than raising it to the cap`, () => {
      const answer = decideApify({ actor, input: { ...query, [field]: 1 } }, { rules: confirming(actor) });
      assert.equal(answer.decision, 'allow');
      assert.equal(inputOf(answer)[field], 1);
    });

    it(`refuses a ${field} above the cap for ${actor}`, () => {
      const answer = decideApify({ actor, input: { ...query, [field]: 4 } }, { rules: confirming(actor) });
      assert.equal(answer.decision, 'deny');
      assert.match(answer.reason, new RegExp(`${field} may be at most 3`));
    });

    it(`refuses a zero or malformed ${field} for ${actor}, which these actors read as every page`, () => {
      for (const asked of [0, -1, 1.5, '2']) {
        const answer = decideApify({ actor, input: { ...query, [field]: asked } }, { rules: confirming(actor) });
        assert.equal(answer.decision, 'deny', String(asked));
      }
    });

    it(`does not force ${field} for ${actor}, so a request is judged rather than overwritten`, () => {
      assert.equal(limits.actors[actor].pageLimitField, field);
      assert.equal(Object.hasOwn(limits.actors[actor].forcedInput, field), false);
    });
  }
});
