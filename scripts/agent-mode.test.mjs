// Tests for the provider-mode record and resolver (#339).
// Run with: node --test scripts/agent-mode.test.mjs (also imported by validate-agent-workflows.test.mjs).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildModeRecord, resolveMode, implementerForBranch, MODE_RECORD_AUTHOR, ROUTES } from './agent-mode.mjs';

const ISSUE = 245;
const CLAUDE_BRANCH = `agent/issue-${ISSUE}-take-inventory`;
const COPILOT_BRANCH = 'copilot/fix-245-take-inventory';
const comment = (body, fields = {}) => ({ id: 1, author: MODE_RECORD_AUTHOR, created_at: '2026-10-03T01:00:00Z', body, ...fields });
const recordComment = (label, fields = {}, issue = ISSUE) => comment(`Provider mode recorded. Run: x\n${buildModeRecord({ issue, label, run: '77' })}`, fields);
const resolve = (headRef, comments, extra = {}) => resolveMode({ issue: ISSUE, headRef, expectedImplementer: implementerForBranch(headRef), comments, ...extra });

test('records name the issue, label, derived mode and claiming run only', () => {
  assert.equal(buildModeRecord({ issue: 12, label: 'agent-ready-claude-high', run: '99' }), '<!-- agent-routing-mode:v1 {"issue":12,"label":"agent-ready-claude-high","mode":"cross-claude","run":"99"} -->');
  for (const bad of [{ issue: 12, label: 'agent-working', run: '1' }, { issue: 0, label: 'agent-ready-claude', run: '1' }, { issue: 12, label: 'agent-ready-claude', run: 'x' }]) {
    assert.throws(() => buildModeRecord(bad));
  }
});

test('all four routes resolve from their record, with the reviewer from trusted policy', () => {
  for (const [label, branch, mode] of [
    ['agent-ready-claude', CLAUDE_BRANCH, 'cross-claude'],
    ['agent-ready-copilot-low', COPILOT_BRANCH, 'cross-copilot'],
    ['agent-ready-full-claude', CLAUDE_BRANCH, 'full-claude'],
    ['agent-ready-full-copilot', COPILOT_BRANCH, 'full-copilot'],
  ]) {
    const result = resolve(branch, [recordComment(label)], { fullProviderEnabled: true });
    assert.equal(result.mode, mode);
    assert.equal(result.reviewer, ROUTES[mode].reviewer);
    assert.equal(result.sameProviderReview, ROUTES[mode].sameProviderReview);
    assert.equal(result.source, 'record');
  }
});

test('full-provider records fail closed while the routes are not enabled', () => {
  assert.throws(() => resolve(CLAUDE_BRANCH, [recordComment('agent-ready-full-claude')]), /not enabled yet/);
  assert.throws(() => resolve(COPILOT_BRANCH, [recordComment('agent-ready-full-copilot')]), /not enabled yet/);
});

test('legacy tasks without a record keep their branch-derived cross-review route', () => {
  assert.deepEqual(resolve(CLAUDE_BRANCH, []), { issue: ISSUE, mode: 'cross-claude', implementer: 'claude', reviewer: 'copilot', sameProviderReview: false, source: 'legacy' });
  assert.equal(resolve(COPILOT_BRANCH, [comment('Implementation model: sonnet (standard).')]).mode, 'cross-copilot');
});

test('records written by anyone but the workflow bot are ignored, so they cannot forge a mode', () => {
  const forged = recordComment('agent-ready-copilot', { author: 'someone' });
  assert.equal(resolve(CLAUDE_BRANCH, [forged]).source, 'legacy');
  assert.equal(resolve(CLAUDE_BRANCH, [recordComment('agent-ready-claude'), { ...recordComment('agent-ready-full-claude'), author: 'Copilot', created_at: '2026-10-03T09:00:00Z' }]).mode, 'cross-claude');
});

