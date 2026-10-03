import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { prepareSelection, resolveSelection, verifySnapshot, parseReadinessLabel, READINESS_LABELS, READINESS_LABEL_PATTERN, FULL_READY_LABELS, DEFAULT_READY_LABEL, FULL_PROVIDER_EXECUTION_ENABLED, MODELS } from './select-implementation-model.mjs';
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

test('readiness labels map to trusted routes; Claude-primary is the default', () => {
  assert.equal(DEFAULT_READY_LABEL, 'agent-ready-claude');
  assert.deepEqual(FULL_READY_LABELS, ['agent-ready-full-claude', 'agent-ready-full-copilot']);
  const expected = {
    'agent-ready-claude': ['cross-claude', 'claude', 'copilot', false, 'default'],
    'agent-ready-claude-low': ['cross-claude', 'claude', 'copilot', false, 'low'],
    'agent-ready-claude-high': ['cross-claude', 'claude', 'copilot', false, 'high'],
    'agent-ready-copilot': ['cross-copilot', 'copilot', 'claude', false, 'default'],
    'agent-ready-copilot-low': ['cross-copilot', 'copilot', 'claude', false, 'low'],
    'agent-ready-copilot-high': ['cross-copilot', 'copilot', 'claude', false, 'high'],
    'agent-ready-full-claude': ['full-claude', 'claude', 'claude', true, 'default'],
    'agent-ready-full-copilot': ['full-copilot', 'copilot', 'copilot', true, 'default'],
  };
  assert.deepEqual([...READINESS_LABELS].sort(), Object.keys(expected).sort());
  for (const [label, [mode, implementer, reviewer, sameProviderReview, tier]] of Object.entries(expected)) {
    assert.deepEqual(parseReadinessLabel(label), { label, mode, implementer, reviewer, sameProviderReview, tier });
    assert.ok(READINESS_LABEL_PATTERN.test(label));
  }
  assert.equal(parseReadinessLabel(DEFAULT_READY_LABEL).reviewer, 'copilot');
  for (const other of ['agent-ready', 'agent-ready-full', 'agent-ready-full-claude-high', 'agent-ready-full-claude-low', 'agent-ready-claude-full', 'agent-ready-gemini', 'Agent-Ready-Claude', ' agent-ready-claude', 'agent-ready-claude\n', 'agent-working', null, undefined, 42]) {
    assert.equal(parseReadinessLabel(other), null, String(other));
    if (typeof other === 'string') assert.equal(READINESS_LABEL_PATTERN.test(other), false, other);
  }
});

test('full-provider labels are recognised but cannot start work until every route is connected', () => {
  assert.equal(FULL_PROVIDER_EXECUTION_ENABLED, false);
  for (const provider of ['claude', 'copilot']) {
    const label = `agent-ready-full-${provider}`;
    assert.throws(() => prepareSelection({ provider, label, issue: issue(label) }), /not enabled yet/);
    // A forged selection cannot switch the gate on during the pre-mutation recheck.
    assert.throws(() => verifySnapshot({ provider, label, fingerprint: 'x', fullProviderEnabled: true }, issue(label)), /not enabled yet/);
  }
});

test('full-provider labels use their provider and the existing tier policy once enabled', () => {
  for (const provider of ['claude', 'copilot']) {
    const label = `agent-ready-full-${provider}`;
    const selection = prepareSelection({ provider, label, issue: issue(label), fullProviderEnabled: true });
    assert.equal(selection.mode, `full-${provider}`);
    assert.equal(selection.reviewer, provider);
    assert.equal(selection.sameProviderReview, true);
    assert.equal(selection.triage, true);
    for (const tier of ['low', 'standard', 'high']) assert.equal(resolveSelection(selection, { tier, reason: 'Triage' }).model, MODELS[provider][tier]);
    const other = provider === 'claude' ? 'copilot' : 'claude';
    assert.throws(() => prepareSelection({ provider: other, label, issue: issue(label), fullProviderEnabled: true }), /Invalid implementation label/);
  }
});

