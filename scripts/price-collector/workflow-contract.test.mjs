import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { verifyCollectorWorkflow } from './workflow-contract.mjs';

const workflow = readFileSync(new URL('../../.github/workflows/price-collector.yml', import.meta.url), 'utf8');

test('the repository workflow satisfies the manual-only credential and finalisation contract', () => {
  assert.doesNotThrow(() => verifyCollectorWorkflow(workflow));
});

test('a cron, a write token, artifact publication or a non-always finalizer breaks the contract', () => {
  for (const changed of [
    workflow.replace('  workflow_dispatch:', "  schedule:\n    - cron: '0 0 * * *'\n  workflow_dispatch:"),
    workflow.replace('      contents: read', '      contents: write'),
    workflow.replace('        if: always()', '        if: success()'),
    workflow + '\n# uses: actions/upload-artifact@v4\n',
    workflow.replace("(github.ref == 'refs/heads/main' || github.ref == 'refs/heads/develop')", "github.ref != ''"),
    workflow.replace("(github.ref == 'refs/heads/main' || github.ref == 'refs/heads/develop')", "(github.ref == 'refs/heads/main' || startsWith(github.ref, 'refs/heads/'))"),
    workflow.replace('            refs/heads/main|refs/heads/develop) ;;', '            refs/heads/*) ;;'),
    workflow.replace("|| github.ref == 'refs/heads/develop')", "|| github.ref == 'refs/heads/feature')"),
  ]) assert.throws(() => verifyCollectorWorkflow(changed));
});

test('the settings fail closed when a policy hook cannot run and expose no general file or web tools', () => {
  const settings = JSON.parse(readFileSync(new URL('./claude-settings.json', import.meta.url)));
  assert.equal(settings.permissions.defaultMode, 'dontAsk');
  const hooks = settings.hooks.PreToolUse;
  assert.equal(hooks.length, 1);
  assert.equal(hooks[0].matcher, '.*');
  assert.equal(hooks[0].hooks[0].onFailure, 'block');
  // The run ledger is written only by the trusted PostToolUse hook; a failed record blocks too.
  const recorders = settings.hooks.PostToolUse;
  assert.equal(recorders.length, 1);
  assert.equal(recorders[0].matcher, '^mcp__apify__');
  assert.equal(recorders[0].hooks[0].command, 'node scripts/price-collector/workflow-tool-guard.mjs');
  assert.equal(recorders[0].hooks[0].onFailure, 'block');
  // A failed Apify call is labelled by the same script, so the summary can say why it failed.
  const failures = settings.hooks.PostToolUseFailure;
  assert.equal(failures.length, 1);
  assert.equal(failures[0].matcher, '^mcp__apify__');
  assert.equal(failures[0].hooks[0].command, 'node scripts/price-collector/workflow-tool-guard.mjs');
  assert.ok(!settings.permissions.allow.includes('Bash'));
  for (const tool of ['Edit', 'WebFetch', 'WebSearch', 'Agent']) assert.ok(settings.permissions.deny.includes(tool));
  // A bare Read deny rule also refuses every Write in Claude Code 2.1.316, before the hook runs, so
  // no result file can be written (live run 41197670772). Read is kept away by --tools Bash,Write
  // and by the hook, which denies every tool outside its allowlist.
  assert.ok(!settings.permissions.deny.some(rule => rule === 'Read' || rule.startsWith('Read(')));
});
