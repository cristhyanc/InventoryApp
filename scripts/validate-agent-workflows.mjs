import { readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FULL_PROVIDER_EXECUTION_ENABLED } from './select-implementation-model.mjs';

export const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

// Workflows are compared as text, so normalise line endings: a Windows checkout with
// `core.autocrlf` presents CRLF while CI presents LF, and the contract must be identical.
export const readRepositoryFile = (path) =>
  readFileSync(resolve(repositoryRoot, path), 'utf8').replaceAll('\r\n', '\n');

function requireText(text, expected, source) {
  if (!text.includes(expected)) {
    throw new Error(`${source}: missing required text: ${expected}`);
  }
}

function forbidText(text, forbidden, source) {
  if (text.includes(forbidden)) {
    throw new Error(`${source}: contains forbidden text: ${forbidden}`);
  }
}

function section(text, start, end, source) {
  const startIndex = text.indexOf(start);
  if (startIndex < 0) {
    throw new Error(`${source}: missing section start: ${start.trim()}`);
  }

  const endIndex = end ? text.indexOf(end, startIndex + start.length) : text.length;
  if (end && endIndex < 0) {
    throw new Error(`${source}: missing section end: ${end.trim()}`);
  }

  return text.slice(startIndex, endIndex);
}

// ---------------------------------------------------------------------------------------
// Deterministic evaluation of the small subset of GitHub Actions expressions that the
// validation workflow uses for its concurrency group and status context. Supported:
// single-quoted strings, numbers, true/false/null, dotted context paths, `==`, `!=`, `&&`,
// `||`, and parentheses, with GitHub's semantics: `&&`/`||` return an operand rather than a
// boolean, string comparison is case-insensitive, and false/0/''/null are falsy.
// ---------------------------------------------------------------------------------------

function tokenize(expression) {
  const tokens = [];
  let index = 0;
  while (index < expression.length) {
    const char = expression[index];
    if (/\s/.test(char)) {
      index += 1;
    } else if (char === '(' || char === ')') {
      tokens.push({ type: char });
      index += 1;
    } else if (expression.startsWith('==', index) || expression.startsWith('!=', index)) {
      tokens.push({ type: 'compare', value: expression.slice(index, index + 2) });
      index += 2;
    } else if (expression.startsWith('&&', index) || expression.startsWith('||', index)) {
      tokens.push({ type: expression.slice(index, index + 2) });
      index += 2;
    } else if (char === "'") {
      let value = '';
      index += 1;
      for (;;) {
        if (index >= expression.length) {
          throw new Error(`Unterminated string literal in expression: ${expression}`);
        }
        if (expression[index] === "'") {
          if (expression[index + 1] === "'") {
            value += "'";
            index += 2;
            continue;
          }
          index += 1;
          break;
        }
        value += expression[index];
        index += 1;
      }
      tokens.push({ type: 'string', value });
    } else {
      const match = /^[A-Za-z0-9_][A-Za-z0-9_.\-]*/.exec(expression.slice(index));
      if (!match) {
        throw new Error(`Unsupported character '${char}' in expression: ${expression}`);
      }
      const word = match[0];
      index += word.length;
      if (word === 'true' || word === 'false') {
        tokens.push({ type: 'literal', value: word === 'true' });
      } else if (word === 'null') {
        tokens.push({ type: 'literal', value: null });
      } else if (/^-?\d+(\.\d+)?$/.test(word)) {
        tokens.push({ type: 'literal', value: Number(word) });
      } else {
        tokens.push({ type: 'path', value: word });
      }
    }
  }
  return tokens;
}

function lookupPath(context, path) {
  let current = context;
  for (const segment of path.split('.')) {
    if (current === null || current === undefined || typeof current !== 'object') {
      return null;
    }
    current = current[segment];
  }
  return current === undefined ? null : current;
}

const isTruthy = (value) =>
  !(value === null || value === undefined || value === false || value === 0 || value === '');

function looselyEqual(left, right) {
  if (typeof left === 'string' && typeof right === 'string') {
    return left.toLowerCase() === right.toLowerCase();
  }
  if (left === null || right === null) {
    return left === right;
  }
  if (typeof left === typeof right) {
    return left === right;
  }
  return String(left).toLowerCase() === String(right).toLowerCase();
}

export function evaluateExpression(expression, context) {
  const tokens = tokenize(expression);
  let position = 0;

  const peek = () => tokens[position];
  const next = () => tokens[position++];
  const expect = (type) => {
    const token = next();
    if (!token || token.type !== type) {
      throw new Error(`Expected ${type} in expression: ${expression}`);
    }
    return token;
  };

  function parsePrimary() {
    const token = next();
    if (!token) {
      throw new Error(`Unexpected end of expression: ${expression}`);
    }
    switch (token.type) {
      case '(': {
        const value = parseOr();
        expect(')');
        return value;
      }
      case 'string':
      case 'literal':
        return token.value;
      case 'path':
        return lookupPath(context, token.value);
      default:
        throw new Error(`Unexpected token '${token.type}' in expression: ${expression}`);
    }
  }

  function parseComparison() {
    let left = parsePrimary();
    while (peek()?.type === 'compare') {
      const operator = next().value;
      const right = parsePrimary();
      const equal = looselyEqual(left, right);
      left = operator === '==' ? equal : !equal;
    }
    return left;
  }

  function parseAnd() {
    let left = parseComparison();
    while (peek()?.type === '&&') {
      next();
      const right = parseComparison();
      left = isTruthy(left) ? right : left;
    }
    return left;
  }

  function parseOr() {
    let left = parseAnd();
    while (peek()?.type === '||') {
      next();
      const right = parseAnd();
      left = isTruthy(left) ? left : right;
    }
    return left;
  }

  const result = parseOr();
  if (position !== tokens.length) {
    throw new Error(`Unexpected trailing tokens in expression: ${expression}`);
  }
  return result;
}

// Renders a workflow value containing `${{ ... }}` placeholders the way GitHub does when the
// value is used as a string: null renders as an empty string.
export function renderTemplate(template, context) {
  return template.replace(/\$\{\{([\s\S]*?)\}\}/g, (_match, expression) => {
    const value = evaluateExpression(expression.trim(), context);
    return value === null || value === undefined ? '' : String(value);
  });
}

export function extractWorkflowName(workflowText) {
  const match = /^name:[ \t]*(.+?)[ \t]*$/m.exec(workflowText);
  if (!match) {
    throw new Error('Workflow has no top-level name.');
  }
  return match[1];
}

export function extractConcurrencyGroup(workflowText) {
  const match = /^concurrency:\n[ \t]+group:[ \t]*(.+?)[ \t]*$/m.exec(workflowText);
  if (!match) {
    throw new Error('Workflow has no top-level concurrency group.');
  }
  return match[1];
}

// Returns the raw value of an `env:` entry of the given name inside a job or step section.
export function extractEnvValue(sectionText, name) {
  const match = new RegExp(`^[ \\t]+${name}:[ \\t]*(.+?)[ \\t]*$`, 'm').exec(sectionText);
  if (!match) {
    throw new Error(`Section has no env value named ${name}.`);
  }
  return match[1];
}

/**
 * Builds the GitHub expression context for one validation run.
 *
 * @param {string} workflowText
 * @param {object} event
 * @param {'pull_request' | 'workflow_dispatch'} event.eventName
 * @param {number} event.prNumber
 * @param {string} event.headSha
 * @param {string} [event.headRepository]
 * @param {boolean} [event.dispatchReview]
 * @param {number} [event.runId]
 */
export function buildRunContext(workflowText, event) {
  const repository = event.repository ?? 'owner/repo';
  const runId = event.runId ?? 1;
  const isPullRequest = event.eventName === 'pull_request';
  return {
    github: {
      workflow: extractWorkflowName(workflowText),
      event_name: event.eventName,
      repository,
      run_id: runId,
      ref: isPullRequest ? `refs/pull/${event.prNumber}/merge` : 'refs/heads/main',
      sha: isPullRequest ? `merge-${event.headSha}` : event.mainSha ?? 'main-sha',
      event: isPullRequest
        ? {
            pull_request: {
              number: event.prNumber,
              head: {
                sha: event.headSha,
                repo: { full_name: event.headRepository ?? repository },
              },
            },
          }
        : {},
    },
    inputs: isPullRequest
      ? {}
      : {
          pr_number: String(event.prNumber),
          head_sha: event.headSha,
          dispatch_review: event.dispatchReview ?? false,
        },
  };
}

/**
 * Simulates how the validation workflow resolves one run's concurrency group and status
 * context, using the workflow file's own expressions rather than a copy of them.
 */
export function simulateValidationRun(workflowText, event) {
  const context = buildRunContext(workflowText, event);
  const contextJob = section(workflowText, '  context:\n', '  validate:\n', 'validate.yml');
  const statusJob = section(workflowText, '  report-status:\n', '  dispatch-review:\n', 'validate.yml');
  const statusContext = renderTemplate(extractEnvValue(contextJob, 'STATUS_CONTEXT'), context);
  const reportStatusContext = renderTemplate(extractEnvValue(statusJob, 'STATUS_CONTEXT'), {
    ...context,
    needs: { context: { outputs: { status_context: statusContext } } },
  });
  return {
    concurrencyGroup: renderTemplate(extractConcurrencyGroup(workflowText), context),
    statusContext,
    reportStatusContext,
  };
}

export const AGENT_VALIDATION_STATUS = 'agent-validation';
export const MERGE_VALIDATION_STATUS = 'merge-validation';

// Proves, from the workflow text itself, that the two validation modes for one pull request
// never share a concurrency group or a status context, while runs of the same mode for the
// same pull request still supersede each other.
export function verifyValidationModeIsolation(workflowText, source) {
  const prNumber = 95;
  const headSha = 'a'.repeat(40);
  const pullRequestRun = simulateValidationRun(workflowText, { eventName: 'pull_request', prNumber, headSha, runId: 1 });
  const dispatchRun = simulateValidationRun(workflowText, { eventName: 'workflow_dispatch', prNumber, headSha, runId: 2 });

  if (pullRequestRun.concurrencyGroup === dispatchRun.concurrencyGroup) {
    throw new Error(
      `${source}: pull_request and workflow_dispatch validation for the same pull request share concurrency group ` +
        `'${dispatchRun.concurrencyGroup}' and would cancel each other.`,
    );
  }
  if (dispatchRun.statusContext !== AGENT_VALIDATION_STATUS) {
    throw new Error(
      `${source}: workflow_dispatch exact-SHA validation must publish '${AGENT_VALIDATION_STATUS}', not '${dispatchRun.statusContext}'.`,
    );
  }
  if (pullRequestRun.statusContext !== MERGE_VALIDATION_STATUS) {
    throw new Error(
      `${source}: pull_request merge-result validation must publish '${MERGE_VALIDATION_STATUS}', not '${pullRequestRun.statusContext}'.`,
    );
  }
  for (const run of [pullRequestRun, dispatchRun]) {
    if (run.reportStatusContext !== run.statusContext) {
      throw new Error(
        `${source}: report-status publishes '${run.reportStatusContext}' but the context job resolved '${run.statusContext}'.`,
      );
    }
  }

  const supersededPullRequestRun = simulateValidationRun(workflowText, {
    eventName: 'pull_request',
    prNumber,
    headSha: 'b'.repeat(40),
    runId: 3,
  });
  const supersededDispatchRun = simulateValidationRun(workflowText, {
    eventName: 'workflow_dispatch',
    prNumber,
    headSha: 'b'.repeat(40),
    runId: 4,
  });
  if (supersededPullRequestRun.concurrencyGroup !== pullRequestRun.concurrencyGroup) {
    throw new Error(`${source}: a newer pull_request validation for the same pull request must cancel the superseded one.`);
  }
  if (supersededDispatchRun.concurrencyGroup !== dispatchRun.concurrencyGroup) {
    throw new Error(`${source}: a newer workflow_dispatch validation for the same pull request must cancel the superseded one.`);
  }

  const otherPullRequestRun = simulateValidationRun(workflowText, { eventName: 'workflow_dispatch', prNumber: prNumber + 1, headSha, runId: 5 });
  if (otherPullRequestRun.concurrencyGroup === dispatchRun.concurrencyGroup) {
    throw new Error(`${source}: validation for different pull requests must not share a concurrency group.`);
  }
}

// pullRequestsPermission is 'read' for every dispatcher except the implementation dispatcher,
// which needs 'write' to label its own already-guarded pull request (gh pr edit --add-label
// resolves to the GraphQL addLabelsToLabelable mutation, which checks the pull-requests
// permission, not issues, regardless of the labelable being a pull request).
function verifySafeDispatcher(text, source, pullRequestsPermission = 'read') {
  verifySafeDispatcherBase(text, source, pullRequestsPermission);
  for (const required of ['EXPECTED_AGENT_AUTHOR: ${{ vars.AGENT_AUTOMATION_APP_BOT_LOGIN }}', 'agent/issue-*']) {
    requireText(text, required, source);
  }
}

function verifySafeDispatcherBase(text, source, pullRequestsPermission = 'read') {
  for (const required of [
    'actions: write',
    `pull-requests: ${pullRequestsPermission}`,
    '--ref main',
    'headRefOid',
    '.github/workflows/',
  ]) {
    requireText(text, required, source);
  }

  const otherPermission = pullRequestsPermission === 'write' ? 'read' : 'write';
  forbidText(text, `pull-requests: ${otherPermission}`, source);

  for (const forbidden of [
    'actions/checkout',
    'CLAUDE_CODE_OAUTH_TOKEN',
    'gh pr merge',
    'gh pr review --approve',
    'gh pr review --request-changes',
    'azure/login',
  ]) {
    forbidText(text, forbidden, source);
  }
}

// The dispatched context jobs hold the pull request number and head SHA in lowercase
// locals; the dispatcher jobs hold them in uppercase step environment variables. Normalise
// both so a single guard contract covers every guarded section.
function normalizeGuardVariables(text) {
  return text.replaceAll('$head_sha', '$HEAD_SHA').replaceAll('$pr_number', '$PR_NUMBER');
}

// Implementation identity. Claude is the only implementer: an `agent/issue-*` branch authored by the
// dedicated agent GitHub App bot (AGENT_AUTOMATION_APP_BOT_LOGIN). Copilot only checks and reviews, so
// no guarded section may accept a `copilot/*` branch or read the retired Copilot author variable.
const CLAUDE_PR_GUARDS = Object.freeze([
  '[[ "$head_ref" == agent/issue-* ]]',
  'AGENT_AUTOMATION_APP_BOT_LOGIN is not configured',
  '[ "$author" = "$EXPECTED_AGENT_AUTHOR" ]',
]);
// Only the REST pull request endpoint returns the canonical author login reliably for bot
// authors, so every guarded section reads `.user.login` over REST and compares it with the
// expected implementer login.
function verifyAgentPrGuards(text, source, staleMessage) {
  for (const required of [
    '--json state,isDraft,baseRefName,headRefName,headRefOid,headRepository,headRepositoryOwner',
    '[ "$state" = "OPEN" ]',
    '[ "$is_draft" = "false" ]',
    '[ "$base_ref" = "develop" ]',
    '[ "$head_repo" = "$GITHUB_REPOSITORY" ]',
    'author="$(gh api "repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER"',
    "--jq '.user.login // empty'",
    '[ "$current_sha" = "$HEAD_SHA" ]',
    staleMessage,
    "grep -Eq '^\\.github/workflows/'",
  ]) {
    requireText(text, required, source);
  }
  for (const required of CLAUDE_PR_GUARDS) requireText(text, required, source);

  for (const forbidden of [
    '.author.login',
    'headRepositoryOwner,author',
    'copilot/*',
    'EXPECTED_COPILOT_AUTHOR',
  ]) {
    forbidText(text, forbidden, source);
  }
}

