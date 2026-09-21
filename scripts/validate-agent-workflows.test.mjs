// Deterministic contract tests for the validation workflow's concurrency and status model.
// Run with: node --test scripts/validate-agent-workflows.test.mjs
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
  AGENT_VALIDATION_STATUS,
  MERGE_VALIDATION_STATUS,
  STATUS_CONTEXT_EXPRESSION,
  VALIDATION_CONCURRENCY_GROUP,
  evaluateExpression,
  readRepositoryFile,
  renderTemplate,
  reviewPath,
  runContractChecks,
  simulateValidationRun,
  validatePath,
  verifyValidationModeIsolation,
} from './validate-agent-workflows.mjs';

const validateWorkflow = readRepositoryFile(validatePath);
const reviewWorkflow = readRepositoryFile(reviewPath);

const PR_NUMBER = 95;
const HEAD_SHA = 'a'.repeat(40);
const NEWER_HEAD_SHA = 'b'.repeat(40);

const pullRequestEvent = (overrides = {}) => ({ eventName: 'pull_request', prNumber: PR_NUMBER, headSha: HEAD_SHA, runId: 1, ...overrides });
const dispatchEvent = (overrides = {}) => ({ eventName: 'workflow_dispatch', prNumber: PR_NUMBER, headSha: HEAD_SHA, runId: 2, ...overrides });

// Pre-fix expressions, kept only so the tests prove they would be rejected.
const RACY_CONCURRENCY_GROUP =
  'group: validation-${{ github.workflow }}-${{ github.event.pull_request.number || inputs.pr_number || github.ref }}';
const RACY_STATUS_CONTEXT = "STATUS_CONTEXT: ${{ 'agent-validation' }}";

function replaceOnce(text, from, to) {
  assert.ok(text.includes(from), `fixture must contain: ${from}`);
  return text.replace(from, to);
}

function readWithOverrides(overrides) {
  return (path) => (Object.hasOwn(overrides, path) ? overrides[path] : readRepositoryFile(path));
}

describe('GitHub expression evaluation', () => {
  const context = {
    github: { event_name: 'workflow_dispatch', ref: 'refs/heads/main', event: {} },
    inputs: { pr_number: '95' },
  };

  it('returns the first truthy operand of an || chain and skips missing paths', () => {
    assert.equal(evaluateExpression('github.event.pull_request.number || inputs.pr_number || github.ref', context), '95');
    assert.equal(evaluateExpression("github.event.pull_request.number || github.event.missing || 'fallback'", context), 'fallback');
  });

  it('implements && / || as operand selection with case-insensitive string equality', () => {
    assert.equal(evaluateExpression("github.event_name == 'workflow_dispatch' && 'a' || 'b'", context), 'a');
    assert.equal(evaluateExpression("github.event_name == 'WORKFLOW_DISPATCH' && 'a' || 'b'", context), 'a');
    assert.equal(evaluateExpression("github.event_name == 'pull_request' && 'a' || 'b'", context), 'b');
    assert.equal(evaluateExpression("github.event_name != 'pull_request'", context), true);
    assert.equal(evaluateExpression("(github.event_name == 'x' || inputs.pr_number == 95) && true", context), true);
  });

  it('renders missing values as empty strings inside templates', () => {
    assert.equal(renderTemplate('v-${{ github.event.pull_request.number }}-${{ inputs.pr_number }}', context), 'v--95');
  });

  it('rejects malformed expressions instead of guessing', () => {
    assert.throws(() => evaluateExpression("github.event_name == 'unterminated", context), /Unterminated string literal/);
    assert.throws(() => evaluateExpression('github.event_name ==', context), /Unexpected end of expression/);
    assert.throws(() => evaluateExpression('a b', context), /Unexpected trailing tokens/);
  });
});

describe('validate.yml concurrency contract', () => {
  it('gives simultaneous pull_request and workflow_dispatch validation of the same PR different groups', () => {
    const pullRequestRun = simulateValidationRun(validateWorkflow, pullRequestEvent());
    const dispatchRun = simulateValidationRun(validateWorkflow, dispatchEvent());

    assert.notEqual(pullRequestRun.concurrencyGroup, dispatchRun.concurrencyGroup);
    assert.match(pullRequestRun.concurrencyGroup, /-pull_request-95$/);
    assert.match(dispatchRun.concurrencyGroup, /-workflow_dispatch-95$/);
  });

  it('still cancels a superseded run of the same mode for the same PR', () => {
    const first = simulateValidationRun(validateWorkflow, pullRequestEvent());
    const newer = simulateValidationRun(validateWorkflow, pullRequestEvent({ headSha: NEWER_HEAD_SHA, runId: 3 }));
    assert.equal(first.concurrencyGroup, newer.concurrencyGroup);

    const firstDispatch = simulateValidationRun(validateWorkflow, dispatchEvent());
    const newerDispatch = simulateValidationRun(validateWorkflow, dispatchEvent({ headSha: NEWER_HEAD_SHA, runId: 4 }));
    assert.equal(firstDispatch.concurrencyGroup, newerDispatch.concurrencyGroup);
  });

  it('never shares a group across different pull requests', () => {
    for (const eventName of ['pull_request', 'workflow_dispatch']) {
      const a = simulateValidationRun(validateWorkflow, { eventName, prNumber: PR_NUMBER, headSha: HEAD_SHA });
      const b = simulateValidationRun(validateWorkflow, { eventName, prNumber: PR_NUMBER + 1, headSha: HEAD_SHA });
      assert.notEqual(a.concurrencyGroup, b.concurrencyGroup);
    }
  });

  it('regression: the pre-fix group collides for the two modes and is rejected', () => {
    const racy = replaceOnce(validateWorkflow, VALIDATION_CONCURRENCY_GROUP, RACY_CONCURRENCY_GROUP);

    const pullRequestRun = simulateValidationRun(racy, pullRequestEvent());
    const dispatchRun = simulateValidationRun(racy, dispatchEvent());
    assert.equal(pullRequestRun.concurrencyGroup, dispatchRun.concurrencyGroup, 'the old expression let the modes cancel each other');

    assert.throws(() => verifyValidationModeIsolation(racy, 'fixture'), /would cancel each other/);
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [validatePath]: racy }) }), /validation-\$\{\{ github.workflow \}\}-\$\{\{ github.event_name \}\}/);
  });

  it('rejects a group that isolates modes but no longer supersedes runs of the same mode', () => {
    const perRun = replaceOnce(
      validateWorkflow,
      VALIDATION_CONCURRENCY_GROUP,
      'group: validation-${{ github.workflow }}-${{ github.event_name }}-${{ github.run_id }}',
    );
    assert.throws(() => verifyValidationModeIsolation(perRun, 'fixture'), /must cancel the superseded one/);
  });
});

