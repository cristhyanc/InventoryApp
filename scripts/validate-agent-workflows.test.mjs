// Deterministic contract tests for the validation workflow's concurrency and status model.
// Run with: node --test scripts/validate-agent-workflows.test.mjs
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
  AGENT_VALIDATION_STATUS,
  DOCUMENTATION_IMPACT_VALIDATOR,
  IMPLEMENT_JOB_DOCUMENTATION_CONTRACT,
  ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT,
  MERGE_VALIDATION_STATUS,
  PREFLIGHT_JOB_CONTRACT,
  PULL_REQUEST_TEMPLATE_DOCUMENTATION_CONTRACT,
  REPAIR_PROMPT_DOCUMENTATION_CONTRACT,
  REVIEW_PROMPT_DOCUMENTATION_CONTRACT,
  STATUS_CONTEXT_EXPRESSION,
  VALIDATE_WORKFLOW_DOCUMENTATION_CONTRACT,
  VALIDATION_CONCURRENCY_GROUP,
  evaluateExpression,
  implementPath,
  issueTemplatePath,
  pullRequestTemplatePath,
  readRepositoryFile,
  renderTemplate,
  repairPath,
  reviewPath,
  runContractChecks,
  simulateValidationRun,
  validatePath,
  verifyDocumentationImpactGate,
  verifyArchitecturePass,
  verifyTrackedFileDeletionPermissions,
  verifyValidationModeIsolation,
} from './validate-agent-workflows.mjs';

const validateWorkflow = readRepositoryFile(validatePath);
const reviewWorkflow = readRepositoryFile(reviewPath);
const implementWorkflow = readRepositoryFile(implementPath);
const repairWorkflow = readRepositoryFile(repairPath);
const issueTemplate = readRepositoryFile(issueTemplatePath);
const pullRequestTemplate = readRepositoryFile(pullRequestTemplatePath);

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

  it('has the implementation dispatcher label the pull request agent-review and request review automatically', () => {
    const dispatcher = implementWorkflow.slice(implementWorkflow.indexOf('  dispatch-validation:\n'));
    assert.ok(dispatcher.includes('pull-requests: write'));
    assert.ok(dispatcher.includes('gh pr edit "$PR_NUMBER" --repo "$GITHUB_REPOSITORY" --add-label agent-review'));
    assert.ok(dispatcher.includes('-f dispatch_review=true'));

    const noAutoReview = replaceOnce(implementWorkflow, '-f dispatch_review=true', '-f dispatch_review=false');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: noAutoReview }) }), /missing required text: -f dispatch_review=true/);

    const noLabelStep = replaceOnce(implementWorkflow,
      '          gh pr edit "$PR_NUMBER" --repo "$GITHUB_REPOSITORY" --add-label agent-review\n\n', '');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: noLabelStep }) }), /missing required text: gh pr edit/);

    // gh pr edit --add-label needs pull-requests: write (it resolves to the GraphQL
    // addLabelsToLabelable mutation, which checks the pull-requests permission even though
    // the labelable is a pull request) -- issues: write is not sufficient and was the bug
    // in the first version of this contract.
    const readOnlyPullRequests = replaceOnce(implementWorkflow, '      actions: write\n      pull-requests: write\n',
      '      actions: write\n      pull-requests: read\n');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: readOnlyPullRequests }) }), /missing required text: pull-requests: write/);
  });

  it('keeps the repair dispatcher read-only on pull requests, since it never labels one', () => {
    const dispatcher = repairWorkflow.slice(repairWorkflow.indexOf('  dispatch-validation:\n'));
    assert.ok(dispatcher.includes('pull-requests: read'));
    assert.ok(!dispatcher.includes('pull-requests: write'));

    const writablePullRequests = replaceOnce(repairWorkflow, '      actions: write\n      pull-requests: read\n',
      '      actions: write\n      pull-requests: read\n      pull-requests: write\n');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [repairPath]: writablePullRequests }) }), /contains forbidden text: pull-requests: write/);
  });
});

// ---------------------------------------------------------------------------------------
// Documentation-impact gate. Each test removes or weakens one requirement and proves the
// contract rejects it, so the gate cannot be silently dismantled.
// ---------------------------------------------------------------------------------------

function assertGateRejects(overrides, pattern) {
  assert.throws(() => verifyDocumentationImpactGate(readWithOverrides(overrides)), pattern);
  assert.throws(() => runContractChecks({ read: readWithOverrides(overrides) }), pattern);
}

function removeAll(text, fragment) {
  assert.ok(text.includes(fragment), `fixture must contain: ${fragment}`);
  return text.replaceAll(fragment, '# removed');
}

