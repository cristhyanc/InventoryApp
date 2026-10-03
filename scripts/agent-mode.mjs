// Trusted provider-mode policy for the agent pipeline (#337, #338, #339).
//
// Readiness labels choose one of four routes. The choice is recorded once, on the issue, by a
// deterministic workflow step (GITHUB_TOKEN, so the comment author is github-actions[bot]) before
// the readiness label is consumed. Every later boundary (architecture dispatch, Copilot handoff,
// validation, review request, review publication, repair and head updates) re-reads that record
// and verifies it against the live pull request instead of trusting a label or PR text.
//
// This file is standalone (no imports besides Node built-ins) because jobs that never check out
// code fetch it from the trusted workflow commit and run it directly.
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

export const PROVIDERS = Object.freeze(['claude', 'copilot']);
export const STANDARD_READY_LABELS = Object.freeze(PROVIDERS.flatMap(provider => [`agent-ready-${provider}`, `agent-ready-${provider}-low`, `agent-ready-${provider}-high`]));
// Single-provider fallbacks: one provider implements, architecture-checks, repairs and reviews.
export const FULL_READY_LABELS = Object.freeze(PROVIDERS.map(provider => `agent-ready-full-${provider}`));
export const READINESS_LABELS = Object.freeze([...STANDARD_READY_LABELS, ...FULL_READY_LABELS]);
// Workflow shell checks must use exactly this pattern (enforced by validate-agent-workflows.mjs).
export const READINESS_LABEL_PATTERN = /^agent-ready-(?:(?:claude|copilot)(?:-low|-high)?|full-(?:claude|copilot))$/;
// Claude-primary is the documented default when both providers are available.
export const DEFAULT_READY_LABEL = 'agent-ready-claude';
// Trusted routing policy. Provider names come from this table, never from model output.
export const ROUTES = Object.freeze({
  'cross-claude': Object.freeze({ implementer: 'claude', reviewer: 'copilot', sameProviderReview: false }),
  'cross-copilot': Object.freeze({ implementer: 'copilot', reviewer: 'claude', sameProviderReview: false }),
  'full-claude': Object.freeze({ implementer: 'claude', reviewer: 'claude', sameProviderReview: true }),
  'full-copilot': Object.freeze({ implementer: 'copilot', reviewer: 'copilot', sameProviderReview: true }),
});
// Full-provider labels are recognised (for exclusivity) but must not start work until #340
// connects same-provider architecture review, repair and final review.
export const FULL_PROVIDER_EXECUTION_ENABLED = false;

// The only author whose mode records count: deterministic workflow steps posting with GITHUB_TOKEN.
export const MODE_RECORD_AUTHOR = 'github-actions[bot]';
export const MODE_RECORD_MARKER = 'agent-routing-mode:v1';
const recordPattern = /<!-- agent-routing-mode:v1 (\{[^\n]*?\}) -->/g;

/** Parses one readiness label into its trusted route, or returns null for any other label. */
export function parseReadinessLabel(label) {
  if (typeof label !== 'string' || !READINESS_LABEL_PATTERN.test(label) || !READINESS_LABELS.includes(label)) return null;
  const full = /^agent-ready-full-(claude|copilot)$/.exec(label);
  if (full) return { label, mode: `full-${full[1]}`, ...ROUTES[`full-${full[1]}`], tier: 'default' };
  const [, provider, suffix = ''] = /^agent-ready-(claude|copilot)(-low|-high)?$/.exec(label);
  return { label, mode: `cross-${provider}`, ...ROUTES[`cross-${provider}`], tier: suffix ? suffix.slice(1) : 'default' };
}

/** The implementer a pull request's head branch belongs to, or null. */
export function implementerForBranch(headRef) {
  if (typeof headRef !== 'string') return null;
  if (headRef.startsWith('agent/issue-')) return 'claude';
  if (headRef.startsWith('copilot/')) return 'copilot';
  return null;
}

