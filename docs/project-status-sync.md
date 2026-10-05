# Project status sync

Project [Multitenant, #4](https://github.com/users/cristhyanc/projects/4/) displays
InventoryApp's agent lifecycle. `project-status-sync.yml` reconciles **existing,
unarchived issue items from cristhyanc/InventoryApp only**. It does not add cards,
edit labels, close/reopen issues, start agents, request repairs, approve, merge or
deploy. Priority, estimates, iteration, dates and card order remain untouched.
Manual cards without agent lifecycle evidence keep their status. Native parent
issues and checklist epic #383 remain manual even if they acquire agent labels.

## Mapping and precedence

| Live state | Project Status |
| --- | --- |
| One readiness label, without conflicting active labels | Ready |
| `agent-working` or `agent-architecture-fix` | In progress |
| Open agent PR, validation/review pending | In review |
| Current-head `agent-review-verdict` failure/error | Changes requested |
| `agent-blocked`, invalid label combination, multiple open implementation PRs, or validation failure | Blocked |
| Current-head `agent-validation`, `merge-validation` and `agent-review-verdict` all success | Awaiting approval |
| Implementation PR merged into develop, with no other open implementation PR | Done |
| Managed issue closed as completed | Done |
| Reopened issue with only older merged work | Backlog |

An explicit blocked label wins over open/merged PR evidence. A fresh readiness
label wins over a previous merged PR. Draft PRs stay In progress. Closed but
unmerged PRs do not mean Done. Issues closed as not planned are left manual.
Done means implementation completed, **not production deployment**.

Linked PRs must be same-repository agent branches targeting develop. The exact
`agent/issue-N-` branch prefix also identifies Claude PRs when closing-keyword
links are absent on a non-default base. Copilot PRs require GitHub's explicit
closing-issue association. Human PRs are left to human planning. This display
mapping does not replace the pipeline's provider-mode verification or authorize
any action. Label history remains the authority for starting agents.

## Board setup

The Status field needs Backlog, Ready, In progress, In review, Changes requested,
Blocked, Awaiting approval and Done. The three additional options were added
through Project settings on 5 October 2026.

Disable these built-in workflows before activation (disabled on 5 October 2026):

- **Auto-close issue**: moving a card to Done must not close an issue.
- **Item closed**: a rejected/abandoned PR must not become Done merely on closure.

The existing add-sub-issues, new-item → Backlog, PR-linked → In progress and
PR-merged → Done rules can remain enabled. Reconciliation corrects intermediate
statuses for managed issue cards. Keep the built-in code-review approval/change
request rules disabled: the agent pipeline publishes comment-only reviews and
an exact-SHA commit status, not a formal approval/request-changes review.

## Credentials and activation

This is a **user-owned** Project. The repository GITHUB_TOKEN cannot read/write
Projects. A human must configure a dedicated `PROJECT_SYNC_TOKEN` repository
Actions secret with access to this Project (classic PAT `project` scope supports
user Projects). Do not reuse a production or agent implementation credential.
The token also needs to see the Project's issue item metadata; for this public
repository no private-repository scope is required. Repository issue, PR and
status reads use the separate, read-only GITHUB_TOKEN. Token scopes can exceed
one Project; code restricts writes to this exact owner/number and Status field.
Never paste the token in an issue, PR, log or chat.

1. Review and merge the PR into develop, then release the workflow to the default
   main branch through the normal human-controlled process. No app deployment is
   needed for this automation. Event/scheduled workflows are not activated by
   merely opening this PR or merging into develop.
2. Configure `PROJECT_SYNC_TOKEN` in repository Actions secrets.
3. Run **Project status sync** from main with **apply=false** and inspect the
   preview job summary. Manual runs do not require the enable variable.
4. Set repository Actions variable `PROJECT_SYNC_ENABLED=true` to enable automatic
   updates. Run manually with **apply=true** for the first reconciliation.
5. Confirm a labelled issue already in Project #4 moves as expected and verify
   labels, issue state and unrelated fields are unchanged.

The code fails visibly on missing credentials, missing/duplicate status options,
API permission errors or incomplete bounded connections; it does not fabricate
a successful sync. Setting PROJECT_SYNC_ENABLED=false stops automatic updates.
Revoking the dedicated token also removes access.

## Triggering, races and validation

Issue/PR events and completed workflow runs reconcile the board. A schedule every
15 minutes catches missed events, new Project membership, and transitions made
using GITHUB_TOKEN (which normally do not trigger downstream event workflows).
Scheduling is best-effort, not a 15-minute SLA. The sync ignores its own workflow
completion to prevent a loop. One concurrency group serializes all its runs.

It executes only the trusted workflow commit; it never checks out PR heads,
downloads workflow artifacts or executes issue/PR text with the Project token.
Manual runs from branches other than main are refused. No npm dependencies or
model calls are used. The only GraphQL mutation updates one item's Status.

Project items and REST collections are paginated. Oversized labels/linked-PR/field
connections fail rather than silently truncate. Status contexts are read from
the current head SHA, newest-first, never from the triggering event's SHA.
Immediately before an update the full decision and current board value are read
again; any change defers that item. GitHub's mutation has no compare-and-swap
precondition, so there remains a small race after that re-read; subsequent runs
converge. This is a display, never a merge/security gate. Partial successes are
safe to rerun: unchanged items produce no writes.

Tests: `node --test scripts/sync-project-status.test.mjs`. Included in both full
validation scripts. Tests use fixtures and no real credentials or Project writes.
API reference: [Projects automation](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/automating-projects-using-actions)
and [Projects GraphQL](https://docs.github.com/en/graphql/reference/projects).
