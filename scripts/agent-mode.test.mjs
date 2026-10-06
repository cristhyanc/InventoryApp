// Tests for the provider-mode resolver (#339).
// Run with: node --test scripts/agent-mode.test.mjs (also imported by validate-agent-workflows.test.mjs).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { resolveMode, implementerForBranch, verifyPullRequest, ROUTES, FULL_PROVIDER_EXECUTION_ENABLED } from './agent-mode.mjs';
import { claimEvents } from './agent-mode.fixtures.mjs';

const ISSUE = 245;
const CLAUDE_BRANCH = `agent/issue-${ISSUE}-take-inventory`;
const COPILOT_BRANCH = 'copilot/fix-245-take-inventory';
const OPENED = '2026-10-03T02:00:00Z';
// The resolver receives events already flattened by verifyPullRequest's jq filter.
const flat = (events) => events.map(({ id, event, actor, label, created_at }) => ({ id, event, actor: actor.login, actorType: actor.type, label: label.name, created_at }));
const claim = (label, at, firstId) => flat(claimEvents(label, at, firstId));
const resolve = (headRef, events, extra = {}) => resolveMode({ issue: ISSUE, headRef, expectedImplementer: implementerForBranch(headRef), prCreatedAt: OPENED, events, ...extra });

test('both routes resolve from the claim in the label history, with the reviewer from trusted policy', () => {
  assert.deepEqual(Object.keys(ROUTES), ['cross-claude', 'full-claude']);
  for (const [label, branch, mode] of [
    ['agent-ready-claude', CLAUDE_BRANCH, 'cross-claude'],
    ['agent-ready-claude-low', CLAUDE_BRANCH, 'cross-claude'],
    ['agent-ready-full-claude', CLAUDE_BRANCH, 'full-claude'],
  ]) {
    const result = resolve(branch, claim(label), { fullProviderEnabled: true });
    assert.equal(result.mode, mode);
    assert.equal(result.label, label);
    assert.equal(result.reviewer, ROUTES[mode].reviewer);
    assert.equal(result.sameProviderReview, ROUTES[mode].sameProviderReview);
  }
});

test('the full-provider route is enabled by default and fail closed when the kill switch is off', () => {
  assert.equal(FULL_PROVIDER_EXECUTION_ENABLED, true);
  assert.equal(resolve(CLAUDE_BRANCH, claim('agent-ready-full-claude')).mode, 'full-claude');
  assert.throws(() => resolve(CLAUDE_BRANCH, claim('agent-ready-full-claude'), { fullProviderEnabled: false }), /not enabled yet/);
  // The kill switch never affects the cross routes.
  assert.equal(resolve(CLAUDE_BRANCH, claim('agent-ready-claude'), { fullProviderEnabled: false }).mode, 'cross-claude');
});

test('a task with no verifiable claim fails closed instead of becoming a cross-review task', () => {
  assert.throws(() => resolve(CLAUDE_BRANCH, []), /no verifiable claim/);
  // A person adding agent-working, or a readiness label that was never consumed, is not a claim.
  const human = claim('agent-ready-full-claude').map(event => ({ ...event, actor: 'cristhyanc', actorType: 'User' }));
  assert.throws(() => resolve(CLAUDE_BRANCH, human), /no verifiable claim/);
  assert.throws(() => resolve(CLAUDE_BRANCH, claim('agent-ready-claude').slice(0, 1)), /no verifiable claim/);
  assert.throws(() => resolve(CLAUDE_BRANCH, undefined), /could not be read/);
});

test('the claimed label must have been applied by a person and consumed by the claim itself', () => {
  const appliedBy = (fields) => claim('agent-ready-claude').map((event, i) => (i === 0 ? { ...event, ...fields } : event));
  for (const fields of [
    { actor: 'github-actions[bot]', actorType: 'Bot' },
    { actor: 'copilot-swe-agent[bot]', actorType: 'Bot' },
    { actor: 'inventoryapp-agent-automation[bot]', actorType: 'Bot' },
    { actor: 'someone', actorType: 'Organization' },
    { actor: 'cristhyanc', actorType: undefined },
    { actor: undefined, actorType: 'User' },
    { actor: '', actorType: 'User' },
  ]) {
    assert.throws(() => resolve(CLAUDE_BRANCH, appliedBy(fields)), /not applied by a person/, JSON.stringify(fields));
  }
  assert.equal(resolve(CLAUDE_BRANCH, appliedBy({ actor: 'cristhyanc', actorType: 'User' })).label, 'agent-ready-claude');
  // The claim itself must come from the workflow bot as a Bot actor.
  const userNamedLikeBot = claim('agent-ready-claude').map((event, i) => (i > 0 ? { ...event, actorType: 'User' } : event));
  assert.throws(() => resolve(CLAUDE_BRANCH, userNamedLikeBot), /no verifiable claim/);
  // A removal far from the agent-working add is not part of the claim.
  const [applied, removed, working] = claim('agent-ready-claude');
  assert.throws(() => resolve(CLAUDE_BRANCH, [applied, { ...removed, created_at: '2026-10-03T00:59:30Z' }, working]), /exactly one readiness label/);
  // Two readiness labels removed at the claim is ambiguous.
  const second = { ...removed, id: 9, label: 'agent-ready-full-claude' };
  assert.throws(() => resolve(CLAUDE_BRANCH, [applied, removed, second, working]), /exactly one readiness label/);
});