/** Builds the hidden record a claim step posts on the issue before consuming readiness. */
export function buildModeRecord({ issue, label, run }) {
  const route = parseReadinessLabel(label);
  if (!route) throw new Error('A mode record needs a readiness label.');
  if (!Number.isInteger(issue) || issue < 1) throw new Error('A mode record needs a valid issue number.');
  if (!/^[1-9]\d*$/.test(String(run))) throw new Error('A mode record needs the claiming run ID.');
  return `<!-- ${MODE_RECORD_MARKER} ${JSON.stringify({ issue, label, mode: route.mode, run: String(run) })} -->`;
}

function parseRecord(text, issue) {
  let record;
  try { record = JSON.parse(text); } catch { throw new Error(`Malformed provider-mode record on issue #${issue}; a human must resolve it.`); }
  const keys = record && typeof record === 'object' && !Array.isArray(record) ? Object.keys(record).sort((a, b) => a.localeCompare(b)).join(',') : '';
  const route = keys === 'issue,label,mode,run' ? parseReadinessLabel(record.label) : null;
  if (!route || route.mode !== record.mode || !Number.isInteger(record.issue) || typeof record.run !== 'string' || !/^[1-9]\d*$/.test(record.run)) {
    throw new Error(`Malformed provider-mode record on issue #${issue}; a human must resolve it.`);
  }
  if (record.issue !== issue) throw new Error(`Provider-mode record names issue #${record.issue}, not #${issue}.`);
  return { ...record, ...ROUTES[record.mode] };
}

/**
 * Resolves the verified mode of an agent pull request.
 * comments: the issue's comments as { id, author, created_at, body }.
 * A record by any author other than MODE_RECORD_AUTHOR is ignored. Without a trusted record the
 * pull request is a legacy task (started before #339) and keeps its branch-derived cross-review
 * route; full-provider tasks always carry a record, so they can never fall back this way.
 */
export function resolveMode({ issue, headRef, expectedImplementer, comments, fullProviderEnabled = FULL_PROVIDER_EXECUTION_ENABLED }) {
  const implementer = verifyBranchIdentity({ issue, headRef, expectedImplementer });
  const records = collectRecords(comments, issue);
  if (!records.length) {
    return { issue, mode: `cross-${implementer}`, ...ROUTES[`cross-${implementer}`], source: 'legacy' };
  }
  // The newest claim wins: a human relabel that started a new run supersedes the old choice.
  records.sort((a, b) => a.at.localeCompare(b.at) || a.id - b.id);
  const { record } = records.at(-1);
  if (record.implementer !== implementer) {
    throw new Error(`Issue #${issue} was last claimed for ${record.mode} (${record.label}), but this pull request belongs to ${implementer}. The provider is never switched automatically; a human must resolve it.`);
  }
  if (record.sameProviderReview && !fullProviderEnabled) {
    throw new Error(`Issue #${issue} is recorded as ${record.mode}, which is not enabled yet; nothing will run for it.`);
  }
  return { issue, mode: record.mode, implementer, reviewer: record.reviewer, sameProviderReview: record.sameProviderReview, source: 'record', label: record.label, run: record.run };
}

/** Checks that the head branch is an agent branch of the expected implementer and issue; returns the implementer. */
function verifyBranchIdentity({ issue, headRef, expectedImplementer }) {
  if (!Number.isInteger(issue) || issue < 1) throw new Error('Provider mode needs the pull request\'s issue number.');
  const implementer = implementerForBranch(headRef);
  if (!implementer) throw new Error(`Branch '${headRef}' is not an agent/issue-* or copilot/* branch.`);
  if (expectedImplementer !== implementer) throw new Error(`Branch '${headRef}' belongs to ${implementer}, not ${expectedImplementer}.`);
  if (implementer === 'claude' && !headRef.startsWith(`agent/issue-${issue}-`)) throw new Error(`Branch '${headRef}' does not belong to issue #${issue}.`);
  return implementer;
}

