// Regression tests for the provenance check agent-repair.yml runs before it accepts an automatic repair
// requested by a ChatGPT review. A review that matches every text check but was not posted by the trusted
// chatgpt-review.yml run must never authorise a repair.
// Run with: node --test scripts/chatgpt-repair-provenance.test.mjs (also imported by validate-agent-workflows.test.mjs)
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, it } from 'node:test';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import {
  DISPATCH_JOB_NAME,
  PUBLISH_JOB_NAME,
  provenanceMarker,
  readProvenanceInputs,
  verifyRepairProvenance,
} from './chatgpt-repair-provenance.mjs';

const REPOSITORY = 'cristhyanc/InventoryApp';
const HEAD_SHA = 'a'.repeat(40);
const RUN_ID = '9001';
const ATTEMPT = '1';
const REVIEW_ID = '555';
const MARKER = provenanceMarker({ runId: RUN_ID, runAttempt: ATTEMPT, prNumber: '42', headSha: HEAD_SHA });
const BODY = [
  '@claude repair Fix the blockers listed in this ChatGPT review.',
  '',
  'Reviewer: ChatGPT (OpenAI gpt-6.1-sol), final advisory review',
  `Head SHA reviewed: ${HEAD_SHA}`,
  '',
  'VERDICT: CHANGES REQUESTED',
  '',
  'Blockers:',
  '- something',
  '',
  'This is an advisory review. Human approval and branch protection remain the merge gate.',
  '',
  MARKER,
].join('\n');

function genuine() {
  const review = {
    id: Number(REVIEW_ID),
    user: { login: 'github-actions[bot]', type: 'Bot' },
    commit_id: HEAD_SHA,
    state: 'COMMENTED',
    body: BODY,
    submitted_at: '2026-10-10T10:00:30Z',
  };
  return {
    request: { repository: REPOSITORY, prNumber: '42', headSha: HEAD_SHA, reviewId: REVIEW_ID, sourceRunId: RUN_ID, sourceRunAttempt: ATTEMPT },
    review,
    reviewLastEditedAt: null,
    reviews: [{ id: 400, body: 'An earlier human review.' }, review],
    run: {
      id: Number(RUN_ID),
      run_attempt: Number(ATTEMPT),
      path: '.github/workflows/chatgpt-review.yml',
      event: 'pull_request_target',
      head_branch: 'agent/issue-1-thing',
      repository: { full_name: REPOSITORY },
      head_repository: { full_name: REPOSITORY },
    },
    jobs: [
      { name: 'Resolve review context', conclusion: 'success', started_at: '2026-10-10T09:58:00Z', completed_at: '2026-10-10T09:58:10Z' },
      { name: PUBLISH_JOB_NAME, conclusion: 'success', started_at: '2026-10-10T10:00:00Z', completed_at: '2026-10-10T10:00:45Z' },
      { name: DISPATCH_JOB_NAME, conclusion: null, started_at: '2026-10-10T10:00:50Z', completed_at: null },
    ],
  };
}

function mutate(change) {
  const input = genuine();
  change(input);
  return input;
}

const refused = (input, pattern) => assert.throws(() => verifyRepairProvenance(input), pattern);