describe('validate.yml status-context contract', () => {
  it('publishes agent-validation only from workflow_dispatch and merge-validation only from pull_request', () => {
    const pullRequestRun = simulateValidationRun(validateWorkflow, pullRequestEvent());
    const dispatchRun = simulateValidationRun(validateWorkflow, dispatchEvent());

    assert.equal(dispatchRun.statusContext, AGENT_VALIDATION_STATUS);
    assert.equal(pullRequestRun.statusContext, MERGE_VALIDATION_STATUS);
    assert.notEqual(pullRequestRun.statusContext, dispatchRun.statusContext);
  });

  it('publishes the same context from the pending and final status steps', () => {
    for (const event of [pullRequestEvent(), dispatchEvent()]) {
      const run = simulateValidationRun(validateWorkflow, event);
      assert.equal(run.reportStatusContext, run.statusContext);
    }
  });

  it('regression: a single shared status context lets the modes overwrite each other and is rejected', () => {
    const racy = replaceOnce(validateWorkflow, STATUS_CONTEXT_EXPRESSION, RACY_STATUS_CONTEXT);

    const pullRequestRun = simulateValidationRun(racy, pullRequestEvent());
    const dispatchRun = simulateValidationRun(racy, dispatchEvent());
    assert.equal(pullRequestRun.statusContext, dispatchRun.statusContext, 'the old contract shared one context');

    assert.throws(() => verifyValidationModeIsolation(racy, 'fixture'), /pull_request merge-result validation must publish 'merge-validation'/);
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [validatePath]: racy }) }));
  });

  it('rejects swapping the contexts so pull_request would own agent-validation', () => {
    const swapped = replaceOnce(
      validateWorkflow,
      STATUS_CONTEXT_EXPRESSION,
      "STATUS_CONTEXT: ${{ github.event_name == 'pull_request' && 'agent-validation' || 'merge-validation' }}",
    );
    assert.throws(() => verifyValidationModeIsolation(swapped, 'fixture'), /workflow_dispatch exact-SHA validation must publish 'agent-validation'/);
  });

  it('rejects a hard-coded status context that bypasses the shared expression', () => {
    const hardCoded = replaceOnce(
      validateWorkflow,
      '            -f context="$STATUS_CONTEXT" \\',
      '            -f context=agent-validation \\',
    );
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [validatePath]: hardCoded }) }), /contains forbidden text: context=agent-validation/);
  });

  it('rejects a report-status job that ignores the context resolved by the context job', () => {
    const detached = replaceOnce(
      validateWorkflow,
      'STATUS_CONTEXT: ${{ needs.context.outputs.status_context }}',
      "STATUS_CONTEXT: ${{ 'agent-validation' }}",
    );
    assert.throws(() => verifyValidationModeIsolation(detached, 'fixture'), /report-status publishes 'agent-validation' but the context job resolved 'merge-validation'/);
  });
});

describe('agent-review.yml consumes only the exact-SHA status', () => {
  it('requires a successful agent-validation status and never merge-validation', () => {
    assert.ok(reviewWorkflow.includes('select(.context == "agent-validation")'));
    assert.ok(!reviewWorkflow.includes(MERGE_VALIDATION_STATUS));
  });

  it('rejects a review guard that would accept the merge-result status', () => {
    const relaxed = replaceOnce(reviewWorkflow, 'select(.context == "agent-validation")', 'select(.context == "merge-validation")');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [reviewPath]: relaxed }) }), /agent-review.yml dispatched context/);
  });
});

describe('preserved guards', () => {
  it('passes the full contract for the committed workflows', () => {
    assert.doesNotThrow(() => runContractChecks());
  });

  it('keeps review dispatch limited to successful workflow_dispatch validation', () => {
    const dispatcher = validateWorkflow.slice(validateWorkflow.indexOf('  dispatch-review:\n'));
    assert.ok(dispatcher.includes("github.event_name == 'workflow_dispatch'"));
    assert.ok(dispatcher.includes('inputs.dispatch_review == true'));
    assert.ok(dispatcher.includes('needs.report-status.result'));
    assert.ok(dispatcher.includes('--ref main'));
  });

  it('rejects removing the exact current-head or workflow-file guard', () => {
    const staleAccepted = replaceOnce(validateWorkflow, '[ "$current_sha" = "$head_sha" ] || fail "Refusing stale validation', '# removed');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [validatePath]: staleAccepted }) }), /Refusing stale validation/);

    const workflowFilesAccepted = replaceOnce(validateWorkflow, "if grep -Eq '^\\.github/workflows/' <<<\"$changed_files\"; then\n              fail", '# removed\n              true');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [validatePath]: workflowFilesAccepted }) }));
  });
});