/** Parses the trusted mode records among an issue's comments; anything by another author is ignored. */
function collectRecords(comments, issue) {
  if (!Array.isArray(comments)) throw new Error('Issue comments could not be read; provider mode is unverified.');
  const records = [];
  for (const comment of comments) {
    if (comment?.author !== MODE_RECORD_AUTHOR || typeof comment.body !== 'string') continue;
    const found = [...comment.body.matchAll(recordPattern)];
    if (!found.length && comment.body.includes(MODE_RECORD_MARKER)) throw new Error(`Malformed provider-mode record on issue #${issue}; a human must resolve it.`);
    if (found.length > 1) throw new Error(`A comment on issue #${issue} carries more than one provider-mode record.`);
    if (found.length) records.push({ at: String(comment.created_at ?? ''), id: Number(comment.id) || 0, record: parseRecord(found[0][1], issue) });
  }
  return records;
}

// An absolute path, so the lookup never depends on PATH. GitHub's Ubuntu runners install gh at
// /usr/bin/gh; tests point AGENT_MODE_GH_PATH at a fake.
const GH_PATH = process.env.AGENT_MODE_GH_PATH || '/usr/bin/gh';
const gh = (args) => execFileSync(GH_PATH, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });

/** Reads the live pull request and its issue's comments, then resolves the mode. */
export function verifyPullRequest({ repository, pr, expectedImplementer }) {
  if (!/^[1-9]\d*$/.test(String(pr))) throw new Error('A valid pull request number is required.');
  if (!/^[\w.-]+\/[\w.-]+$/.test(String(repository))) throw new Error('A valid owner/repository is required.');
  if (!PROVIDERS.includes(expectedImplementer)) throw new Error('The expected implementer must be claude or copilot.');
  const live = JSON.parse(gh(['pr', 'view', String(pr), '--repo', repository, '--json', 'headRefName,closingIssuesReferences']));
  const closing = (live.closingIssuesReferences ?? []).map(item => item.number);
  const branchIssue = /^agent\/issue-([1-9]\d*)-/.exec(live.headRefName ?? '')?.[1];
  let issue;
  if (branchIssue) {
    issue = Number(branchIssue);
    if (closing.length && !closing.includes(issue)) throw new Error(`Pull request #${pr} closes ${closing.join(', ')}, not its branch issue #${issue}.`);
  } else {
    if (closing.length !== 1) throw new Error(`Pull request #${pr} must close exactly one issue (found ${closing.length}).`);
    issue = closing[0];
  }
  const lines = gh(['api', '--paginate', `repos/${repository}/issues/${issue}/comments?per_page=100`, '--jq', '.[] | {id, author: .user.login, created_at, body} | @json']);
  const comments = lines.split('\n').filter(Boolean).map(line => JSON.parse(line));
  return resolveMode({ issue, headRef: live.headRefName, expectedImplementer, comments });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [command, ...rest] = process.argv.slice(2);
    if (command === 'record') {
      const [issue, label, run] = rest;
      process.stdout.write(buildModeRecord({ issue: Number(issue), label, run }));
    } else if (command === 'verify-pr') {
      const [pr, expectedImplementer] = rest;
      const result = verifyPullRequest({ repository: process.env.GITHUB_REPOSITORY, pr, expectedImplementer });
      const review = result.sameProviderReview ? ', same-provider review' : '';
      const origin = result.source === 'legacy' ? 'legacy task without a mode record' : `recorded by run ${result.run} from ${result.label}`;
      const summary = `Provider mode for pull request #${pr}: ${result.mode} (implementer ${result.implementer}, reviewer ${result.reviewer}${review}; ${origin}).`;
      console.error(`::notice::${summary}`);
      process.stdout.write(`${result.mode}\n`);
    } else throw new Error('Expected record or verify-pr.');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