describe('ChatGPT repair provenance', () => {
  it('accepts the review posted by the trusted chatgpt-review.yml run', () => {
    assert.deepEqual(verifyRepairProvenance(genuine()), { reviewId: REVIEW_ID, sourceRunId: RUN_ID, sourceRunAttempt: ATTEMPT });
  });

  it('accepts a run dispatched from main', () => {
    verifyRepairProvenance(mutate(input => Object.assign(input.run, { event: 'workflow_dispatch', head_branch: 'main' })));
  });

  describe('a matching review from another workflow cannot authorise a repair', () => {
    it('refuses a run of another workflow, even with a matching review body', () => {
      refused(mutate(input => { input.run.path = '.github/workflows/other-automation.yml'; }), /is \.github\/workflows\/other-automation\.yml, not \.github\/workflows\/chatgpt-review\.yml/);
    });

    it('refuses a forged review posted by the bot outside the trusted run', () => {
      // Same bot, same text, same provenance line, but submitted after the publish job finished.
      refused(mutate(input => { input.review.submitted_at = '2026-10-10T10:05:00Z'; }), /was not submitted while the publish job of run 9001 was running/);
      refused(mutate(input => { input.review.submitted_at = '2026-10-10T09:59:59Z'; }), /was not submitted while the publish job/);
    });

    it('refuses a second review that claims the trusted run', () => {
      refused(
        mutate(input => { input.reviews.push({ id: 556, body: BODY }); }),
        /run 9001 is claimed by reviews 555, 556, not only by review 555/,
      );
      // A forged review that copies the genuine one's provenance line is refused in either position.
      refused(
        mutate(input => {
          const forged = { ...input.review, id: 556 };
          input.reviews.push(forged);
          input.review = forged;
          input.request.reviewId = '556';
        }),
        /claimed by reviews 555, 556, not only by review 556/,
      );
    });

    it('refuses a review whose provenance line is missing, wrong or duplicated', () => {
      refused(mutate(input => { input.review.body = BODY.replace(MARKER, ''); input.reviews[1] = input.review; }), /does not carry exactly one provenance line/);
      refused(mutate(input => { input.review.body = BODY.replace('run_id=9001', 'run_id=9002'); }), /does not carry exactly one provenance line/);
      refused(mutate(input => { input.review.body = BODY.replace(`head_sha=${HEAD_SHA}`, `head_sha=${'b'.repeat(40)}`); }), /does not carry exactly one provenance line/);
      refused(mutate(input => { input.review.body = BODY.replace('pr=42', 'pr=43'); }), /does not carry exactly one provenance line/);
      refused(mutate(input => { input.review.body = `${BODY}\n${MARKER}`; }), /does not carry exactly one provenance line/);
    });

    it('refuses a review edited after it was posted', () => {
      refused(mutate(input => { input.reviewLastEditedAt = '2026-10-10T10:03:00Z'; }), /was edited after it was posted/);
    });

    it('refuses a run whose publish job did not succeed or that has no repair dispatcher', () => {
      refused(mutate(input => { input.jobs[1].conclusion = 'failure'; }), /publish job of run 9001 did not succeed/);
      refused(mutate(input => { input.jobs.splice(1, 1); }), /has no single 'Guarded review publication' job/);
      refused(mutate(input => { input.jobs.splice(2, 1); }), /has no 'Dispatch agent repair for a changes-requested review' job/);
    });

    it('refuses an untrusted trigger, an untrusted ref or another repository', () => {
      refused(mutate(input => { input.run.event = 'pull_request'; }), /was started by 'pull_request'/);
      refused(mutate(input => { input.run.event = 'workflow_run'; }), /was started by 'workflow_run'/);
      refused(mutate(input => Object.assign(input.run, { event: 'workflow_dispatch', head_branch: 'agent/issue-1-thing' })), /was dispatched from 'agent\/issue-1-thing', not main/);
      refused(mutate(input => { input.run.head_repository.full_name = 'someone/fork'; }), /does not belong to cristhyanc\/InventoryApp/);
    });

    it('refuses a dispatch that names a different run or attempt than the API returned', () => {
      refused(mutate(input => { input.run.id = 9002; }), /run 9001 attempt 1 could not be read/);
      refused(mutate(input => { input.run.run_attempt = 2; }), /run 9001 attempt 1 could not be read/);
      refused(mutate(input => { input.request.sourceRunId = ''; }), /must name the ChatGPT review run and attempt/);
    });
  });

  it('keeps the review checks: author, exact SHA and comment-only', () => {
    refused(mutate(input => { input.review.user = { login: 'someone', type: 'User' }; }), /was not posted by github-actions\[bot\]/);
    refused(mutate(input => { input.review.user.type = 'User'; }), /was not posted by github-actions\[bot\]/);
    refused(mutate(input => { input.review.commit_id = 'b'.repeat(40); }), /is not bound to a{40}/);
    refused(mutate(input => { input.review.state = 'APPROVED'; }), /is not a comment-only review/);
    refused(mutate(input => { input.request.reviewId = '777'; }), /review 777 could not be read/);
  });

  it('reads the workflow API responses and fails the command line closed', () => {
    const dir = mkdtempSync(join(tmpdir(), 'chatgpt-provenance-'));
    const script = fileURLToPath(new URL('./chatgpt-repair-provenance.mjs', import.meta.url));
    try {
      const input = genuine();
      writeFileSync(join(dir, 'request.json'), JSON.stringify(input.request));
      writeFileSync(join(dir, 'review.json'), JSON.stringify(input.review));
      writeFileSync(join(dir, 'review-edit.json'), JSON.stringify({ lastEditedAt: null }));
      // gh api --paginate --slurp: an array of pages; the jobs endpoint pages are objects.
      writeFileSync(join(dir, 'reviews.json'), JSON.stringify([input.reviews]));
      writeFileSync(join(dir, 'run.json'), JSON.stringify(input.run));
      writeFileSync(join(dir, 'jobs.json'), JSON.stringify([{ total_count: 3, jobs: input.jobs }]));
      assert.deepEqual(readProvenanceInputs(dir), input);
      execFileSync(process.execPath, [script, 'verify', dir], { stdio: 'pipe' });

      writeFileSync(join(dir, 'run.json'), JSON.stringify({ ...input.run, path: '.github/workflows/other-automation.yml' }));
      assert.throws(() => execFileSync(process.execPath, [script, 'verify', dir], { stdio: 'pipe' }), /other-automation\.yml/);
      assert.throws(() => execFileSync(process.execPath, [script, 'verify'], { stdio: 'pipe' }), /Expected: verify <directory>/);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
