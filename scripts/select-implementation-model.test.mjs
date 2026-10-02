import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { prepareSelection, resolveSelection, verifySnapshot } from './select-implementation-model.mjs';
import { verifyImplementationModelSelection, readRepositoryFile } from './validate-agent-workflows.mjs';

const issue = (label, extra = {}) => ({ state: 'OPEN', title: 'Fix typo', body: 'Bounded task', labels: [{ name: label }], ...extra });
const prepare = (provider, tier = '', extra = {}) => prepareSelection({ provider, label: `agent-ready-${provider}${tier}`, issue: issue(`agent-ready-${provider}${tier}`, extra) });

for (const provider of ['claude', 'copilot']) {
  test(`${provider}: explicit tiers bypass triage and select fixed models`, () => {
    const low = resolveSelection(prepare(provider, '-low'));
    const high = resolveSelection(prepare(provider, '-high'));
    assert.equal(low.tier, 'low');
    assert.equal(high.tier, 'high');
    assert.equal(low.maxTurns, 300);
    assert.equal(high.maxTurns, 250);
    assert.notEqual(low.model, high.model);
  });
  test(`${provider}: default triage selects low, standard or high and stops only for clarification`, () => {
    const selection = prepare(provider);
    assert.equal(selection.triage, true);
    assert.equal(resolveSelection(selection, { tier: 'low', reason: 'One local change' }).tier, 'low');
    assert.equal(resolveSelection(selection, { tier: 'standard', reason: 'Several related components' }).tier, 'standard');
    const high = resolveSelection(selection, { tier: 'high', reason: 'Broad structural change' });
    assert.equal(high.tier, 'high');
    assert.equal(high.model, resolveSelection(prepare(provider, '-high')).model);
    assert.equal(high.maxTurns, 250);
    assert.throws(() => resolveSelection(selection, { tier: 'clarification-required', reason: 'Acceptance criteria contradict' }), /clarification required/i);
    assert.throws(() => resolveSelection(selection, { tier: 'high-required', reason: 'Old tier' }), /Invalid triage/);
    assert.throws(() => resolveSelection(selection, { tier: 'opus', reason: 'Model ID instead of tier' }), /Invalid triage/);
  });
}
test('malformed, injected or missing model output fails closed', () => {
  const selection = prepare('claude');
  for (const result of [null, {}, { tier: 'low', reason: '' }, { tier: 'low', reason: 'a\nmodel=opus' }, { tier: 'low', reason: 'a\u007fb' }, { tier: 'low', reason: 'a'.repeat(241) }, { tier: 'low', reason: 'ok', model: 'opus' }]) {
    assert.throws(() => resolveSelection(selection, result), /Invalid triage/);
  }
});
test('conflicting providers, removed labels, closed issues and active work are rejected', () => {
  for (const labels of [[], ['agent-ready-claude', 'agent-ready-claude-high'], ['agent-ready-claude', 'agent-ready-copilot'], ['agent-ready-claude', 'agent-working']]) {
    assert.throws(() => prepare('claude', '', { labels: labels.map(name => ({ name })) }), /readiness|active/i);
  }
  assert.throws(() => prepare('claude', '', { state: 'CLOSED' }), /open/i);
  assert.throws(() => prepareSelection({ provider: 'claude', label: 'agent-ready-copilot', issue: issue('agent-ready-copilot') }), /label/i);
});
test('live task edits or readiness changes invalidate the selection before implementation', () => {
  const snapshot = issue('agent-ready-claude');
  const selection = prepareSelection({ provider: 'claude', label: 'agent-ready-claude', issue: snapshot });
  assert.doesNotThrow(() => verifySnapshot(selection, snapshot));
  for (const changed of [{ ...snapshot, body: 'Expanded scope' }, { ...snapshot, title: 'Changed task' }, { ...snapshot, labels: [] }, { ...snapshot, labels: [...snapshot.labels, { name: 'agent-working' }] }]) {
    assert.throws(() => verifySnapshot(selection, changed), /changed|readiness|active/i);
  }
});
test('Claude reruns can resume working tasks, but a fresh run and Copilot cannot reassign them', () => {
  const working = issue('agent-working');
  assert.doesNotThrow(() => prepareSelection({ provider: 'claude', label: 'agent-ready-claude-low', issue: working, attempt: 2 }));
  assert.throws(() => prepareSelection({ provider: 'claude', label: 'agent-ready-claude-low', issue: working }), /active|readiness/i);
  assert.throws(() => prepareSelection({ provider: 'copilot', label: 'agent-ready-copilot', issue: working, attempt: 2 }), /active|readiness/i);
});