test('Copilot no longer implements: copilot/* branches and retired Copilot readiness labels fail closed', () => {
  assert.equal(implementerForBranch(COPILOT_BRANCH), null);
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: COPILOT_BRANCH, expectedImplementer: 'claude', prCreatedAt: OPENED, events: claim('agent-ready-claude') }), /not an agent\/issue-\* branch/);
  // A claim that consumed a retired Copilot label is not a readiness claim any more.
  for (const retired of ['agent-ready-copilot', 'agent-ready-copilot-high', 'agent-ready-full-copilot']) {
    assert.throws(() => resolve(CLAUDE_BRANCH, claim(retired)), /exactly one readiness label/, retired);
  }
});

test('the newest claim wins regardless of event order, and an older claim cannot overwrite it', () => {
  const history = [...claim('agent-ready-claude', '2026-10-03T00:00:00Z', 1), ...claim('agent-ready-full-claude', '2026-10-03T01:00:00Z', 10)];
  assert.equal(resolve(CLAUDE_BRANCH, history).mode, 'full-claude');
  assert.equal(resolve(CLAUDE_BRANCH, [...history].reverse()).mode, 'full-claude');
});

test('a pull request opened before the newest claim belongs to an earlier run and is refused', () => {
  // Same provider, but the issue was claimed again after this pull request was opened.
  const history = [...claim('agent-ready-claude', '2026-10-03T00:00:00Z', 1), ...claim('agent-ready-claude-high', '2026-10-03T03:00:00Z', 10)];
  assert.throws(() => resolve(CLAUDE_BRANCH, history), /opened before issue #245's latest claim/);
  assert.equal(resolve(CLAUDE_BRANCH, history, { prCreatedAt: '2026-10-03T04:00:00Z' }).label, 'agent-ready-claude-high');
  assert.throws(() => resolve(CLAUDE_BRANCH, claim('agent-ready-claude'), { prCreatedAt: undefined }), /creation time could not be read/);
});

test('the pull request identity must match the issue and the expected implementer', () => {
  const events = claim('agent-ready-claude');
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: 'agent/issue-9-other', expectedImplementer: 'claude', prCreatedAt: OPENED, events }), /does not belong to issue/);
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: CLAUDE_BRANCH, expectedImplementer: 'copilot', prCreatedAt: OPENED, events }), /belongs to claude/);
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: 'feature/x', expectedImplementer: 'claude', prCreatedAt: OPENED, events }), /not an agent/);
});

test('verify-pr rejects unexpected arguments before calling GitHub', () => {
  const valid = { repository: 'cristhyanc/InventoryApp', pr: '12', expectedImplementer: 'claude' };
  for (const bad of [{ pr: '12; rm' }, { pr: '0' }, { repository: 'a/b/c' }, { repository: '--repo=x' }, { expectedImplementer: 'gemini' }, { expectedImplementer: 'copilot' }]) {
    assert.throws(() => verifyPullRequest({ ...valid, ...bad }), /valid|must be claude/, JSON.stringify(bad));
  }
});

test('workflow contract requires the mode gate at every boundary and the record before readiness is consumed', async () => {
  const { verifyProviderModeProvenance, readRepositoryFile, PROVIDER_MODE_GATES, MODE_RESOLVER_FETCH } = await import('./validate-agent-workflows.mjs');
  assert.doesNotThrow(() => verifyProviderModeProvenance());
  const mutate = (path, from, to) => p => {
    const text = readRepositoryFile(p);
    if (p !== path) return text;
    assert.ok(text.includes(from), `${path} must contain ${from}`);
    return text.replace(from, to);
  };
  for (const [path, , , verify] of PROVIDER_MODE_GATES) {
    assert.throws(() => verifyProviderModeProvenance(mutate(path, verify, 'agent_mode="cross-claude"')), /missing required text/, path);
  }
  for (const [path, from, to] of [
    ['.github/workflows/agent-review.yml', MODE_RESOLVER_FETCH, MODE_RESOLVER_FETCH.replace('$GITHUB_WORKFLOW_SHA', '$HEAD_SHA')],
    ['.github/workflows/agent-review.yml', '[ "$live_mode" = "$AGENT_MODE" ] || suppress', 'true || suppress'],
    ['.github/workflows/validate.yml', '      contents: read\n      issues: read\n', ''],
    ['.github/workflows/agent-model-selection.yml', 'mode: ${{ steps.resolve.outputs.mode }}', ''],
  ]) {
    assert.throws(() => verifyProviderModeProvenance(mutate(path, from, to)), /missing required|forbidden|must/, `${path}: ${from}`);
  }
  // A second agent-working add, anywhere, would look like a claim to the resolver.
  const extra = p => (p === '.github/workflows/agent-repair.yml' ? `${readRepositoryFile(p)}\n# gh issue edit 1 --add-label agent-working\n` : readRepositoryFile(p));
  assert.throws(() => verifyProviderModeProvenance(extra), /only the claim steps may add agent-working/);
  // The claim must swap the labels in one edit.
  assert.throws(() => verifyProviderModeProvenance(mutate('.github/workflows/agent-implement.yml', '--remove-label "$READY_LABEL" --add-label agent-working', '--add-label agent-working')), /missing required text/);
});
