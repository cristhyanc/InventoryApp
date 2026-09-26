import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

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
  for (const required of [
    'actions: write',
    `pull-requests: ${pullRequestsPermission}`,
    '--ref main',
    'headRefOid',
    'github-actions[bot]',
    'agent/issue-*',
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

// Only the REST pull request endpoint returns the canonical author login. `gh pr view
// --json author` resolves the GraphQL actor, which reports `github-actions` without the
// `[bot]` suffix, so every guarded section must read `.user.login` over REST instead.
function verifyAgentPrGuards(text, source, staleMessage) {
  for (const required of [
    '--json state,isDraft,baseRefName,headRefName,headRefOid,headRepository,headRepositoryOwner',
    '[ "$state" = "OPEN" ]',
    '[ "$is_draft" = "false" ]',
    '[ "$base_ref" = "develop" ]',
    '[ "$head_repo" = "$GITHUB_REPOSITORY" ]',
    '[[ "$head_ref" == agent/issue-* ]]',
    'author="$(gh api "repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER"',
    "--jq '.user.login // empty'",
    '[ "$author" = "github-actions[bot]" ]',
    '[ "$current_sha" = "$HEAD_SHA" ]',
    staleMessage,
    "grep -Eq '^\\.github/workflows/'",
  ]) {
    requireText(text, required, source);
  }

  for (const forbidden of [
    '.author.login',
    'headRepositoryOwner,author',
  ]) {
    forbidText(text, forbidden, source);
  }
}

export const validatePath = '.github/workflows/validate.yml';
export const reviewPath = '.github/workflows/agent-review.yml';
export const implementPath = '.github/workflows/agent-implement.yml';
export const repairPath = '.github/workflows/agent-repair.yml';
export const issueTemplatePath = '.github/ISSUE_TEMPLATE/agent-task.yml';
export const pullRequestTemplatePath = '.github/pull_request_template.md';

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
    "if: github.event.label.name == 'agent-ready' && github.event.issue.pull_request == null",
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
  needs: '    needs: preflight\n',
  condition: "if: needs.preflight.result == 'success' && github.event.label.name == 'agent-ready' && github.event.issue.pull_request == null",
  prompt: [
    'Restate its acceptance criteria, its explicit exclusions, and its Documentation impact decision and Documentation impact details.',
    "Follow the issue's Documentation impact decision exactly.",
    'If it is "Documentation changes required", update every documentation file and section listed in the Documentation impact details',
    'Never leave documentation that the change contradicts.',
    'Fill the "## Documentation impact" section accurately with exactly one `Decision:` line and one `Evidence:` line',
    '`Decision: UPDATED` when this pull request changes documentation, with Evidence listing each documentation file and what changed in it',
    '`Decision: NOT REQUIRED` only when no documentation changed, with Evidence explaining for this specific change why behaviour, contracts, architecture, configuration, automation, deployment, operations and user workflows are unaffected',
    `node ${DOCUMENTATION_IMPACT_VALIDATOR} --pr-body <file>`,
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
  const implementJob = section(implement, '  implement:\n', '  dispatch-validation:\n', `${implementPath} implement job`);
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
  const reviewJob = section(review, '  review:\n', null, reviewPath);
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

/** Verifies the architect handoff and final-head guard in the implementation workflow. */
export function verifyArchitecturePass(workflow) {
  const job = section(workflow, '  implement:\n', '  dispatch-validation:\n', 'agent-implement.yml implementation job');
  const implementationPrompt = extractPromptText(job, 'agent-implement.yml implementation prompt');
  requireText(
    implementationPrompt,
    'Do not delegate, spawn, or use Claude sub-agents, and do not invoke the `Agent` tool.',
    'agent-implement.yml implementation prompt',
  );
  const implementationAgent = section(job, '      - name: Run Claude Code implementation agent\n', '      - name: Verify the architecture pass target\n', 'agent-implement.yml implementation agent');
  requireText(implementationAgent, '--disallowedTools "Agent,WebFetch,WebSearch"', 'agent-implement.yml implementation agent');
  requireText(
    implementationPrompt,
    'use the Write tool to create `.agent-run-status` containing exactly `blocked` on one line',
    'agent-implement.yml implementation prompt',
  );
  requireOrder(job, '      - name: Run Claude Code implementation agent', '      - name: Verify the architecture pass target', 'agent-implement.yml architecture order');
  requireOrder(job, '      - name: Verify the architecture pass target', '      - name: Run Claude Code architecture agent', 'agent-implement.yml architecture order');
  requireOrder(job, '      - name: Run Claude Code architecture agent', '      - name: Record outcome on the issue', 'agent-implement.yml architecture order');

  const target = section(job, '      - name: Verify the architecture pass target\n', '      - name: Run Claude Code architecture agent\n', 'agent-implement.yml architecture target');
  for (const required of ["if: steps.claude.outcome == 'success'", 'echo "pr_ready=false"', 'echo "blocked=false"', '.agent-run-status', '[ -z "$(git ls-files -- .agent-run-status)" ]', '[ "$(cat .agent-run-status)" = "blocked" ]', 'echo "blocked=true"', 'if length == 1 then .[0] else {} end', 'gh api', '.head.repo.full_name == $repo', '.user.login == "github-actions[bot]"', 'git rev-parse HEAD', 'gh pr list', 'echo "pr_number=$pr_number"', 'echo "pr_ready=true"']) {
    requireText(target, required, 'agent-implement.yml architecture target');
  }

  const architect = section(job, '      - name: Run Claude Code architecture agent\n', '      - name: Record outcome on the issue\n', 'agent-implement.yml architect');
  requireText(architect, 'Do not delegate, spawn, or use Claude sub-agents, and do not invoke the `Agent` tool.', 'agent-implement.yml architect');
  requireText(architect, '--disallowedTools "Agent,WebFetch,WebSearch"', 'agent-implement.yml architect');
  for (const required of ["id: architect", "if: steps.architecture_target.outputs.pr_ready == 'true'", 'Read the issue', 'Preserve all observable behavior', 'bash scripts/validate.sh', 'gh pr comment', 'Bash(git push origin agent/issue-']) {
    requireText(architect, required, 'agent-implement.yml architect');
  }
  const allowed = section(architect, '          claude_args: |\n', '            --disallowedTools', 'agent-implement.yml architect allowed tools');
  verifyTrackedFileDeletionPermissions(allowed, 'agent-implement.yml architect allowed tools');
  for (const forbidden of ['gh pr edit', 'gh pr create', 'gh issue edit', 'gh workflow', 'gh api']) {
    forbidText(allowed, forbidden, 'agent-implement.yml architect allowed tools');
  }

  const outcome = section(job, '      - name: Record outcome on the issue\n', null, 'agent-implement.yml outcome');
  for (const required of ['ARCHITECT_OUTCOME', 'TARGET_OUTCOME', 'AGENT_BLOCKED: ${{ steps.architecture_target.outputs.blocked }}', '[ "$AGENT_BLOCKED" = "true" ]', 'expected blocked-task outcome, not an implementation workflow failure', '[ "$ARCHITECT_OUTCOME" = "success" ]', 'git branch --show-current', 'git rev-parse HEAD', 'git diff --quiet', 'git diff --cached --quiet']) {
    requireText(outcome, required, 'agent-implement.yml outcome');
  }
}

/** Runs every agent workflow contract check with an overridable repository reader. */
export function runContractChecks({ read = readRepositoryFile } = {}) {
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
  verifySafeDispatcher(reviewDispatcher, 'validate.yml review dispatcher');
  verifyAgentPrGuards(reviewDispatcher, 'validate.yml review dispatcher', 'Refusing stale review dispatch');
  requireText(reviewDispatcher, "github.event_name == 'workflow_dispatch'", 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'inputs.dispatch_review == true', 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'needs.report-status.result', 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'agent-review', 'validate.yml review dispatcher');
  requireText(reviewDispatcher, 'any(.labels[]?; .name == "agent-review")', 'validate.yml review dispatcher');

  verifyValidationModeIsolation(validate, validatePath);

  for (const [path, producerName] of [
    ['.github/workflows/agent-implement.yml', 'implement'],
    ['.github/workflows/agent-repair.yml', 'repair'],
  ]) {
    const workflow = read(path);
    const producer = section(workflow, `  ${producerName}:\n`, '  dispatch-validation:\n', path);
    forbidText(producer, 'actions: write', `${path} ${producerName} job`);

    const dispatcher = section(workflow, '  dispatch-validation:\n', null, path);
    verifySafeDispatcher(dispatcher, `${path} validation dispatcher`, producerName === 'implement' ? 'write' : 'read');
    verifyAgentPrGuards(dispatcher, `${path} validation dispatcher`, 'Refusing stale validation dispatch');
    requireText(dispatcher, 'validate.yml', `${path} validation dispatcher`);
    if (producerName === 'implement') {
      // The implementation dispatcher is the one place allowed to label the pull request
      // itself: it applies agent-review as a deterministic step (Claude is never granted
      // gh pr edit/gh label) before requesting review automatically via dispatch_review=true.
      requireText(dispatcher, 'gh pr edit "$PR_NUMBER" --repo "$GITHUB_REPOSITORY" --add-label agent-review', `${path} validation dispatcher`);
      requireText(dispatcher, '-f dispatch_review=true', `${path} validation dispatcher`);
    } else {
      requireText(dispatcher, '-f dispatch_review=true', `${path} validation dispatcher`);
      requireText(dispatcher, 'any(.labels[]?; .name == "agent-review")', `${path} validation dispatcher`);
    }
  }

  const review = read(reviewPath);
  for (const required of [
    'workflow_dispatch:',
    'pr_number:',
    'head_sha:',
    'inputs.pr_number',
    'agent-validation',
    'Refusing stale review',
    'persist-credentials: false',
    '.github/workflows/',
    'group: agent-review-pr-${{ github.event.pull_request.number || inputs.pr_number || github.run_id }}',
  ]) {
    requireText(review, required, reviewPath);
  }

  const reviewContext = section(review, '  context:\n', '  review:\n', reviewPath);
  verifyAgentPrGuards(normalizeGuardVariables(reviewContext), 'agent-review.yml dispatched context', 'Refusing stale review');
  requireText(reviewContext, 'statuses: read', 'agent-review.yml dispatched context');
  requireText(reviewContext, 'any(.labels[]?; .name == "agent-review")', 'agent-review.yml dispatched context');
  requireText(reviewContext, 'validation_state', 'agent-review.yml dispatched context');
  // A dispatched review accepts only the exact-SHA agent-validation status as evidence; the
  // merge-result status published by pull_request validation is never a substitute.
  requireText(reviewContext, 'select(.context == "agent-validation")', 'agent-review.yml dispatched context');
  forbidText(reviewContext, MERGE_VALIDATION_STATUS, 'agent-review.yml dispatched context');

  const reviewJob = section(review, '  review:\n', null, reviewPath);
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

  verifyArchitecturePass(read(implementPath));

  // Defence in depth: no agent workflow may resolve a pull request author through the
  // GraphQL actor login anywhere, including in a guard added after this contract was written.
  for (const path of [
    '.github/workflows/agent-implement.yml',
    '.github/workflows/agent-repair.yml',
    reviewPath,
    validatePath,
  ]) {
    forbidText(read(path), '.author.login', path);
  }

  verifyDocumentationImpactGate(read);
}

const invokedDirectly =
  process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (invokedDirectly) {
  runContractChecks();
  console.log('Agent workflow contract validation passed.');
}