export const validatePath = '.github/workflows/validate.yml';
export const reviewPath = '.github/workflows/agent-review.yml';
export const implementPath = '.github/workflows/agent-implement.yml';
export const architecturePath = '.github/workflows/agent-architecture.yml';
export const repairPath = '.github/workflows/agent-repair.yml';
export const headUpdatePath = '.github/workflows/agent-head-update.yml';
export const issueTemplatePath = '.github/ISSUE_TEMPLATE/agent-task.yml';
export const pullRequestTemplatePath = '.github/pull_request_template.md';
export const appPushRetryPath = 'scripts/git-push-with-app-retry.sh';
// Copilot no longer implements: these workflows were removed and must not come back.
export const RETIRED_COPILOT_IMPLEMENTATION_PATHS = Object.freeze([
  '.github/workflows/agent-copilot.yml',
  '.github/workflows/agent-copilot-handoff.yml',
  '.github/workflows/agent-copilot-architecture.yml',
  '.github/workflows/copilot-setup-steps.yml',
]);
export const reviewRequestPath = '.github/workflows/agent-review-request.yml';
export const reviewTransportCheckPath = '.github/workflows/agent-review-transport-check.yml';

// ---------------------------------------------------------------------------------------
// Documentation-impact gate. The templates collect the decision, the preflight and validation
// jobs prove that a meaningful declaration exists (scripts/validate-documentation-impact.mjs),
// the review prompt requires the reviewer to judge whether it is truthful, and the repair
// prompt requires repairs to keep documentation accurate without editing the PR description.
// Every requirement below is a literal the workflows and templates must keep.
// ---------------------------------------------------------------------------------------

export const DOCUMENTATION_IMPACT_VALIDATOR = 'scripts/validate-documentation-impact.mjs';

export const ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT = Object.freeze({
  decisionField: [
    'id: documentation-impact-decision',
    'label: Documentation impact decision',
    '        - Documentation changes required\n        - No documentation changes required\n',
    'required: true',
  ],
  detailsField: [
    'id: documentation-impact-details',
    'label: Documentation impact details',
    'required: true',
  ],
  readinessConfirmation:
    'The documentation impact decision is stated, and its details list the affected documentation files/sections or explain specifically why documentation is unaffected',
});

export const PULL_REQUEST_TEMPLATE_DOCUMENTATION_CONTRACT = Object.freeze({
  heading: '## Documentation impact\n',
  decisionLine: 'Decision: <UPDATED or NOT REQUIRED>',
  evidenceLine: 'Evidence: <meaningful evidence>',
  mustPrecede: '## Known limitations and follow-up work\n',
});

export const PREFLIGHT_JOB_CONTRACT = Object.freeze({
  required: [
    "if: (github.event.label.name == 'agent-ready-claude' || github.event.label.name == 'agent-ready-claude-low' || github.event.label.name == 'agent-ready-claude-high' || github.event.label.name == 'agent-ready-full-claude') && github.event.issue.pull_request == null",
    'contents: read',
    'issues: read',
    'actions/checkout',
    'ref: develop',
    'persist-credentials: false',
    `node ${DOCUMENTATION_IMPACT_VALIDATOR} --issue-body`,
    '::error title=Documentation impact preflight failed::',
  ],
  forbidden: [
    'contents: write',
    'issues: write',
    'pull-requests: write',
    'actions: write',
    'CLAUDE_CODE_OAUTH_TOKEN',
    'claude-code-action',
    'gh issue edit',
    'gh issue comment',
    '--add-label',
    '--remove-label',
    'git push',
    'git checkout -b',
  ],
});

export const IMPLEMENT_JOB_DOCUMENTATION_CONTRACT = Object.freeze({
  needs: '    needs: [preflight, model]\n',
  condition: "if: needs.preflight.result == 'success' && needs.model.result == 'success' && (github.event.label.name == 'agent-ready-claude' || github.event.label.name == 'agent-ready-claude-low' || github.event.label.name == 'agent-ready-claude-high' || github.event.label.name == 'agent-ready-full-claude') && github.event.issue.pull_request == null",
  prompt: [
    'Restate its acceptance criteria, its explicit exclusions, and its Documentation impact decision and Documentation impact details.',
    "Follow the issue's Documentation impact decision exactly.",
    'If it is "Documentation changes required", update every documentation file and section listed in the Documentation impact details',
    'Never leave documentation that the change contradicts.',
    'Fill the "## Documentation impact" section accurately with exactly one `Decision:` line and one `Evidence:` line',
    '`Decision: UPDATED` when this pull request changes documentation, with Evidence listing each documentation file and what changed in it',
    '`Decision: NOT REQUIRED` only when no documentation changed, with Evidence explaining for this specific change why behaviour, contracts, architecture, configuration, automation, deployment, operations and user workflows are unaffected',
    `node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body .agent-pr-body.md`,
  ],
});

export const VALIDATE_WORKFLOW_DOCUMENTATION_CONTRACT = Object.freeze({
  pullRequestTypes: 'types: [opened, synchronize, reopened, edited]',
  contextJob: [
    'pr_body_b64: ${{ steps.context.outputs.pr_body_b64 }}',
    'EVENT_PR_BODY: ${{ github.event.pull_request.body }}',
    'pr_body="$(gh pr view "$pr_number" --repo "$GITHUB_REPOSITORY" --json body --jq \'.body // ""\')"',
    'pr_body="$EVENT_PR_BODY"',
    'pr_body_b64="$(printf \'%s\' "$pr_body" | base64 -w0)"',
    'echo "pr_body_b64=$pr_body_b64"',
  ],
  validateJob: [
    'PR_BODY_B64: ${{ needs.context.outputs.pr_body_b64 }}',
    'body_file="$RUNNER_TEMP/pull-request-body.md"',
    'printf \'%s\' "$PR_BODY_B64" | base64 -d > "$body_file"',
    `node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body "$body_file"`,
    'node --test scripts/validate-documentation-impact.test.mjs',
  ],
  validateJobForbidden: ['GH_TOKEN', 'GITHUB_TOKEN', 'github.token', 'pull-requests: write'],
  repositoryValidation: 'run: bash scripts/validate.sh',
});

export const REVIEW_PROMPT_DOCUMENTATION_CONTRACT = Object.freeze([
  'Compare three things: the issue\'s Documentation impact decision and details, the pull request\'s "## Documentation impact" declaration (Decision and Evidence), and the actual diff.',
  'Missing, inaccurate or incomplete required documentation is a blocker.',
  'verify from the diff, not from filenames alone',
]);

export const REPAIR_PROMPT_DOCUMENTATION_CONTRACT = Object.freeze([
  'update the affected documentation on the head branch in the same repair',
  'You cannot and must not edit the pull request description.',
  'state in your closing comment exactly what must be corrected',
]);

function requireOrder(text, first, second, source, description) {
  const firstIndex = text.indexOf(first);
  const secondIndex = text.indexOf(second);
  if (firstIndex < 0) {
    throw new Error(`${source}: missing required text: ${first.trim()}`);
  }
  if (secondIndex < 0) {
    throw new Error(`${source}: missing required text: ${second.trim()}`);
  }
  if (firstIndex > secondIndex) {
    throw new Error(`${source}: ${description}`);
  }
}

function extractPromptText(jobText, source) {
  return section(jobText, '          prompt: |\n', '          claude_args: |\n', source);
}

function extractAllowedTools(jobText, source) {
  return section(jobText, '          claude_args: |\n', '            --disallowedTools', source);
}

export function verifyDocumentationImpactGate(read = readRepositoryFile) {
  // Issue template: the decision dropdown with exactly the two options, the details textarea,
  // both required, and a readiness confirmation that covers the decision.
  const issueTemplate = read(issueTemplatePath);
  const decisionField = section(issueTemplate, '  - type: dropdown\n    id: documentation-impact-decision\n', '\n  - type: ', `${issueTemplatePath} decision field`);
  for (const required of ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT.decisionField) {
    requireText(decisionField, required, `${issueTemplatePath} decision field`);
  }
  const optionLines = decisionField.split('\n').filter((line) => /^        - /.test(line));
  if (optionLines.length !== 2) {
    throw new Error(`${issueTemplatePath} decision field: expected exactly two options, found ${optionLines.length}.`);
  }
  const detailsField = section(issueTemplate, '  - type: textarea\n    id: documentation-impact-details\n', '\n  - type: ', `${issueTemplatePath} details field`);
  for (const required of ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT.detailsField) {
    requireText(detailsField, required, `${issueTemplatePath} details field`);
  }
  const readiness = section(issueTemplate, '    id: readiness\n', null, `${issueTemplatePath} readiness`);
  requireText(readiness, ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT.readinessConfirmation, `${issueTemplatePath} readiness`);
  const readinessItem = section(readiness, ISSUE_TEMPLATE_DOCUMENTATION_CONTRACT.readinessConfirmation, '\n        - label:', `${issueTemplatePath} readiness`);
  requireText(readinessItem, 'required: true', `${issueTemplatePath} readiness documentation confirmation`);

  // Pull request template: the deterministic section, with its placeholders, before known limitations.
  const pullRequestTemplate = read(pullRequestTemplatePath);
  const prContract = PULL_REQUEST_TEMPLATE_DOCUMENTATION_CONTRACT;
  if (pullRequestTemplate.split(prContract.heading).length !== 2) {
    throw new Error(`${pullRequestTemplatePath}: the "${prContract.heading.trim()}" section must appear exactly once.`);
  }
  requireOrder(pullRequestTemplate, prContract.heading, prContract.mustPrecede, pullRequestTemplatePath, 'the Documentation impact section must come before Known limitations and follow-up work.');
  const prSection = section(pullRequestTemplate, prContract.heading, prContract.mustPrecede, pullRequestTemplatePath);
  requireText(prSection, prContract.decisionLine, `${pullRequestTemplatePath} Documentation impact section`);
  requireText(prSection, prContract.evidenceLine, `${pullRequestTemplatePath} Documentation impact section`);
  const uncommented = prSection.replace(/<!--[\s\S]*?-->/g, '');
  for (const [field, count] of [['Decision:', (uncommented.match(/^Decision:/gm) ?? []).length], ['Evidence:', (uncommented.match(/^Evidence:/gm) ?? []).length]]) {
    if (count !== 1) {
      throw new Error(`${pullRequestTemplatePath} Documentation impact section: expected exactly one "${field}" line outside comments, found ${count}.`);
    }
  }

  // Implementation workflow: a read-only preflight job before the implementation job, which
  // must depend on it, and a prompt that carries the documentation requirements.
  const implement = read(implementPath);
  requireOrder(implement, '  preflight:\n', '  implement:\n', implementPath, 'the preflight job must be defined before the implementation job.');
  const preflight = section(implement, '  preflight:\n', '  implement:\n', `${implementPath} preflight job`);
  for (const required of PREFLIGHT_JOB_CONTRACT.required) {
    requireText(preflight, required, `${implementPath} preflight job`);
  }
  for (const forbidden of PREFLIGHT_JOB_CONTRACT.forbidden) {
    forbidText(preflight, forbidden, `${implementPath} preflight job`);
  }
  const implementJob = section(implement, '  implement:\n', '  dispatch-architecture:\n', `${implementPath} implement job`);
  requireText(implementJob, IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.needs, `${implementPath} implement job`);
  requireText(implementJob, IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.condition, `${implementPath} implement job`);
  requireOrder(implementJob, IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.needs, '    steps:\n', `${implementPath} implement job`, 'needs: preflight must be declared on the job.');
  const implementPrompt = extractPromptText(implementJob, `${implementPath} implement prompt`);
  for (const required of IMPLEMENT_JOB_DOCUMENTATION_CONTRACT.prompt) {
    requireText(implementPrompt, required, `${implementPath} implement prompt`);
  }
  const implementAllowedTools = extractAllowedTools(implementJob, `${implementPath} allowed tools`);
  requireText(implementAllowedTools, `Bash(node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body *)`, `${implementPath} allowed tools`);
  for (const forbidden of ['gh pr edit', 'gh issue edit', 'gh label']) {
    forbidText(implementAllowedTools, forbidden, `${implementPath} allowed tools`);
  }

  verifyTrackedFileDeletionPermissions(implementAllowedTools, `${implementPath} implementation allowed tools`);
  verifyScopedStagingCleanupPermissions(implementAllowedTools, `${implementPath} implementation allowed tools`);

  // Validation workflow: the body is obtained in the trusted context job for both events,
  // handed over base64-encoded, decoded into a temporary file, and validated before the
  // repository validation, by a job that still holds no GitHub token.
  const validate = read(validatePath);
  const validateContract = VALIDATE_WORKFLOW_DOCUMENTATION_CONTRACT;
  requireText(validate, validateContract.pullRequestTypes, validatePath);
  const validationContext = section(validate, '  context:\n', '  validate:\n', validatePath);
  for (const required of validateContract.contextJob) {
    requireText(validationContext, required, 'validate.yml context job');
  }
  const dispatchBranch = section(validationContext, 'if [ "$GITHUB_EVENT_NAME" = "workflow_dispatch" ]; then\n', '\n          else\n', 'validate.yml dispatched context');
  requireText(dispatchBranch, 'pr_body="$(gh pr view "$pr_number"', 'validate.yml dispatched context');
  const pullRequestBranch = section(validationContext, '\n          else\n', '\n          fi\n', 'validate.yml pull_request context');
  requireText(pullRequestBranch, 'pr_body="$EVENT_PR_BODY"', 'validate.yml pull_request context');
  const validationJob = section(validate, '  validate:\n', '  report-status:\n', validatePath);
  for (const required of validateContract.validateJob) {
    requireText(validationJob, required, 'validate.yml validate job');
  }
  for (const forbidden of validateContract.validateJobForbidden) {
    forbidText(validationJob, forbidden, 'validate.yml validate job');
  }
  requireOrder(validationJob, `node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body "$body_file"`, validateContract.repositoryValidation, 'validate.yml validate job', 'the documentation-impact validator must run before repository validation.');
  requireOrder(validationJob, 'actions/checkout', `node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body "$body_file"`, 'validate.yml validate job', 'the documentation-impact validator must run after checkout.');

  // Review workflow: the prompt must compare the issue decision, the PR declaration and the
  // diff, and treat missing documentation as a blocker, with no additional write authority.
  const review = read(reviewPath);
  const reviewJob = section(review, '  review:\n', '  copilot-review:\n', reviewPath);
  const reviewPrompt = extractPromptText(reviewJob, 'agent-review.yml review prompt');
  for (const required of REVIEW_PROMPT_DOCUMENTATION_CONTRACT) {
    requireText(reviewPrompt, required, 'agent-review.yml review prompt');
  }
  const reviewPermissions = section(reviewJob, '    permissions:\n', '\n    steps:\n', 'agent-review.yml review permissions');
  for (const forbidden of ['contents: write', 'issues: write', 'actions: write', 'statuses: write', 'checks: write']) {
    forbidText(reviewPermissions, forbidden, 'agent-review.yml review permissions');
  }
  const reviewAllowedTools = extractAllowedTools(reviewJob, 'agent-review.yml allowed tools');
  for (const forbidden of ['gh pr edit', 'gh issue edit', 'gh pr comment', 'gh label', 'Edit,', 'Write']) {
    forbidText(reviewAllowedTools, forbidden, 'agent-review.yml allowed tools');
  }

  // Repair workflow: repairs update documentation but never the PR description.
  const repair = read(repairPath);
  const repairJob = section(repair, '  repair:\n', '  dispatch-validation:\n', repairPath);
  const repairPrompt = extractPromptText(repairJob, 'agent-repair.yml repair prompt');
  for (const required of REPAIR_PROMPT_DOCUMENTATION_CONTRACT) {
    requireText(repairPrompt, required, 'agent-repair.yml repair prompt');
  }
  const repairAllowedTools = extractAllowedTools(repairJob, 'agent-repair.yml allowed tools');
  forbidText(repairAllowedTools, 'gh pr edit', 'agent-repair.yml allowed tools');
  verifyTrackedFileDeletionPermissions(repairAllowedTools, 'agent-repair.yml allowed tools');
  const repairDisallowedTools = section(repairJob, '            --disallowedTools', '\n      - name: Record outcome', 'agent-repair.yml disallowed tools');
  requireText(repairDisallowedTools, 'Bash(gh pr edit *)', 'agent-repair.yml disallowed tools');
}