describe('documentation-impact gate: templates', () => {
  it('passes for the committed templates', () => {
    assert.doesNotThrow(() => verifyDocumentationImpactGate());
  });

  it('rejects an issue template without the decision dropdown, with extra options, or with a changed option', () => {
    assertGateRejects({ [issueTemplatePath]: replaceOnce(issueTemplate, 'id: documentation-impact-decision', 'id: docs-decision') }, /missing section start/);
    assertGateRejects(
      { [issueTemplatePath]: replaceOnce(issueTemplate, '        - No documentation changes required\n', '        - No documentation changes required\n        - Unsure\n') },
      /expected exactly two options/,
    );
    assertGateRejects(
      { [issueTemplatePath]: replaceOnce(issueTemplate, '        - No documentation changes required\n', '        - Documentation not needed\n') },
      /decision field: missing required text/,
    );
  });

  it('rejects an issue template whose decision or details field is optional', () => {
    const decisionField = issueTemplate.slice(issueTemplate.indexOf('id: documentation-impact-decision'));
    const optionalDecision = issueTemplate.replace(decisionField, decisionField.replace('required: true', 'required: false'));
    assertGateRejects({ [issueTemplatePath]: optionalDecision }, /decision field: missing required text: required: true/);

    const detailsField = issueTemplate.slice(issueTemplate.indexOf('id: documentation-impact-details'));
    const optionalDetails = issueTemplate.replace(detailsField, detailsField.replace('required: true', 'required: false'));
    assertGateRejects({ [issueTemplatePath]: optionalDetails }, /details field: missing required text: required: true/);
  });

  it('rejects an issue template without the details textarea or the readiness confirmation', () => {
    assertGateRejects({ [issueTemplatePath]: replaceOnce(issueTemplate, 'id: documentation-impact-details', 'id: docs-details') }, /details field: missing section start/);
    assertGateRejects(
      { [issueTemplatePath]: replaceOnce(issueTemplate, ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT.readinessConfirmation, 'I thought about documentation') },
      /readiness: missing required text/,
    );
    const readiness = issueTemplate.slice(issueTemplate.indexOf(ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT.readinessConfirmation));
    const optionalConfirmation = issueTemplate.replace(readiness, readiness.replace('required: true', 'required: false'));
    assertGateRejects({ [issueTemplatePath]: optionalConfirmation }, /readiness documentation confirmation: missing required text: required: true/);
  });

  it('rejects a pull request template without the section, with it duplicated, or after known limitations', () => {
    const { heading, mustPrecede, decisionLine, evidenceLine } = PULL_REQUEST_TEMPLATE_DOCUMENTATION_CONTRACT;
    assertGateRejects({ [pullRequestTemplatePath]: replaceOnce(pullRequestTemplate, heading, '## Docs\n') }, /must appear exactly once/);
    assertGateRejects({ [pullRequestTemplatePath]: pullRequestTemplate + '\n' + heading }, /must appear exactly once/);

    const sectionStart = pullRequestTemplate.indexOf(heading);
    const sectionEnd = pullRequestTemplate.indexOf(mustPrecede);
    const sectionText = pullRequestTemplate.slice(sectionStart, sectionEnd);
    const moved = pullRequestTemplate.replace(sectionText, '').replace(mustPrecede, mustPrecede + '\n' + sectionText);
    assertGateRejects({ [pullRequestTemplatePath]: moved }, /must come before Known limitations/);

    assertGateRejects({ [pullRequestTemplatePath]: replaceOnce(pullRequestTemplate, decisionLine, 'Decision: UPDATED') }, /missing required text: Decision: <UPDATED or NOT REQUIRED>/);
    assertGateRejects({ [pullRequestTemplatePath]: replaceOnce(pullRequestTemplate, evidenceLine, 'Evidence: see diff') }, /missing required text: Evidence: <meaningful evidence>/);
    assertGateRejects({ [pullRequestTemplatePath]: replaceOnce(pullRequestTemplate, evidenceLine, evidenceLine + '\n\nEvidence: <meaningful evidence>') }, /expected exactly one "Evidence:" line/);
  });
});

