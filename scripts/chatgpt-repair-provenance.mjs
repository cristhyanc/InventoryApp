// Trusted provenance check for an automatic repair requested by a ChatGPT review (agent-repair.yml).
//
// A review posted by github-actions[bot] with the expected text is not proof on its own: any workflow
// in this repository that holds pull-requests: write posts as the same bot. agent-repair.yml therefore
// accepts a dispatched repair only when the review can be tied to one specific run of the trusted
// chatgpt-review.yml workflow:
// - the dispatch names that run (id and attempt), and the Actions API confirms the run is
//   .github/workflows/chatgpt-review.yml in this repository, started by pull_request_target or by a
//   workflow_dispatch from main;
// - the review body carries exactly one provenance line naming that run, pull request and head, and no
//   other review on the pull request names the same run (a second review claiming the run is refused);
// - the review was submitted while that run's publish job was running, the publish job succeeded, and
//   the run has the repair dispatcher job;
// - the review was never edited after it was posted.
//
// This file is standalone (no imports besides Node built-ins) because agent-repair.yml fetches it from
// its own workflow commit and runs it before checking out the pull request.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export const TRUSTED_WORKFLOW_PATH = '.github/workflows/chatgpt-review.yml';
export const TRUSTED_EVENTS = Object.freeze(['pull_request_target', 'workflow_dispatch']);
export const PUBLISH_JOB_NAME = 'Guarded review publication';
export const DISPATCH_JOB_NAME = 'Dispatch agent repair for a changes-requested review';
export const REVIEW_BOT = 'github-actions[bot]';
export const MARKER_PREFIX = '<!-- chatgpt-review-provenance ';
const MARKER_LINE = /^<!-- chatgpt-review-provenance [^\n]* -->$/gm;

export function provenanceMarker({ runId, runAttempt, prNumber, headSha }) {
  return `${MARKER_PREFIX}run_id=${runId} run_attempt=${runAttempt} pr=${prNumber} head_sha=${headSha} -->`;
}

const positiveInteger = value => /^[1-9][0-9]*$/.test(String(value ?? ''));
const time = value => {
  const parsed = Date.parse(value ?? '');
  return Number.isNaN(parsed) ? null : parsed;
};

// Throws with the reason when the review cannot be proven to come from the trusted workflow run.
export function verifyRepairProvenance({ request, review, reviewLastEditedAt, reviews, run, jobs }) {
  const { repository, prNumber, headSha, reviewId, sourceRunId, sourceRunAttempt } = request ?? {};
  const fail = reason => { throw new Error(`ChatGPT repair provenance not established: ${reason}`); };

  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository ?? '')) fail('the repository is unknown.');
  if (!positiveInteger(prNumber)) fail('a valid pull request number is required.');
  if (!/^[0-9a-f]{40}$/.test(headSha ?? '')) fail('a full head SHA is required.');
  if (!positiveInteger(reviewId)) fail('a valid review id is required.');
  if (!positiveInteger(sourceRunId) || !positiveInteger(sourceRunAttempt)) fail('the dispatch must name the ChatGPT review run and attempt.');

  // The review itself.
  if (String(review?.id) !== String(reviewId)) fail(`review ${reviewId} could not be read.`);
  if (review.user?.login !== REVIEW_BOT || review.user?.type !== 'Bot') fail(`review ${reviewId} was not posted by ${REVIEW_BOT}.`);
  if (review.commit_id !== headSha) fail(`review ${reviewId} is not bound to ${headSha}.`);
  if (review.state !== 'COMMENTED') fail(`review ${reviewId} is not a comment-only review.`);
  if (reviewLastEditedAt) fail(`review ${reviewId} was edited after it was posted (${reviewLastEditedAt}).`);
  const expected = provenanceMarker({ runId: sourceRunId, runAttempt: sourceRunAttempt, prNumber, headSha });
  const markers = String(review.body ?? '').match(MARKER_LINE) ?? [];
  if (markers.length !== 1 || markers[0] !== expected) fail(`review ${reviewId} does not carry exactly one provenance line for run ${sourceRunId} attempt ${sourceRunAttempt}.`);

  // No other review may claim the same run.
  const runClaim = `${MARKER_PREFIX}run_id=${sourceRunId} `;
  const claimants = (reviews ?? []).filter(item => String(item?.body ?? '').includes(runClaim)).map(item => String(item.id));
  if (claimants.length !== 1 || claimants[0] !== String(reviewId)) fail(`run ${sourceRunId} is claimed by reviews ${claimants.join(', ') || 'none'}, not only by review ${reviewId}.`);

  // The run is the trusted ChatGPT review workflow in this repository, from a trusted source.
  if (String(run?.id) !== String(sourceRunId) || String(run?.run_attempt) !== String(sourceRunAttempt)) fail(`run ${sourceRunId} attempt ${sourceRunAttempt} could not be read.`);
  if (String(run.path ?? '').split('@')[0] !== TRUSTED_WORKFLOW_PATH) fail(`run ${sourceRunId} is ${run.path || 'an unknown workflow'}, not ${TRUSTED_WORKFLOW_PATH}.`);
  if (run.repository?.full_name !== repository || run.head_repository?.full_name !== repository) fail(`run ${sourceRunId} does not belong to ${repository}.`);
  if (!TRUSTED_EVENTS.includes(run.event)) fail(`run ${sourceRunId} was started by '${run.event}', not by ${TRUSTED_EVENTS.join(' or ')}.`);
  if (run.event === 'workflow_dispatch' && run.head_branch !== 'main') fail(`run ${sourceRunId} was dispatched from '${run.head_branch}', not main.`);

  // The review was posted by that run's publish job, which succeeded, and the run requested the repair.
  const named = name => (jobs ?? []).filter(job => job?.name === name);
  const publish = named(PUBLISH_JOB_NAME);
  if (publish.length !== 1) fail(`run ${sourceRunId} has no single '${PUBLISH_JOB_NAME}' job.`);
  if (publish[0].conclusion !== 'success') fail(`the publish job of run ${sourceRunId} did not succeed.`);
  if (named(DISPATCH_JOB_NAME).length !== 1) fail(`run ${sourceRunId} has no '${DISPATCH_JOB_NAME}' job.`);
  const started = time(publish[0].started_at);
  const completed = time(publish[0].completed_at);
  const submitted = time(review.submitted_at);
  if (started === null || completed === null || submitted === null || submitted < started || submitted > completed) {
    fail(`review ${reviewId} was not submitted while the publish job of run ${sourceRunId} was running.`);
  }
  return { reviewId: String(reviewId), sourceRunId: String(sourceRunId), sourceRunAttempt: String(sourceRunAttempt) };
}

// The workflow writes each API response to <dir>; paginated responses are written with --slurp.
export function readProvenanceInputs(dir) {
  const json = name => JSON.parse(readFileSync(join(dir, name), 'utf8'));
  return {
    request: json('request.json'),
    review: json('review.json'),
    reviewLastEditedAt: json('review-edit.json').lastEditedAt ?? null,
    reviews: json('reviews.json').flat(),
    run: json('run.json'),
    jobs: json('jobs.json').flatMap(page => page.jobs ?? []),
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [command, dir] = process.argv.slice(2);
    if (command !== 'verify' || !dir) throw new Error('Expected: verify <directory>.');
    const result = verifyRepairProvenance(readProvenanceInputs(dir));
    console.error(`::notice::ChatGPT review ${result.reviewId} was posted by chatgpt-review.yml run ${result.sourceRunId} (attempt ${result.sourceRunAttempt}).`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
