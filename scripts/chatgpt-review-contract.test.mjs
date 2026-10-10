// Contract tests for the optional ChatGPT final review (.github/workflows/chatgpt-review.yml).
// Each test mutates the committed workflow and proves the contract rejects the weakened version.
// Run with: node --test scripts/chatgpt-review-contract.test.mjs (also imported by validate-agent-workflows.test.mjs)
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { chatGptReviewPath, readRepositoryFile, runContractChecks, verifyChatGptReview } from './validate-agent-workflows.mjs';

const workflow = readRepositoryFile(chatGptReviewPath);

function replaceOnce(text, from, to) {
  assert.equal(text.split(from).length, 2, `fixture must contain exactly once: ${from}`);
  return text.replace(from, to);
}

function rejects(mutated, pattern) {
  const read = (path) => (path === chatGptReviewPath ? mutated : readRepositoryFile(path));
  assert.throws(() => verifyChatGptReview(read), pattern);
  assert.throws(() => runContractChecks({ read }), pattern);
}

const REVIEW_PERMISSIONS = '      contents: read\n      pull-requests: read\n      issues: read\n    outputs:\n      structured_output:';
const PUBLISH_PERMISSIONS = '    permissions:\n      pull-requests: write\n      statuses: write\n';
const REVIEW_STEPS = '    steps:\n      - name: Collect review inputs\n';

