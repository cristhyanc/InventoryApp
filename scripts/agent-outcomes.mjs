// The fixed set of outcomes the agent pipeline records as commit statuses, so a reader can tell from
// a pull request's checks alone what happened to a commit and what, if anything, to do next.
//
// Every description a trusted workflow writes to these contexts starts with one of the prefixes
// below (enforced by validate-agent-workflows.mjs and exercised by the behavioural tests).
// `classify` maps a status back to its outcome; scripts/sync-project-status.mjs and the tests use it.
//
// This file is standalone (Node built-ins only).
import { pathToFileURL } from 'node:url';

export const VALIDATION_CONTEXTS = Object.freeze(['agent-validation', 'merge-validation']);
export const REVIEW_CONTEXT = 'agent-review-verdict';

export const OUTCOMES = Object.freeze({
  // Validation of one exact commit (validate.yml, report-status job).
  'validation-passed': Object.freeze({ contexts: VALIDATION_CONTEXTS, state: 'success', prefix: 'Validation passed', meaning: 'The build and tests passed on this commit.' }),
  'validation-failed': Object.freeze({ contexts: VALIDATION_CONTEXTS, state: 'failure', prefix: 'Validation failed', meaning: 'The build or tests failed on this commit. Fix the code; no review runs.' }),
  'validation-cancelled': Object.freeze({ contexts: VALIDATION_CONTEXTS, state: 'failure', prefix: 'Validation cancelled', meaning: 'Validation stopped before it finished, usually because a newer run of the same pull request replaced it.' }),
  // The final review of one exact commit (agent-review.yml, publish job).
  ready: Object.freeze({ contexts: [REVIEW_CONTEXT], state: 'success', prefix: 'Ready for human review', meaning: 'The review found no blockers. Advisory: a person still approves and merges.' }),
  'changes-requested': Object.freeze({ contexts: [REVIEW_CONTEXT], state: 'failure', prefix: 'Changes requested', meaning: 'The review listed blockers. A person may ask for a repair with @claude repair.' }),
  superseded: Object.freeze({ contexts: [REVIEW_CONTEXT], state: 'error', prefix: 'Superseded', meaning: 'A newer commit or review run replaced this one. Look at the newest commit instead.' }),
  'provider-failed': Object.freeze({ contexts: [REVIEW_CONTEXT], state: 'error', prefix: 'AI provider failed', meaning: 'The reviewer failed, was unavailable, or returned a review that broke the contract. Nothing was published; re-request the review.' }),
  'publication-failed': Object.freeze({ contexts: [REVIEW_CONTEXT], state: 'error', prefix: 'Publication failed', meaning: 'The review could not be delivered to, verified by, or posted by the publisher. An infrastructure fault, not a code finding.' }),
  'not-eligible': Object.freeze({ contexts: [REVIEW_CONTEXT], state: 'error', prefix: 'Not eligible', meaning: 'The pull request no longer qualifies for automated review (closed, draft, relabelled, workflow files, validation or provider mode changed).' }),
});

/** The outcome a recorded status belongs to, or null when it matches none (an old or foreign description). */
export function classify(context, state, description) {
  if (typeof description !== 'string') return null;
  for (const [name, outcome] of Object.entries(OUTCOMES)) {
    if (!outcome.contexts.includes(context) || outcome.state !== state) continue;
    if (description === outcome.prefix || description.startsWith(`${outcome.prefix}: `)) return name;
  }
  return null;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [command, context, state, ...description] = process.argv.slice(2);
  if (command === 'classify' && context && state) {
    process.stdout.write(`${classify(context, state, description.join(' ')) ?? 'unknown'}\n`);
  } else if (command === 'list') {
    for (const [name, outcome] of Object.entries(OUTCOMES)) process.stdout.write(`${name}\t${outcome.state}\t${outcome.prefix}\t${outcome.meaning}\n`);
  } else {
    console.error('Usage: node scripts/agent-outcomes.mjs list | classify <context> <state> <description>');
    process.exitCode = 2;
  }
}
