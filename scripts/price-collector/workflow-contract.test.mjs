import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { COLLECT_ENVIRONMENT, FINALISE_STEP, RUN_LOG_STEP, SCHEDULED_SETTINGS, SCHEDULE_SWITCH, SCHEDULE_TRIGGER, verifyCollectorWorkflow } from './workflow-contract.mjs';

const workflow = readFileSync(new URL('../../.github/workflows/price-collector.yml', import.meta.url), 'utf8');

test('the repository workflow satisfies the credential, schedule and finalisation contract', () => {
  assert.doesNotThrow(() => verifyCollectorWorkflow(workflow));
});

test('a write token, a second or unpinned artifact, a missing run log or a non-always finalizer breaks the contract', () => {
  for (const changed of [
    workflow.replace('      contents: read', '      contents: write'),
    workflow.replace(FINALISE_STEP, FINALISE_STEP.replace('always() && ', 'success() && ')),
    workflow.replace(FINALISE_STEP, FINALISE_STEP.replace(" && steps.prepare.outputs.run_issued != 'false'", " && steps.prepare.outputs.run_issued == 'true'")),
    workflow + '\n# uses: actions/upload-artifact@v4\n',
    workflow.replace(RUN_LOG_STEP, ''),
    workflow.replace('          retention-days: 7', '          retention-days: 90'),
    // Uploaded after cleanup, the log folder would already be gone.
    workflow.replace(RUN_LOG_STEP, '') + `\n${RUN_LOG_STEP}`,
    workflow.replace("(github.ref == 'refs/heads/main' || github.ref == 'refs/heads/develop')", "github.ref != ''"),
    workflow.replace("(github.ref == 'refs/heads/main' || github.ref == 'refs/heads/develop')", "(github.ref == 'refs/heads/main' || startsWith(github.ref, 'refs/heads/'))"),
    workflow.replace('            refs/heads/main|refs/heads/develop) ;;', '            refs/heads/*) ;;'),
    workflow.replace("|| github.ref == 'refs/heads/develop')", "|| github.ref == 'refs/heads/feature')"),
  ]) assert.throws(() => verifyCollectorWorkflow(changed));
});

test('production is main only, in its own environment with its own API setting, and an unknown target is refused', () => {
  for (const changed of [
    // The credentialed job would accept production from develop.
    workflow.replace("(needs.gate.outputs.target == 'production' && github.ref == 'refs/heads/main')", "needs.gate.outputs.target == 'production'"),
    // The gate would accept production from develop, or without its own confirmation.
    workflow.replace("              if [ \"$RUN_REF\" != 'refs/heads/main' ]; then\n                echo '::error::Only the reviewed main workflow may run against production.'", "              if false; then\n                echo '::error::Only the reviewed main workflow may run against production.'"),
    workflow.replace(`if [ "$CONFIRM_PRODUCTION_API" != 'true' ] || [ "$CONFIRM_TEST_API" != 'false' ]; then`, 'if false; then'),
    workflow.replace("            *) echo '::error::Choose the test or production target.'; exit 1 ;;", '            *) ;;'),
    // Production credentials from the test environment, or one environment for both targets.
    workflow.replace("'price-collector-production' || 'price-collector'", "'price-collector' || 'price-collector'"),
    workflow.replace(COLLECT_ENVIRONMENT, '    environment: price-collector\n'),
    // A second job in a credentialed environment.
    workflow.replace("    timeout-minutes: 5\n", "    timeout-minutes: 5\n    environment: price-collector-production\n"),
    workflow.replace('      PRICE_COLLECTOR_TARGET: ${{ needs.gate.outputs.target }}\n', ''),
    workflow.replace('      PRICE_COLLECTOR_TARGET: ${{ needs.gate.outputs.target }}\n', '      PRICE_COLLECTOR_TARGET: ${{ inputs.target }}\n'),
    workflow.replace('      PRICE_COLLECTOR_PRODUCTION_API_URL: ${{ vars.PRICE_COLLECTOR_PRODUCTION_API_URL }}\n', ''),
  ]) assert.throws(() => verifyCollectorWorkflow(changed));
});

test('one daily Sydney schedule, switched off by default, with no hour check and nothing else automatic', () => {
  for (const changed of [
    // A second schedule, a UTC schedule, or a schedule on the hour.
    workflow.replace("    - cron: '17 5 * * *'\n", "    - cron: '17 5 * * *'\n    - cron: '17 17 * * *'\n"),
    workflow.replace('      timezone: Australia/Sydney\n', ''),
    workflow.replace("    - cron: '17 5 * * *'", "    - cron: '0 5 * * *'"),
    // Another automatic start.
    workflow.replace(SCHEDULE_TRIGGER, `${SCHEDULE_TRIGGER.replace('on:\n', 'on:\n  push:\n    branches: [main]\n')}`),
    workflow.replace('  workflow_dispatch:\n', '  workflow_run:\n    workflows: [Deploy Production]\n  workflow_dispatch:\n'),
    // A runtime hour check would throw away a delayed run.
    workflow.replace("          case \"$EVENT_NAME\" in\n", "          [ \"$(date +%H)\" = '05' ] || exit 0\n          case \"$EVENT_NAME\" in\n"),
    // The switch read from anywhere else, or not compared with exactly true.
    workflow.replace('          SCHEDULE_ENABLED: ${{ vars.PRICE_COLLECTOR_SCHEDULE_ENABLED }}\n', '          SCHEDULE_ENABLED: true\n'),
    workflow.replace(SCHEDULE_SWITCH, SCHEDULE_SWITCH.replace("!= 'true'", "= 'false'")),
    workflow.replace("                echo '::error::A scheduled collector run must be on main.'", "                echo 'A scheduled collector run is usually on main.'"),
    // A scheduled run that is not a full production run of the due work only.
    workflow.replace(SCHEDULED_SETTINGS, SCHEDULED_SETTINGS.replace('SCHEDULE=due', 'SCHEDULE=all')),
    workflow.replace(SCHEDULED_SETTINGS, SCHEDULED_SETTINGS.replace('TARGET=production', 'TARGET=test')),
    workflow.replace('          PRICE_COLLECTOR_SCHEDULE: ${{ needs.gate.outputs.schedule }}\n', ''),
    // The manual confirmation skipped for a manual run.
    workflow.replace("        if: github.event_name == 'workflow_dispatch'\n", ''),
  ]) assert.throws(() => verifyCollectorWorkflow(changed));
});

test('the switch is checked in a gate job that holds no credential, and Claude waits for an issued run', () => {
  for (const changed of [
    workflow.replace('    needs: gate\n', ''),
    workflow.replace("    # before anything requests the protected price-collector-production environment.\n    permissions: {}\n", "    # before anything requests the protected price-collector-production environment.\n    permissions:\n      id-token: write\n"),
    workflow.replace("          SCHEDULE_ENABLED: ${{ vars.PRICE_COLLECTOR_SCHEDULE_ENABLED }}\n", "          SCHEDULE_ENABLED: ${{ vars.PRICE_COLLECTOR_SCHEDULE_ENABLED }}\n          TOKEN: ${{ secrets.APIFY_TOKEN }}\n"),
    workflow.replace("        id: claude\n        if: steps.prepare.outputs.run_issued == 'true'\n", '        id: claude\n'),
  ]) assert.throws(() => verifyCollectorWorkflow(changed));
  // A comment that names the protected environment is not an environment.
  assert.doesNotThrow(() => verifyCollectorWorkflow(workflow.replace('    permissions: {}\n    outputs:', '    # environment: none, no secrets.\n    permissions: {}\n    outputs:')));
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
