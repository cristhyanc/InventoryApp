import './agent-persistence.test.mjs';
import './agent-review-publication.test.mjs';
import './agent-architecture-handoff.test.mjs';
import './agent-copilot-handoff.test.mjs';
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
  REVIEW_PROMPT_JUDGMENT_CONTRACT,
  evaluateExpression,
  headUpdatePath,
  verifyReviewPublicationAndScheduling,
  verifyScopedDirectoryCreationPermissions,
  appPushRetryPath,
  implementPath,
  architecturePath,
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
  verifyScopedStagingCleanupPermissions,
  verifyValidationModeIsolation,
  copilotImplementPath,
  copilotArchitecturePath,
} from './validate-agent-workflows.mjs';

const validateWorkflow = readRepositoryFile(validatePath);
const reviewWorkflow = readRepositoryFile(reviewPath);
const implementWorkflow = readRepositoryFile(implementPath);
const architectureWorkflow = readRepositoryFile(architecturePath);
const appPushRetryScript = readRepositoryFile(appPushRetryPath);
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
  it('requires a successful agent-validation status and never uses merge-validation as review evidence', () => {
    assert.ok(reviewWorkflow.includes('select(.context == "agent-validation")'));
    const reviewContext = reviewWorkflow.slice(
      reviewWorkflow.indexOf('  context:\n'),
      reviewWorkflow.indexOf('  review:\n'),
    );
    assert.ok(!reviewContext.includes(MERGE_VALIDATION_STATUS));
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

  it('has the implementation dispatcher hand off only to the trusted architecture workflow', () => {
    const dispatcher = implementWorkflow.slice(implementWorkflow.indexOf('  dispatch-architecture:\n'));
    assert.ok(dispatcher.includes('actions: write'));
    assert.ok(dispatcher.includes('pull-requests: read'));
    assert.ok(dispatcher.includes('issues: write'));
    assert.ok(!dispatcher.includes('pull-requests: write'));
    assert.ok(dispatcher.includes('agent-architecture.yml'));
    assert.ok(dispatcher.includes('trap block_undispatched EXIT'));
    assert.ok(dispatcher.includes('RUN_URL: ${{ github.server_url }}'));
    assert.ok(dispatcher.includes('--remove-label agent-working --add-label agent-blocked'));
    assert.ok(dispatcher.includes('--ref main'));
    assert.ok(dispatcher.includes('-f issue_number="$ISSUE_NUMBER"'));
    assert.ok(dispatcher.includes('-f pr_number="$PR_NUMBER"'));
    assert.ok(dispatcher.includes('-f head_sha="$HEAD_SHA"'));
    assert.ok(!dispatcher.includes('validate.yml'));
    assert.ok(!dispatcher.includes('gh pr edit'));

    const bypass = replaceOnce(implementWorkflow, 'agent-architecture.yml', 'validate.yml');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: bypass }) }), /architecture dispatcher/);
  });

  it('has architecture finalization label the PR and dispatch exact-SHA validation only after success', () => {
    const finalize = architectureWorkflow.slice(architectureWorkflow.indexOf('  finalize:\n'));
    assert.ok(finalize.includes('ARCHITECTURE_JOB_RESULT: ${{ needs.architecture.result }}'));
    assert.ok(finalize.includes('gh pr edit "$PR_NUMBER" --repo "$GITHUB_REPOSITORY" --add-label agent-review'));
    assert.ok(finalize.includes('gh workflow run validate.yml'));
    assert.ok(finalize.includes('-f dispatch_review=true'));

    const noSuccessGate = replaceOnce(architectureWorkflow, '[ "$ARCHITECTURE_JOB_RESULT" = "success" ]', '[ "$ARCHITECTURE_JOB_RESULT" != "failure" ]');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [architecturePath]: noSuccessGate }) }), /agent-architecture.yml finalize/);
    const skippedOnRejectedTarget = replaceOnce(architectureWorkflow, '    if: always()\n', "    if: always() && needs.context.result == 'success'\n");
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [architecturePath]: skippedOnRejectedTarget }) }), /agent-architecture.yml finalize/);
    const noDuplicateCheck = replaceOnce(architectureWorkflow, 'select(.context == "agent-validation")', 'select(.context == "merge-validation")');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [architecturePath]: noDuplicateCheck }) }), /agent-architecture.yml finalize/);
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
    const dispatchStart = implementWorkflow.indexOf('  dispatch-architecture:\n');
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
      { [implementPath]: replaceOnce(implementWorkflow, IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.condition, "if: always() && github.event.label.name == 'agent-ready-claude' && github.event.issue.pull_request == null") },
      /implement job: missing required text: if: needs.preflight.result == 'success'/,
    );
  });

  it('requires every documentation requirement in the implementation prompt and the validator in its allowed tools', () => {
    for (const required of IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.prompt) {
      assertGateRejects({ [implementPath]: removeAll(implementWorkflow, required) }, /(?:implement prompt|implementation prompt|implementation result): missing required text/);
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
    assertGateRejects({ [reviewPath]: replaceOnce(reviewWorkflow, '      contents: read\n      pull-requests: read\n      issues: read\n', '      contents: write\n      pull-requests: read\n      issues: read\n') }, /contains forbidden text: contents: write/);
    assertGateRejects({ [reviewPath]: replaceOnce(reviewWorkflow, '      contents: read\n      pull-requests: read\n      issues: read\n', '      contents: read\n      pull-requests: read\n      issues: write\n') }, /review permissions: contains forbidden text: issues: write/);
    assertGateRejects({ [reviewPath]: replaceOnce(reviewWorkflow, '"Bash(gh pr view *),Bash(gh pr diff *)', '"Bash(gh pr edit *),Bash(gh pr view *),Bash(gh pr diff *)') }, /allowed tools: contains forbidden text: gh pr edit/);
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

describe('review dispatch contract', () => {
  it('does not review App-authenticated synchronize pushes before exact-SHA validation', () => {
    assert.ok(reviewWorkflow.includes('types: [labeled]'));
    assert.ok(!reviewWorkflow.slice(reviewWorkflow.indexOf('on:\n'), reviewWorkflow.indexOf('permissions:\n')).includes('synchronize'));
    assert.ok(reviewWorkflow.includes('A manual agent-review request requires a successful latest agent-validation status on the exact head SHA.'));

    const unsafe = replaceOnce(reviewWorkflow, 'types: [labeled]', 'types: [labeled, synchronize]');
    assert.throws(
      () => runContractChecks({ read: readWithOverrides({ [reviewPath]: unsafe }) }),
      /agent-review.yml triggers: (missing required text: types: \[labeled\]|contains forbidden text: synchronize)/,
    );
  });
});

describe('architecture pass contract', () => {
  it('accepts implementation publication -> trusted architecture -> exact-head validation ordering', () => {
    assert.doesNotThrow(() => verifyArchitecturePass(implementWorkflow, architectureWorkflow));
  });

  it('keeps the architecture agent out of the implementation workflow', () => {
    assert.ok(!implementWorkflow.includes('Run Claude Code architecture agent'));
    const injected = implementWorkflow.replace('      - name: Record implementation outcome\n', '      - name: Run Claude Code architecture agent\n        run: echo unsafe\n\n      - name: Record implementation outcome\n');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: injected }) }), /must not contain architecture execution/);
  });

  it('requires just-in-time repository-scoped GitHub App tokens for deterministic publishes', () => {
    for (const [path, workflow, required] of [
      [implementPath, implementWorkflow, 'id: implementation_app_token'],
      [architecturePath, architectureWorkflow, 'id: architecture_app_token'],
      [repairPath, repairWorkflow, 'id: repair_app_token'],
      [implementPath, implementWorkflow, 'client-id: ${{ vars.AGENT_AUTOMATION_APP_CLIENT_ID }}'],
      [architecturePath, architectureWorkflow, 'private-key: ${{ secrets.AGENT_AUTOMATION_APP_PRIVATE_KEY }}'],
      [implementPath, implementWorkflow, 'GH_TOKEN: ${{ steps.implementation_app_token.outputs.token }}'],
      [architecturePath, architectureWorkflow, 'GH_TOKEN: ${{ steps.architecture_app_token.outputs.token }}'],
      [repairPath, repairWorkflow, 'GH_TOKEN: ${{ steps.repair_app_token.outputs.token }}'],
    ]) {
      const weakened = replaceOnce(workflow, required, '# removed');
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [path]: weakened }) }), /(App token|implementation publish(?: diagnostic)?|architecture publish|repair publish): missing required text/);
    }
  });

  it('requires bounded retry for transient GitHub App Git authorization denials', () => {
    for (const required of ['delays=(0 2 5 10)', 'sleep "$delay"', "grep -Eqi '403|Permission to .* denied'", 'core.hooksPath=/dev/null', 'push "$remote" "$refspec"']) {
      const weakened = replaceOnce(appPushRetryScript, required, '# removed retry contract');
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [appPushRetryPath]: weakened }) }), /git-push-with-app-retry\.sh: missing required text/);
    }
  });

  it('never exposes the GitHub App credential to Claude invocations', () => {
    for (const [path, workflow, marker] of [
      [implementPath, implementWorkflow, '      - name: Run Claude Code implementation agent\n'],
      [architecturePath, architectureWorkflow, '      - name: Run Claude Code architecture agent\n'],
      [repairPath, repairWorkflow, '      - name: Run Claude Code repair agent\n'],
    ]) {
      const injected = workflow.replace(marker, marker + '        env:\n          LEAKED_APP_KEY: ${{ secrets.AGENT_AUTOMATION_APP_PRIVATE_KEY }}\n');
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [path]: injected }) }), /(implementation agent|architect|repair agent): contains forbidden text/);
    }
  });

  it('requires guarded agent PRs to match the configured GitHub App bot login', () => {
    for (const [path, workflow] of [
      [implementPath, implementWorkflow],
      [architecturePath, architectureWorkflow],
      [repairPath, repairWorkflow],
      [validatePath, validateWorkflow],
    ]) {
      const weakened = removeAll(workflow, 'EXPECTED_AGENT_AUTHOR: ${{ vars.AGENT_AUTOMATION_APP_BOT_LOGIN }}');
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [path]: weakened }) }), /(implementation publish|architecture dispatcher|agent-architecture.yml context|validation dispatcher|dispatched context|review dispatcher|finalize): missing required text/, path);
    }
  });

  it('keeps coder and architect work in their foreground invocations', () => {
    const prompt = 'Do not delegate, spawn, or use Claude sub-agents, and do not invoke the `Agent` tool.';
    for (const [path, workflow] of [[implementPath, implementWorkflow], [architecturePath, architectureWorkflow]]) {
      const weakened = removeAll(workflow, prompt);
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [path]: weakened }) }), /(implementation prompt|architect): missing required text/);
    }
  });

  it('requires an explicit pre-PR blocked marker and verified local implementation package', () => {
    for (const required of [
      'use the Write tool to create `.agent-run-status` containing exactly `blocked` on one line',
      '[ -z "$(git ls-files -- .agent-run-status)" ]',
      '[ "$(cat .agent-run-status)" = "blocked" ]',
      'echo "blocked=true"', '.agent-pr-title', '.agent-pr-body.md', 'echo "ready=true"',
      'AGENT_BLOCKED: ${{ steps.implementation_result.outputs.blocked }}', '[ "$AGENT_BLOCKED" = "true" ]',
    ]) {
      const weakened = removeAll(implementWorkflow, required);
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [implementPath]: weakened }) }), /(implementation prompt|implementation result|implementation publish|outcome): missing required text/);
    }
  });

  it('requires the architecture workflow to start from the exact verified SHA and current agent-working issue', () => {
    for (const required of [
      'ref: ${{ needs.context.outputs.head_sha }}',
      '[ "$current_sha" = "$HEAD_SHA" ] || fail "Refusing stale architecture run:',
      'any(.labels[]?; .name == "agent-working")',
      'git checkout -b "$BRANCH" "$HEAD_SHA"',
    ]) {
      const weakened = removeAll(architectureWorkflow, required);
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [architecturePath]: weakened }) }), /agent-architecture.yml/);
    }
  });

  it('rejects an architect allowed to edit the PR description', () => {
    const unsafe = replaceOnce(architectureWorkflow, 'Bash(gh pr comment *)', 'Bash(gh pr comment *),Bash(gh pr edit *)');
    assert.throws(() => runContractChecks({ read: readWithOverrides({ [architecturePath]: unsafe }) }), /agent-architecture.yml architect allowed tools/);
  });
});