// ---------------------------------------------------------------------------------------
// Review judgment, guarded publication and updated-head scheduling (issue #268).
// The review model is read-only and returns structured output; a separate deterministic job
// re-verifies the live pull request before publishing a commit-bound review and records the
// per-SHA agent-review-verdict status; agent-head-update.yml schedules exact-SHA validation for
// a new head from the trusted default-branch definition. Every requirement is a literal the
// workflows must keep.
// ---------------------------------------------------------------------------------------

export const REVIEW_VERDICT_STATUS = 'agent-review-verdict';

export const REVIEW_PROMPT_JUDGMENT_CONTRACT = Object.freeze([
  'Assess every acceptance criterion of the linked issue individually as `met`, `not met`, or `not verified`',
  'A criterion you could not verify is `not verified`, never `met`.',
  '`VERDICT: READY FOR HUMAN REVIEW` is allowed only when every acceptance criterion is `met` and there are no blockers.',
  'is never waived because the pre-existing or legacy behaviour is worse, because the failure seems unlikely or narrow, because the tests pass, or because the Sonar quality gate or any other check is green.',
  'Record a blocker that starts with "Human decision required:"',
  'inspect the authoritative read, the expected-state comparison, and the mutation as one operation.',
  'A transaction around only the write, or a check performed on a separate earlier read, is not evidence of atomicity.',
  'Require a regression test in which the state changes between the initial read and the mutation',
  'Deliver the result only as the structured output defined by the JSON schema; do not publish anything yourself.',
]);

export const REVIEW_JOB_CONTRACT = Object.freeze({
  required: [
    'id: review_agent',
    'REVIEW_OUTPUT: ${{ steps.review_agent.outputs.structured_output }}',
    "--json-schema '",
    '"reviewed_head_sha":{"type":"string","pattern":"^[0-9a-f]{40}$"}',
    '"enum":["met","not met","not verified"]',
    '"enum":["CHANGES REQUESTED","READY FOR HUMAN REVIEW"]',
    'Bash(gh pr review *)',
    '"mcp__github_inline_comment__create_inline_comment"',
  ],
  permissionsRequired: ['contents: read', 'pull-requests: read'],
  permissionsForbidden: ['pull-requests: write', 'issues: write', 'statuses: write', 'contents: write', 'actions: write', 'checks: write'],
  allowedToolsForbidden: ['gh pr review', 'mcp__github_inline_comment', 'gh pr comment', 'gh api', 'gh workflow'],
});

export const REVIEW_PUBLISH_CONTRACT = Object.freeze({
  required: [
    "if: always() && needs.context.result == 'success'",
    '      - context\n      - review\n',
    'pull-requests: write',
    'statuses: write',
    'VERDICT_CONTEXT: agent-review-verdict',
    '[ "$REVIEW_RESULT" = "success" ] || suppress',
    '[ "$reviewed_sha" = "$HEAD_SHA" ] || suppress',
    'all(.criteria[]; .status == "met")',
    '(.blockers | length) > 0',
    'Refusing stale review publication',
    'any(.labels[]?; .name == "agent-review")',
    'select(.context == "agent-validation")',
    '[ "$validation_state" = "success" ] || suppress',
    "grep -c '^VERDICT:'",
    'commit_id: $sha, event: "COMMENT"',
    'repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER/reviews',
    'set_verdict_status error',
    'if: failure()',
  ],
  forbidden: [
    'actions/checkout',
    'CLAUDE_CODE_OAUTH_TOKEN',
    'claude-code-action',
    'actions: write',
    'contents: write',
    'APPROVE',
    'REQUEST_CHANGES',
    'gh pr merge',
    'gh pr edit',
    'gh workflow',
    '--add-label',
    'merge-validation',
  ],
});

export const HEAD_UPDATE_CONTRACT = Object.freeze({
  triggerRequired: ['pull_request_target:', 'types: [synchronize]', '      - develop'],
  triggerForbidden: ['  pull_request:\n', 'workflow_run', 'push:', 'issue_comment'],
  required: [
    'group: agent-head-update-pr-${{ github.event.pull_request.number }}',
    'cancel-in-progress: true',
    "contains(github.event.pull_request.labels.*.name, 'agent-review')",
    'statuses: read',
    'Refusing stale scheduled validation',
    'any(.labels[]?; .name == "agent-review")',
    'skip "the agent-review label is absent."',
    'select(.context == "agent-validation")',
    '[ -z "$existing_validation" ]',
    'gh workflow run validate.yml',
    '-f dispatch_review=true',
  ],
  forbidden: ['statuses: write', 'contents:', 'gh pr review', 'agent-review.yml', 'git push', 'ref: ${{ github.event.pull_request', 'pull-requests: write', 'issues: write', 'gh pr edit', "'copilot/'", 'agent-architecture-fix'],
});

/** Manual re-review: a human's agent-review label dispatches agent-review.yml from main, nothing else. */
function verifyReviewRequestDispatcher(workflow) {
  const triggers = section(workflow, 'on:\n', '\npermissions: {}\n', `${reviewRequestPath} triggers`);
  for (const required of ['pull_request_target:', 'types: [labeled]', '      - develop']) requireText(triggers, required, `${reviewRequestPath} triggers`);
  for (const forbidden of ['  pull_request:\n', 'synchronize', 'issue_comment', 'workflow_run', 'push:']) forbidText(triggers, forbidden, `${reviewRequestPath} triggers`);
  requireText(workflow, 'permissions: {}', reviewRequestPath);
  const job = section(workflow, '  dispatch-review:\n', null, `${reviewRequestPath} dispatcher`);
  verifySafeDispatcher(job, `${reviewRequestPath} dispatcher`);
  verifyAgentPrGuards(job, `${reviewRequestPath} dispatcher`, 'Refusing stale review request');
  for (const required of [
    "github.event.label.name == 'agent-review'", 'statuses: read',
    'any(.labels[]?; .name == "agent-review")', 'select(.context == "agent-validation")', '[ "$validation_state" = "success" ]',
    'A manual agent-review request requires a successful latest agent-validation status on the exact head SHA.',
    'gh workflow run agent-review.yml', '-f pr_number="$PR_NUMBER"', '-f head_sha="$HEAD_SHA"',
  ]) requireText(job, required, `${reviewRequestPath} dispatcher`);
  for (const forbidden of ['secrets.COPILOT', 'CLAUDE_CODE_OAUTH_TOKEN', 'AGENT_AUTOMATION_APP_PRIVATE_KEY', 'issues: write', 'contents:', 'statuses: write', 'gh pr edit', 'gh issue edit', 'validate.yml', MERGE_VALIDATION_STATUS]) {
    forbidText(job, forbidden, `${reviewRequestPath} dispatcher`);
  }
  requireOrder(job, '[ "$validation_state" = "success" ]', 'gh workflow run agent-review.yml', `${reviewRequestPath} dispatcher`, 'the exact head must have passed validation before a review is dispatched.');
}

/**
 * The verified provider mode alone selects the reviewer (docs/automation.md "Provider roles and readiness labels"):
 * Copilot on the cross-claude route, a fresh Claude invocation on the full-claude fallback, which the
 * prompt and the published review must call a same-provider review, not an independent one.
 */
export const REVIEW_ROUTES = Object.freeze({
  'cross-claude': { implementer: 'claude', reviewer: 'copilot', sameProvider: false },
  'full-claude': { implementer: 'claude', reviewer: 'claude', sameProvider: true },
});

function verifyReviewRoutes(review) {
  const context = section(review, '  context:\n', '  review:\n', 'agent-review.yml context');
  for (const [mode, route] of Object.entries(REVIEW_ROUTES)) {
    const branch = section(context, `            ${mode}:${route.implementer})\n`, ' ;;\n', `agent-review.yml ${mode} route`);
    requireText(branch, `reviewer=${route.reviewer}\n`, `agent-review.yml ${mode} route`);
    if (route.sameProvider) requireText(branch, 'This is a same-provider review, not an independent one', `agent-review.yml ${mode} route`);
    else forbidText(branch, 'same-provider', `agent-review.yml ${mode} route`);
  }
  for (const required of ['*) fail "Provider mode', 'reviewer: ${{ steps.context.outputs.reviewer }}', 'echo "reviewer=$reviewer"', 'echo "review_relation=$review_relation"']) {
    requireText(context, required, 'agent-review.yml context');
  }
  const publish = section(review, '  publish:\n', null, 'agent-review.yml publish job');
  for (const required of [
    'cross-claude:claude:copilot)',
    'full-claude:claude:claude)', 'Same-provider review (full-claude fallback): not independent',
    '*) suppress "unknown review route', 'Review type: \\($review_type)',
  ]) requireText(publish, required, 'agent-review.yml publish job');
  for (const retired of ['cross-copilot', 'full-copilot']) {
    forbidText(context, retired, 'agent-review.yml context');
    forbidText(publish, retired, 'agent-review.yml publish job');
  }
}

/** Copilot's final review: read-only, pinned CLI, schema-checked output. */
function verifyCopilotReviewJob(review) {
  const job = section(review, '  copilot-review:\n', '  publish:\n', 'agent-review.yml Copilot review job');
  for (const required of [
    "if: needs.context.outputs.reviewer == 'copilot'",
    'REVIEW_RELATION: ${{ needs.context.outputs.review_relation }}', 'prompt="${prompt//@RELATION@/$REVIEW_RELATION}"',
    'ref: ${{ needs.context.outputs.head_sha }}', 'persist-credentials: false',
    'TRUSTED_SHA: ${{ github.sha }}', 'git show "$TRUSTED_SHA:.github/copilot-cli/$file"', '--ignore-scripts',
    '[ "$GITHUB_REF" = "refs/heads/main" ] ||',
    'COPILOT_GITHUB_TOKEN: ${{ secrets.COPILOT_CLI_TOKEN }}', 'COPILOT_AUTO_UPDATE: "false"',
    "--deny-tool='write' \\\n            -p \"$prompt\" > \"$work/copilot-output.md\"", '--no-ask-user',
    '[ -z "$(git status --porcelain)" ]', '[ "$(git rev-parse HEAD)" = "$HEAD_SHA" ]',
    'BEGIN_REVIEW_JSON', 'END_REVIEW_JSON',
    'jq -c . "$work/review.json" > "$RUNNER_TEMP/review-raw.json"',
  ]) requireText(job, required, 'agent-review.yml Copilot review job');
  // The enforced contract lives in validate_copilot_output; the diagnostics that repeat it do not count.
  const validator = section(job, 'validate_copilot_output() {\n', '\n          }\n', 'agent-review.yml Copilot review contract');
  for (const required of [
    '.verdict == "CHANGES REQUESTED" or .verdict == "READY FOR HUMAN REVIEW"',
    '.status == "met" or .status == "not met" or .status == "not verified"',
  ]) requireText(validator, required, 'agent-review.yml Copilot review contract');
  // The single format-only repair may not run shell commands, write files, fetch URLs or use built-in MCP servers.
  requireText(
    job,
    "copilot -s --no-ask-user --disable-builtin-mcps \\\n              --deny-tool='shell' --deny-tool='write' --deny-tool='url' \\\n              -p \"$repair_prompt\"",
    'agent-review.yml Copilot review format repair',
  );
  const permissions = section(job, '    permissions:\n', '\n    outputs:\n', 'agent-review.yml Copilot review permissions');
  forbidText(permissions, 'write', 'agent-review.yml Copilot review permissions');
  for (const forbidden of [
    'CLAUDE_CODE_OAUTH_TOKEN', 'AGENT_AUTOMATION_APP_PRIVATE_KEY', 'create-github-app-token', 'COPILOT_AGENT_TOKEN',
    'npm install', '@github/copilot@', '--allow-all', "--allow-tool='write'", "--allow-tool='shell'",
    "shell(gh api", "shell(gh pr comment", "shell(gh pr review", "shell(gh pr edit", "shell(gh issue edit", "shell(git push", "shell(git commit",
  ]) forbidText(job, forbidden, 'agent-review.yml Copilot review job');
}

export const REVIEW_TRANSPORT_CONTRACT = Object.freeze({
  reviewerOutputs: [
    'artifact_id: ${{ steps.upload.outputs.artifact-id }}',
    'artifact_digest: ${{ steps.package.outputs.digest }}',
  ],
  packageRequired: [
    '      - name: Package review result\n        id: package\n',
    '"repos/$GITHUB_REPOSITORY/contents/scripts/agent-review-transport.mjs?ref=$GITHUB_WORKFLOW_SHA"',
    'node "$transport" package "$RUNNER_TEMP/review-raw.json" "$result_dir/review-envelope.json"',
    'rm -f "$RUNNER_TEMP/review-raw.json"',
    '      - name: Upload review result\n        id: upload\n        uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2\n',
    'name: ${{ steps.package.outputs.name }}',
    'path: ${{ runner.temp }}/agent-review-result/review-envelope.json',
    'if-no-files-found: error',
    'retention-days: 1',
  ],
  publishRequired: [
    '    permissions:\n      pull-requests: write\n      statuses: write\n      contents: read\n      issues: read\n      actions: read\n',
    '"repos/$GITHUB_REPOSITORY/contents/scripts/agent-review-transport.mjs?ref=$GITHUB_WORKFLOW_SHA"',
    'node "$transport" fetch "$work/review.json"',
    'transport_failed() {',
    'set_verdict_status error "Review result not delivered: ',
  ],
  publishForbidden: ['REVIEW_OUTPUT', 'structured_output', 'actions/download-artifact', 'scripts/agent-review-transport.mjs "'],
});

