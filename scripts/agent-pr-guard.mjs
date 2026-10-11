// Trusted eligibility guard for agent pull requests: the one copy of the checks every dispatcher and
// the review publisher run before they act on a pull request.
//
// A pull request is eligible only while it is open, not a draft, targets develop, comes from a
// branch in this repository, uses a Claude agent/issue-* branch (of the given issue, when one is
// named), is authored by the agent automation App (AGENT_AUTOMATION_APP_BOT_LOGIN, read over REST
// because only that endpoint reports bot authors reliably), still has exactly the expected head
// SHA, and changes nothing under .github/workflows/ (those pull requests need a human). The checks
// run in that order and the first one that fails is reported.
//
// Each workflow keeps its own response to a refusal (fail the job, skip quietly, or suppress a
// review) and its own extra checks (provider mode, labels, issue state, validation status). This
// script only decides eligibility. It never writes anything.
//
// Usage: node agent-pr-guard.mjs check <pr-number> <head-sha> [--issue <issue-number>]
// Env:   GITHUB_REPOSITORY, EXPECTED_AGENT_AUTHOR, GH_TOKEN (read access to pull requests).
// On success it prints {"base_ref", "head_ref", "head_sha", "implementer", "labels"} as JSON and exits 0.
// On refusal it prints one line "<code>: <message>" to stderr and exits 2; codes are listed in
// REFUSAL_CODES. Any other exit status is a fault in the guard itself, so callers fail closed.
//
// This file is standalone (no imports besides Node built-ins) because jobs that never check out
// code fetch it from the trusted workflow commit and run it directly.
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

export const REFUSAL_CODES = Object.freeze([
  'input', // the caller passed an invalid pull request number, SHA, issue or repository
  'unavailable', // the live pull request state could not be read
  'closed',
  'draft',
  'base',
  'fork',
  'branch',
  'config', // AGENT_AUTOMATION_APP_BOT_LOGIN is not configured
  'author',
  'stale',
  'workflow-files',
]);
export const PR_VIEW_FIELDS = 'state,isDraft,baseRefName,headRefName,headRefOid,headRepository,headRepositoryOwner,labels';
export const AGENT_BRANCH_PREFIX = 'agent/issue-';
export const WORKFLOW_DIR = '.github/workflows/';
export const REFUSED_EXIT = 2;

export class GuardRefusal extends Error {
  constructor(code, message) {
    super(message);
    if (!REFUSAL_CODES.includes(code)) throw new Error(`Unknown refusal code '${code}'.`);
    this.code = code;
  }
}

const refuse = (code, message) => { throw new GuardRefusal(code, message); };

/** Validates the caller's input before anything is read. */
export function parseRequest({ repository, pr, sha, issue }) {
  if (!/^[\w.-]+\/[\w.-]+$/.test(String(repository ?? ''))) refuse('input', 'a valid owner/repository is required.');
  if (!/^[1-9]\d*$/.test(String(pr ?? ''))) refuse('input', 'a valid pull request number is required.');
  if (!/^[0-9a-f]{40}$/.test(String(sha ?? ''))) refuse('input', 'a full 40-character lowercase head SHA is required.');
  if (issue !== undefined && !/^[1-9]\d*$/.test(String(issue))) refuse('input', 'a valid issue number is required.');
  return { repository: String(repository), pr: String(pr), sha: String(sha), issue: issue === undefined ? undefined : String(issue) };
}

/**
 * Decides eligibility from the live state already read: `pr` is the `gh pr view` JSON, `author` the
 * REST author login, `files` the changed paths. Returns the guard's success output or throws a
 * GuardRefusal.
 */