describe('scoped staging cleanup permissions', () => {
  const scoped = 'Bash(git restore --staged -- backend/*),Bash(git restore --staged -- frontend/*),Bash(git restore --staged -- docs/*),Bash(git restore --staged -- scripts/*)';

  it('allows unstaging only inside normal project directories with the option terminator', () => {
    assert.doesNotThrow(() => verifyScopedStagingCleanupPermissions(scoped, 'fixture'));
  });

  it('rejects broad reset/restore and workflow-file cleanup permissions', () => {
    for (const extra of [
      'Bash(git restore --staged *)',
      'Bash(git restore --staged -- *)',
      'Bash(git restore --staged -- .github/*)',
      'Bash(git reset *)',
    ]) {
      assert.throws(() => verifyScopedStagingCleanupPermissions(scoped + ',' + extra, 'fixture'), /forbidden text/);
    }
  });

  it('requires scoped cleanup permission in implementation and architecture agents', () => {
    assert.doesNotThrow(() => runContractChecks());
    for (const [path, workflow] of [[implementPath, implementWorkflow], [architecturePath, architectureWorkflow]]) {
      const altered = replaceOnce(workflow, scoped, '');
      assert.throws(
        () => runContractChecks({ read: readWithOverrides({ [path]: altered }) }),
        /missing required text: Bash\(git restore --staged -- backend\/\*\)/,
      );
    }
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
    for (const [path, workflow] of [[implementPath, implementWorkflow], [architecturePath, architectureWorkflow], [repairPath, repairWorkflow]]) {
      const altered = replaceOnce(workflow, scoped, '');
      assert.throws(() => runContractChecks({ read: readWithOverrides({ [path]: altered }) }), /missing required text: Bash\(git rm -- backend\/\*\)/);
    }
  });
});