describe('documentation-impact gate: implementation workflow', () => {
  it('requires the preflight job to exist before the implementation job', () => {
    assertGateRejects({ [implementPath]: replaceOnce(implementWorkflow, '  preflight:\n', '  precheck:\n') }, /missing required text: preflight:/);

    const preflightStart = implementWorkflow.indexOf('  preflight:\n');
    const implementStart = implementWorkflow.indexOf('  implement:\n');
    const dispatchStart = implementWorkflow.indexOf('  dispatch-validation:\n');
    const preflightJob = implementWorkflow.slice(preflightStart, implementStart);
    const implementJob = implementWorkflow.slice(implementStart, dispatchStart);
    const reordered = implementWorkflow.slice(0, preflightStart) + implementJob + preflightJob + implementWorkflow.slice(dispatchStart);
    assertGateRejects({ [implementPath]: reordered }, /preflight job must be defined before the implementation job/);
  });

  it('requires the preflight job to keep read-only permissions and no agent or write tooling', () => {
    for (const [from, to] of [
      ['      contents: read\n      issues: read\n', '      contents: write\n      issues: read\n'],
      ['      contents: read\n      issues: read\n', '      contents: read\n      issues: write\n'],
      ['      contents: read\n      issues: read\n', '      contents: read\n      issues: read\n      pull-requests: write\n'],
    ]) {
      assertGateRejects({ [implementPath]: replaceOnce(implementWorkflow, from, to) }, /preflight job: (contains forbidden text|missing required text)/);
    }
    const preflightEnd = implementWorkflow.indexOf('  implement:\n');
    const withLabelChange = implementWorkflow.slice(0, preflightEnd) + '          gh issue edit "$ISSUE_NUMBER" --add-label agent-blocked\n' + implementWorkflow.slice(preflightEnd);
    assertGateRejects({ [implementPath]: withLabelChange }, /preflight job: contains forbidden text: gh issue edit/);
    const withClaude = implementWorkflow.slice(0, preflightEnd) + '          claude_code_oauth_token: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}\n' + implementWorkflow.slice(preflightEnd);
    assertGateRejects({ [implementPath]: withClaude }, /preflight job: contains forbidden text: CLAUDE_CODE_OAUTH_TOKEN/);
  });

  it('requires the preflight job to check out develop without credentials and run the issue validator', () => {
    for (const required of PREFLIGHT_JOB_CONTRACT.required) {
      const preflightStart = implementWorkflow.indexOf('  preflight:\n');
      const preflightEnd = implementWorkflow.indexOf('  implement:\n');
      const preflight = implementWorkflow.slice(preflightStart, preflightEnd);
      assert.ok(preflight.includes(required), `preflight must contain: ${required}`);
      const weakened = implementWorkflow.slice(0, preflightStart) + preflight.replace(required, '# removed') + implementWorkflow.slice(preflightEnd);
      assertGateRejects({ [implementPath]: weakened }, /preflight job: missing required text/);
    }
  });

  it('requires the implementation job to depend on preflight and to gate on its success', () => {
    assertGateRejects({ [implementPath]: replaceOnce(implementWorkflow, IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.needs, '')}, /implement job: missing required text:\s+needs: preflight/);
    assertGateRejects(
      { [implementPath]: replaceOnce(implementWorkflow, IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.condition, "if: always() && github.event.label.name == 'agent-ready' && github.event.issue.pull_request == null") },
      /implement job: missing required text: if: needs.preflight.result == 'success'/,
    );
  });

  it('requires every documentation requirement in the implementation prompt and the validator in its allowed tools', () => {
    for (const required of IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.prompt) {
      assertGateRejects({ [implementPath]: replaceOnce(implementWorkflow, required, 'weakened') }, /implement prompt: missing required text/);
    }
    assertGateRejects(
      { [implementPath]: replaceOnce(implementWorkflow, `,Bash(node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body *)`, '') },
      /allowed tools: missing required text/,
    );
    assertGateRejects(
      { [implementPath]: replaceOnce(implementWorkflow, `Bash(node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body *)`, `Bash(node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body *),Bash(gh pr edit *)`) },
      /allowed tools: contains forbidden text: gh pr edit/,
    );
  });
});