test('workflow contract rejects missing model gates, triage write tools and unbounded models', () => {
  const selectionPath = '.github/workflows/agent-model-selection.yml';
  const implementPath = '.github/workflows/agent-implement.yml';
  const copilotPath = '.github/workflows/agent-copilot.yml';
  for (const [path, from, to] of [
    [selectionPath, '--model haiku', '--model opus'],
    [selectionPath, '"enum":["low","standard","high","clarification-required"]', '"enum":["low","standard","high","opus"]'],
    [selectionPath, '--max-turns 4', '--max-turns 100'],
    [selectionPath, '--allowedTools "Read"', '--allowedTools "Read,Write"'],
    [selectionPath, 'issues: read', 'issues: write'],
    [selectionPath, 'ref: ${{ github.workflow_sha }}', 'ref: develop'],
    [implementPath, 'needs: [preflight, model]', 'needs: preflight'],
    [implementPath, '--model ${{ needs.model.outputs.model }}', '--model opus'],
    [copilotPath, 'model: $model', 'model: ""'],
  ]) {
    const original = readRepositoryFile(path);
    assert.ok(original.includes(from));
    assert.throws(() => verifyImplementationModelSelection(p => p === path ? original.replace(from, to) : readRepositoryFile(p)), /missing required|forbidden/);
  }
});

test('real Copilot assignment shell forwards each tier model, refuses scope edits before mutations and reports model rejection', () => {
  const workflow = readRepositoryFile('.github/workflows/agent-copilot.yml');
  const script = workflow.slice(workflow.indexOf('        run: |', workflow.indexOf('      - name: Relabel and assign Copilot'))).split('\n').slice(1).map(line => line.startsWith('          ') ? line.slice(10) : line).join('\n');
  const dir = mkdtempSync(join(tmpdir(), 'model-assignment-'));
  try {
    writeFileSync(join(dir, 'gh'), `#!/bin/bash
if [[ "$1 $2" == "issue view" ]]; then cat "$TEST_ISSUE"; exit 0; fi
echo "$*" >> "$TEST_CALLS"
if [[ "$1" == "api" ]]; then cat > "$TEST_PAYLOAD"; if [[ -n "$TEST_API_FAIL" ]]; then echo "$TEST_API_FAIL" >&2; exit 1; fi; fi
`, { mode: 0o755 });
    writeFileSync(join(dir, 'assign.sh'), script);
    for (const suffix of ['-low', '', '-high']) {
      const snapshot = issue(`agent-ready-copilot${suffix}`);
      const prepared = prepareSelection({ provider: 'copilot', label: `agent-ready-copilot${suffix}`, issue: snapshot });
      const resolved = resolveSelection(prepared, suffix === '' ? { tier: 'standard', reason: 'Normal development' } : undefined);
      writeFileSync(join(dir, 'issue.json'), JSON.stringify(snapshot));
      writeFileSync(join(dir, 'calls'), '');
      const env = { ...process.env, PATH: `${dir}:${process.env.PATH}`, TEST_ISSUE: join(dir, 'issue.json'), TEST_CALLS: join(dir, 'calls'), TEST_PAYLOAD: join(dir, 'payload.json'), ISSUE_NUMBER: '123', GITHUB_REPOSITORY: 'test/repo', READY_LABEL: prepared.label, TASK_FINGERPRINT: prepared.fingerprint, IMPLEMENTATION_MODEL: resolved.model, MODEL_TIER: resolved.tier, MODEL_REASON: resolved.reason, COPILOT_AGENT_TOKEN: 'synthetic-token', GH_TOKEN: 'synthetic-token', RUN_URL: 'https://example.invalid/run' };
      const run = spawnSync('bash', [join(dir, 'assign.sh')], { env, encoding: 'utf8' });
      assert.equal(run.status, 0, run.stderr);
      const payload = JSON.parse(readFileSync(join(dir, 'payload.json'), 'utf8'));
      assert.equal(payload.agent_assignment.model, resolved.model);
      assert.equal(payload.agent_assignment.base_branch, 'develop');
      assert.deepEqual(payload.assignees, ['copilot-swe-agent[bot]']);
      assert.match(payload.agent_assignment.custom_instructions, /Do not switch models/);
      assert.ok(readFileSync(join(dir, 'calls'), 'utf8').includes(`--remove-label ${prepared.label} --add-label agent-working`));
      writeFileSync(join(dir, 'issue.json'), JSON.stringify({ ...snapshot, body: 'New scope' }));
      writeFileSync(join(dir, 'calls'), '');
      const stale = spawnSync('bash', [join(dir, 'assign.sh')], { env, encoding: 'utf8' });
      assert.notEqual(stale.status, 0);
      assert.equal(readFileSync(join(dir, 'calls'), 'utf8'), '');
      writeFileSync(join(dir, 'issue.json'), JSON.stringify(snapshot));
      const rejected = spawnSync('bash', [join(dir, 'assign.sh')], { env: { ...env, TEST_API_FAIL: 'Model not available (HTTP 422)' }, encoding: 'utf8' });
      assert.notEqual(rejected.status, 0);
      const calls = readFileSync(join(dir, 'calls'), 'utf8');
      assert.ok(calls.includes('--add-label agent-blocked'));
      assert.ok(calls.includes(`Requested implementation model: ${resolved.model}`));
      assert.ok(calls.includes('GitHub responded: Model not available (HTTP 422)'));
    }
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