describe('scoped directory creation permissions', () => {
  const scoped = 'Bash(mkdir -p backend/*),Bash(mkdir -p frontend/*),Bash(mkdir -p docs/*),Bash(mkdir -p scripts/*)';
  it('allows mkdir -p only inside normal project directories', () => {
    assert.doesNotThrow(() => verifyScopedDirectoryCreationPermissions(scoped, 'fixture'));
  });
  it('rejects broad and workflow-directory creation permissions', () => {
    for (const extra of ['Bash(mkdir *)', 'Bash(mkdir -p *)', 'Bash(mkdir -p .github/*)']) {
      assert.throws(() => verifyScopedDirectoryCreationPermissions(scoped + ',' + extra, 'fixture'), /forbidden text/);
    }
  });
  it('requires the permission in the implementation and architecture agents', () => {
    assert.doesNotThrow(() => runContractChecks());
    const implementationMissing = replaceOnce(implementWorkflow, scoped, '');
    assert.throws(
      () => runContractChecks({ read: readWithOverrides({ [implementPath]: implementationMissing }) }),
      /implementation allowed tools: missing required text: Bash\(mkdir -p backend\/\*\)/,
    );
    const architectureMissing = replaceOnce(architectureWorkflow, scoped, '');
    assert.throws(
      () => runContractChecks({ read: readWithOverrides({ [architecturePath]: architectureMissing }) }),
      /architect allowed tools: missing required text: Bash\(mkdir -p backend\/\*\)/,
    );
  });
});


