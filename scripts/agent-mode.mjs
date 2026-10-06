// Trusted provider-mode policy for the agent pipeline (#337, #338, #339).
//
// Readiness labels choose one of two routes. Claude is the only implementer: on the default route
// the Copilot CLI checks and reviews its work, and on the full-claude fallback separate read-only
// Claude invocations do. The route of an agent pull request is never taken
// from a comment, a label that is still on the issue, or PR text. It is derived from the issue's
// own label history, which GitHub records and nobody can edit or delete: the newest *claim* (the
// claiming workflow step replacing the readiness label with `agent-working`, as
// github-actions[bot]) names the label that was consumed, and the label must have been applied by
// a person (GitHub actor type User, never any bot) before that. Agents cannot change issue labels (their tool lists deny `gh issue edit`
// and `gh api`), so they cannot fake a claim. Every boundary (architecture dispatch,
// validation, review, publication and repair) re-derives the route from that history and
// checks that the pull request was opened after the newest claim, so a pull request left over
// from an earlier claim is refused instead of re-routed.
//
// This file is standalone (no imports besides Node built-ins) because jobs that never check out
// code fetch it from the trusted workflow commit and run it directly.
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

// Claude is the only implementer. Copilot only checks and reviews (the cross-claude route); it no
// longer implements, so there is no copilot readiness label, route or `copilot/*` branch.
export const PROVIDERS = Object.freeze(['claude']);
export const STANDARD_READY_LABELS = Object.freeze(['agent-ready-claude', 'agent-ready-claude-low', 'agent-ready-claude-high']);
// Single-provider fallback: Claude implements, architecture-checks, repairs and reviews.
export const FULL_READY_LABELS = Object.freeze(['agent-ready-full-claude']);
export const READINESS_LABELS = Object.freeze([...STANDARD_READY_LABELS, ...FULL_READY_LABELS]);
// Workflow shell checks must use exactly this pattern (enforced by validate-agent-workflows.mjs).
export const READINESS_LABEL_PATTERN = /^agent-ready-(?:claude(?:-low|-high)?|full-claude)$/;
// The documented default.
export const DEFAULT_READY_LABEL = 'agent-ready-claude';
// Trusted routing policy. Provider names come from this table, never from model output.
export const ROUTES = Object.freeze({
  'cross-claude': Object.freeze({ implementer: 'claude', reviewer: 'copilot', sameProviderReview: false }),
  'full-claude': Object.freeze({ implementer: 'claude', reviewer: 'claude', sameProviderReview: true }),
});
// Kill switch for the single-provider fallback. #340 connected its same-provider architecture
// check, repair and final review, so the full label may start work. Setting this to false makes
// every full-provider claim fail closed again at every boundary.
export const FULL_PROVIDER_EXECUTION_ENABLED = true;

// The only actor whose label changes count as a claim: deterministic workflow steps using GITHUB_TOKEN.
export const CLAIM_ACTOR = 'github-actions[bot]';
export const CLAIM_LABEL = 'agent-working';
// The claiming step removes the readiness label and adds agent-working in one `gh issue edit`;
// GitHub may record the two events up to a few seconds apart.
const CLAIM_PAIR_WINDOW_MS = 10_000;

/** Parses one readiness label into its trusted route, or returns null for any other label. */
export function parseReadinessLabel(label) {
  if (typeof label !== 'string' || !READINESS_LABEL_PATTERN.test(label) || !READINESS_LABELS.includes(label)) return null;
  const full = /^agent-ready-full-(claude)$/.exec(label);
  if (full) return { label, mode: `full-${full[1]}`, ...ROUTES[`full-${full[1]}`], tier: 'default' };
  const [, provider, suffix = ''] = /^agent-ready-(claude)(-low|-high)?$/.exec(label);
  return { label, mode: `cross-${provider}`, ...ROUTES[`cross-${provider}`], tier: suffix ? suffix.slice(1) : 'default' };
}

/** The implementer a pull request's head branch belongs to, or null. */
export function implementerForBranch(headRef) {
  if (typeof headRef !== 'string') return null;
  if (headRef.startsWith('agent/issue-')) return 'claude';
  return null;
}

/**
 * Resolves the verified mode of an agent pull request.
 * events: the issue's label events as { id, event: 'labeled' | 'unlabeled', actor, actorType, label, created_at },
 * where actor and actorType are GitHub's actor.login and actor.type ('User', 'Bot', ...).
 * prCreatedAt: when the pull request was opened.
 */
export function resolveMode({ issue, headRef, expectedImplementer, prCreatedAt, events, fullProviderEnabled = FULL_PROVIDER_EXECUTION_ENABLED }) {
  const implementer = verifyBranchIdentity({ issue, headRef, expectedImplementer });
  const claim = latestClaim(events, issue);
  const route = parseReadinessLabel(claim.label);
  if (route.implementer !== implementer) {
    throw new Error(`Issue #${issue} was last claimed for ${route.mode} (${claim.label}), but this pull request belongs to ${implementer}. The provider is never switched automatically; a human must resolve it.`);
  }
  if (route.sameProviderReview && !fullProviderEnabled) {
    throw new Error(`Issue #${issue} was claimed for ${route.mode}, which is not enabled yet; nothing will run for it.`);
  }
  const opened = Date.parse(prCreatedAt);
  if (!Number.isFinite(opened)) throw new Error('The pull request creation time could not be read; provider mode is unverified.');
  if (opened < claim.at) {
    throw new Error(`This pull request was opened before issue #${issue}'s latest claim (${claim.label} at ${new Date(claim.at).toISOString()}), so it belongs to an earlier run. A human must resolve it.`);
  }
  return { issue, mode: route.mode, implementer, reviewer: route.reviewer, sameProviderReview: route.sameProviderReview, label: claim.label, claimedAt: new Date(claim.at).toISOString() };
}