describe('documentation-impact gate: validation workflow', () => {
  it('requires the edited pull-request activity type', () => {
    assertGateRejects(
      { [validatePath]: replaceOnce(validateWorkflow, VALIDATE_WORKFLOW_DOCUMENTATION_CONTRACT.pullRequestTypes, 'types: [opened, synchronize, reopened]') },
      /missing required text: types: \[opened, synchronize, reopened, edited\]/,
    );
  });

  it('requires the body to be obtained in the context job for both events and handed over base64-encoded', () => {
    for (const required of VALIDATE_WORKFLOW_DOCUMENTATION_CONTRACT.contextJob) {
      assertGateRejects({ [validatePath]: replaceOnce(validateWorkflow, required, '# removed') }, /context job: missing required text/);
    }
    const bodyFromPullRequestEventOnly = replaceOnce(validateWorkflow, 'pr_body="$(gh pr view "$pr_number" --repo "$GITHUB_REPOSITORY" --json body --jq \'.body // ""\')"', 'pr_body="$EVENT_PR_BODY"');
    assertGateRejects({ [validatePath]: bodyFromPullRequestEventOnly }, /context job: missing required text: pr_body="\$\(gh pr view/);
  });

  it('requires the validate job to decode the body into a temporary file and run the validator before repository validation', () => {
    for (const required of VALIDATE_WORKFLOW_DOCUMENTATION_CONTRACT.validateJob) {
      assertGateRejects({ [validatePath]: replaceOnce(validateWorkflow, required, '# removed') }, /validate job: missing required text/);
    }

    const validatorStep = validateWorkflow.slice(
      validateWorkflow.indexOf('      - name: Validate documentation-impact declaration\n'),
      validateWorkflow.indexOf('      - name: Validate agent workflow contracts\n'),
    );
    const afterRepositoryValidation = validateWorkflow.replace(validatorStep, '').replace('      - name: Validate repository\n        run: bash scripts/validate.sh\n', '      - name: Validate repository\n        run: bash scripts/validate.sh\n\n' + validatorStep);
    assertGateRejects({ [validatePath]: afterRepositoryValidation }, /must run before repository validation/);
  });

  it('never exposes a GitHub token or write permission to the job that checks out pull request code', () => {
    const validateJobStart = validateWorkflow.indexOf('  validate:\n');
    const withToken = validateWorkflow.slice(0, validateJobStart) + '  validate:\n    env:\n      GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}\n' + validateWorkflow.slice(validateJobStart + '  validate:\n'.length);
    assertGateRejects({ [validatePath]: withToken }, /validate job: contains forbidden text: GH_TOKEN/);
    assertGateRejects({ [validatePath]: replaceOnce(validateWorkflow, '    permissions:\n      contents: read\n\n    steps:\n      - name: Check out repository', '    permissions:\n      contents: read\n      pull-requests: write\n\n    steps:\n      - name: Check out repository') }, /validate job: contains forbidden text: pull-requests: write/);
  });

  it('preserves the exact-SHA and merge-result status and concurrency behaviour with the gate in place', () => {
    assert.doesNotThrow(() => verifyValidationModeIsolation(validateWorkflow, validatePath));
    const pullRequestRun = simulateValidationRun(validateWorkflow, pullRequestEvent());
    const dispatchRun = simulateValidationRun(validateWorkflow, dispatchEvent());
    assert.equal(pullRequestRun.statusContext, MERGE_VALIDATION_STATUS);
    assert.equal(dispatchRun.statusContext, AGENT_VALIDATION_STATUS);
    assert.notEqual(pullRequestRun.concurrencyGroup, dispatchRun.concurrencyGroup);
  });
});

describe('documentation-impact gate: review and repair workflows', () => {
  it('requires the review prompt to compare the issue decision, the PR declaration and the diff, and to treat missing documentation as a blocker', () => {
    for (const required of REVIEW_PROMPT_DOCUMENTATION_CONTRACT) {
      assertGateRejects({ [reviewPath]: replaceOnce(reviewWorkflow, required, 'weakened')}, /review prompt: missing required text/);
    }
  });

  it('does not let the review job gain write authority alongside the gate', () => {
    assertGateRejects({ [reviewPath]: replaceOnce(reviewWorkflow, '      contents: read\n      pull-requests: write\n      issues: read\n', '      contents: write\n      pull-requests: write\n      issues: read\n') }, /contains forbidden text: contents: write/);
    assertGateRejects({ [reviewPath]: replaceOnce(reviewWorkflow, '      contents: read\n      pull-requests: write\n      issues: read\n', '      contents: read\n      pull-requests: write\n      issues: write\n') }, /review permissions: contains forbidden text: issues: write/);
    assertGateRejects({ [reviewPath]: replaceOnce(reviewWorkflow, '"Bash(gh pr review * --comment *)"', '"Bash(gh pr review * --comment *),Bash(gh pr edit *)"') }, /allowed tools: contains forbidden text: gh pr edit/);
  });

  it('requires repairs to update documentation and forbids editing the pull request description', () => {
    for (const required of REPAIR_PROMPT_DOCUMENTATION_CONTRACT) {
      assertGateRejects({ [repairPath]: replaceOnce(repairWorkflow, required, 'weakened') }, /repair prompt: missing required text/);
    }
    assertGateRejects({ [repairPath]: removeAll(repairWorkflow, 'Bash(gh pr edit *),') }, /disallowed tools: missing required text: Bash\(gh pr edit \*\)/);
    assertGateRejects(
      { [repairPath]: replaceOnce(repairWorkflow, '"Bash(gh pr view *),Bash(gh pr diff *),Bash(gh pr checks *),Bash(gh pr comment *)', '"Bash(gh pr view *),Bash(gh pr diff *),Bash(gh pr checks *),Bash(gh pr comment *),Bash(gh pr edit *)') },
      /allowed tools: contains forbidden text: gh pr edit/,
    );
  });
});

describe('architecture pass contract', () => {
  it('accepts the coder to architect to exact-head-validation ordering', () => {
    assert.doesNotThrow(() => verifyArchitecturePass(implementWorkflow));
  });

  it('keeps coder and architect work in their foreground invocations', () => {
    const prompt = 'Do not delegate, spawn, or use Claude sub-agents, and do not invoke the \`Agent\` tool.';
    for (const unsafe of [
      removeAll(implementWorkflow, prompt),
      removeAll(implementWorkflow, '--disallowedTools "Agent,WebFetch,WebSearch"'),
    ]) {
      assert.throws(
        () => runContractChecks({ read: readWithOverrides({ [implementPath]: unsafe }) }),
        /(implementation prompt|implementation agent|architect): missing required text/,
      );
    }
  });

  it('requires an explicit pre-PR blocked marker and verified PR-ready output', () => {
    for (const required of [
      'use the Write tool to create `.agent-run-status` containing exactly `blocked` on one line',
      'echo "pr_ready=false"',
      'echo "blocked=false"',
      '[ -z "$(git ls-files -- .agent-run-status)" ]',
      '[ "$(cat .agent-run-status)" = "blocked" ]',
      'echo "blocked=true"',
      'echo "pr_ready=true"',
      'AGENT_BLOCKED: ${{ steps.architecture_target.outputs.blocked }}',
      '[ "$AGENT_BLOCKED" = "true" ]',
    ]) {
      const weakened = replaceOnce(implementWorkflow, required, '# removed');
      assert.throws(
        () => runContractChecks({ read: readWithOverrides({ [implementPath]: weakened }) }),
        /(implementation prompt|architecture target|outcome): missing required text/,
      );
    }
  });

  it('runs the architect only when the target step verified a PR is ready', () => {
    const unsafe = replaceOnce(
      implementWorkflow,
      "if: steps.architecture_target.outputs.pr_ready == 'true'",
      "if: steps.architecture_target.outcome == 'success'",
    );
    assert.throws(
      () => runContractChecks({ read: readWithOverrides({ [implementPath]: unsafe }) }),
      /agent-implement.yml architect: missing required text/,
    );
  });

  it('rejects an architect stage that can be skipped while dispatching validation', () => {
    const unsafe = replaceOnce(implementWorkflow, '[ "$ARCHITECT_OUTCOME" = "success" ] &&', '[ "$ARCHITECT_OUTCOME" != "failure" ] &&');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: unsafe }) }), /agent-implement.yml outcome/);
  });

  it('rejects an architect allowed to edit the PR description', () => {
    const unsafe = replaceOnce(implementWorkflow, 'Bash(gh pr comment *)', 'Bash(gh pr comment *),Bash(gh pr edit *)');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: unsafe }) }), /agent-implement.yml architect allowed tools/);
  });
});



describe('scoped tracked-file deletion permissions', () => {
  const scoped = 'Bash(git rm -- backend/*),Bash(git rm -- frontend/*),Bash(git rm -- docs/*),Bash(git rm -- scripts/*)';
  it('allows tracked files in project directories only with the option terminator', () => {
    assert.doesNotThrow(() => verifyTrackedFileDeletionPermissions(scoped, 'fixture'));
  });
  it('rejects broad, recursive, force and workflow deletion permissions', () => {
    for (const extra of ['Bash(git rm *)', 'Bash(git rm -- *)', 'Bash(git rm -r *)', 'Bash(git rm -f *)', 'Bash(git rm -- .github/*)']) {
      assert.throws(() => verifyTrackedFileDeletionPermissions(scoped + ',' + extra, 'fixture'), /forbidden text/);
    }
  });
  it('requires the permission in implementation, architecture and repair', () => {
    assert.doesNotThrow(() => runContractChecks());
    for (const [path, workflow] of [[implementPath, implementWorkflow], [repairPath, repairWorkflow]]) {
      const altered = replaceOnce(workflow, scoped, '');
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [path]: altered }) }), /missing required text: Bash\(git rm -- backend\/\*\)/);
    }
  });
});