describe('agent shell timeout covers full validation', () => {
  it('requires a 30-minute default and maximum Bash timeout for implementation and architecture Claude steps', () => {
    assert.doesNotThrow(() => runContractChecks());
    for (const [path, workflow, source] of [
      [implementPath, implementWorkflow, 'implementation agent'],
      [architecturePath, architectureWorkflow, 'architect'],
    ]) {
      for (const [from, to] of [
        ['BASH_DEFAULT_TIMEOUT_MS: "1800000"', 'BASH_DEFAULT_TIMEOUT_MS: "120000"'],
        ['BASH_MAX_TIMEOUT_MS: "1800000"', 'BASH_MAX_TIMEOUT_MS: "600000"'],
      ]) {
        const altered = replaceOnce(workflow, from, to);
        assert.throws(() => runContractChecks({ read: readWithOverrides({ [path]: altered }) }), new RegExp(`${source}: missing required text: BASH_`));
      }
    }
  });
});

// ---------------------------------------------------------------------------------------
// Review judgment, guarded publication and updated-head scheduling (issue #268). Each test
// weakens one requirement and proves the contract rejects it.
// ---------------------------------------------------------------------------------------

const headUpdateWorkflow = readRepositoryFile(headUpdatePath);

function assertPublicationRejects(overrides, pattern) {
  assert.throws(() => verifyReviewPublicationAndScheduling(readWithOverrides(overrides)), pattern);
  assert.throws(() => runContractChecks({ read: readWithOverrides(overrides) }), pattern);
}