describe('ChatGPT final review contract', () => {
  it('passes for the committed workflow', () => {
    assert.doesNotThrow(() => verifyChatGptReview());
  });

  describe('secret isolation', () => {
    it('rejects the OpenAI key in the publish job', () => {
      rejects(
        replaceOnce(workflow, '          VERDICT_CONTEXT: chatgpt-review-verdict\n', '          VERDICT_CONTEXT: chatgpt-review-verdict\n          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}\n'),
        /OPENAI_API_KEY must be referenced exactly once|publish job: contains forbidden text: OPENAI_API_KEY/,
      );
    });

    it('rejects the OpenAI key in the context job', () => {
      rejects(
        replaceOnce(workflow, '          INPUT_PR_NUMBER: ${{ inputs.pr_number }}\n', '          INPUT_PR_NUMBER: ${{ inputs.pr_number }}\n          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}\n'),
        /OPENAI_API_KEY must be referenced exactly once|context job: contains forbidden text: OPENAI_API_KEY/,
      );
    });

    it('rejects moving the key out of the review job', () => {
      const withoutKey = replaceOnce(workflow, '          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}\n', '');
      rejects(withoutKey, /exactly once/);
    });
  });

  describe('read-only reviewer permissions', () => {
    for (const [from, to] of [
      ['      contents: read\n      pull-requests: read\n      issues: read\n    outputs:', '      contents: write\n      pull-requests: read\n      issues: read\n    outputs:'],
      ['      contents: read\n      pull-requests: read\n      issues: read\n    outputs:', '      contents: read\n      pull-requests: write\n      issues: read\n    outputs:'],
      ['      contents: read\n      pull-requests: read\n      issues: read\n    outputs:', '      contents: read\n      pull-requests: read\n      issues: read\n      statuses: write\n    outputs:'],
    ]) {
      it(`rejects a write grant in the review job (${to.split('\n').find((line) => line.includes('write')).trim()})`, () => {
        assert.ok(workflow.includes(REVIEW_PERMISSIONS));
        rejects(replaceOnce(workflow, from, to), /permissions: contains forbidden text|contains forbidden text: contents: write/);
      });
    }

    it('rejects a write grant in the context job', () => {
      rejects(replaceOnce(workflow, '      pull-requests: read\n      statuses: read\n', '      pull-requests: write\n      statuses: read\n'), /context permissions: contains forbidden text: write/);
    });

    it('limits the publish job to the review and its status', () => {
      rejects(replaceOnce(workflow, PUBLISH_PERMISSIONS, PUBLISH_PERMISSIONS + '      checks: write\n'), /publish permissions: must be exactly/);
      rejects(replaceOnce(workflow, PUBLISH_PERMISSIONS, PUBLISH_PERMISSIONS + '      contents: write\n'), /contains forbidden text: contents: write|publish permissions: must be exactly/);
    });

    it('rejects workflow-wide permissions', () => {
      rejects(replaceOnce(workflow, '\npermissions: {}\n', '\npermissions:\n  contents: read\n'), /triggers: missing section end|missing section/);
    });
  });

  describe('no checkout or execution of pull request code', () => {
    it('rejects a checkout step', () => {
      rejects(
        replaceOnce(workflow, REVIEW_STEPS, '    steps:\n      - uses: actions/checkout@v4\n        with:\n          ref: ${{ needs.context.outputs.head_sha }}\n      - name: Collect review inputs\n'),
        /actions\/checkout|uses:/,
      );
    });

    it('rejects any action at all', () => {
      rejects(replaceOnce(workflow, REVIEW_STEPS, '    steps:\n      - uses: some/action@v1\n      - name: Collect review inputs\n'), /uses:/);
    });

    for (const command of ['gh pr checkout "$PR_NUMBER"', 'git clone "https://github.com/$GITHUB_REPOSITORY"', 'git fetch origin "$HEAD_SHA"']) {
      it(`rejects ${command.split(' ').slice(0, 2).join(' ')}`, () => {
        rejects(replaceOnce(workflow, '          mkdir -p "$inputs"\n', `          mkdir -p "$inputs"\n          ${command}\n`), /contains forbidden text/);
      });
    }

    it('rejects a pull_request trigger, which would run the pull request copy of the workflow', () => {
      rejects(replaceOnce(workflow, '\non:\n', '\non:\n  pull_request:\n    types: [opened]\n'), /triggers: contains forbidden text: +pull_request:/);
    });

    it('rejects reading the review rules from the pull request instead of the workflow commit', () => {
      rejects(replaceOnce(workflow, 'contents/AGENTS.md?ref=$GITHUB_WORKFLOW_SHA', 'contents/AGENTS.md?ref=$HEAD_SHA'), /review job: missing required text: contents\/AGENTS.md/);
    });

    it('rejects approving, requesting changes, labelling or merging', () => {
      rejects(replaceOnce(workflow, 'commit_id: $sha, event: "COMMENT"', 'commit_id: $sha, event: "APPROVE"'), /APPROVE|event: "COMMENT"/);
      rejects(replaceOnce(workflow, '          if [ "$verdict" = "READY FOR HUMAN REVIEW" ]; then\n', '          gh pr merge "$PR_NUMBER" --squash\n          if [ "$verdict" = "READY FOR HUMAN REVIEW" ]; then\n'), /gh pr merge/);
      rejects(replaceOnce(workflow, '          if [ "$verdict" = "READY FOR HUMAN REVIEW" ]; then\n', '          gh pr edit "$PR_NUMBER" --add-label ready\n          if [ "$verdict" = "READY FOR HUMAN REVIEW" ]; then\n'), /gh pr edit|--add-label/);
    });
  });

  describe('exact-SHA guarding', () => {
    it('rejects dropping the stale-label check', () => {
      rejects(replaceOnce(workflow, '[ "$head_sha" = "$EVENT_HEAD_SHA" ] || fail "Refusing stale request', 'true || fail "Refusing stale request'), /Refusing stale request/);
    });

    it('rejects a dispatch from a branch other than main', () => {
      rejects(replaceOnce(workflow, '[ "$GITHUB_REF" = "refs/heads/main" ] || fail', 'true || fail'), /refs\/heads\/main/);
    });

    it('rejects dropping the validation requirement', () => {
      rejects(replaceOnce(workflow, '[ "$(latest agent-validation)" = "success" ] || [ "$(latest merge-validation)" = "success" ]', 'true'), /latest agent-validation/);
    });

    it('rejects dropping the head-moved check while collecting inputs', () => {
      rejects(replaceOnce(workflow, '[ "$current" = "$HEAD_SHA" ] || { echo "::error::Head moved', 'true || { echo "::error::Head moved'), /Head moved/);
    });

    it('rejects dropping the head recheck or the output SHA binding before publication', () => {
      rejects(replaceOnce(workflow, '[ "$current_sha" = "$HEAD_SHA" ] || suppress', 'true || suppress'), /current_sha/);
      rejects(replaceOnce(workflow, `[ "$(jq -r '.reviewed_head_sha // ""' "$work/review.json")" = "$HEAD_SHA" ] || suppress`, 'true || suppress'), /reviewed_head_sha/);
    });

    it('rejects a review not bound to the reviewed commit', () => {
      rejects(replaceOnce(workflow, 'commit_id: $sha, event: "COMMENT"', 'event: "COMMENT"'), /commit_id/);
    });
  });

  describe('agent review must pass before ChatGPT reviews', () => {
    it('rejects treating a missing agent verdict as passed', () => {
      rejects(
        replaceOnce(workflow, '            [ "$agent_verdict" = "success" ] \\\n              || fail', '            [ -z "$agent_verdict" ] || [ "$agent_verdict" = "success" ] \\\n              || fail'),
        /\[ -z "\$agent_verdict" \]/,
      );
    });

    it('rejects dropping the agent pull request gate in the context job', () => {
      rejects(
        replaceOnce(workflow, '          if [[ "$head_ref" == agent/issue-* ]] || jq -e \'any(.labels[]?; .name == "agent-review")\' <<<"$pr_json" >/dev/null; then\n            agent_verdict="$(latest', '          if false; then\n            agent_verdict="$(latest'),
        /context job: missing required text: if \[\[ "\$head_ref" == agent\/issue-\*/,
      );
    });

    it('rejects narrowing the gate to agent branches only', () => {
      rejects(
        replaceOnce(workflow, '          if [[ "$head_ref" == agent/issue-* ]] || jq -e \'any(.labels[]?; .name == "agent-review")\' <<<"$pr_json" >/dev/null; then\n            agent_verdict="$(latest', '          if [[ "$head_ref" == agent/issue-* ]]; then\n            agent_verdict="$(latest'),
        /context job: missing required text/,
      );
    });

    it('rejects dropping the agent verdict recheck at publication', () => {
      rejects(replaceOnce(workflow, '[ "$agent_verdict" = "success" ] || suppress', 'true || suppress'), /publish job: missing required text: \[ "\$agent_verdict" = "success" \] \|\| suppress/);
    });
  });
});