/**
 * The review travels from each read-only reviewer job to the publisher as a run-bound artifact, never
 * as a job output: GitHub withholds a job output containing any masked value, which once silently
 * lost a finished review (PR #558). Both sides run the transport script from the trusted workflow
 * commit, never from the pull request head.
 */
function verifyReviewTransport(review) {
  // Each reviewer job hands its own credentials to the package step for exact-value redaction.
  const heldCredentials = {
    review: ['REVIEW_REDACT_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}', 'REVIEW_REDACT_PROVIDER_TOKEN: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}'],
    'copilot-review': ['REVIEW_REDACT_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}', 'REVIEW_REDACT_PROVIDER_TOKEN: ${{ secrets.COPILOT_CLI_TOKEN }}'],
  };
  for (const [name, start, end] of [['review', '  review:\n', '  copilot-review:\n'], ['copilot-review', '  copilot-review:\n', '  publish:\n']]) {
    const job = section(review, start, end, `agent-review.yml ${name} job`);
    const packageStep = section(job, '      - name: Package review result\n', '      - name: Upload review result\n', `agent-review.yml ${name} package step`);
    for (const required of heldCredentials[name]) requireText(packageStep, required, `agent-review.yml ${name} review transport`);
    const outputs = section(job, '    outputs:\n', '\n    steps:\n', `agent-review.yml ${name} outputs`);
    for (const required of REVIEW_TRANSPORT_CONTRACT.reviewerOutputs) requireText(outputs, required, `agent-review.yml ${name} outputs`);
    forbidText(outputs, 'structured_output', `agent-review.yml ${name} outputs`);
    for (const required of REVIEW_TRANSPORT_CONTRACT.packageRequired) requireText(job, required, `agent-review.yml ${name} review transport`);
    forbidText(job, 'scripts/agent-review-transport.mjs package', `agent-review.yml ${name} review transport`);
    const permissions = section(job, '    permissions:\n', '\n    outputs:\n', `agent-review.yml ${name} permissions`);
    forbidText(permissions, 'write', `agent-review.yml ${name} permissions`);
  }
  const publish = section(review, '  publish:\n', null, 'agent-review.yml publish job');
  for (const required of REVIEW_TRANSPORT_CONTRACT.publishRequired) requireText(publish, required, 'agent-review.yml publish transport');
  for (const forbidden of REVIEW_TRANSPORT_CONTRACT.publishForbidden) forbidText(publish, forbidden, 'agent-review.yml publish transport');
  requireOrder(publish, 'node "$transport" fetch', 'repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER/reviews', 'agent-review.yml publish transport', 'the review must be received and verified before anything is published.');
}

/**
 * The live hand-off check exercises the transport on real runners with a fixture only: read-only
 * permissions, no repository secret, no model, and the same pinned upload action and retention.
 */
function verifyReviewTransportCheck(check) {
  const source = reviewTransportCheckPath;
  for (const required of [
    'permissions: {}',
    '      - scripts/agent-review-transport.mjs\n',
    '      - .github/workflows/agent-review.yml\n',
    'node scripts/agent-review-transport.mjs package',
    'node scripts/agent-review-transport.mjs fetch',
    'uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2',
    'retention-days: 1',
    'persist-credentials: false',
  ]) requireText(check, required, source);
  for (const forbidden of ['secrets.', ': write', 'pull_request_target', 'claude-code-action', 'copilot -', 'gh pr ', 'gh workflow']) forbidText(check, forbidden, source);
}

/** Enforces the review judgment, guarded publication and updated-head scheduling contract. */
export function verifyReviewPublicationAndScheduling(read = readRepositoryFile) {
  const review = read(reviewPath);
  const reviewJob = section(review, '  review:\n', '  copilot-review:\n', reviewPath);
  requireText(reviewJob, "if: needs.context.outputs.reviewer == 'claude'", 'agent-review.yml review job');
  requireText(reviewJob, '${{ needs.context.outputs.review_relation }}', 'agent-review.yml review job');
  verifyReviewRoutes(review);
  const prompt = extractPromptText(reviewJob, 'agent-review.yml review prompt');
  for (const required of REVIEW_PROMPT_JUDGMENT_CONTRACT) {
    requireText(prompt, required, 'agent-review.yml review prompt');
  }
  for (const required of REVIEW_JOB_CONTRACT.required) {
    requireText(reviewJob, required, 'agent-review.yml review job');
  }
  const permissions = section(reviewJob, '    permissions:\n', '\n    outputs:\n', 'agent-review.yml review permissions');
  for (const required of REVIEW_JOB_CONTRACT.permissionsRequired) {
    requireText(permissions, required, 'agent-review.yml review permissions');
  }
  for (const forbidden of REVIEW_JOB_CONTRACT.permissionsForbidden) {
    forbidText(permissions, forbidden, 'agent-review.yml review permissions');
  }
  const allowed = extractAllowedTools(reviewJob, 'agent-review.yml allowed tools');
  for (const forbidden of REVIEW_JOB_CONTRACT.allowedToolsForbidden) {
    forbidText(allowed, forbidden, 'agent-review.yml allowed tools');
  }

  const publish = section(review, '  publish:\n', null, reviewPath);
  for (const required of REVIEW_PUBLISH_CONTRACT.required) {
    requireText(publish, required, 'agent-review.yml publish job');
  }
  for (const forbidden of REVIEW_PUBLISH_CONTRACT.forbidden) {
    forbidText(publish, forbidden, 'agent-review.yml publish job');
  }
  verifyAgentPrGuards(publish, 'agent-review.yml publish job', 'Refusing stale review publication');
  // The publisher re-derives the implementer and provider mode from the live pull request and takes
  // the review only from the job of the reviewer the verified route selected.
  for (const required of [
    'IMPLEMENTER: ${{ needs.context.outputs.implementer }}',
    '[ "$live_implementer" = "$IMPLEMENTER" ]',
    "REVIEW_RESULT: ${{ needs.context.outputs.reviewer == 'copilot' && needs.copilot-review.result || needs.review.result }}",
    "REVIEW_ARTIFACT_ID: ${{ needs.context.outputs.reviewer == 'copilot' && needs.copilot-review.outputs.artifact_id || needs.review.outputs.artifact_id }}",
    "REVIEW_ARTIFACT_DIGEST: ${{ needs.context.outputs.reviewer == 'copilot' && needs.copilot-review.outputs.artifact_digest || needs.review.outputs.artifact_digest }}",
    'REVIEWER: ${{ needs.context.outputs.reviewer }}',
    '      - review\n      - copilot-review\n',
  ]) requireText(publish, required, 'agent-review.yml publish job');
  // The fail-closed status must not claim "nothing was published" once the review is public.
  for (const required of ['echo "review_posted=true" >> "$GITHUB_OUTPUT"', 'REVIEW_POSTED: ${{ steps.publish.outputs.review_posted }}', 'if [ "$REVIEW_POSTED" = "true" ]; then']) {
    requireText(publish, required, 'agent-review.yml publish job');
  }
  requireOrder(publish, '--input "$work/payload-fallback.json" >/dev/null\n          fi\n', 'echo "review_posted=true" >> "$GITHUB_OUTPUT"', 'agent-review.yml publish job', 'publication must be recorded only after the review was posted.');
  verifyCopilotReviewJob(review);
  verifyReviewTransport(review);
  verifyReviewTransportCheck(read(reviewTransportCheckPath));
  requireOrder(publish, '[ "$current_sha" = "$HEAD_SHA" ]', 'repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER/reviews', 'agent-review.yml publish job', 'the current head must be re-verified before the review is published.');
  requireOrder(publish, '[ "$validation_state" = "success" ]', 'repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER/reviews', 'agent-review.yml publish job', 'validation must be re-verified before the review is published.');

  const headUpdate = read(headUpdatePath);
  const triggers = section(headUpdate, 'on:\n', 'permissions:\n', `${headUpdatePath} triggers`);
  for (const required of HEAD_UPDATE_CONTRACT.triggerRequired) {
    requireText(triggers, required, `${headUpdatePath} triggers`);
  }
  for (const forbidden of HEAD_UPDATE_CONTRACT.triggerForbidden) {
    forbidText(triggers, forbidden, `${headUpdatePath} triggers`);
  }
  requireText(headUpdate, 'permissions: {}', headUpdatePath);
  for (const required of HEAD_UPDATE_CONTRACT.required) {
    requireText(headUpdate, required, headUpdatePath);
  }
  for (const forbidden of HEAD_UPDATE_CONTRACT.forbidden) {
    forbidText(headUpdate, forbidden, headUpdatePath);
  }
  const dispatcher = section(headUpdate, '  dispatch-validation:\n', null, headUpdatePath);
  verifySafeDispatcher(dispatcher, `${headUpdatePath} dispatcher`);
  verifyAgentPrGuards(dispatcher, `${headUpdatePath} dispatcher`, 'Refusing stale scheduled validation');
  requireOrder(dispatcher, '[ -z "$existing_validation" ]', 'gh workflow run validate.yml', `${headUpdatePath} dispatcher`, 'the duplicate check must run before dispatch.');

  const repair = read(repairPath);
  const repairDispatcher = section(repair, '  dispatch-validation:\n', null, repairPath);
  requireText(repairDispatcher, 'statuses: read', `${repairPath} validation dispatcher`);
  requireText(repairDispatcher, 'select(.context == "agent-validation")', `${repairPath} validation dispatcher`);
  requireOrder(repairDispatcher, 'if [ -n "$existing_validation" ]; then', 'gh workflow run validate.yml', `${repairPath} validation dispatcher`, 'the duplicate check must run before dispatch.');
}

export const VALIDATION_CONCURRENCY_GROUP =
  'group: validation-${{ github.workflow }}-${{ github.event_name }}-${{ github.event.pull_request.number || inputs.pr_number || github.ref }}';
export const STATUS_CONTEXT_EXPRESSION =
  "STATUS_CONTEXT: ${{ github.event_name == 'workflow_dispatch' && 'agent-validation' || 'merge-validation' }}";

/** A tracked-file deletion must stay in project directories and use -- before paths. */
export function verifyTrackedFileDeletionPermissions(allowed, source) {
  for (const directory of ['backend', 'frontend', 'docs', 'scripts']) {
    requireText(allowed, `Bash(git rm -- ${directory}/*)`, source);
  }
  for (const forbidden of ['Bash(git rm *)', 'Bash(git rm -- *)', 'Bash(git rm -r *)', 'Bash(git rm -f *)', 'Bash(git rm -- .github/*)']) {
    forbidText(allowed, forbidden, source);
  }
}

/** Accidental staging cleanup must stay scoped to normal project directories. */
export function verifyScopedStagingCleanupPermissions(allowed, source) {
  for (const directory of ['backend', 'frontend', 'docs', 'scripts']) {
    requireText(allowed, `Bash(git restore --staged -- ${directory}/*)`, source);
  }
  for (const forbidden of [
    'Bash(git restore --staged *)',
    'Bash(git restore --staged -- *)',
    'Bash(git restore --staged -- .github/*)',
    'Bash(git reset *)',
  ]) {
    forbidText(allowed, forbidden, source);
  }
}

export function verifyScopedDirectoryCreationPermissions(allowed, source) {
  for (const directory of ['backend', 'frontend', 'docs', 'scripts']) {
    requireText(allowed, `Bash(mkdir -p ${directory}/*)`, source);
  }
  for (const forbidden of ['Bash(mkdir *)', 'Bash(mkdir -p *)', 'Bash(mkdir -p .github/*)']) {
    forbidText(allowed, forbidden, source);
  }
}

// validate.sh runs for several minutes. With Claude Code's short default Bash timeout the agent
// backgrounds or redirects it, the literal-command allowlist refuses that, and the run ends with
// uncommitted work. The default must cover a full run because the agent invokes the bare command.
export function verifyAgentShellTimeout(stepText, source) {
  requireText(stepText, 'BASH_DEFAULT_TIMEOUT_MS: "1800000"', source);
  requireText(stepText, 'BASH_MAX_TIMEOUT_MS: "1800000"', source);
}

export const AGENT_APP_TOKEN_ACTION =
  'actions/create-github-app-token@bcd2ba49218906704ab6c1aa796996da409d3eb1 # v3.2.0';

function verifyAgentAppTokenStep(text, source, stepId, { pullRequests = false } = {}) {
  for (const required of [
    AGENT_APP_TOKEN_ACTION,
    `id: ${stepId}`,
    'client-id: ${{ vars.AGENT_AUTOMATION_APP_CLIENT_ID }}',
    'private-key: ${{ secrets.AGENT_AUTOMATION_APP_PRIVATE_KEY }}',
    'permission-contents: write',
  ]) {
    requireText(text, required, source);
  }
  if (pullRequests) {
    requireText(text, 'permission-pull-requests: write', source);
  } else {
    forbidText(text, 'permission-pull-requests:', source);
  }
  for (const forbidden of [
    'permission-issues:',
    'permission-actions:',
    'permission-administration:',
    'permission-secrets:',
    'permission-deployments:',
    'personal access token',
    'PAT_TOKEN',
    'GH_PAT',
  ]) {
    forbidText(text, forbidden, source);
  }
}

/** Verifies implementation publication, trusted architecture handoff and final-head validation dispatch. */
export function verifyArchitecturePass(implementationWorkflow, architectureWorkflow) {
  verifyImplementationJob(implementationWorkflow);
  verifyArchitectureDispatcher(implementationWorkflow);
  verifyArchitectureContext(architectureWorkflow);
  verifyArchitectureCopilotCheck(architectureWorkflow);
  verifyArchitectureClaudeCheck(architectureWorkflow);
  verifySonarIssuesJob(architectureWorkflow, 'agent-architecture.yml', '  architecture:\n');
  verifyArchitectureFixJob(architectureWorkflow);
  verifyArchitectureFinalizer(architectureWorkflow);
}