describe('review judgment contract', () => {
  it('passes for the committed review prompt', () => {
    assert.doesNotThrow(() => verifyReviewPublicationAndScheduling());
  });

  it('requires every blocking rule in the review prompt', () => {
    for (const required of REVIEW_PROMPT_JUDGMENT_CONTRACT) {
      assertPublicationRejects({ [reviewPath]: replaceOnce(reviewWorkflow, required, 'weakened') }, /review prompt: missing required text/);
    }
  });
});

describe('guarded publication contract', () => {
  it('keeps the model job read-only so it cannot publish around the guard', () => {
    assertPublicationRejects(
      { [reviewPath]: replaceOnce(reviewWorkflow, '      contents: read\n      pull-requests: read\n      issues: read\n', '      contents: read\n      pull-requests: write\n      issues: read\n') },
      /review permissions: (missing required text: pull-requests: read|contains forbidden text: pull-requests: write)/,
    );
    assertPublicationRejects(
      { [reviewPath]: replaceOnce(reviewWorkflow, '"Bash(gh pr view *),Bash(gh pr diff *)', '"Bash(gh pr review * --comment *),Bash(gh pr view *),Bash(gh pr diff *)') },
      /allowed tools: contains forbidden text: gh pr review/,
    );
    assertPublicationRejects(
      { [reviewPath]: replaceOnce(reviewWorkflow, '--allowedTools "mcp__github_ci__get_ci_status', '--allowedTools "mcp__github_inline_comment__create_inline_comment,mcp__github_ci__get_ci_status') },
      /allowed tools: contains forbidden text: mcp__github_inline_comment/,
    );
  });

  it('requires schema-bound structured output from the model', () => {
    assertPublicationRejects({ [reviewPath]: removeAll(reviewWorkflow, "--json-schema '") }, /review job: missing required text: --json-schema/);
  });

  it('requires the publisher to re-verify the head, eligibility and validation before publishing', () => {
    for (const fragment of [
      '[ "$current_sha" = "$HEAD_SHA" ] || suppress',
      '[ "$validation_state" = "success" ] || suppress',
      '[ "$reviewed_sha" = "$HEAD_SHA" ] || suppress',
      'all(.criteria[]; .status == "met")',
      '[ "$is_draft" = "false" ] || suppress',
    ]) {
      assertPublicationRejects({ [reviewPath]: replaceOnce(reviewWorkflow, fragment, '# removed') }, /publish job/);
    }
  });

  it('requires the published review to be bound to the reviewed commit and to stay comment-only', () => {
    assertPublicationRejects({ [reviewPath]: replaceOnce(reviewWorkflow, 'commit_id: $sha, event: "COMMENT"', 'event: "COMMENT"') }, /publish job: missing required text: commit_id/);
    assertPublicationRejects({ [reviewPath]: replaceOnce(reviewWorkflow, 'commit_id: $sha, event: "COMMENT"', 'commit_id: $sha, event: "APPROVE"') }, /publish job/);
  });

  it('rejects a publisher that checks out code or holds the model credential', () => {
    const withCheckout = replaceOnce(reviewWorkflow, '    steps:\n      - name: Guard and publish review\n', '    steps:\n      - uses: actions/checkout@v4\n      - name: Guard and publish review\n');
    assertPublicationRejects({ [reviewPath]: withCheckout }, /publish job: contains forbidden text: actions\/checkout/);
  });

  it('records an outcome even when the review job fails or is cancelled', () => {
    assertPublicationRejects({ [reviewPath]: replaceOnce(reviewWorkflow, "if: always() && needs.context.result == 'success'", "if: needs.review.result == 'success'") }, /publish job: missing required text: if: always\(\)/);
  });
});