/**
 * Finds the newest claim in the issue's label history and the readiness label it consumed.
 * A claim is CLAIM_ACTOR adding CLAIM_LABEL; it must be paired with CLAIM_ACTOR removing exactly
 * one readiness label at the same moment, and that label must have been applied before the claim
 * by a person (actor type User). Anything else fails closed.
 */
function latestClaim(events, issue) {
  if (!Array.isArray(events)) throw new Error(`The label history of issue #${issue} could not be read; provider mode is unverified.`);
  const timeline = events
    .map(event => ({ ...event, at: Date.parse(event?.created_at), id: Number(event?.id) || 0 }))
    .filter(event => Number.isFinite(event.at) && typeof event.label === 'string')
    .sort((a, b) => a.at - b.at || a.id - b.id);
  const claims = timeline.filter(event => event.event === 'labeled' && event.label === CLAIM_LABEL && isClaimActor(event));
  if (!claims.length) throw new Error(`Issue #${issue} has no verifiable claim in its label history; nothing runs without one. A human must resolve it.`);
  const claim = claims.at(-1);
  const consumed = new Set(timeline
    .filter(event => event.event === 'unlabeled' && isClaimActor(event) && parseReadinessLabel(event.label) && Math.abs(event.at - claim.at) <= CLAIM_PAIR_WINDOW_MS)
    .map(event => event.label));
  if (consumed.size !== 1) throw new Error(`Issue #${issue}'s latest claim did not consume exactly one readiness label; a human must resolve it.`);
  const [label] = consumed;
  const applied = timeline.filter(event => event.event === 'labeled' && event.label === label && event.at <= claim.at).at(-1);
  // Only a person may choose the route: any bot, an unknown actor type or a missing identity is refused.
  if (!applied || applied.actorType !== 'User' || typeof applied.actor !== 'string' || !applied.actor) {
    throw new Error(`Issue #${issue}'s claimed label ${label} was not applied by a person; a human must resolve it.`);
  }
  return { label, at: claim.at };
}

const isClaimActor = (event) => event.actor === CLAIM_ACTOR && event.actorType === 'Bot';

/** Checks that the head branch is an agent branch of the expected implementer and issue; returns the implementer. */
function verifyBranchIdentity({ issue, headRef, expectedImplementer }) {
  if (!Number.isInteger(issue) || issue < 1) throw new Error('Provider mode needs the pull request\'s issue number.');
  const implementer = implementerForBranch(headRef);
  if (!implementer) throw new Error(`Branch '${headRef}' is not an agent/issue-* branch.`);
  if (expectedImplementer !== implementer) throw new Error(`Branch '${headRef}' belongs to ${implementer}, not ${expectedImplementer}.`);
  if (implementer === 'claude' && !headRef.startsWith(`agent/issue-${issue}-`)) throw new Error(`Branch '${headRef}' does not belong to issue #${issue}.`);
  return implementer;
}

// An absolute path, so the lookup never depends on PATH. GitHub's Ubuntu runners install gh at
// /usr/bin/gh; tests point AGENT_MODE_GH_PATH at a fake.
const GH_PATH = process.env.AGENT_MODE_GH_PATH || '/usr/bin/gh';
const gh = (args) => execFileSync(GH_PATH, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });

/** Reads the live pull request and its issue's label history, then resolves the mode. */
export function verifyPullRequest({ repository, pr, expectedImplementer }) {
  if (!/^[1-9]\d*$/.test(String(pr))) throw new Error('A valid pull request number is required.');
  if (!/^[\w.-]+\/[\w.-]+$/.test(String(repository))) throw new Error('A valid owner/repository is required.');
  if (!PROVIDERS.includes(expectedImplementer)) throw new Error('The expected implementer must be claude.');
  const live = JSON.parse(gh(['pr', 'view', String(pr), '--repo', repository, '--json', 'headRefName,closingIssuesReferences,createdAt']));
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
  const lines = gh(['api', '--paginate', `repos/${repository}/issues/${issue}/events?per_page=100`, '--jq', '.[] | select(.event == "labeled" or .event == "unlabeled") | {id, event, actor: .actor.login, actorType: .actor.type, label: .label.name, created_at} | @json']);
  const events = lines.split('\n').filter(Boolean).map(line => JSON.parse(line));
  return resolveMode({ issue, headRef: live.headRefName, expectedImplementer, prCreatedAt: live.createdAt, events });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [command, ...rest] = process.argv.slice(2);
    if (command === 'verify-pr') {
      const [pr, expectedImplementer] = rest;
      const result = verifyPullRequest({ repository: process.env.GITHUB_REPOSITORY, pr, expectedImplementer });
      const review = result.sameProviderReview ? ', same-provider review' : '';
      const summary = `Provider mode for pull request #${pr}: ${result.mode} (implementer ${result.implementer}, reviewer ${result.reviewer}${review}; claimed from ${result.label} at ${result.claimedAt}).`;
      console.error(`::notice::${summary}`);
      process.stdout.write(`${result.mode}\n`);
    } else throw new Error('Expected verify-pr.');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