function verifyImplementationJob(implementationWorkflow) {
  const job = section(implementationWorkflow, '  implement:\n', '  dispatch-architecture:\n', 'agent-implement.yml implementation job');

  requireText(job, '      contents: read', 'agent-implement.yml implementation permissions');
  requireText(job, 'persist-credentials: false', 'agent-implement.yml implementation checkout');

  const implementationPrompt = extractPromptText(job, 'agent-implement.yml implementation prompt');
  for (const required of [
    'Do not delegate, spawn, or use Claude sub-agents, and do not invoke the `Agent` tool.',
    'use the Write tool to create `.agent-run-status` containing exactly `blocked` on one line',
    'Do not push any branch and do not create or edit a pull request',
    '`.agent-pr-title`',
    '`.agent-pr-body.md`',
    'short-lived GitHub App token that is never exposed to you',
  ]) requireText(implementationPrompt, required, 'agent-implement.yml implementation prompt');

  requireOrder(job, '      - name: Capture trusted base and resume checkpoint', '      - name: Run Claude Code implementation agent', 'trusted base capture');
  requireOrder(job, '      - name: Run Claude Code implementation agent', '      - name: Persist committed implementation', 'durability boundary');
  requireOrder(job, '      - name: Persist committed implementation', '      - name: Verify implementation result', 'durability before postconditions');
  requireOrder(job, '      - name: Verify implementation result', '      - name: Create GitHub App token for implementation publish', 'agent-implement.yml publish order');
  requireOrder(job, '      - name: Create GitHub App token for implementation publish', '      - name: Push implementation and create pull request', 'agent-implement.yml publish order');
  requireOrder(job, '      - name: Push implementation and create pull request', '      - name: Record implementation outcome', 'agent-implement.yml handoff order');

  const implementationAgent = section(job, '      - name: Run Claude Code implementation agent\n', '      # Storage only:', 'agent-implement.yml implementation agent');
  requireText(implementationAgent, 'github_token: ${{ secrets.GITHUB_TOKEN }}', 'agent-implement.yml implementation agent');
  requireText(implementationAgent, '--disallowedTools "Agent,WebFetch,WebSearch"', 'agent-implement.yml implementation agent');
  requireText(implementationAgent, 'Bash(git push *)', 'agent-implement.yml implementation agent');
  requireText(implementationAgent, 'Bash(gh pr create *)', 'agent-implement.yml implementation agent');
  verifyAgentShellTimeout(implementationAgent, 'agent-implement.yml implementation agent');
  const implementationAllowed = extractAllowedTools(implementationAgent, 'agent-implement.yml implementation allowed tools');
  verifyTrackedFileDeletionPermissions(implementationAllowed, 'agent-implement.yml implementation allowed tools');
  verifyScopedStagingCleanupPermissions(implementationAllowed, 'agent-implement.yml implementation allowed tools');
  verifyScopedDirectoryCreationPermissions(implementationAllowed, 'agent-implement.yml implementation allowed tools');
  for (const forbidden of ['AGENT_AUTOMATION_APP_PRIVATE_KEY', 'create-github-app-token', 'steps.implementation_app_token.outputs.token', 'git push -u origin']) {
    forbidText(implementationAgent, forbidden, 'agent-implement.yml implementation agent');
  }

  verifyAgentAppTokenStep(section(job, '      - name: Create GitHub App token for persistence\n', '      - name: Persist committed implementation\n', 'persistence App token'), 'persistence App token', 'persistence_app_token');
  const persistence = section(job, '      - name: Persist committed implementation\n', '      - name: Verify implementation result\n', 'trusted persistence');
  for (const required of [
    'if: always()', 'BASE_SHA: ${{ steps.starting_point.outputs.base_sha }}',
    'EXPECTED_HEAD: ${{ steps.persistence_candidate.outputs.head_sha }}',
    'GH_TOKEN: ${{ steps.persistence_app_token.outputs.token }}',
    'git merge-base --is-ancestor', '--diff-merges=separate',
    'Existing persistence branch differs; refusing overwrite', 'Remote persistence verification failed',
    'agent/recovery-', `bash ${appPushRetryPath} "$publish_remote" "$EXPECTED_HEAD:refs/heads/$saved_branch"`,
  ]) requireText(persistence, required, 'trusted persistence');
  requireText(implementationWorkflow, 'cancel-in-progress: false', 'do not cancel work before persistence');

  const implementationResult = section(job, '      - name: Verify implementation result\n', '      - name: Create GitHub App token for implementation publish\n', 'agent-implement.yml implementation result');
  for (const required of [
    'id: implementation_result', '.agent-run-status', '[ -z "$(git ls-files -- .agent-run-status)" ]',
    '[ "$(cat .agent-run-status)" = "blocked" ]', 'echo "blocked=true"', '.agent-pr-title', '.agent-pr-body.md',
    'PR metadata scratch files must remain untracked', `node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body .agent-pr-body.md`,
    'git diff --quiet', 'git diff --cached --quiet', 'echo "ready=true"', 'echo "branch=$branch"', 'echo "head_sha=$head_sha"',
  ]) requireText(implementationResult, required, 'agent-implement.yml implementation result');

  const implementationToken = section(job, '      - name: Create GitHub App token for implementation publish\n', '      - name: Push implementation and create pull request\n', 'agent-implement.yml implementation App token');
  verifyAgentAppTokenStep(implementationToken, 'agent-implement.yml implementation App token', 'implementation_app_token', { pullRequests: true });
  const implementationDiagnostic = section(job, '      - name: Diagnose GitHub App publish permissions\n', '      - name: Push implementation and create pull request\n', 'agent-implement.yml implementation publish diagnostic');
  requireText(implementationDiagnostic, 'GH_TOKEN: ${{ steps.implementation_app_token.outputs.token }}', 'agent-implement.yml implementation publish diagnostic');
  const implementationPublish = section(job, '      - name: Push implementation and create pull request\n', '      - name: Record implementation outcome\n', 'agent-implement.yml implementation publish');
  for (const required of [
    'GH_TOKEN: ${{ steps.implementation_app_token.outputs.token }}',
    'EXPECTED_AGENT_AUTHOR: ${{ vars.AGENT_AUTOMATION_APP_BOT_LOGIN }}',
    `bash ${appPushRetryPath} "https://github.com/\${GITHUB_REPOSITORY}.git" "HEAD:refs/heads/$BRANCH"`,
    'gh pr create', '--body-file .agent-pr-body.md', '.user.login == $author', '.head.sha == $sha',
    'echo "pr_number=$pr_number"', 'echo "head_sha=$HEAD_SHA"',
  ]) requireText(implementationPublish, required, 'agent-implement.yml implementation publish');

  for (const forbidden of ['Run Claude Code architecture agent', 'architecture_app_token', 'Push architecture head']) {
    forbidText(job, forbidden, 'agent-implement.yml must not contain architecture execution');
  }

  const outcome = section(job, '      - name: Record implementation outcome\n', null, 'agent-implement.yml outcome');
  for (const required of [
    'PERSIST_OUTCOME: ${{ steps.persist_implementation.outcome }}',
    'PUBLISH_OUTCOME: ${{ steps.publish_implementation.outcome }}',
    'IMPLEMENTATION_READY: ${{ steps.implementation_result.outputs.ready }}',
    'AGENT_BLOCKED: ${{ steps.implementation_result.outputs.blocked }}',
    '[ "$AGENT_BLOCKED" = "true" ]', '[ "$PUBLISH_OUTCOME" = "success" ]',
    'echo "dispatch_architecture=true"', 'git branch --show-current', 'git rev-parse HEAD',
    'git diff --quiet', 'git diff --cached --quiet',
  ]) requireText(outcome, required, 'agent-implement.yml outcome');

}

function verifyArchitectureDispatcher(implementationWorkflow) {
  const implementationDispatcher = section(implementationWorkflow, '  dispatch-architecture:\n', null, 'agent-implement.yml architecture dispatcher');
  verifySafeDispatcher(implementationDispatcher, 'agent-implement.yml architecture dispatcher');
  verifyAgentPrGuards(implementationDispatcher, 'agent-implement.yml architecture dispatcher', 'Refusing stale architecture dispatch');
  for (const required of [
    'issues: write',
    'agent-architecture.yml', '-f issue_number="$ISSUE_NUMBER"', '-f pr_number="$PR_NUMBER"', '-f head_sha="$HEAD_SHA"',
    '[[ "$head_ref" == agent/issue-"$ISSUE_NUMBER"-* ]]',
    'RUN_URL: ${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}',
    'trap block_undispatched EXIT', 'dispatched=true',
    '.state == "OPEN" and any(.labels[]?; .name == "agent-working")',
    '--remove-label agent-working --add-label agent-blocked',
    'could not be dispatched. The task is now agent-blocked.',
  ]) requireText(implementationDispatcher, required, 'agent-implement.yml architecture dispatcher');
  for (const forbidden of ['validate.yml', 'dispatch_review=true', 'gh pr edit', 'agent-review']) {
    forbidText(implementationDispatcher, forbidden, 'agent-implement.yml architecture dispatcher');
  }

}

function verifyArchitectureContext(architectureWorkflow) {
  const triggers = section(architectureWorkflow, 'on:\n', 'permissions:\n', 'agent-architecture.yml triggers');
  for (const required of ['workflow_dispatch:', 'issue_number:', 'pr_number:', 'head_sha:']) requireText(triggers, required, 'agent-architecture.yml triggers');
  for (const forbidden of ['pull_request:', 'issues:', 'issue_comment:']) forbidText(triggers, forbidden, 'agent-architecture.yml triggers');
  requireText(architectureWorkflow, 'permissions: {}', 'agent-architecture.yml top-level permissions');
  requireText(architectureWorkflow, 'group: agent-architecture-pr-${{ inputs.pr_number }}', 'agent-architecture.yml concurrency');
  requireText(architectureWorkflow, 'cancel-in-progress: false', 'agent-architecture.yml concurrency');

  const context = section(architectureWorkflow, '  context:\n', '  copilot-check:\n', 'agent-architecture.yml context');
  requireText(context, 'pull-requests: read', 'agent-architecture.yml context');
  requireText(context, 'issues: read', 'agent-architecture.yml context');
  requireText(context, 'EXPECTED_AGENT_AUTHOR: ${{ vars.AGENT_AUTOMATION_APP_BOT_LOGIN }}', 'agent-architecture.yml context');
  forbidText(context, 'actions/checkout', 'agent-architecture.yml context');
  forbidText(context, 'CLAUDE_CODE_OAUTH_TOKEN', 'agent-architecture.yml context');
  verifyAgentPrGuards(context, 'agent-architecture.yml context', 'Refusing stale architecture run');
  requireText(context, '[[ "$head_ref" == agent/issue-"$ISSUE_NUMBER"-* ]]', 'agent-architecture.yml context');
  requireText(context, 'any(.labels[]?; .name == "agent-working")', 'agent-architecture.yml context');
  // The verified route names exactly one checker; any other mode fails closed.
  for (const required of [
    'cross-claude) checker=copilot;', 'full-claude) checker=claude;', '*) fail "Provider mode',
    'checker: ${{ steps.context.outputs.checker }}', 'echo "checker=$checker"', 'echo "agent_mode=$agent_mode"',
  ]) requireText(context, required, 'agent-architecture.yml context');

}

/**
 * A read-only Copilot CLI architecture check: no write permission, no persisted checkout credentials,
 * no App or Anthropic credential, a pinned CLI, and it may not change the tree.
 */
function verifyReadOnlyCopilotCheck(copilotCheck, source) {
  for (const required of [
    '    needs: context', '      contents: read', 'ref: ${{ needs.context.outputs.head_sha }}', 'persist-credentials: false',
    'COPILOT_GITHUB_TOKEN: ${{ secrets.COPILOT_CLI_TOKEN }}', "--deny-tool='write'", '--no-ask-user',
    '[ -z "$(git status --porcelain)" ]', '[ "$(git rev-parse HEAD)" = "$HEAD_SHA" ]',
    'ARCHITECTURE: CLEAN', 'ARCHITECTURE: FINDINGS', 'echo "verdict=$verdict"',
    // The CLI is pinned by the trusted workflow commit's lockfile and installed without lifecycle scripts.
    'TRUSTED_SHA: ${{ github.sha }}', 'git show "$TRUSTED_SHA:.github/copilot-cli/$file"', '--ignore-scripts',
    '[ "$GITHUB_REF" = "refs/heads/main" ] ||', 'COPILOT_AUTO_UPDATE: "false"',
  ]) requireText(copilotCheck, required, source);
  for (const forbidden of [
    'contents: write', 'pull-requests: write', 'issues: write', 'actions: write', 'copilot-requests: write',
    'CLAUDE_CODE_OAUTH_TOKEN', 'claude-code-action', 'AGENT_AUTOMATION_APP_PRIVATE_KEY', 'create-github-app-token', 'COPILOT_AGENT_TOKEN',
    'secrets.GITHUB_TOKEN', 'git push', 'gh pr', 'gh issue', 'npm install', '@github/copilot@', '--allow-all', "--allow-tool='write'", "--allow-tool='shell'",
  ]) forbidText(copilotCheck, forbidden, source);
}

/**
 * A read-only Claude architecture check: no write permission, no editing or publishing tools, no
 * Copilot or App credential, and its result is schema-validated structured output.
 */
function verifyReadOnlyClaudeCheck(claudeCheck, source) {
  const checkPermissions = section(claudeCheck, '    permissions:\n', '\n    outputs:\n', `${source} permissions`);
  forbidText(checkPermissions, 'write', `${source} permissions`);
  for (const required of [
    'ref: ${{ needs.context.outputs.head_sha }}', 'persist-credentials: false', 'id: architecture_check',
    'steps.architecture_check.outputs.structured_output', "--json-schema '",
    '"enum":["CLEAN","FINDINGS"]', '"checked_head_sha":{"type":"string","pattern":"^[0-9a-f]{40}$"}',
    '--disallowedTools "Edit,MultiEdit,Write,NotebookEdit,Agent,WebFetch,WebSearch"', 'Bash(git push *)', 'Bash(gh pr comment *)',
  ]) requireText(claudeCheck, required, source);
  const checkAllowed = extractAllowedTools(claudeCheck, `${source} allowed tools`);
  for (const forbidden of ['Edit', 'Write', 'git add', 'git commit', 'git push', 'gh pr comment', 'gh pr edit', 'gh pr review', 'gh issue edit', 'gh api', 'gh workflow', 'validate.sh']) {
    forbidText(checkAllowed, forbidden, `${source} allowed tools`);
  }
  for (const forbidden of ['COPILOT_', 'copilot -s', 'AGENT_AUTOMATION_APP_PRIVATE_KEY', 'create-github-app-token']) {
    forbidText(claudeCheck, forbidden, source);
  }
}

function verifyArchitectureCopilotCheck(architectureWorkflow) {
  // Default route: Copilot reviews Claude's architecture read-only, and only on cross-claude.
  const source = 'agent-architecture.yml Copilot check';
  const copilotCheck = section(architectureWorkflow, '  copilot-check:\n', '  claude-check:\n', source);
  requireText(copilotCheck, "    if: needs.context.outputs.checker == 'copilot'\n", source);
  verifyReadOnlyCopilotCheck(copilotCheck, source);
}