describe('updated-head scheduling contract', () => {
  it('runs only from the trusted default-branch definition, never from a pull_request-triggered job', () => {
    assertPublicationRejects({ [headUpdatePath]: replaceOnce(headUpdateWorkflow, '  pull_request_target:\n', '  pull_request:\n') }, /triggers: (missing required text: pull_request_target|contains forbidden text: {2}pull_request:)/);
  });

  it('never checks out or executes pull request code while holding dispatch authority', () => {
    const withCheckout = replaceOnce(headUpdateWorkflow, '    steps:\n', '    steps:\n      - uses: actions/checkout@v4\n        with:\n          ref: ${{ github.event.pull_request.head.sha }}\n');
    assertPublicationRejects({ [headUpdatePath]: withCheckout }, /agent-head-update.yml/);
  });

  it('requires the stale-head guard, the duplicate check and a review only after validation', () => {
    assertPublicationRejects({ [headUpdatePath]: replaceOnce(headUpdateWorkflow, '[ -z "$existing_validation" ]', 'true') }, /missing required text: \[ -z "\$existing_validation" \]/);
    assertPublicationRejects({ [headUpdatePath]: replaceOnce(headUpdateWorkflow, '[ "$current_sha" = "$HEAD_SHA" ]', 'true') }, /current_sha/);
    assertPublicationRejects({ [headUpdatePath]: replaceOnce(headUpdateWorkflow, 'gh workflow run validate.yml', 'gh workflow run agent-review.yml') }, /agent-head-update.yml/);
  });

  it('requires the repair dispatcher to stand down for a SHA that is already scheduled', () => {
    assertPublicationRejects({ [repairPath]: replaceOnce(repairWorkflow, 'if [ -n "$existing_validation" ]; then', 'if false; then') }, /agent-repair.yml validation dispatcher/);
  });
});