test('exactly one readiness label across standard and full variants may authorise a run', () => {
  const conflicts = [
    ['agent-ready-claude', 'agent-ready-full-claude'],
    ['agent-ready-claude', 'agent-ready-full-copilot'],
    ['agent-ready-copilot', 'agent-ready-full-copilot'],
    ['agent-ready-claude-high', 'agent-ready-full-claude'],
    ['agent-ready-full-claude', 'agent-ready-full-copilot'],
  ];
  for (const labels of conflicts) {
    for (const label of labels) {
      const provider = parseReadinessLabel(label).implementer;
      assert.throws(() => prepareSelection({ provider, label, issue: issue(label, { labels: labels.map(name => ({ name })) }), fullProviderEnabled: true }), /Exactly one matching readiness/, labels.join('+'));
    }
  }
  for (const state of ['agent-working', 'agent-architecture-fix', 'agent-review', 'agent-blocked']) {
    for (const label of ['agent-ready-claude', 'agent-ready-copilot', 'agent-ready-full-claude', 'agent-ready-full-copilot']) {
      const provider = parseReadinessLabel(label).implementer;
      assert.throws(() => prepareSelection({ provider, label, issue: issue(label, { labels: [{ name: label }, { name: state }] }), fullProviderEnabled: true }), /active or blocked/);
    }
  }
  // A stale event: the label that fired was swapped for another readiness label before the run.
  assert.throws(() => prepareSelection({ provider: 'claude', label: 'agent-ready-claude', issue: issue('agent-ready-full-claude') }), /Exactly one matching readiness/);
  assert.throws(() => prepareSelection({ provider: 'claude', label: 'agent-ready-full-claude', issue: issue('agent-ready-claude'), fullProviderEnabled: true }), /Exactly one matching readiness/);
  // A Claude re-run may resume its own working task, but never while any readiness label (full included) is present.
  assert.throws(() => prepareSelection({ provider: 'claude', label: 'agent-ready-claude', issue: issue('agent-working', { labels: [{ name: 'agent-working' }, { name: 'agent-ready-full-claude' }] }), attempt: 2 }), /active|readiness/i);
});

test('workflow contract rejects missing model gates, triage write tools and unbounded models', () => {
  const selectionPath = '.github/workflows/agent-model-selection.yml';
  const implementPath = '.github/workflows/agent-implement.yml';
  const copilotPath = '.github/workflows/agent-copilot.yml';
  for (const [path, from, to] of [
    [selectionPath, '--model haiku', '--model opus'],
    [selectionPath, '"enum":["low","standard","high","clarification-required"]', '"enum":["low","standard","high","opus"]'],
    [selectionPath, '--max-turns 8', '--max-turns 100'],
    [selectionPath, '--allowedTools "Read"', '--allowedTools "Read,Write"'],
    [selectionPath, 'issues: read', 'issues: write'],
    [selectionPath, 'ref: ${{ github.workflow_sha }}', 'ref: develop'],
    [implementPath, 'needs: [preflight, model]', 'needs: preflight'],
    [implementPath, '--model ${{ needs.model.outputs.model }}', '--model opus'],
    [copilotPath, 'model: $model', 'model: ""'],
    [copilotPath, '/^agent-ready-(?:(?:claude|copilot)(?:-low|-high)?|full-(?:claude|copilot))$/', '/^agent-ready-(claude|copilot)(-low|-high)?$/'],
    [implementPath, "github.event.label.name == 'agent-ready-claude-high')", "github.event.label.name == 'agent-ready-claude-high' || github.event.label.name == 'agent-ready-full-claude')"],
    [copilotPath, "github.event.label.name == 'agent-ready-copilot-high')", "github.event.label.name == 'agent-ready-copilot-high' || github.event.label.name == 'agent-ready-full-copilot')"],
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
      for (const extra of ['agent-ready-full-copilot', 'agent-ready-full-claude']) {
        writeFileSync(join(dir, 'issue.json'), JSON.stringify({ ...snapshot, labels: [...snapshot.labels, { name: extra }] }));
        writeFileSync(join(dir, 'calls'), '');
        const conflicting = spawnSync('bash', [join(dir, 'assign.sh')], { env, encoding: 'utf8' });
        assert.notEqual(conflicting.status, 0, extra);
        assert.equal(readFileSync(join(dir, 'calls'), 'utf8'), '', extra);
      }
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
