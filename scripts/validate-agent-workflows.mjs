import { readFileSync } from 'node:fs';

const read = (path) => readFileSync(path, 'utf8');

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

function verifySafeDispatcher(text, source) {
  for (const required of [
    'actions: write',
    'pull-requests: read',
    '--ref main',
    'headRefOid',
    'github-actions[bot]',
    'agent/issue-*',
    '.github/workflows/',
  ]) {
    requireText(text, required, source);
  }

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

function verifyAgentPrGuards(text, source, staleMessage) {
  for (const required of [
    '--json state,isDraft,baseRefName,headRefName,headRefOid,headRepository,headRepositoryOwner,author',
    '[ "$state" = "OPEN" ]',
    '[ "$is_draft" = "false" ]',
    '[ "$base_ref" = "develop" ]',
    '[ "$head_repo" = "$GITHUB_REPOSITORY" ]',
    '[[ "$head_ref" == agent/issue-* ]]',
    '[ "$author" = "github-actions[bot]" ]',
    '[ "$current_sha" = "$HEAD_SHA" ]',
    staleMessage,
    "grep -Eq '^\\.github/workflows/'",
  ]) {
    requireText(text, required, source);
  }
}

const validatePath = '.github/workflows/validate.yml';
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
  'group: validation-${{ github.workflow }}-${{ github.event.pull_request.number || inputs.pr_number || github.ref }}',
]) {
  requireText(validate, required, validatePath);
}

const validationContext = section(validate, '  context:\n', '  validate:\n', validatePath);
requireText(validationContext, 'statuses: write', 'validate.yml context job');
requireText(validationContext, 'statuses/$head_sha', 'validate.yml context job');
requireText(validationContext, 'target_url="$RUN_URL"', 'validate.yml context job');
forbidText(validationContext, 'actions/checkout', 'validate.yml context job');
forbidText(validationContext, 'CLAUDE_CODE_OAUTH_TOKEN', 'validate.yml context job');
verifyAgentPrGuards(validationContext.replaceAll('$head_sha', '$HEAD_SHA'), 'validate.yml dispatched context', 'Refusing stale validation');

const validationJob = section(validate, '  validate:\n', '  report-status:\n', validatePath);
requireText(validationJob, 'contents: read', 'validate.yml validate job');
requireText(validationJob, 'persist-credentials: false', 'validate.yml validate job');
for (const forbidden of ['actions: write', 'statuses: write', 'CLAUDE_CODE_OAUTH_TOKEN']) {
  forbidText(validationJob, forbidden, 'validate.yml validate job');
}

const statusJob = section(validate, '  report-status:\n', '  dispatch-review:\n', validatePath);
requireText(statusJob, 'statuses: write', 'validate.yml status job');
requireText(statusJob, 'statuses/$HEAD_SHA', 'validate.yml status job');
requireText(statusJob, 'target_url="$RUN_URL"', 'validate.yml status job');
forbidText(statusJob, 'actions/checkout', 'validate.yml status job');
forbidText(statusJob, 'CLAUDE_CODE_OAUTH_TOKEN', 'validate.yml status job');

const reviewDispatcher = section(validate, '  dispatch-review:\n', null, validatePath);
verifySafeDispatcher(reviewDispatcher, 'validate.yml review dispatcher');
verifyAgentPrGuards(reviewDispatcher, 'validate.yml review dispatcher', 'Refusing stale review dispatch');
requireText(reviewDispatcher, 'inputs.dispatch_review == true', 'validate.yml review dispatcher');
requireText(reviewDispatcher, 'needs.report-status.result', 'validate.yml review dispatcher');
requireText(reviewDispatcher, 'agent-review', 'validate.yml review dispatcher');
requireText(reviewDispatcher, 'any(.labels[]?; .name == "agent-review")', 'validate.yml review dispatcher');

for (const [path, producerName] of [
  ['.github/workflows/agent-implement.yml', 'implement'],
  ['.github/workflows/agent-repair.yml', 'repair'],
]) {
  const workflow = read(path);
  const producer = section(workflow, `  ${producerName}:\n`, '  dispatch-validation:\n', path);
  forbidText(producer, 'actions: write', `${path} ${producerName} job`);

  const dispatcher = section(workflow, '  dispatch-validation:\n', null, path);
  verifySafeDispatcher(dispatcher, `${path} validation dispatcher`);
  verifyAgentPrGuards(dispatcher, `${path} validation dispatcher`, 'Refusing stale validation dispatch');
  requireText(dispatcher, 'validate.yml', `${path} validation dispatcher`);
  if (producerName === 'implement') {
    requireText(dispatcher, '-f dispatch_review=false', `${path} validation dispatcher`);
    forbidText(dispatcher, '-f dispatch_review=true', `${path} validation dispatcher`);
  } else {
    requireText(dispatcher, '-f dispatch_review=true', `${path} validation dispatcher`);
    requireText(dispatcher, 'any(.labels[]?; .name == "agent-review")', `${path} validation dispatcher`);
  }
}

const reviewPath = '.github/workflows/agent-review.yml';
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
verifyAgentPrGuards(reviewContext.replaceAll('$head_sha', '$HEAD_SHA'), 'agent-review.yml dispatched context', 'Refusing stale review');
requireText(reviewContext, 'statuses: read', 'agent-review.yml dispatched context');
requireText(reviewContext, 'any(.labels[]?; .name == "agent-review")', 'agent-review.yml dispatched context');
requireText(reviewContext, 'validation_state', 'agent-review.yml dispatched context');

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

console.log('Agent workflow contract validation passed.');