describe('cross-review contract (agent-ready-claude / agent-ready-copilot)', () => {
  const copilotWorkflow = readRepositoryFile(copilotImplementPath);
  const copilotArchitectureWorkflow = readRepositoryFile(copilotArchitecturePath);
  const headUpdateWorkflow = readRepositoryFile(headUpdatePath);
  const rejects = (overrides, pattern) => assert.throws(() => runContractChecks({ read: readWithOverrides(overrides) }), pattern);

  it('passes for the committed workflows', () => {
    assert.doesNotThrow(() => runContractChecks());
  });

  it('starts Claude only from agent-ready-claude and Copilot only from agent-ready-copilot', () => {
    assert.match(implementWorkflow, /github\.event\.label\.name == 'agent-ready-claude'/);
    assert.doesNotMatch(implementWorkflow, /'agent-ready'/);
    rejects({ [copilotImplementPath]: copilotWorkflow.replaceAll("'agent-ready-copilot'", "'agent-ready-claude'") }, /agent-copilot.yml (preflight|assign)/);
  });

  it('rejects a Claude review that would accept a Claude-implemented pull request', () => {
    const selfReview = reviewWorkflow.replace('[[ "$head_ref" == copilot/* ]] || fail', '[[ "$head_ref" == agent/issue-* || "$head_ref" == copilot/* ]] || fail');
    rejects({ [reviewPath]: selfReview }, /agent-review.yml dispatched context: contains forbidden text: agent\/issue-\*/);
  });

  it('rejects review routing that sends a Claude implementation to the Claude review', () => {
    rejects({ [validatePath]: replaceOnce(validateWorkflow, 'if [ "$implementer" = "claude" ]; then', 'if [ "$implementer" = "nobody" ]; then') }, /validate.yml review dispatcher/);
    rejects({ [validatePath]: replaceOnce(validateWorkflow, "-f 'reviewers[]=copilot-pull-request-reviewer[bot]'", "-f 'reviewers[]=cristhyanc'") }, /validate.yml review dispatcher/);
  });

  it('rejects a Copilot architecture check that could write, push or use another credential', () => {
    for (const unsafe of [
      replaceOnce(architectureWorkflow, "            --deny-tool='write' \\\n", ''),
      replaceOnce(architectureWorkflow, "    permissions:\n      contents: read\n    outputs:\n      verdict:", "    permissions:\n      contents: write\n    outputs:\n      verdict:"),
      replaceOnce(architectureWorkflow, 'COPILOT_GITHUB_TOKEN: ${{ secrets.COPILOT_CLI_TOKEN }}', 'COPILOT_GITHUB_TOKEN: ${{ secrets.COPILOT_AGENT_TOKEN }}'),
      replaceOnce(architectureWorkflow, '          [ -z "$(git status --porcelain)" ] || fail', '          true || fail'),
    ]) {
      rejects({ [architecturePath]: unsafe }, /agent-architecture.yml Copilot check/);
    }
  });

  it('rejects a Claude fix pass that runs without Copilot findings', () => {
    rejects({ [architecturePath]: replaceOnce(architectureWorkflow, "    if: needs.copilot-check.outputs.verdict == 'findings'\n", '') }, /agent-architecture.yml architecture job/);
  });

  it('rejects a Claude architecture check of Copilot work that could edit or publish', () => {
    for (const unsafe of [
      replaceOnce(copilotArchitectureWorkflow, '--allowedTools "Read,Glob,Grep"', '--allowedTools "Read,Glob,Grep,Edit"'),
      replaceOnce(copilotArchitectureWorkflow, '"Bash(gh pr view *),Bash(gh pr diff *),Bash(gh issue view *)"', '"Bash(gh pr view *),Bash(gh pr diff *),Bash(gh issue view *),Bash(gh pr comment *)"'),
      replaceOnce(copilotArchitectureWorkflow, '      contents: read\n      pull-requests: read\n      issues: read\n    outputs:', '      contents: write\n      pull-requests: read\n      issues: read\n    outputs:'),
    ]) {
      rejects({ [copilotArchitecturePath]: unsafe }, /agent-copilot-architecture.yml check/);
    }
  });

  it('rejects a Copilot handoff that skips the architecture check or accepts non-Copilot work', () => {
    rejects({ [copilotImplementPath]: replaceOnce(copilotWorkflow, 'gh workflow run agent-copilot-architecture.yml', 'gh workflow run validate.yml') }, /agent-copilot.yml handoff/);
    rejects({ [copilotImplementPath]: replaceOnce(copilotWorkflow, '[ "$author" = "$EXPECTED_COPILOT_AUTHOR" ] || skip', 'true || skip') }, /agent-copilot.yml handoff/);
  });

  it('rejects a Copilot assignment that could target a branch other than develop', () => {
    rejects({ [copilotImplementPath]: replaceOnce(copilotWorkflow, 'base_branch: "develop"', 'base_branch: "main"') }, /agent-copilot.yml assign/);
  });

  it('rejects Claude repairs of Copilot-implemented pull requests', () => {
    rejects({ [repairPath]: replaceOnce(repairWorkflow, 'case "$head_ref" in copilot/*) fail', 'case "$head_ref" in nothing/*) fail') }, /agent-repair.yml repair job/);
  });

  it('keeps updated-head scheduling for both implementers', () => {
    rejects({ [headUpdatePath]: replaceOnce(headUpdateWorkflow, 'elif [[ "$head_ref" == copilot/* ]]; then', 'elif false; then') }, /agent-head-update.yml dispatcher/);
  });
});