export function evaluate({ repository, pr: number, sha, issue }, { pr, author, files }, expectedAuthor) {
  const head = pr.headRefName ?? '';
  const current = pr.headRefOid ?? '';
  if (pr.state !== 'OPEN') refuse('closed', `pull request #${number} is not open (${pr.state ?? 'unknown'}).`);
  if (pr.isDraft !== false) refuse('draft', `pull request #${number} is a draft.`);
  if (pr.baseRefName !== 'develop') refuse('base', `pull request #${number} targets '${pr.baseRefName ?? ''}', not develop.`);
  const headRepository = `${pr.headRepositoryOwner?.login}/${pr.headRepository?.name}`;
  if (headRepository !== repository) refuse('fork', `pull request #${number} does not use a same-repository branch.`);
  // Claude is the only implementer: only its agent/issue-* branches are eligible.
  if (!head.startsWith(AGENT_BRANCH_PREFIX)) refuse('branch', `pull request #${number} does not use an agent/issue-* branch (head is '${head}').`);
  if (issue !== undefined && !head.startsWith(`${AGENT_BRANCH_PREFIX}${issue}-`)) refuse('branch', `pull request #${number} branch '${head}' does not match issue #${issue}.`);
  if (!expectedAuthor) refuse('config', 'AGENT_AUTOMATION_APP_BOT_LOGIN is not configured.');
  if (author !== expectedAuthor) refuse('author', `pull request #${number} is not authored by ${expectedAuthor}.`);
  if (current !== sha) refuse('stale', `the head of pull request #${number} is now ${current || 'unknown'}, not ${sha}.`);
  if (files.some((file) => file.startsWith(WORKFLOW_DIR))) {
    refuse('workflow-files', 'pull requests that change .github/workflows/** require manual validation and review.');
  }
  return {
    base_ref: pr.baseRefName,
    head_ref: head,
    head_sha: current,
    implementer: 'claude',
    labels: (pr.labels ?? []).map((label) => label.name).filter((name) => typeof name === 'string'),
  };
}

// An absolute path, so the lookup never depends on PATH. GitHub's Ubuntu runners install gh at
// /usr/bin/gh; tests point AGENT_PR_GUARD_GH_PATH at a fake.
const GH_PATH = process.env.AGENT_PR_GUARD_GH_PATH || '/usr/bin/gh';
const gh = (args) => execFileSync(GH_PATH, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });

/** Reads the live pull request state the guard needs. Any read failure is `unavailable`. */
export function readLiveState({ repository, pr }) {
  try {
    const view = JSON.parse(gh(['pr', 'view', pr, '--repo', repository, '--json', PR_VIEW_FIELDS]));
    const author = gh(['api', `repos/${repository}/pulls/${pr}`, '--jq', '.user.login // empty']).trim();
    const files = gh(['api', '--paginate', `repos/${repository}/pulls/${pr}/files?per_page=100`, '--jq', '.[].filename'])
      .split('\n').filter(Boolean);
    if (!view || typeof view !== 'object') throw new Error('malformed');
    return { pr: view, author, files };
  } catch {
    return refuse('unavailable', `the live state of pull request #${pr} could not be read.`);
  }
}

export function check({ repository, pr, sha, issue, expectedAuthor }) {
  const request = parseRequest({ repository, pr, sha, issue });
  return evaluate(request, readLiveState(request), expectedAuthor);
}

function parseArgs(args) {
  const [command, pr, sha, ...rest] = args;
  if (command !== 'check') throw new GuardRefusal('input', 'expected: check <pr-number> <head-sha> [--issue <issue-number>].');
  let issue;
  for (let i = 0; i < rest.length; i += 1) {
    if (rest[i] === '--issue' && i + 1 < rest.length && issue === undefined) issue = rest[++i];
    else throw new GuardRefusal('input', `unexpected argument '${rest[i]}'.`);
  }
  return { pr, sha, issue };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const { pr, sha, issue } = parseArgs(process.argv.slice(2));
    const result = check({ repository: process.env.GITHUB_REPOSITORY, pr, sha, issue, expectedAuthor: process.env.EXPECTED_AGENT_AUTHOR ?? '' });
    process.stdout.write(`${JSON.stringify(result)}\n`);
  } catch (error) {
    if (error instanceof GuardRefusal) {
      console.error(`${error.code}: ${error.message}`);
      process.exitCode = REFUSED_EXIT;
    } else {
      console.error('fault: the pull request guard failed unexpectedly.');
      process.exitCode = 1;
    }
  }
}