function verifyArchitectureClaudeCheck(architectureWorkflow) {
  // Full-claude route: a separate read-only Claude invocation, labelled same-provider, and only on full-claude.
  const source = 'agent-architecture.yml same-provider Claude check';
  const claudeCheck = section(architectureWorkflow, '  claude-check:\n', '  sonar:\n', source);
  requireText(claudeCheck, "    if: needs.context.outputs.checker == 'claude'\n", source);
  verifyReadOnlyClaudeCheck(claudeCheck, source);
  for (const required of [
    'This is a same-provider check, not an independent one', 'claude_code_oauth_token: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}',
    '[ -z "$(git status --porcelain)" ]', '[ "$(git rev-parse HEAD)" = "$HEAD_SHA" ]', '[ "$checked_sha" = "$HEAD_SHA" ]',
    'verdict: ${{ steps.check.outputs.verdict }}', 'findings: ${{ steps.check.outputs.findings }}',
  ]) requireText(claudeCheck, required, source);
}

// SonarCloud issues are read by a deterministic, read-only job that runs only the trusted script from
// the workflow's own commit; it holds no write permission and no agent or App credential.
function verifySonarIssuesJob(workflow, source, nextJob) {
  const sonar = section(workflow, '  sonar:\n', nextJob, `${source} SonarCloud job`);
  for (const required of [
    '    needs: context', '      contents: read', '      checks: read', 'ref: ${{ github.sha }}', 'persist-credentials: false',
    'run: node scripts/sonar-new-issues.mjs', 'HEAD_SHA: ${{ needs.context.outputs.head_sha }}', 'PR_NUMBER: ${{ needs.context.outputs.pr_number }}',
    'count: ${{ steps.sonar.outputs.count }}', 'issues: ${{ steps.sonar.outputs.issues }}',
  ]) requireText(sonar, required, `${source} SonarCloud job`);
  for (const forbidden of [
    'write', 'claude-code-action', 'CLAUDE_CODE_OAUTH_TOKEN', 'COPILOT_', 'AGENT_AUTOMATION_APP_PRIVATE_KEY', 'create-github-app-token',
    'ref: ${{ needs.context.outputs.head_sha }}', 'gh pr', 'gh issue', 'git push',
  ]) forbidText(sonar, forbidden, `${source} SonarCloud job`);
}

function verifyArchitectureFixJob(architectureWorkflow) {
  const architectureJob = section(architectureWorkflow, '  architecture:\n', '  finalize:\n', 'agent-architecture.yml architecture job');
  requireText(architectureJob, '      - context\n      - copilot-check\n      - claude-check\n      - sonar\n', 'agent-architecture.yml architecture job');
  requireText(
    architectureJob,
    "if: ${{ !cancelled() && needs.context.result == 'success' && ((needs.context.outputs.checker == 'copilot' && needs.copilot-check.result == 'success') || (needs.context.outputs.checker == 'claude' && needs.claude-check.result == 'success')) && ((needs.context.outputs.checker == 'copilot' && needs.copilot-check.outputs.verdict == 'findings') || (needs.context.outputs.checker == 'claude' && needs.claude-check.outputs.verdict == 'findings') || fromJSON(needs.sonar.outputs.count || '0') > 0) }}",
    'agent-architecture.yml architecture job',
  );
  requireText(architectureJob, "CHECK_FINDINGS: ${{ needs.context.outputs.checker == 'claude' && needs.claude-check.outputs.findings || needs.copilot-check.outputs.findings }}", 'agent-architecture.yml architecture job');
  requireText(architectureJob, '> .git/architecture-findings.md', 'agent-architecture.yml architecture job');
  requireText(architectureJob, 'SONAR_ISSUES: ${{ needs.sonar.outputs.issues }}', 'agent-architecture.yml architecture job');
  requireText(architectureJob, '> .git/sonar-new-issues.md', 'agent-architecture.yml architecture job');
  requireText(architectureJob, 'Read that file first', 'agent-architecture.yml architecture job');
  requireText(architectureJob, 'Never silence an issue instead of fixing it', 'agent-architecture.yml architecture job');
  for (const external of ['needs.copilot-check.outputs.findings', 'needs.claude-check.outputs.findings', 'needs.sonar.outputs.issues']) {
    forbidText(architectureJob.slice(architectureJob.indexOf('prompt: |')), external, 'agent-architecture.yml architecture prompt');
  }
  requireText(architectureJob, '      contents: read', 'agent-architecture.yml architecture permissions');
  requireText(architectureJob, '      pull-requests: write', 'agent-architecture.yml architecture permissions');
  requireText(architectureJob, '      issues: read', 'agent-architecture.yml architecture permissions');
  forbidText(architectureJob, 'actions: write', 'agent-architecture.yml architecture permissions');
  requireText(architectureJob, 'ref: ${{ needs.context.outputs.head_sha }}', 'agent-architecture.yml exact checkout');
  requireText(architectureJob, 'persist-credentials: false', 'agent-architecture.yml exact checkout');
  requireText(architectureJob, 'git checkout -b "$BRANCH" "$HEAD_SHA"', 'agent-architecture.yml local feature branch');

  const architect = section(architectureJob, '      - name: Run Claude Code architecture agent\n', '      - name: Verify architecture result\n', 'agent-architecture.yml architect');
  for (const required of [
    'id: architect', 'github_token: ${{ secrets.GITHUB_TOKEN }}',
    'Do not delegate, spawn, or use Claude sub-agents, and do not invoke the `Agent` tool.',
    'Preserve all observable behavior', 'bash scripts/validate.sh', 'gh pr comment',
    'Do not push; a deterministic workflow step will mint a fresh GitHub App token',
    '--disallowedTools "Agent,WebFetch,WebSearch"', 'Bash(git push *)',
  ]) requireText(architect, required, 'agent-architecture.yml architect');
  for (const forbidden of ['AGENT_AUTOMATION_APP_PRIVATE_KEY', 'steps.architecture_app_token.outputs.token']) forbidText(architect, forbidden, 'agent-architecture.yml architect');
  const architectureAllowed = extractAllowedTools(architect, 'agent-architecture.yml architect allowed tools');
  verifyTrackedFileDeletionPermissions(architectureAllowed, 'agent-architecture.yml architect allowed tools');
  verifyScopedStagingCleanupPermissions(architectureAllowed, 'agent-architecture.yml architect allowed tools');
  verifyScopedDirectoryCreationPermissions(architectureAllowed, 'agent-architecture.yml architect allowed tools');
  verifyAgentShellTimeout(architect, 'agent-architecture.yml architect');
  for (const forbidden of ['gh pr edit', 'gh pr create', 'gh issue edit', 'gh workflow', 'gh api']) forbidText(architectureAllowed, forbidden, 'agent-architecture.yml architect allowed tools');

  const architectureResult = section(architectureJob, '      - name: Verify architecture result\n', '      - name: Create GitHub App token for architecture publish\n', 'agent-architecture.yml architecture result');
  for (const required of ['id: architecture_result', 'START_SHA: ${{ needs.context.outputs.head_sha }}', 'git diff --quiet', 'git diff --cached --quiet', 'echo "head_sha=$head_sha"', 'echo "push_required=false"', 'echo "push_required=true"']) requireText(architectureResult, required, 'agent-architecture.yml architecture result');
  const architectureToken = section(architectureJob, '      - name: Create GitHub App token for architecture publish\n', '      - name: Push architecture head\n', 'agent-architecture.yml architecture App token');
  verifyAgentAppTokenStep(architectureToken, 'agent-architecture.yml architecture App token', 'architecture_app_token');
  const architecturePublish = section(architectureJob, '      - name: Push architecture head\n', null, 'agent-architecture.yml architecture publish');
  for (const required of ['GH_TOKEN: ${{ steps.architecture_app_token.outputs.token }}', `bash ${appPushRetryPath} "https://github.com/\${GITHUB_REPOSITORY}.git" "HEAD:refs/heads/$BRANCH"`, 'HEAD_SHA: ${{ steps.architecture_result.outputs.head_sha }}']) requireText(architecturePublish, required, 'agent-architecture.yml architecture publish');

}

function verifyArchitectureFinalizer(architectureWorkflow) {
  const finalize = section(architectureWorkflow, '  finalize:\n', null, 'agent-architecture.yml finalize');
  for (const required of [
    "    if: always()\n", '      - context\n      - copilot-check\n      - claude-check\n      - sonar\n      - architecture\n',
    'SONAR_COUNT: ${{ needs.sonar.outputs.count }}', '[[ "$sonar_count" =~ ^[0-9]+$ ]] || sonar_count=0', 'actions: write', 'pull-requests: write', 'issues: write', 'statuses: read',
    'ISSUE_NUMBER: ${{ inputs.issue_number }}', 'CONTEXT_JOB_RESULT: ${{ needs.context.result }}',
    '[ "$CONTEXT_JOB_RESULT" = "success" ]', 'trap block_undispatched EXIT', 'dispatched=true',
    '[ "$labelled_by_this_run" = "true" ]', 'labelled_by_this_run=true',
    'select(.context == "agent-validation")',
    'EXPECTED_AGENT_AUTHOR: ${{ vars.AGENT_AUTOMATION_APP_BOT_LOGIN }}',
    'ARCHITECTURE_JOB_RESULT: ${{ needs.architecture.result }}', '[ "$ARCHITECTURE_JOB_RESULT" = "success" ]',
    "CHECK_JOB_RESULT: ${{ needs.context.outputs.checker == 'claude' && needs.claude-check.result || needs.copilot-check.result }}", '[ "$CHECK_JOB_RESULT" = "success" ]',
    "CHECK_VERDICT: ${{ needs.context.outputs.checker == 'claude' && needs.claude-check.outputs.verdict || needs.copilot-check.outputs.verdict }}", '[ "$ARCHITECTURE_JOB_RESULT" = "skipped" ]',
    '[ "$checker_label" != "Unknown" ] || fail',
    'FINAL_SHA="$EXPECTED_START_SHA"', 'FIX_SHA: ${{ needs.architecture.outputs.head_sha }}', 'FINAL_SHA="$FIX_SHA"',
    '[ "$current_sha" = "$FINAL_SHA" ]',
    '[[ "$head_ref" == agent/issue-"$ISSUE_NUMBER"-* ]]', '.user.login // empty',
    'any(.labels[]?; .name == "agent-working")', "grep -Eq '^\\.github/workflows/'",
    'gh pr edit "$PR_NUMBER" --repo "$GITHUB_REPOSITORY" --add-label agent-review',
    'gh issue edit "$ISSUE_NUMBER" --repo "$GITHUB_REPOSITORY" --remove-label agent-working --add-label agent-review',
    'gh workflow run validate.yml', '--ref main', '-f head_sha="$FINAL_SHA"', '-f dispatch_review=true',
    '--remove-label agent-review --add-label agent-blocked',
  ]) requireText(finalize, required, 'agent-architecture.yml finalize');
  // finalize must also run when the target check fails, or a rejected handoff strands the issue.
  forbidText(finalize, "needs.context.result == 'success'", 'agent-architecture.yml finalize');
  requireOrder(finalize, 'labelled_by_this_run=true', 'gh pr edit "$PR_NUMBER" --repo "$GITHUB_REPOSITORY" --add-label agent-review', 'agent-architecture.yml finalize');
  requireOrder(finalize, 'gh workflow run validate.yml', 'dispatched=true', 'agent-architecture.yml finalize');
  for (const forbidden of ['actions/checkout', 'CLAUDE_CODE_OAUTH_TOKEN', 'AGENT_AUTOMATION_APP_PRIVATE_KEY', 'contents: write', 'claude-code-action']) forbidText(finalize, forbidden, 'agent-architecture.yml finalize');
}

