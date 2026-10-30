// Tests for scripts/price-collector/apify-guard.mjs and apify-limits.json: the `PreToolUse` hook
// that enforces the collector's spending caps and its write-once output folder (issue #926, parent
// #817).
//
// The point of the hook is that **the caps are not Claude's to obey**: an unlisted actor, a call with
// no charge ceiling, a call past the run's own caps and a rewrite of a finished result file are all
// refused by code, in a separate process, before the tool runs. No test makes a live call: the hook
// never calls anything itself.
import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, it } from 'node:test';
import { GUARD_STATE_ENV, OUTPUT_DIR_ENV, decide, emptyState, loadLimits, loadMcpConfig, runGuard } from './apify-guard.mjs';

const limits = loadLimits();
const VARNMILL = 'varnmill/au-supermarket-prices';

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

const decideApify = (toolInput, { state = emptyState(), toolName } = {}) =>
  decide({ event: apifyEvent(toolInput, toolName), limits, state, outputDir: '/work/price-collector-out', exists: () => false });

const inputOf = (answer) => answer.updatedInput?.input ?? answer.updatedInput ?? {};

let dir = null;

afterEach(async () => {
  if (dir !== null) await rm(dir, { recursive: true, force: true });
  dir = null;
});

describe('the limits file', () => {
  it('names the actors the owner chose in testing, and only those', () => {
    assert.deepEqual(Object.keys(limits.actors).sort(), [
      'abotapi/kmart-product-scraper',
      'axlymxp/bigw-product-search',
      'dromb/bigw-sku-lookup',
      VARNMILL,
    ]);
  });

  it('gives every actor a page size, a page cap, a charge ceiling and an item-limit field', () => {
    for (const [actorId, actor] of Object.entries(limits.actors)) {
      assert.ok(Number.isInteger(actor.pageSize) && actor.pageSize > 0, `${actorId} pageSize`);
      assert.ok(Number.isInteger(actor.maxPages) && actor.maxPages > 0, `${actorId} maxPages`);
      assert.ok(actor.maxTotalChargeUsd > 0, `${actorId} maxTotalChargeUsd`);
      assert.ok(typeof actor.itemLimitField === 'string' && actor.itemLimitField !== '', `${actorId} itemLimitField`);
      assert.ok(Array.isArray(actor.serves) && actor.serves.length > 0, `${actorId} serves`);
    }
  });

  it('bounds one collector run by a call count and a charge ceiling', () => {
    assert.ok(Number.isInteger(limits.perRun.maxActorCalls) && limits.perRun.maxActorCalls > 0);
    assert.ok(limits.perRun.maxTotalChargeUsd > 0);
  });

  it('forces the enrichments and the watcher off, which is where the money goes', () => {
    assert.equal(limits.actors[VARNMILL].forcedInput.onlyChanged, false);
    assert.equal(limits.actors[VARNMILL].forcedInput.fetchPaidDetails, false);
    assert.equal(limits.actors['abotapi/kmart-product-scraper'].forcedInput.includeReviews, false);
    assert.equal(limits.actors['abotapi/kmart-product-scraper'].forcedInput.includeLocalStock, false);
  });

  it('says which actor ids are still to be confirmed in the dry run', () => {
    assert.equal(limits.actors[VARNMILL].actorIdConfirmed, true);
    for (const actorId of ['abotapi/kmart-product-scraper', 'axlymxp/bigw-product-search', 'dromb/bigw-sku-lookup']) {
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
});

describe('the limits it injects or verifies', () => {
  it('injects the charge ceiling and the item limit when the call carries neither', () => {
    const answer = decideApify({ actor: VARNMILL, input: { query: 'coca cola 375ml' } });

    assert.equal(answer.decision, 'allow');
    assert.equal(inputOf(answer).maxTotalChargeUsd, limits.actors[VARNMILL].maxTotalChargeUsd);
    assert.equal(inputOf(answer).maxItems, limits.actors[VARNMILL].pageSize);
  });

  it('keeps a call that asks for less than the limits allow', () => {
    const answer = decideApify({ actor: VARNMILL, input: { maxItems: 10, maxTotalChargeUsd: 0.01 } });

    assert.equal(answer.decision, 'allow');
    assert.equal(inputOf(answer).maxItems, 10);
    assert.equal(inputOf(answer).maxTotalChargeUsd, 0.01);
  });

  it('blocks a charge ceiling above the actor\'s own', () => {
    const answer = decideApify({
      actor: VARNMILL,
      input: { maxTotalChargeUsd: limits.actors[VARNMILL].maxTotalChargeUsd + 1 },
    });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /maxTotalChargeUsd/);
  });

  it('blocks an item limit above the actor\'s page size', () => {
    const answer = decideApify({ actor: VARNMILL, input: { maxItems: limits.actors[VARNMILL].pageSize + 1 } });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /maxItems/);
  });

  it('blocks a charge ceiling or item limit that is not a positive number', () => {
    assert.equal(decideApify({ actor: VARNMILL, input: { maxTotalChargeUsd: 0 } }).decision, 'deny');
    assert.equal(decideApify({ actor: VARNMILL, input: { maxItems: 0 } }).decision, 'deny');
    assert.equal(decideApify({ actor: VARNMILL, input: { maxItems: 'all' } }).decision, 'deny');
  });

  it('forces the enrichments off however the call asked for them', () => {
    const answer = decideApify({ actor: VARNMILL, input: { onlyChanged: true, fetchPaidDetails: true } });

    assert.equal(answer.decision, 'allow');
    assert.equal(inputOf(answer).onlyChanged, false);
    assert.equal(inputOf(answer).fetchPaidDetails, false);

    const kmart = decideApify({
      actor: 'abotapi/kmart-product-scraper',
      input: { includeReviews: true, includeLocalStock: true },
    });
    assert.equal(kmart.decision, 'allow');
    assert.equal(inputOf(kmart).includeReviews, false);
    assert.equal(inputOf(kmart).includeLocalStock, false);
  });

  it('fixes an input the call carries flat rather than under input', () => {
    const answer = decideApify({ actor: VARNMILL, query: 'coca cola' });

    assert.equal(answer.decision, 'allow');
    assert.equal(answer.updatedInput.maxTotalChargeUsd, limits.actors[VARNMILL].maxTotalChargeUsd);
    assert.equal(answer.updatedInput.actor, VARNMILL);
    assert.equal(answer.updatedInput.query, 'coca cola');
  });
});

describe('the per-run caps', () => {
  it('counts every allowed call and the charge it committed to', () => {
    const first = decideApify({ actor: VARNMILL, input: { maxTotalChargeUsd: 0.01 } });

    assert.equal(first.state.actorCalls, 1);
    assert.equal(first.state.chargeUsd, 0.01);

    const second = decide({
      event: apifyEvent({ actor: VARNMILL, input: { maxTotalChargeUsd: 0.02 } }),
      limits,
      state: first.state,
      outputDir: '/work/price-collector-out',
      exists: () => false,
    });

    assert.equal(second.decision, 'allow', second.reason);
    assert.equal(second.state.actorCalls, 2);
    assert.equal(Math.round(second.state.chargeUsd * 100) / 100, 0.03);
  });

  it('blocks the call that would pass the run\'s call cap', () => {
    const state = { ...emptyState(), actorCalls: limits.perRun.maxActorCalls };

    const answer = decideApify({ actor: VARNMILL }, { state });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /calls/i);
    assert.equal(answer.state.actorCalls, limits.perRun.maxActorCalls);
  });

  it('blocks the call that would pass the run\'s charge cap, and keeps the earlier spend', () => {
    const state = { ...emptyState(), chargeUsd: limits.perRun.maxTotalChargeUsd };

    const answer = decideApify({ actor: VARNMILL }, { state });

    assert.equal(answer.decision, 'deny');
    assert.match(answer.reason, /charge/i);
    assert.equal(answer.state.chargeUsd, limits.perRun.maxTotalChargeUsd);
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
  it('keeps the run\'s spend in its state file and blocks once the cap is reached', async () => {
    dir = await mkdtemp(join(tmpdir(), 'price-collector-guard-'));
    const env = { [GUARD_STATE_ENV]: join(dir, 'state.json'), [OUTPUT_DIR_ENV]: dir };
    const call = () => runGuard({ event: apifyEvent({ actor: VARNMILL }), env, limits: { ...limits, perRun: { maxActorCalls: 2, maxTotalChargeUsd: 99 } } });

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
