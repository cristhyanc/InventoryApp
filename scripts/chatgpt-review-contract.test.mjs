// Contract tests for the optional ChatGPT final review (.github/workflows/chatgpt-review.yml).
// Each test mutates the committed workflow and proves the contract rejects the weakened version.
// Run with: node --test scripts/chatgpt-review-contract.test.mjs (also imported by validate-agent-workflows.test.mjs)
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { chatGptReviewPath, readRepositoryFile, repairPath, runContractChecks, verifyChatGptReview } from './validate-agent-workflows.mjs';

const workflow = readRepositoryFile(chatGptReviewPath);
const repairWorkflow = readRepositoryFile(repairPath);

function replaceOnce(text, from, to) {
  assert.equal(text.split(from).length, 2, `fixture must contain exactly once: ${from}`);
  return text.replace(from, to);
}

function rejects(mutated, pattern, path = chatGptReviewPath) {
  const read = (candidate) => (candidate === path ? mutated : readRepositoryFile(candidate));
  assert.throws(() => verifyChatGptReview(read), pattern);
  assert.throws(() => runContractChecks({ read }), pattern);
}

const rejectsRepair = (mutated, pattern) => rejects(mutated, pattern, repairPath);

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

  describe('accurate fail-closed outcome', () => {
    it('rejects dropping the posted-review marker', () => {
      rejects(replaceOnce(workflow, '          echo "review_posted=true" >> "$GITHUB_OUTPUT"\n', ''), /review_posted=true/);
    });

    it('rejects recording publication before the review is posted', () => {
      const moved = replaceOnce(
        replaceOnce(workflow, '          echo "review_posted=true" >> "$GITHUB_OUTPUT"\n', ''),
        '          if ! review_id="$(gh api --method POST "repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER/reviews"',
        '          echo "review_posted=true" >> "$GITHUB_OUTPUT"\n          if ! review_id="$(gh api --method POST "repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER/reviews"',
      );
      rejects(moved, /publication must be recorded only after the review was posted/);
    });

    it('rejects a failure handler that always says nothing was published', () => {
      rejects(replaceOnce(workflow, '          REVIEW_POSTED: ${{ steps.publish.outputs.review_posted }}\n', ''), /REVIEW_POSTED/);
      rejects(replaceOnce(workflow, '          if [ "$REVIEW_POSTED" = "true" ]; then\n', '          if false; then\n'), /REVIEW_POSTED/);
    });
  });

  describe('repair hand-off after a changes-requested review', () => {
    it('rejects dropping the @claude repair prefix', () => {
      rejects(replaceOnce(workflow, '"@claude repair Fix the blockers listed in this ChatGPT review.\\n\\n"', '""'), /publish job: missing required text: \(if \.verdict/);
    });

    it('rejects requesting a repair for a ready verdict or a non-agent branch', () => {
      rejects(replaceOnce(workflow, 'if [ "$verdict" = "CHANGES REQUESTED" ] && [[ "$head_ref" == agent/issue-* ]]', 'if [[ "$head_ref" == agent/issue-* ]]'), /publish job: missing required text: if \[ "\$verdict"/);
      rejects(replaceOnce(workflow, 'if [ "$verdict" = "CHANGES REQUESTED" ] && [[ "$head_ref" == agent/issue-* ]]', 'if [ "$verdict" = "CHANGES REQUESTED" ]'), /publish job: missing required text: if \[ "\$verdict"/);
    });

    it('rejects widening the dispatcher permissions', () => {
      rejects(replaceOnce(workflow, '      actions: write\n      pull-requests: read\n', '      actions: write\n      pull-requests: write\n'), /repair dispatcher permissions: must be exactly/);
      rejects(replaceOnce(workflow, '      actions: write\n      pull-requests: read\n', '      actions: write\n      pull-requests: read\n      contents: write\n'), /repair dispatcher permissions: must be exactly|contents: write/);
    });

    it('rejects dispatching from another job or another workflow', () => {
      rejects(replaceOnce(workflow, '          echo "repair=true" >> "$GITHUB_OUTPUT"\n', '          echo "repair=true" >> "$GITHUB_OUTPUT"\n          gh workflow run agent-repair.yml --ref main\n'), /contains forbidden text: gh workflow/);
      rejects(replaceOnce(workflow, 'gh workflow run agent-repair.yml \\', 'gh workflow run agent-implement.yml \\'), /repair dispatcher: missing required text: gh workflow run agent-repair.yml/);
      rejects(replaceOnce(workflow, '            --ref main \\\n            -f pr_number="$PR_NUMBER" \\\n            -f head_sha="$HEAD_SHA" \\\n            -f review_id', '            --ref "$GITHUB_HEAD_REF" \\\n            -f pr_number="$PR_NUMBER" \\\n            -f head_sha="$HEAD_SHA" \\\n            -f review_id'), /repair dispatcher: missing required text/);
    });

    it('rejects dropping the dispatcher head recheck or the OpenAI key leaking into it', () => {
      rejects(replaceOnce(workflow, '[ "$(jq -r \'.headRefOid\' <<<"$pr_json")" = "$HEAD_SHA" ] || fail "Refusing stale repair dispatch', 'true || fail "Refusing stale repair dispatch'), /repair dispatcher: missing required text/);
      rejects(replaceOnce(workflow, '          REVIEW_ID: ${{ needs.publish.outputs.review_id }}\n', '          REVIEW_ID: ${{ needs.publish.outputs.review_id }}\n          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}\n'), /exactly once|OPENAI_API_KEY/);
    });
  });

  describe('agent-repair.yml accepts only a verified ChatGPT repair request', () => {
    it('rejects a dispatched repair that may run from a branch other than main', () => {
      rejectsRepair(replaceOnce(repairWorkflow, "(github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main')", "(github.event_name == 'workflow_dispatch')"), /ChatGPT repair intake: missing required text/);
      rejectsRepair(replaceOnce(repairWorkflow, '[ "$GITHUB_REF" = "refs/heads/main" ] || fail "A dispatched repair must run from main', 'true || fail "A dispatched repair must run from main'), /ChatGPT repair intake: missing required text/);
    });

    it('keeps the human comment path owner-only', () => {
      rejectsRepair(replaceOnce(repairWorkflow, '      github.event.comment.user.login == github.repository_owner &&\n', ''), /ChatGPT repair intake: missing required text: github.event.comment.user.login == github.repository_owner/);
    });

    for (const [name, guard] of [
      ['the stale-head check', '[ "$start_sha" = "$INPUT_HEAD_SHA" ] || fail "Refusing stale repair'],
      ['the review author check', '[ "$(jq -r \'.user.login\' <<<"$review_json")" = "github-actions[bot]" ] || fail'],
      ['the review commit check', '[ "$(jq -r \'.commit_id\' <<<"$review_json")" = "$INPUT_HEAD_SHA" ] || fail'],
      ['the @claude repair check', '[[ "$request" == "@claude repair "* ]] || fail'],
      ['the ChatGPT reviewer check', "grep -q '^Reviewer: ChatGPT ' <<<\"$request\" || fail"],
      ['the changes-requested check', "grep -q '^VERDICT: CHANGES REQUESTED$' <<<\"$request\" || fail"],
      ['the two-repair limit', '[ "$repair_requests" -le 2 ] || fail "Repair limit reached'],
      ['counting the owner\'s repair comments', 'repair_requests=$((human_requests + automatic_requests))'],
      ['the agent-review label check', 'jq -e \'any(.labels[]?; .name == "agent-review")\' <<<"$pr_json" >/dev/null || fail "The agent-review label is required for a repair."'],
    ]) {
      it(`rejects dropping ${name}`, () => {
        rejectsRepair(replaceOnce(repairWorkflow, guard, 'true'), /ChatGPT repair intake: missing required text/);
      });
    }

    it('rejects passing the raw comment body to the repair agent', () => {
      rejectsRepair(replaceOnce(repairWorkflow, '            ${{ steps.pr.outputs.request }}', '            ${{ github.event.comment.body }}'), /ChatGPT repair intake: (missing required text|contains forbidden text)/);
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