/** Runs every agent workflow contract check with an overridable repository reader. */
export function runContractChecks({ read = readRepositoryFile } = {}) {
  const appPushRetry = read(appPushRetryPath);
  for (const required of [
    'set -euo pipefail',
    '[ -n "${GH_TOKEN:-}" ]',
    'delays=(0 2 5 10)',
    'sleep "$delay"',
    "grep -Eqi '403|Permission to .* denied'",
    'credential.helper=',
    'core.hooksPath=/dev/null',
    'http.extraheader="AUTHORIZATION: basic $auth"',
    'push "$remote" "$refspec"',
  ]) {
    requireText(appPushRetry, required, appPushRetryPath);
  }
  for (const forbidden of ['--force', 'git config --global', 'x-access-token:${GH_TOKEN}@']) {
    forbidText(appPushRetry, forbidden, appPushRetryPath);
  }

  const validate = read(validatePath);
  for (const required of [
    'workflow_dispatch:',
    'pr_number:',
    'head_sha:',
    'dispatch_review:',
    'inputs.pr_number',
    'agent-validation',
    'Refusing stale validation',
    'persist-credentials: false',
    VALIDATION_CONCURRENCY_GROUP,
    'cancel-in-progress: true',
  ]) {
    requireText(validate, required, validatePath);
  }

  // The two validation modes must never share a status context. Every status publication
  // goes through the single STATUS_CONTEXT expression, never a hard-coded context.
  for (const forbidden of ['context=agent-validation', 'context=merge-validation']) {
    forbidText(validate, forbidden, validatePath);
  }

  const validationContext = section(validate, '  context:\n', '  validate:\n', validatePath);
  requireText(validationContext, 'statuses: write', 'validate.yml context job');
  requireText(validationContext, 'statuses/$head_sha', 'validate.yml context job');
  requireText(validationContext, 'target_url="$RUN_URL"', 'validate.yml context job');
  requireText(validationContext, STATUS_CONTEXT_EXPRESSION, 'validate.yml context job');
  requireText(validationContext, '-f context="$STATUS_CONTEXT"', 'validate.yml context job');
  requireText(validationContext, 'status_context: ${{ steps.context.outputs.status_context }}', 'validate.yml context job');
  requireText(validationContext, 'echo "status_context=$STATUS_CONTEXT"', 'validate.yml context job');
  forbidText(validationContext, 'actions/checkout', 'validate.yml context job');
  forbidText(validationContext, 'CLAUDE_CODE_OAUTH_TOKEN', 'validate.yml context job');
  verifyAgentPrGuards(normalizeGuardVariables(validationContext), 'validate.yml dispatched context', 'Refusing stale validation');

  // The branch anchors carry a leading newline so the inner, deeper-indented if/else/fi
  // inside the pull_request branch cannot be mistaken for the outer one.
  const dispatchBranch = section(
    validationContext,
    'if [ "$GITHUB_EVENT_NAME" = "workflow_dispatch" ]; then\n',
    '\n          else\n',
    'validate.yml dispatched context',
  );
  requireText(dispatchBranch, '[ "$STATUS_CONTEXT" = "agent-validation" ]', 'validate.yml dispatched context');
  const pullRequestBranch = section(validationContext, '\n          else\n', '\n          fi\n', 'validate.yml pull_request context');
  requireText(pullRequestBranch, '[ "$STATUS_CONTEXT" = "merge-validation" ]', 'validate.yml pull_request context');

  const validationJob = section(validate, '  validate:\n', '  report-status:\n', validatePath);
  requireText(validationJob, 'contents: read', 'validate.yml validate job');
  requireText(validationJob, 'persist-credentials: false', 'validate.yml validate job');
  requireText(validationJob, 'node scripts/validate-agent-workflows.mjs', 'validate.yml validate job');
  requireText(validationJob, 'node --test scripts/validate-agent-workflows.test.mjs', 'validate.yml validate job');
  requireText(validationJob, 'node --test scripts/copilot-review-contract.test.mjs', 'validate.yml validate job');
  for (const forbidden of ['actions: write', 'statuses: write', 'CLAUDE_CODE_OAUTH_TOKEN']) {
    forbidText(validationJob, forbidden, 'validate.yml validate job');
  }

  const statusJob = section(validate, '  report-status:\n', '  dispatch-review:\n', validatePath);
  requireText(statusJob, 'statuses: write', 'validate.yml status job');
  requireText(statusJob, 'statuses/$HEAD_SHA', 'validate.yml status job');
  requireText(statusJob, 'target_url="$RUN_URL"', 'validate.yml status job');
  requireText(statusJob, 'STATUS_CONTEXT: ${{ needs.context.outputs.status_context }}', 'validate.yml status job');
  requireText(statusJob, '-f context="$STATUS_CONTEXT"', 'validate.yml status job');
  requireText(statusJob, 'agent-validation|merge-validation) ;;', 'validate.yml status job');
  forbidText(statusJob, 'actions/checkout', 'validate.yml status job');
  forbidText(statusJob, 'CLAUDE_CODE_OAUTH_TOKEN', 'validate.yml status job');

  const reviewDispatcher = section(validate, '  dispatch-review:\n', null, validatePath);
  // Read-only apart from dispatching: the dispatcher changes no labels.
  verifySafeDispatcher(reviewDispatcher, 'validate.yml review dispatcher');
  verifyAgentPrGuards(reviewDispatcher, 'validate.yml review dispatcher', 'Refusing stale review dispatch');
  // agent-review.yml picks the reviewer from the verified route, so the dispatcher routes every
  // implementation there and holds no Copilot credential.
  for (const required of [
    'gh workflow run agent-review.yml',
    '      issues: read',
  ]) requireText(reviewDispatcher, required, 'validate.yml review dispatcher');
  for (const forbidden of ['COPILOT_AGENT_TOKEN', 'requested_reviewers', 'copilot-pull-request-reviewer', 'agent-architecture-fix', 'issues: write', 'gh pr edit', 'gh issue edit', 'gh pr merge', 'gh pr review']) {
    forbidText(reviewDispatcher, forbidden, 'validate.yml review dispatcher');
  }
  requireText(reviewDispatcher, "github.event_name == 'workflow_dispatch'", 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'inputs.dispatch_review == true', 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'needs.report-status.result', 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'agent-review', 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'any(.labels[]?; .name == "agent-review")', 'validate.yml review dispatcher');

  verifyValidationModeIsolation(validate, validatePath);

  {
    const path = repairPath;
    const workflow = read(path);
    const producer = section(workflow, '  repair:\n', '  dispatch-validation:\n', path);
    forbidText(producer, 'actions: write', `${path} repair job`);
    requireText(producer, 'case "$head_ref" in agent/issue-*) ;; *) fail', `${path} repair job`);
    requireText(producer, '      contents: read', `${path} repair permissions`);
    requireText(producer, 'persist-credentials: false', `${path} repair checkout`);

    const repairAgent = section(producer, '      - name: Run Claude Code repair agent\n', '      - name: Verify repair result\n', `${path} repair agent`);
    requireText(repairAgent, 'github_token: ${{ secrets.GITHUB_TOKEN }}', `${path} repair agent`);
    requireText(repairAgent, 'Do not push, rebase, or rewrite history', `${path} repair agent`);
    requireText(repairAgent, 'Bash(git push *)', `${path} repair agent`);
    verifyAgentShellTimeout(repairAgent, `${path} repair agent`);
    for (const forbidden of ['AGENT_AUTOMATION_APP_PRIVATE_KEY', 'steps.repair_app_token.outputs.token']) forbidText(repairAgent, forbidden, `${path} repair agent`);

    const repairResult = section(producer, '      - name: Verify repair result\n', '      - name: Create GitHub App token for repair publish\n', `${path} repair result`);
    for (const required of ['id: repair_result', 'git diff --quiet', 'git diff --cached --quiet', 'echo "head_sha=$head_sha"', 'echo "push_required=true"']) requireText(repairResult, required, `${path} repair result`);

    const repairToken = section(producer, '      - name: Create GitHub App token for repair publish\n', '      - name: Push repaired head\n', `${path} repair App token`);
    verifyAgentAppTokenStep(repairToken, `${path} repair App token`, 'repair_app_token');
    const repairPublish = section(producer, '      - name: Push repaired head\n', '      - name: Record outcome on the pull request\n', `${path} repair publish`);
    requireText(repairPublish, 'GH_TOKEN: ${{ steps.repair_app_token.outputs.token }}', `${path} repair publish`);
    requireText(repairPublish, 'git push "https://x-access-token:${GH_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"', `${path} repair publish`);

    const repairOutcome = section(producer, '      - name: Record outcome on the pull request\n', null, `${path} repair outcome`);
    for (const required of ['REPAIR_RESULT_OUTCOME: ${{ steps.repair_result.outcome }}', 'EXPECTED_SHA: ${{ steps.repair_result.outputs.head_sha }}', '[ "$REPAIR_RESULT_OUTCOME" = "success" ]', '[ "$end_sha" = "$EXPECTED_SHA" ]']) requireText(repairOutcome, required, `${path} repair outcome`);

    const dispatcher = section(workflow, '  dispatch-validation:\n', null, path);
    verifySafeDispatcher(dispatcher, `${path} validation dispatcher`);
    verifyAgentPrGuards(dispatcher, `${path} validation dispatcher`, 'Refusing stale validation dispatch');
    requireText(dispatcher, 'validate.yml', `${path} validation dispatcher`);
    requireText(dispatcher, '-f dispatch_review=true', `${path} validation dispatcher`);
    requireText(dispatcher, 'any(.labels[]?; .name == "agent-review")', `${path} validation dispatcher`);
  }
  const review = read(reviewPath);
  // Reviews run only as a workflow_dispatch from main. A pull_request run would use the pull
  // request's merge commit as github.sha, so the workflow and the pinned Copilot CLI it installs
  // could come from the pull request.
  const reviewTriggers = section(review, 'on:\n', 'permissions:\n', 'agent-review.yml triggers');
  requireText(reviewTriggers, 'workflow_dispatch:', 'agent-review.yml triggers');
  for (const forbidden of ['pull_request', 'issues:', 'issue_comment', 'workflow_run', 'push:', 'synchronize', 'ready_for_review']) {
    forbidText(reviewTriggers, forbidden, 'agent-review.yml triggers');
  }
  verifyReviewRequestDispatcher(read(reviewRequestPath));

  for (const required of [
    'workflow_dispatch:',
    'pr_number:',
    'head_sha:',
    'inputs.pr_number',
    'agent-validation',
    'Refusing stale review',
    'persist-credentials: false',
    '.github/workflows/',
    'group: agent-review-pr-${{ inputs.pr_number || github.run_id }}',
  ]) {
    requireText(review, required, reviewPath);
  }

  const reviewContext = section(review, '  context:\n', '  review:\n', reviewPath);
  verifyAgentPrGuards(normalizeGuardVariables(reviewContext), 'agent-review.yml dispatched context', 'Refusing stale review');
  requireText(reviewContext, 'echo "implementer=$implementer"', 'agent-review.yml context');
  requireText(reviewContext, 'statuses: read', 'agent-review.yml dispatched context');
  requireText(reviewContext, 'any(.labels[]?; .name == "agent-review")', 'agent-review.yml dispatched context');
  requireText(reviewContext, 'validation_state', 'agent-review.yml dispatched context');
  // A dispatched review accepts only the exact-SHA agent-validation status as evidence; the
  // merge-result status published by pull_request validation is never a substitute.
  requireText(reviewContext, 'select(.context == "agent-validation")', 'agent-review.yml dispatched context');
  requireText(reviewContext, '[ "$GITHUB_EVENT_NAME" = "workflow_dispatch" ] || fail', 'agent-review.yml context');
  requireText(reviewContext, '[ "$GITHUB_REF" = "refs/heads/main" ] || fail', 'agent-review.yml context');
  forbidText(reviewContext, 'github.event.pull_request', 'agent-review.yml context');
  forbidText(reviewContext, MERGE_VALIDATION_STATUS, 'agent-review.yml dispatched context');

  const reviewJob = section(review, '  review:\n', '  copilot-review:\n', reviewPath);
  for (const forbidden of ['contents: write', 'actions: write']) {
    forbidText(reviewJob, forbidden, 'agent-review.yml review job');
  }
  const reviewAllowedTools = section(
    reviewJob,
    '          claude_args: |\n',
    '            --disallowedTools',
    'agent-review.yml allowed tools',
  );
  for (const forbidden of ['gh pr merge', 'gh pr review * --approve', 'gh pr review * --request-changes']) {
    forbidText(reviewAllowedTools, forbidden, 'agent-review.yml allowed tools');
  }

  verifyArchitecturePass(read(implementPath), read(architecturePath));

  // Defence in depth: no agent workflow may resolve a pull request author through the
  // GraphQL actor login anywhere, including in a guard added after this contract was written.
  for (const path of [
    '.github/workflows/agent-implement.yml',
    architecturePath,
    '.github/workflows/agent-repair.yml',
    reviewPath,
    validatePath,
    headUpdatePath,
    reviewRequestPath,
  ]) {
    forbidText(read(path), '.author.login', path);
  }
  verifyCopilotImplementationRetired(read);

  verifyDocumentationImpactGate(read);
  verifyReviewPublicationAndScheduling(read);
  verifyImplementationModelSelection(read);
  verifyProviderModeProvenance(read);
  verifyNayaxDocumentationAccess(read);
}

/**
 * Copilot no longer implements: it only checks and reviews Claude's work. Its implementation
 * workflows stay removed, and no agent workflow may route a readiness label, branch or credential
 * to a Copilot implementation again.
 */
export function verifyCopilotImplementationRetired(read = readRepositoryFile) {
  for (const path of RETIRED_COPILOT_IMPLEMENTATION_PATHS) {
    if (fileExists(read, path)) throw new Error(`${path}: Copilot no longer implements; this workflow must not be restored.`);
  }
  for (const path of listWorkflowFiles()) {
    const text = read(path);
    for (const forbidden of ['agent-ready-copilot', 'agent-ready-full-copilot', 'COPILOT_AGENT_TOKEN', 'COPILOT_AGENT_BOT_LOGIN', 'copilot-swe-agent', 'agent-architecture-fix', 'cross-copilot', 'full-copilot']) {
      forbidText(text, forbidden, path);
    }
  }
}

export function verifyImplementationModelSelection(read = readRepositoryFile) {
  const selection = read('.github/workflows/agent-model-selection.yml');
  const triagePrompt = section(selection, '          prompt: |\n', '          claude_args:', 'implementation model triage prompt');
  requireText(triagePrompt, '${{ steps.prepare.outputs.task }}', 'implementation model triage prompt');
  requireText(triagePrompt, 'untrusted task data', 'implementation model triage prompt');
  forbidText(triagePrompt, '.git/model-triage.json', 'implementation model triage prompt');
  for (const required of ['workflow_call:', 'permissions: {}', 'contents: read', 'issues: read', 'ref: ${{ github.workflow_sha }}', 'persist-credentials: false', "if: steps.prepare.outputs.triage == 'true'", '--model haiku', '--max-turns 8', '"enum":["low","standard","high","clarification-required"]', '--settings \'{"availableModels":["haiku"]}\'', '--allowedTools "Read"', '--disallowedTools "Agent,Bash,Edit,MultiEdit,Write,WebFetch,WebSearch"', 'node scripts/select-implementation-model.mjs prepare', 'node scripts/select-implementation-model.mjs resolve', 'TRIAGE_OUTPUT: ${{ steps.triage.outputs.structured_output }}']) requireText(selection, required, 'implementation model selection');
  for (const forbidden of ['contents: write', 'issues: write', 'pull-requests: write', 'actions: write', 'COPILOT_AGENT_TOKEN', 'AGENT_AUTOMATION_APP_PRIVATE_KEY', 'gh issue edit', 'gh issue comment', 'git push', 'github.event.pull_request']) forbidText(selection, forbidden, 'implementation model selection');
  for (const [provider, path, job] of [['claude', implementPath, 'implement']]) {
    const workflow = read(path);
    // Full-provider labels start work only while the kill switch is on (#337, #340); turning it off
    // must also take them out of the triggers.
    if (FULL_PROVIDER_EXECUTION_ENABLED) requireText(workflow, `github.event.label.name == 'agent-ready-full-${provider}'`, `${path} (full-provider execution is enabled)`);
    else forbidText(workflow, 'agent-ready-full-', `${path} (full-provider execution is not enabled)`);
    const caller = section(workflow, '  model:\n', '  preflight:\n', `${path} model caller`);
    for (const required of ['needs: preflight', 'uses: ./.github/workflows/agent-model-selection.yml', `provider: ${provider}`, 'ready_label: ${{ github.event.label.name }}', 'contents: read', 'issues: read']) requireText(caller, required, `${path} model caller`);
    requireText(workflow, 'group: agent-implementation-issue-${{ github.event.issue.number }}', path);
    requireText(workflow, 'cancel-in-progress: false', path);
    const consumer = section(workflow, `  ${job}:\n`, '  dispatch-architecture:\n', path);
    for (const required of ['needs: [preflight, model]', "needs.model.result == 'success'", 'TASK_FINGERPRINT: ${{ needs.model.outputs.fingerprint }}', 'IMPLEMENTATION_MODEL: ${{ needs.model.outputs.model }}', '--remove-label "$READY_LABEL" --add-label agent-working']) requireText(consumer, required, `${path} model consumer`);
    for (const required of ['--model ${{ needs.model.outputs.model }}', '--settings \'{"availableModels":["${{ needs.model.outputs.model }}"]}\'', '--max-turns ${{ needs.model.outputs.max_turns }}', 'node scripts/select-implementation-model.mjs verify']) requireText(consumer, required, `${path} model consumer`);
  }
}

// ---------------------------------------------------------------------------------------
// Provider mode provenance (#339): the route of an agent pull request is derived from the
// issue's own label history (the newest claim, which swaps the readiness label for agent-working
// as github-actions[bot]), never from a comment or PR text. Every boundary that acts on an agent
// pull request re-derives it with the trusted resolver from its own workflow commit. Pure
// dispatchers that hold no contents permission (agent-review-request.yml, agent-head-update.yml)
// rely on the workflow they dispatch, which verifies the mode before doing anything.
// ---------------------------------------------------------------------------------------

export const MODE_RESOLVER_FETCH = 'gh api -H "Accept: application/vnd.github.raw" "repos/$GITHUB_REPOSITORY/contents/scripts/agent-mode.mjs?ref=$GITHUB_WORKFLOW_SHA" > "$RUNNER_TEMP/agent-mode.mjs"';