test('a record that conflicts with the pull request fails closed instead of switching provider', () => {
  assert.throws(() => resolve(CLAUDE_BRANCH, [recordComment('agent-ready-copilot')]), /never switched automatically/);
  assert.throws(() => resolve(COPILOT_BRANCH, [recordComment('agent-ready-claude')]), /never switched automatically/);
  // A human relabel that started a new run supersedes the old record, so the old pull request is refused.
  const relabelled = [recordComment('agent-ready-claude', { id: 1 }), recordComment('agent-ready-copilot', { id: 2, created_at: '2026-10-03T05:00:00Z' })];
  assert.throws(() => resolve(CLAUDE_BRANCH, relabelled), /never switched automatically/);
  assert.equal(resolve(COPILOT_BRANCH, relabelled).mode, 'cross-copilot');
  // Comments listed out of order still resolve to the newest claim.
  assert.equal(resolve(COPILOT_BRANCH, [...relabelled].reverse()).mode, 'cross-copilot');
});

test('malformed, tampered or mismatched records fail closed', () => {
  const marker = (json) => comment(`<!-- agent-routing-mode:v1 ${json} -->`);
  for (const bad of [
    marker('{"issue":245,"label":"agent-ready-claude","mode":"full-claude","run":"1"}'),
    marker('{"issue":245,"label":"agent-ready-claude","mode":"cross-claude","run":"1","reviewer":"claude"}'),
    marker('{"issue":245,"label":"agent-ready-gemini","mode":"cross-claude","run":"1"}'),
    marker('{"issue":"245","label":"agent-ready-claude","mode":"cross-claude","run":"1"}'),
    marker('{not json}'),
    comment('agent-routing-mode:v1 without the comment wrapper'),
    comment(`${buildModeRecord({ issue: ISSUE, label: 'agent-ready-claude', run: '1' })}\n${buildModeRecord({ issue: ISSUE, label: 'agent-ready-claude', run: '2' })}`),
  ]) {
    assert.throws(() => resolve(CLAUDE_BRANCH, [bad]), /provider-mode record|Malformed/, bad.body);
  }
  assert.throws(() => resolve(CLAUDE_BRANCH, [recordComment('agent-ready-claude', {}, 999)]), /names issue #999/);
});

test('the pull request identity must match the issue and the expected implementer', () => {
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: 'agent/issue-9-other', expectedImplementer: 'claude', comments: [] }), /does not belong to issue/);
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: CLAUDE_BRANCH, expectedImplementer: 'copilot', comments: [] }), /belongs to claude/);
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: 'feature/x', expectedImplementer: 'claude', comments: [] }), /not an agent/);
  assert.throws(() => resolveMode({ issue: ISSUE, headRef: CLAUDE_BRANCH, expectedImplementer: 'claude', comments: undefined }), /could not be read/);
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
    ['.github/workflows/agent-copilot.yml', "if (process.env.AGENT_MODE !== 'cross-copilot') throw", 'if (false) throw'],
  ]) {
    assert.throws(() => verifyProviderModeProvenance(mutate(path, from, to)), /missing required|forbidden|must/, `${path}: ${from}`);
  }
  // Consuming readiness before the record exists is rejected for both implementers.
  const implement = readRepositoryFile('.github/workflows/agent-implement.yml');
  const claimStart = implement.indexOf('          # Record the provider mode on the issue');
  const relabel = '          gh issue edit "$ISSUE_NUMBER" --repo "$GITHUB_REPOSITORY" \\\n            --remove-label "$READY_LABEL" --add-label agent-working\n';
  assert.ok(claimStart > 0 && implement.includes(relabel));
  const reordered = implement.replace(relabel, '').replace('          # Record the provider mode on the issue', relabel + '          # Record the provider mode on the issue');
  assert.throws(() => verifyProviderModeProvenance(p => p === '.github/workflows/agent-implement.yml' ? reordered : readRepositoryFile(p)), /must be recorded before readiness/);
});