export const PROVIDER_MODE_GATES = [
  // [workflow, job, step name, exact verify command]
  [implementPath, 'dispatch-architecture', 'Verify pull request and dispatch trusted architecture workflow', 'agent_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$PR_NUMBER" "claude")" || fail'],
  ['.github/workflows/agent-architecture.yml', 'context', 'Verify exact agent pull request', 'agent_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$PR_NUMBER" "claude")" || fail'],
  ['.github/workflows/validate.yml', 'context', 'Resolve and verify pull request', 'agent_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$pr_number" "$implementer")" || fail'],
  ['.github/workflows/validate.yml', 'dispatch-review', 'Reverify current head and dispatch review', 'agent_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$PR_NUMBER" "$implementer")" || fail'],
  ['.github/workflows/agent-review.yml', 'context', 'Resolve and verify pull request', 'agent_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$pr_number" "$implementer")" || fail'],
  ['.github/workflows/agent-review.yml', 'publish', 'Guard and publish review', 'live_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$PR_NUMBER" "$live_implementer")"'],
  ['.github/workflows/agent-repair.yml', 'repair', 'Resolve and verify pull request head', 'agent_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$PR_NUMBER" "claude")" || fail'],
  ['.github/workflows/agent-repair.yml', 'dispatch-validation', 'Verify repaired head and dispatch trusted validation', 'agent_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr "$PR_NUMBER" "claude")" || fail'],
];

export const CLAIM_STEPS = [
  // [workflow, job, claim step]
  [implementPath, 'implement', 'Mark issue as agent-working'],
];

function listWorkflowFiles() {
  return readdirSync(new URL('../.github/workflows/', import.meta.url)).filter(name => /\.ya?ml$/.test(name)).map(name => `.github/workflows/${name}`);
}

function jobBlock(workflow, job, source) {
  const start = workflow.indexOf(`\n  ${job}:\n`);
  if (start < 0) throw new Error(`${source}: missing job ${job}.`);
  const rest = workflow.slice(start + 1);
  const next = rest.slice(1).search(/\n {2}[a-z][a-z0-9-]*:\n/);
  return next < 0 ? rest : rest.slice(0, next + 1);
}

function stepBlock(jobText, name, source) {
  const step = jobText.split(`      - name: ${name}\n`)[1]?.split('\n      - name: ')[0];
  if (!step) throw new Error(`${source}: missing step ${name}.`);
  return step;
}

export function verifyProviderModeProvenance(read = readRepositoryFile) {
  for (const [path, job, step, verify] of PROVIDER_MODE_GATES) {
    const source = `${path} ${job} provider mode`;
    const jobText = jobBlock(read(path), job, source);
    // Reading the resolver needs contents: read; reading the issue's label history needs issues access.
    requireText(jobText, 'contents: read', source);
    if (!/\n {6}issues: (read|write)\n/.test(jobText)) throw new Error(`${source}: missing required text: issues: read`);
    const text = stepBlock(jobText, step, source);
    requireText(text, MODE_RESOLVER_FETCH, source);
    requireText(text, verify, source);
    // The resolver is always fetched from the trusted workflow commit, never from a checkout or a PR ref.
    forbidText(text, 'node scripts/agent-mode.mjs verify-pr', source);
    forbidText(text, 'agent-mode.mjs?ref=$HEAD_SHA', source);
  }
  const review = read('.github/workflows/agent-review.yml');
  requireText(review, 'agent_mode: ${{ steps.context.outputs.agent_mode }}', 'agent-review.yml context outputs');
  requireText(review, 'AGENT_MODE: ${{ needs.context.outputs.agent_mode }}', 'agent-review.yml publish');
  requireText(review, '[ "$live_mode" = "$AGENT_MODE" ] || suppress', 'agent-review.yml publish');
  requireOrder(review, 'live_mode="$(node "$RUNNER_TEMP/agent-mode.mjs" verify-pr', 'gh api --method POST "repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER/reviews"', 'agent-review.yml publish', 'the provider mode must be rechecked before a review is posted.');

  // A claim is the only workflow change that adds agent-working, done in one edit with the
  // readiness label's removal, in the step that consumes readiness. Any other agent-working add
  // would look like a claim to the resolver.
  const claimSwap = '--remove-label "$READY_LABEL" --add-label agent-working';
  for (const [path, job, step] of CLAIM_STEPS) {
    const source = `${path} ${job} claim`;
    const text = stepBlock(jobBlock(read(path), job, source), step, source);
    requireText(text, claimSwap, source);
    if (text.split('--add-label agent-working').length !== 2) throw new Error(`${source}: must add agent-working exactly once, in the claim.`);
  }
  for (const path of listWorkflowFiles()) {
    const text = read(path);
    const allowed = CLAIM_STEPS.filter(([claimPath]) => claimPath === path).length;
    if (text.split('add-label agent-working').length - 1 !== allowed) throw new Error(`${path}: only the claim steps may add agent-working.`);
  }
  const implement = read(implementPath);
  requireText(implement, 'AGENT_MODE: ${{ needs.model.outputs.mode }}', `${implementPath} implement`);
  const selection = read('.github/workflows/agent-model-selection.yml');
  requireText(selection, 'mode: ${{ steps.resolve.outputs.mode }}', 'implementation model selection');
}

// ---------------------------------------------------------------------------------------
// Nayax documentation access (issue #192): the analysis and implementation agents may read
// Nayax's official documentation MCP server through exactly two read-only tools; model triage
// and the format-only Copilot review repair get no access at all.
// ---------------------------------------------------------------------------------------

export const modelSelectionPath = '.github/workflows/agent-model-selection.yml';
export const copilotInstructionsPath = '.github/copilot-instructions.md';
export const agentsPath = 'AGENTS.md';

export const NAYAX_MCP_CONTRACT = Object.freeze({
  // Shared by every covered prompt; it sits inside a double-quoted shell string in the Copilot
  // architecture check, so it must never contain a backtick, a dollar sign or a double quote.
  promptRule:
    'Nayax contract verification (AGENTS.md § Nayax contract verification): if this work touches code that calls the Nayax API or models a Nayax request or response, ' +
    'look up the relevant endpoint contract with the read-only Nayax documentation tools (search_nayax_developer_portal and query_docs_filesystem_nayax_developer_portal on the nayax MCP server) ' +
    'before defining, changing or accepting field names, types, nullability, identifiers, timestamps or endpoint semantics. Never invent a Nayax property that the documentation can confirm or refute. ' +
    'If the tools are unavailable or the contract cannot be found, state explicitly that authoritative Nayax verification could not be completed and treat the affected contract as unverified rather than guessing. ' +
    'Work that does not touch Nayax needs no lookup.',
  claudeMcpConfig: `            --mcp-config '{"mcpServers":{"nayax":{"type":"http","url":"https://devzone.nayax.com/mcp"}}}'\n`,
  claudeAllowedTools: '            "mcp__nayax__search_nayax_developer_portal,mcp__nayax__query_docs_filesystem_nayax_developer_portal"\n',
  claudeDeniedTool: '            "mcp__nayax__submit_feedback"\n',
  copilotMcpConfig:
    `--additional-mcp-config '{"mcpServers":{"nayax":{"type":"http","url":"https://devzone.nayax.com/mcp","tools":["search_nayax_developer_portal","query_docs_filesystem_nayax_developer_portal"]}}}'`,
  copilotToolRules:
    "--allow-tool='nayax(search_nayax_developer_portal)' --allow-tool='nayax(query_docs_filesystem_nayax_developer_portal)' --deny-tool='nayax(submit_feedback)'",
  agentsRequired: [
    '### Nayax contract verification',
    'https://devzone.nayax.com/mcp',
    'look up the relevant endpoint contract in that documentation **before** defining, changing or accepting request/response DTOs',
    'Never invent a Nayax response property when the authoritative contract can be retrieved.',
    'state that authoritative Nayax verification could not be completed',
    'Never silently fall back to guessed fields for a contract-sensitive change.',
    'Work that does not touch the Nayax integration needs no lookup',
    'Model triage and the format-only Copilot review JSON repair have no Nayax documentation access',
  ],
  copilotInstructionsRequired: [
    '`AGENTS.md` § Nayax contract verification',
    'Never invent a Nayax response property when the authoritative contract can be retrieved.',
    'state explicitly that authoritative Nayax verification could not be completed',
    'Never call the server\'s `submit_feedback` tool.',
  ],
  claudePaths: [implementPath, architecturePath, repairPath, reviewPath],
  workspaceMcpConfigPaths: ['.mcp.json', '.github/mcp.json'],
});

function countOccurrences(text, needle) {
  return text.split(needle).length - 1;
}

/** The claude_args block: from its header to the first line indented less than its arguments. */
function claudeArgsBlock(workflow, source) {
  const start = workflow.indexOf('          claude_args: |\n');
  if (start < 0) throw new Error(`${source}: missing section start: claude_args: |`);
  const lines = workflow.slice(start).split('\n');
  let end = 1;
  while (end < lines.length && (lines[end].startsWith('            ') || lines[end] === '')) {
    if (lines[end] === '' && !(lines[end + 1] ?? '').startsWith('            ')) break;
    end += 1;
  }
  return lines.slice(0, end).join('\n') + '\n';
}

function verifyNoNayaxAccess(text, source) {
  for (const forbidden of ['nayax', 'Nayax', 'mcp-config', 'mcp__']) forbidText(text, forbidden, source);
}

function verifyNayaxPromptRuleIsShellSafe(contract) {
  for (const unsafe of ['`', '$', '"']) {
    if (contract.promptRule.includes(unsafe)) throw new Error(`Nayax prompt rule must not contain ${unsafe}.`);
  }
}

/** Every Claude invocation in the workflow, as its prompt and its claude_args block. */
function claudeInvocations(workflow, source) {
  const marker = '          claude_args: |\n';
  const parts = workflow.split(marker);
  return parts.slice(1).map((rest, index) => {
    const before = parts[index];
    const promptStart = before.lastIndexOf('          prompt: |\n');
    if (promptStart < 0) throw new Error(`${source}: Claude invocation ${index + 1} has no prompt.`);
    return { prompt: before.slice(promptStart), args: claudeArgsBlock(marker + rest, source) };
  });
}

/** Each Claude invocation: one server, two read-only tools, feedback denied, prompt rule present. */
function verifyClaudeNayaxAccess(workflow, path, contract) {
  const source = `${path} Nayax documentation access`;
  const invocations = claudeInvocations(workflow, source);
  if (invocations.length === 0) throw new Error(`${source}: no Claude invocation found.`);
  if (countOccurrences(workflow, '--mcp-config') !== invocations.length) throw new Error(`${source}: expected exactly one --mcp-config per Claude invocation.`);
  for (const [index, { prompt, args }] of invocations.entries()) {
    const invocation = `${source} (invocation ${index + 1})`;
    requireText(args, contract.claudeMcpConfig, invocation);
    const allowed = extractAllowedTools(args, invocation);
    requireText(allowed, contract.claudeAllowedTools, invocation);
    for (const forbidden of ['submit_feedback', 'mcp__nayax"', 'mcp__nayax,', 'mcp__nayax__*', 'WebFetch,mcp', '--strict-mcp-config']) forbidText(allowed, forbidden, invocation);
    const disallowed = section(args, '            --disallowedTools', null, invocation);
    requireText(disallowed, contract.claudeDeniedTool, invocation);
    forbidText(disallowed, 'search_nayax_developer_portal', invocation);
    requireText(prompt, contract.promptRule, `${path} prompt (invocation ${index + 1})`);
  }
}

/** One Copilot CLI analysis step: the same server, filtered and approved to the same two tools. */
function verifyCopilotNayaxAccess({ invocation, prompt, workflow, source }, contract) {
  requireText(invocation, contract.copilotMcpConfig, source);
  requireText(invocation, contract.copilotToolRules, source);
  requireText(prompt, contract.promptRule, `${source} prompt`);
  if (countOccurrences(workflow, '--additional-mcp-config') !== 1) throw new Error(`${source}: expected exactly one --additional-mcp-config.`);
  if (countOccurrences(invocation, "--allow-tool='nayax") !== 2 || countOccurrences(workflow, "--allow-tool='nayax") !== 2) throw new Error(`${source}: expected exactly two Nayax tool approvals.`);
  for (const forbidden of ["--allow-tool='nayax'", "--allow-tool='nayax(submit_feedback)'", '--allow-all']) forbidText(invocation, forbidden, source);
}

function copilotNayaxSteps(read) {
  const architecture = read(architecturePath);
  const review = read(reviewPath);
  const checkSource = 'agent-architecture.yml Copilot architecture check';
  const check = section(architecture, '          copilot -s --no-ask-user \\\n', '> "$out"', checkSource);
  const reviewSource = 'agent-review.yml Copilot review';
  return [
    { invocation: check, prompt: check, workflow: architecture, source: checkSource },
    {
      invocation: section(review, "          copilot -s --no-ask-user \\\n            --allow-tool='shell(git diff:*)'", '-p "$prompt" > "$work/copilot-output.md"', reviewSource),
      prompt: section(review, `prompt="$(cat <<'EOF'\n`, '\n          EOF\n', `${reviewSource} prompt`),
      workflow: review,
      source: reviewSource,
    },
  ];
}

function fileExists(read, path) {
  try {
    read(path);
    return true;
  } catch {
    return false;
  }
}

/** Explicitly excluded: the format-only review repair, model triage, and any workspace MCP file. */
function verifyNayaxExclusions(read, contract) {
  const repairSource = 'agent-review.yml Copilot format repair';
  const repair = section(read(reviewPath), 'repair_prompt="', '> "$work/copilot-output.md"', repairSource);
  requireText(repair, "copilot -s --no-ask-user --disable-builtin-mcps \\\n              --deny-tool='shell' --deny-tool='write' --deny-tool='url' \\\n", repairSource);
  verifyNoNayaxAccess(repair, repairSource);
  verifyNoNayaxAccess(read(modelSelectionPath), `${modelSelectionPath} model triage`);
  // A workspace MCP file would be loaded by every Copilot CLI invocation, including the excluded ones.
  const present = contract.workspaceMcpConfigPaths.find((path) => fileExists(read, path));
  if (present) throw new Error(`${present}: a workspace MCP configuration would give every Copilot CLI invocation, including the format-only repair, MCP access.`);
}

function verifyNayaxRuleDocuments(read, contract) {
  const agents = read(agentsPath);
  for (const required of contract.agentsRequired) requireText(agents, required, `${agentsPath} Nayax contract verification`);
  const instructions = read(copilotInstructionsPath);
  for (const required of contract.copilotInstructionsRequired) requireText(instructions, required, `${copilotInstructionsPath} Nayax contract verification`);
}

export function verifyNayaxDocumentationAccess(read = readRepositoryFile) {
  const contract = NAYAX_MCP_CONTRACT;
  verifyNayaxPromptRuleIsShellSafe(contract);
  for (const path of contract.claudePaths) verifyClaudeNayaxAccess(read(path), path, contract);
  for (const step of copilotNayaxSteps(read)) verifyCopilotNayaxAccess(step, contract);
  verifyNayaxExclusions(read, contract);
  verifyNayaxRuleDocuments(read, contract);
}

const invokedDirectly =
  process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (invokedDirectly) {
  runContractChecks();
  console.log('Agent workflow contract validation passed.');
}
