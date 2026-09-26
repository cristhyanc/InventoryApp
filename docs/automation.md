# InventoryApp automated development lifecycle

## Purpose

This document defines how InventoryApp intends to run a controlled, automated software-development system:

```text
GitHub issue → implementation agent → feature branch → pull request → architecture pass
→ CI validation → independent review → human approval → develop → release PR → main → Azure deployment → monitoring
```

It describes the lifecycle, the authority of each participant, task states, risk classification, failure handling, traceability, branch policy, and the incremental phases in which the system will be built.

It deliberately separates three things:

| Category | Meaning |
| --- | --- |
| **Exists now** | Behaviour implemented by files in this repository today: the validation and deployment workflows, the Claude Code implementation (including architecture pass), review, and repair workflows (`agent-implement.yml`, `agent-review.yml`, `agent-repair.yml`), the validation scripts, the workflow-contract and documentation-impact validators in `scripts/`, `AGENTS.md`, `CLAUDE.md`, the agent task issue form, the pull request template, and the [documentation impact gate](#documentation-impact-gate). |
| **Proposed for future pull requests** | Automation that is designed here but **not implemented**: mechanical enforcement of the two-attempt repair limit, staging, release automation, monitoring, and any auto-merge. |
| **Human-controlled** | Decisions that stay with a human regardless of how much automation is added: applying `agent-ready`, authorising and counting repair attempts (and the fresh review that follows one), approving, merging, releasing, deploying. The *initial* independent review is requested automatically once validation succeeds; it is no longer a separate human decision. |

Nothing in this document creates an automation capability by itself. Where a capability is described as future work, it does not exist until a later pull request implements it and this document is updated.

The agent provider is **Claude Code**, run through the `anthropics/claude-code-action` GitHub Action. `CLAUDE.md` at the repository root directs Claude to read and obey `AGENTS.md`, this document, `docs/architecture.md` where relevant, and the linked issue's acceptance criteria and exclusions.

`AGENTS.md` is the authoritative engineering and safety policy, and `docs/architecture.md` is the authoritative architectural description. This document does not restate their financial, inventory, database, Nayax, or security invariants; it refers to them.

## Desired lifecycle

The target lifecycle for one automated change is:

1. A human creates or refines an agent task issue using the **Agent task** issue form, including its documentation impact decision and details.
2. A human confirms that the acceptance criteria are complete, testable, and bounded by explicit exclusions, and that the documentation impact decision is correct.
3. A human applies the `agent-ready` label. A read-only preflight job validates that the issue carries a meaningful documentation impact declaration; when it does not, nothing else runs and the issue is left unchanged.
4. An implementation agent creates a feature branch from the latest `develop`.
5. The agent implements only the approved scope defined by the issue's acceptance criteria and exclusions, and updates the documentation the issue's decision requires.
6. The agent runs the complete repository validation (`scripts/validate.sh` or `scripts/validate.ps1`).
7. The agent opens a pull request targeting `develop`, using the pull request template, with an accurate `## Documentation impact` declaration. A separate Claude architecture invocation reviews the PR and may commit and push narrowly scoped, behavior-preserving structural edits on the same feature branch. The architecture stage must finish successfully before validation is dispatched.
8. CI independently validates the pull request, starting with the documentation impact declaration.
9. A separate reviewer (an independent review agent, then a human) evaluates requirements, architecture, security, tests, validation evidence, scope, and whether the documentation impact declaration matches the issue and the diff.
10. If validation or review fails, a human may request a repair; the implementation agent may make **no more than two** repair attempts per pull request.
11. If the pull request still fails, or the requirements are materially ambiguous or conflicting, the task becomes blocked and returns to a human with a precise statement of the decision needed.
12. A human decides whether to merge into `develop`.
13. A separate release pull request from `develop` to `main` controls production deployment.
14. Production deployment and any production migration remain human-controlled.

Today: steps 1–3 are human, except that the preflight in step 3 is the deterministic first job of `agent-implement.yml`. Steps 4–7 are performed by the Claude Code implementation workflow (`agent-implement.yml`), including its second architecture invocation after the coder opens a PR when a human applies `agent-ready` and the preflight passes. For step 8, the implementation workflow's separate dispatcher job verifies the resulting PR, applies the `agent-review` label to it as a deterministic step (not by Claude itself, which is never granted `gh pr edit`/`gh label`), and invokes the trusted `validate.yml` from `main` for the exact current head SHA with `dispatch_review: true`; validation publishes the stable `agent-validation` commit status. For step 9, the initial Claude Code review is then dispatched automatically once `agent-validation` succeeds; no human action requests it. Step 10 exists only as a human-invoked repair (`agent-repair.yml`, started by an `@claude repair` comment); the two-attempt limit is counted by the human, not by a workflow. When a repair actually pushes a new head, its separate dispatcher invokes exact-SHA validation, and successful validation dispatches a fresh review while the `agent-review` label (applied automatically at step 8, or by a human if ever reapplied) remains present. Step 11 is partly automatic (the implementation workflow labels the issue `agent-blocked` when implementation or architecture cannot complete with a verified PR head) and otherwise human. Steps 12–14 are human.

## Current repository behaviour

This section is derived from the triggers, conditions, and jobs in `.github/workflows/` and from `scripts/`, as inspected when this document was written. If a workflow changes, update this section in the same pull request.

### Pull-request validation

`.github/workflows/validate.yml` ("Validate pull request"):

- Triggers on `pull_request` events of type `opened`, `synchronize`, `reopened` and `edited` whose base branch is `develop` or `main` (`edited` is included so that editing the pull request description reruns the documentation impact gate), and on `workflow_dispatch` with required `pr_number` and `head_sha` inputs plus an optional `dispatch_review` boolean.
- Runs in one of two **validation modes**, which never share a concurrency group or a commit-status context (see [Validation modes](#validation-modes)):
  - For a normal `pull_request` event, preserves **merge-result validation**: it checks out the event's merge commit and publishes the `merge-validation` commit status on same-repository PR heads.
  - For a `workflow_dispatch`, performs **exact-SHA validation**: the trusted workflow definition runs from `main`, verifies that the PR is open, non-draft, targets `develop`, is authored by `github-actions[bot]`, has a same-repository `agent/issue-*` branch, still has the supplied exact head SHA, and does not change `.github/workflows/**`; it then checks out only that SHA and publishes the authoritative `agent-validation` commit status.
- Each status is linked to its run and is `pending` before validation and `success` or `failure` afterward. The context job resolves the status context once, from `github.event_name`, and refuses to continue if the mode and the context disagree; the status job publishes only that resolved context.
- The context job also obtains the pull request body in both modes (from the `pull_request` event payload, or with `gh pr view --json body` for a dispatch) and passes it to the validation job as a base64-encoded job output. The body is untrusted text: it travels through an environment variable, never through an expression in a `run:` script, and no GitHub token is given to the job that checks out pull request code merely to retrieve it.
- Runs **"Backend tests and frontend build"** in a separate job with only `contents: read`, `persist-credentials: false`, no GitHub token, and no Anthropic or deployment secret. It installs .NET 10 and Node.js 20, decodes the body into a file under `$RUNNER_TEMP` (outside the checkout) and runs `node scripts/validate-documentation-impact.mjs --pr-body` on it **before** anything else, so an invalid `## Documentation impact` declaration fails the job and therefore the normal `merge-validation` or `agent-validation` status; then runs `node scripts/validate-agent-workflows.mjs` to enforce the dispatcher/permission/guard/concurrency/status/documentation-gate contract, `node --test scripts/validate-agent-workflows.test.mjs` and `node --test scripts/validate-documentation-impact.test.mjs` (the deterministic contract and parser tests), and then `bash scripts/validate.sh`.
- `scripts/validate.sh` and `scripts/validate.ps1` perform the same steps: `dotnet restore`, `dotnet build --configuration Release`, `dotnet test`, `npm ci`, and `npm run build` for the Angular application. The frontend has no configured test or lint script; its gate is the production build.
- When a dispatched repair validation succeeds with `dispatch_review: true`, a separate job with `actions: write` and no checkout reverifies the current SHA and guards, requires `agent-review`, and dispatches `agent-review.yml` from `main`.
- Concurrency is scoped by validation mode **and** PR number (`validation-<workflow>-<event_name>-<pr_number>`): a newer run of the same mode for the same PR cancels the superseded one, while `pull_request` and `workflow_dispatch` validation of the same PR run independently and cannot cancel each other, and another PR's validation is never affected.
- Does not approve, merge, release, deploy, change labels, or start a repair.

### Push to `develop`

`.github/workflows/vm-manager.yml` ("Build and deploy .NET application to Azure Web App vm-manager") triggers on `push` to `develop` and to `main`.

On a push to `develop`:

- The `build` job restores, builds, and tests `backend/InventoryApi.Tests` in Release configuration.
- The `Publish` and `Publish Artifacts` steps are skipped because they are conditioned on `github.ref == 'refs/heads/main'`.
- The `deploy` job is skipped for the same reason.
- No workflow builds or deploys the frontend on a push to `develop`.

A push to `develop` therefore runs backend build and tests but **does not deploy** the API or the frontend.

### Push or merge to `main`

A merge of a pull request into `main` is a push to `main` and triggers two deployment workflows:

- `.github/workflows/vm-manager.yml`: the `build` job restores, builds, tests, publishes the API, and uploads the package; the `deploy` job then logs in to Azure and deploys the package to the Azure Web App `vm-manager`. The `deploy` job runs against the GitHub environment `VmInventoryApi_Env`. Whether that environment requires a reviewer is a repository setting, not a repository file, and is not verified by this document.
- `.github/workflows/azure-static-web-apps-red-island-0c128c000.yml` ("Azure Static Web Apps CI/CD"): triggers only on `push` to `main`, builds `frontend/inventory-app`, and deploys `dist/inventory-app/browser` to Azure Static Web Apps. The workflow also contains a `close_pull_request_job`, but because the workflow has no `pull_request` trigger that job never runs.

Deploying the API does not change its database schema (issue #54). **Production never migrates automatically, whatever the configuration says**: `DatabaseSchemaStartup` applies nothing there and fails closed with a `PendingMigrationsException` naming the pending migrations if the deployed code expects a schema the database does not have. Development and `Testing` do migrate automatically, where the database is disposable. A non-Production environment that is neither of those migrates automatically only when `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` is `true` — an opt-in for an ephemeral integration or staging database, checked only after Production has already been ruled out by environment name — and otherwise fails closed the same way. A human applies a pending schema change separately, before or after the deploy, with the explicit `migrate-database` command shipped inside the published application (`dotnet InventoryApi.dll migrate-database --dry-run`, then `--apply`, after taking a verified backup — see `docs/tenant-rollout.md`). Recovery from a failed or unwanted apply is a database restore from that backup, not a further automated migration: `migrate-database` never rolls back a migration, and re-running `--apply` only applies what is still pending.

### Implementation workflow

`.github/workflows/agent-implement.yml` ("Agent implementation"):

- Triggers only on `issues` / `labeled`, and its jobs run only when the added label is `agent-ready` (and the entity is an issue, not a pull request). No other event, comment, mention, or label starts it. Like every `issues`-triggered workflow, GitHub runs the copy on the default branch (`main`).
- A separate **`preflight`** job runs first with only `contents: read` and `issues: read`. It checks out trusted `develop` with `persist-credentials: false`, reads the issue body with `gh issue view --json body`, and runs `node scripts/validate-documentation-impact.mjs --issue-body` on it. It has no Anthropic credential, runs no Claude step, creates no branch, and changes no label or comment. When it fails, it emits an `::error` annotation stating exactly what to fix (edit the issue's documentation impact decision/details, then remove and re-apply `agent-ready`), and the implementation job is skipped because it declares `needs: preflight` and `if: needs.preflight.result == 'success' && ...`. The check is deliberately not a step inside the implementation job: that job's always-running outcome step would otherwise relabel the issue `agent-blocked` after a preflight failure.
- The implementation job permissions remain `contents: write`, `pull-requests: write`, and `issues: write`. It has no `actions: write` and cannot dispatch a workflow.
- Checks out `develop` at its latest commit, installs .NET 10 and Node.js 20, and moves the issue from `agent-ready` to `agent-working`.
- Runs Claude Code with a fixed prompt that requires it to read the issue, restate the acceptance criteria, exclusions and documentation impact decision/details, create `agent/issue-<n>-<slug>` from the checked-out `develop`, make the smallest change with tests, follow the issue's documentation impact decision (update every listed documentation file, and any documentation the change would otherwise contradict), run `bash scripts/validate.sh` synchronously in the foreground and wait for its exit result, push that branch only, open one pull request targeting `develop`, and verify that the open PR head matches the pushed local HEAD before finishing using the pull request template with an accurate `## Documentation impact` section (`Decision: UPDATED` or `Decision: NOT REQUIRED` plus specific evidence), checked with `node scripts/validate-documentation-impact.mjs --pr-body` before creation. Tool permissions enforce the same limits: file edits, `dotnet`, `npm --prefix frontend/inventory-app`, `bash scripts/validate.sh`, `node scripts/validate-documentation-impact.mjs --pr-body`, read-only `git`, scoped `git rm --` for tracked files in `backend/`, `frontend/`, `docs/` and `scripts/` (never `.github/`), `git add`/`commit`, `git checkout -b agent/issue-<n>-*`, `git push -u origin agent/issue-<n>-*`, `gh issue view`/`comment`, and `gh pr create`/`view`/`list`. Force pushes, resets, rebases, switching to `develop`/`main`, `gh pr merge`/`review`/`edit`, `gh issue edit`, `gh api`, `gh workflow`, `gh run`, and web access are denied.
- When the coder and architecture invocations succeed and the local branch, clean working tree and commit match a verified open pull request targeting `develop` from an `agent/issue-<n>-*` branch, the implementation job outputs its PR number and final head SHA, moves the issue to `agent-review`, and comments with the pull request link. When an invocation fails, the architecture target check fails, or no verified matching PR/head exists, it moves the issue to `agent-blocked` and comments with the run link. A cancelled (superseded) coder run only comments.
- A separate `dispatch-validation` job has `actions: write` and `pull-requests: write` (the other agent dispatchers only need `pull-requests: read`; this one is `write` only to label the pull request — `gh pr edit --add-label` resolves to the GraphQL `addLabelsToLabelable` mutation, which checks the pull-requests permission rather than `issues`), but no checkout and no Anthropic or deployment secret. It reverifies the open, non-draft, same-repository, bot-authored `agent/issue-*` PR, exact current SHA, `develop` base, and workflow-file exclusion, applies the `agent-review` label to the pull request with `gh pr edit --add-label`, then dispatches `validate.yml` from trusted `main` with `dispatch_review: true`.
- The workflow applies `agent-review` to the pull request itself, as a deterministic step of the `dispatch-validation` job — Claude is never granted `gh pr edit` or `gh label` in any agent workflow. The label is applied before validation is dispatched, so it is already present by the time validation succeeds and `validate.yml`'s `dispatch-review` job re-checks for it; no human action is needed to request the initial independent review.
- Concurrency group `agent-implement-issue-<n>` with `cancel-in-progress: true`: re-applying `agent-ready` while a run is in progress cancels the older run.
- Does not merge, deploy, apply `agent-ready`, or push to `develop` or `main`.

### Review workflow

`.github/workflows/agent-review.yml` ("Agent review"):

- Triggers on `pull_request` events `labeled`, `synchronize`, and `ready_for_review` for pull requests whose base branch is `develop`, and on `workflow_dispatch` with required `pr_number` and `head_sha` inputs. For a `pull_request` event, the context job requires a non-draft, same-repository PR carrying `agent-review`; for `labeled`, the label just added must be `agent-review`. In practice the initial review reaches this workflow through the trusted `workflow_dispatch` path described above (`agent-implement.yml`'s dispatcher applies the label, then dispatches `validate.yml`, which dispatches this workflow once validation succeeds), the same mechanism already used for a post-repair review; the event-driven `labeled` trigger still exists and would also fire if a human ever applied the label manually.
- A dispatched post-repair review uses the trusted workflow from `main` and additionally requires an open, non-draft, bot-authored, same-repository `agent/issue-*` PR targeting `develop`, the supplied current head SHA, the existing `agent-review` label, no `.github/workflows/**` change, and a successful latest `agent-validation` status on that exact SHA.
- Review-job permissions are `contents: read`, `pull-requests: write`, `issues: read`, `actions: read`, `checks: read`, and `statuses: read`. It cannot push, change labels, approve, or merge. The action is given `allowed_bots: "github-actions[bot]"` so it accepts the trusted post-validation dispatch created with the workflow token.
- The context job resolves one PR number, base, and exact head SHA. The review job checks out that SHA with persisted credentials disabled and passes it into the prompt. Every run is a fresh review of that SHA; it is instructed to disregard earlier review rounds except to check that earlier findings are resolved.
- The prompt requires it to read `CLAUDE.md`, `AGENTS.md`, this document, `docs/architecture.md` where relevant, the pull request and its diff, the linked issue's acceptance criteria and exclusions, the pull request's validation evidence, and the actual CI state, then evaluate requirements, invariants, architecture, tests, evidence, hygiene, risk classification, and documentation impact. For documentation impact it must compare three things: the issue's documentation impact decision and details, the pull request's `## Documentation impact` declaration, and the actual diff, verifying from the diff rather than from filenames alone. Missing, inaccurate or incomplete required documentation, or a declaration that contradicts the diff or silently ignores the issue decision, is a blocker. The gate adds no write authority to the review job.
- It may post inline comments and exactly one review, always submitted with `gh pr review --comment`. The review body starts with the head SHA and exactly one verdict line, `VERDICT: CHANGES REQUESTED` or `VERDICT: READY FOR HUMAN REVIEW`, followed by the numbered blockers (or `Blockers: none`), the acceptance-criteria checklist, non-blocking suggestions, and the validation evidence confirmed. It never uses `--approve` or `--request-changes`: agent pull requests are authored by `github-actions[bot]`, and GitHub rejects approve/request-changes reviews from a pull request's own author, so the verdict line carries the outcome. Tool permissions allow only `gh pr review --comment`, inline comments, read-only `gh`/`git` commands, and the CI log tools; `gh pr review --approve`/`--request-changes`, `gh pr comment`, file editing, `git push`/`commit`, `gh pr merge`/`edit`, label changes, `gh api`, and web access are denied. Human approval and branch protection remain the merge gate.
- Concurrency group `agent-review-pr-<n>` with `cancel-in-progress: true` covers both event and dispatched runs: a newer review for the same PR cancels an older one.
- It does not use the multi-agent code-review plugin.

### Repair workflow

`.github/workflows/agent-repair.yml` ("Agent repair"):

- Triggers only on `issue_comment` / `created`. The job runs only when the comment is on an open pull request that carries `agent-review`, the comment body starts with `@claude repair`, and the commenter is the repository owner (`github.event.comment.user.login == github.repository_owner`; the owner/member/collaborator association check is kept as well). Widening this to other collaborators is a deliberate later change. Before checking anything out it verifies through the API that the pull request targets `develop`, is not a draft, and that its head branch is in this repository and is not `develop` or `main`.
- The repair job permissions remain `contents: write`, `pull-requests: write`, `issues: write`, `actions: read`, `checks: read`, and `statuses: read`. It has no `actions: write`.
- Checks out the pull request's head branch, installs .NET 10 and Node.js 20, and runs Claude Code with the human's request comment, the review findings, and the CI state as input. The prompt requires a repair that changes behaviour, contracts, architecture, configuration, automation, deployment, operations or user workflows, or that addresses a documentation finding, to update the affected documentation on the head branch in the same repair. The agent cannot edit the pull request description: when the `## Documentation impact` declaration is no longer accurate, its closing comment must state exactly what the human must correct, and the `edited` trigger of `validate.yml` revalidates the description once it is changed. Tool permissions allow file edits, scoped `git rm --` for tracked files in `backend/`, `frontend/`, `docs/` and `scripts/` (never `.github/`), validation and build commands, `git add`/`commit`, and `git push origin <that head branch>` only; force pushes, `git checkout`/`switch`/`rebase`/`merge`, `gh pr create`/`merge`/`review`/`edit`, label changes, and `gh api` are denied.
- Always posts a closing comment on the pull request with the outcome and human next steps. It compares the starting and ending head SHAs. When the head changed, it outputs the new SHA for a follow-up job; when unchanged or unverifiable, it does not dispatch validation or review and does not claim that it did.
- A separate `dispatch-validation` job has `actions: write` and `pull-requests: read`, but no checkout and no Anthropic or deployment secret. It reverifies the open, non-draft, bot-authored, same-repository `agent/issue-*` PR, exact current SHA, `develop` base, existing `agent-review` label, and workflow-file exclusion, then dispatches `validate.yml` from trusted `main` with `dispatch_review: true`. Validation dispatches review only after it succeeds.
- **Repair counting is human-controlled.** The workflow does not count attempts and never starts on its own. The human who comments `@claude repair` is responsible for the two-attempt limit in the [failure and retry policy](#failure-and-retry-policy) and for labelling the issue `agent-blocked` after the second failed attempt.
- Concurrency group `agent-repair-pr-<n>` with `cancel-in-progress: true`.

### Credentials used by the agent workflows

- **Anthropic:** all three workflows authenticate to Anthropic with the repository secret `CLAUDE_CODE_OAUTH_TOKEN` (a long-lived Claude Code OAuth token) through the action's `claude_code_oauth_token` input. Implementation and review are permitted to share this billing credential; see [Shared billing credential, separate invocations](#shared-billing-credential-separate-invocations).
- **GitHub:** all three agent workflows pass the job-scoped `GITHUB_TOKEN` explicitly through the action's `github_token` input. Each job declares the minimum permissions listed above, so the token an agent operates with is exactly the job's permissions and expires when the job ends. The Claude implementation and repair jobs do not receive `actions: write`; only their separate, no-checkout dispatcher jobs do. In `validate.yml`, status publication and review dispatch are likewise isolated from the read-only job that checks out and executes PR code. The workflows do not request `id-token: write` and do not use the Claude GitHub App's installation token, a personal access token, or any Azure credential.
- **Pinned actions:** every third-party action in the agent workflows is pinned to a full commit SHA with the corresponding release tag in a comment: `anthropics/claude-code-action` v1.0.231, `actions/checkout` v4.4.0, `actions/setup-dotnet` v4.3.1, `actions/setup-node` v4.4.0.

### Workflow-token behaviour

The agent workflows act on GitHub with the job-scoped `GITHUB_TOKEN`. GitHub treats activity created with that token differently from human activity:

- A pull request `opened`, `synchronize`, or `reopened` event caused by the workflow token creates the corresponding `pull_request` workflow runs in an approval-required state. Those duplicate event-driven runs may remain visible on the PR.
- `workflow_dispatch` is an explicit exception: a job with `actions: write` may create the trusted run without a human clicking **"Approve workflows to run"**. InventoryApp uses that exception only from no-checkout dispatcher jobs after verifying the PR number, current head SHA, base, author, head repository/branch, and workflow-file exclusion.
- **The bot-author check reads the canonical login from the REST pull request endpoint.** Every guarded section resolves the author with `gh api "repos/$GITHUB_REPOSITORY/pulls/<number>" --jq '.user.login // empty'` and compares it for exact equality with `github-actions[bot]`. It must not use `gh pr view --json author`: that field resolves the GraphQL *actor*, which for the Actions bot reports the login as `github-actions` (rendered `app/github-actions` by `gh` 2.101.0). Neither spelling equals `github-actions[bot]`, and the spelling is a `gh`/GraphQL presentation detail that can change between CLI versions. Only REST `.user.login` returns the canonical, stable value the guard compares against, so reading the GraphQL actor made every dispatcher reject its own genuinely bot-authored pull request. The guard is deliberately not relaxed to accept both spellings: `github-actions` is a claimable ordinary account name, while `github-actions[bot]` cannot be registered by a user, so exact equality against the REST value is what makes the check a real authenticity test. `scripts/validate-agent-workflows.mjs` enforces the REST lookup in all five guarded sections and fails if `.author.login` reappears.
- The dispatched workflow definition always comes from `main`. The validation job then checks out the separately verified PR SHA with a read-only token and persisted credentials disabled.
- The `agent-validation` status linked to the dispatched run is the authoritative exact-SHA validation result for a bot-created or bot-updated PR. Only `workflow_dispatch` validation publishes it; `pull_request` validation publishes `merge-validation` and can never overwrite it. An approval-required duplicate run is not evidence that the dispatched validation failed and does not need to be approved when that exact-SHA status exists.
- The `agent-review` label the implementation dispatcher applies to the pull request does not by itself need to create another workflow run: review is reached through the trusted `workflow_dispatch` chain (`agent-implement.yml` → `validate.yml` → `agent-review.yml`), the same path already used after a repair, not through the `pull_request: labeled` event.
- Labels the implementation workflow applies to the *issue* are state bookkeeping only and start nothing.

The human's normal initial path is therefore: wait for `agent-validation`, then read the automatically dispatched review's verdict and decide. After a human-authorised repair pushes a new head, validation and a fresh review (under the same `agent-review` label) run automatically; no repair or merge is started automatically. No PAT, GitHub App credential, or long-lived GitHub secret is introduced.

### Validation modes

`validate.yml` serves two callers that can fire for the same pull request at the same time: GitHub's own `pull_request` event (for every PR, including a human approving the duplicate run on an agent PR) and the trusted `workflow_dispatch` from the implementation and repair dispatchers. Before this contract existed, both shared one concurrency group and one `agent-validation` status context, so the later event could cancel the earlier run and the last status written, whichever mode produced it, became the "authoritative" exact-SHA result that `agent-review.yml` trusts. The contract is now:

| | `pull_request` event | `workflow_dispatch` |
| --- | --- | --- |
| Mode | Merge-result validation | Exact-SHA validation |
| Workflow definition | The PR's base branch | Trusted `main` |
| Checkout | The event's merge commit (`github.sha`) | The verified current head SHA only |
| Guards | Same-repository check before publishing a status | Open, non-draft, `develop` base, same repository, `agent/issue-*` branch, `github-actions[bot]` author, exact current head SHA, no `.github/workflows/**` change |
| Concurrency group | `validation-<workflow>-pull_request-<pr>` | `validation-<workflow>-workflow_dispatch-<pr>` |
| Commit status | `merge-validation` | `agent-validation` |
| Consumed by | Humans and branch protection | `agent-review.yml` dispatched review, humans, branch protection |
| Dispatches review | Never | Only with `dispatch_review: true`, after success, while `agent-review` is present |

Rules enforced by `scripts/validate-agent-workflows.mjs` and proven by `scripts/validate-agent-workflows.test.mjs`:

- The concurrency group must contain `github.event_name` so the two modes for one PR never share a group, and must still contain the PR number so a newer run of the same mode supersedes the older one.
- The status context is a single workflow-level expression (`github.event_name == 'workflow_dispatch' && 'agent-validation' || 'merge-validation'`). The context job's bash refuses a mismatching context for its mode, the status job publishes only the context the context job resolved, and no job may hard-code a status context.
- `agent-review.yml` accepts only a successful latest `agent-validation` status on the exact head SHA as validation evidence and never `merge-validation`.
- The tests simulate both events for the same PR against the committed workflow text and also prove that the pre-fix expressions (a group without the event name, or one shared status context) are rejected.

### Documentation impact gate

Documentation (`AGENTS.md`, `CLAUDE.md`, `docs/`, `README.md`, and the issue and pull request templates) is what the agents obey and what humans rely on, so every change makes an explicit, checked decision about it. The gate separates four responsibilities:

| Part | Owner | What it does |
| --- | --- | --- |
| Collect the decision | Templates | The agent task form requires a `Documentation impact decision` (exactly `Documentation changes required` or `No documentation changes required`) and `Documentation impact details`, plus a readiness confirmation. The pull request template requires a `## Documentation impact` section with exactly one `Decision:` line (`UPDATED` or `NOT REQUIRED`) and one `Evidence:` entry, placed before **Known limitations and follow-up work**. |
| Validate that a meaningful declaration exists | Automation (`scripts/validate-documentation-impact.mjs`, the `preflight` job of `agent-implement.yml`, the validation job of `validate.yml`) | Rejects missing or duplicate sections/fields, unsupported or duplicate decisions, any pull request decision other than exactly `UPDATED` or `NOT REQUIRED`, empty evidence, unreplaced template placeholders, bare `None`/`N/A`/`Not applicable`, and generic answers (`UPDATED` must name documentation files and what changed; `NOT REQUIRED` must explain, specifically for the change, why behaviour, contracts, architecture, configuration, automation, deployment, operations and user workflows are unaffected). It never infers impact from changed filenames. |
| Decide whether the declaration is correct | Independent review (`agent-review.yml`), then the human reviewer | Compares the issue decision, the pull request declaration and the actual diff. Missing, inaccurate or incomplete required documentation is a blocker. |
| Approve and merge | Human | Confirms the decision when applying `agent-ready` and again when approving and merging. Automation never approves, merges or edits a declaration. |

`scripts/validate-agent-workflows.mjs` enforces, and `scripts/validate-agent-workflows.test.mjs` proves by mutation, that both templates keep the contract, that the read-only preflight job exists before the implementation job and gates it, that `validate.yml` obtains the body in the trusted context job, hands it over base64-encoded and invokes the validator before repository validation without a GitHub token, that the implementation, architecture, review and repair prompts keep their documentation requirements, that the architecture pass cannot be skipped before validation dispatch, and that no reviewer or repair write authority (in particular `gh pr edit`) is added. `scripts/validate-documentation-impact.test.mjs` covers both valid decisions, missing and duplicate sections, invalid decisions, empty evidence, the HTML template placeholders, and generic answers.

**Backfill of issues #87–#92.** Those agent task issues predate the gate and do not contain the two documentation impact fields. The preflight runs from the copy of `agent-implement.yml` on the default branch, so it becomes active the moment this change reaches `main`. Before that, or at least before `agent-ready` is next applied to any of them, a human must edit each issue to add a `Documentation impact decision` section containing exactly one of the two options and a `Documentation impact details` section with specific content. The parser accepts both the form's `### Heading` rendering and the `## Heading` style those issues already use. Applying `agent-ready` to an un-backfilled issue fails the preflight, changes nothing on the issue, and reports the required edit in the run log. This repository change does not edit those issues.

### Which workflows can deploy

| Workflow | Deploys | Trigger that deploys |
| --- | --- | --- |
| `validate.yml` | Nothing | — |
| `agent-implement.yml` | Nothing | — |
| `agent-review.yml` | Nothing | — |
| `agent-repair.yml` | Nothing | — |
| `vm-manager.yml` | API to Azure App Service | `push` to `main` only |
| `azure-static-web-apps-red-island-0c128c000.yml` | Frontend to Azure Static Web Apps | `push` to `main` only |

There is no staging environment and no automated path from an issue to `main`.

### Where agents stop today

`AGENTS.md` requires every agent to work on a feature branch, run complete validation, and open a pull request targeting `develop`. After that, the agent may update only its feature branch, for at most two permitted repair attempts in response to CI or review failures, each explicitly requested by a human, and then stops and returns control to a human. Agents do not merge and do not deploy. Because a merge to `main` deploys production, this boundary is a safety control, not a convention, and the workflow permissions above are chosen so that the agents cannot cross it even if instructed to.

## Roles and authority

The authority matrix below applies to every phase. The implementation agent is Claude Code run by `agent-implement.yml` and, for human-requested repairs, `agent-repair.yml`. The review agent is Claude Code run by `agent-review.yml`.

| Capability | Human owner/maintainer | Implementation agent | Review agent | CI (`validate.yml`) | Deployment workflows |
| --- | --- | --- | --- | --- | --- |
| Create or refine an agent task issue | Yes | No (may propose in a comment) | No | No | No |
| Apply `agent-ready` | Yes | **No** | No | No | No |
| Read repository files | Yes | Yes | Yes | Yes | Yes |
| Create a feature branch from `develop` | Yes | Yes | No | No | No |
| Modify files within the approved issue scope | Yes | Yes | No | No | No |
| Add or update tests | Yes | Yes | No | No | No |
| Run repository validation | Yes | Yes | No (reads the PR's evidence and CI results instead) | Yes | Build/test steps only |
| Commit and push the feature branch | Yes | Yes | **No** (read-only code access) | No | No |
| Open and update a pull request | Yes | Yes | Comment/review only; may not edit the PR | No | No |
| Apply or change labels | Yes | `agent-implement.yml` transitions `agent-working`/`agent-review`/`agent-blocked` on the issue and applies `agent-review` to its own pull request; Claude itself may not | **No** | No | No |
| Report blockers | Yes | Yes | Yes | Via failed check | Via failed run |
| Declare documentation impact | Yes (issue decision and details) | Yes (pull request `## Documentation impact` declaration; must honour the issue decision) | No | No | No |
| Validate that a documentation impact declaration exists and is meaningful | Yes | No (`agent-implement.yml` preflight does, before Claude runs) | No | Yes (`validate.yml`, before repository validation) | No |
| Decide whether a documentation impact declaration is truthful | Yes | No | Yes (blocker when documentation is missing, inaccurate or incomplete) | **No** | No |
| Broaden acceptance criteria | Yes | **No** | No | No | No |
| Request or count a repair attempt | Yes (repository owner, for now) | **No** | No (may return `VERDICT: CHANGES REQUESTED`) | No | No |
| Approve a pull request | Yes | No | **No** (may recommend "ready for human review") | Reports status | No |
| Merge to `develop` | Yes | **No** | **No** | No | No |
| Prepare or update a `develop` → `main` release PR | Yes | Only when a human explicitly and separately requests it | **No** | No | No |
| Approve or merge a release PR | Yes | **No** | **No** | No | No |
| Deploy an environment | Yes, indirectly, by merging a release PR to `main` | **No** | **No** | No | Yes, on `push` to `main` |
| Run production migrations | Yes, via the human-invoked `migrate-database --apply` command; never automatic | **No** | **No** | No | No — deploying does not migrate the schema (see [Push or merge to `main`](#push-or-merge-to-main)) |
| Modify production data | Yes | **No** | **No** | No | No |
| Access production secrets | Only through approved secure platform administration when required | **No** | **No** | No | Consume configured secrets without displaying or returning them |
| Expose production secrets | **No** | **No** | **No** | **No** | **No** |
| Change GitHub or Azure credentials | Yes | **No** | **No** | No | No |
| Modify branch protection or repository settings | Yes | **No** | **No** | No | No |
| Perform destructive remote operations | Yes, deliberately | **No** | **No** | No | No |
| Force-push or rewrite history | Discouraged | **No** | **No** | No | No |

No participant may expose a production secret. Secrets must never appear in source, logs, issues, pull requests, test fixtures, screenshots, build output, or public responses.

### Implementation agent

The implementation agent **may**:

- Read repository files.
- Create a feature branch from `develop`.
- Modify files within the approved issue scope.
- Add or update tests.
- Run validation.
- Commit and push its feature branch.
- Open and update a pull request.
- Report blockers.

The implementation agent **may not**:

- Mark its own issue `agent-ready`.
- Broaden acceptance criteria.
- Merge any pull request, feature or release.
- Prepare a release pull request on its own initiative; it does so only on a separate, explicit human request, and never approves or merges it.
- Push directly to `develop` or `main`.
- Deploy an environment.
- Modify production data.
- Run production migrations.
- Access production secrets.
- Expose production secrets (no participant may).
- Change GitHub or Azure credentials.
- Modify branch protection or repository settings.
- Perform destructive remote operations.
- Force-push or rewrite history.

### Architecture pass

After the coder has opened a PR, `agent-implement.yml` verifies that the current branch and commit match exactly one open PR for this issue. It starts a fresh Claude invocation in the same job and feature checkout, before the outcome step and exact-SHA validation dispatcher. This is an editing pass distinct from the read-only independent reviewer. It runs once during initial implementation; a later human-requested repair does not automatically restart the architecture pass.

The architect reads the approved issue, PR diff, tests, `AGENTS.md`, and `docs/architecture.md`. It may fix a concrete boundary or dependency-direction problem in the touched code, with no observable behavior, API, data, or acceptance-criteria change. It reruns complete validation after edits, then commits and pushes only the same feature branch without rewriting history. If no in-scope correction is needed, it leaves the code unchanged. It comments on the PR with its result or a finding requiring human review. An edit that would require a migration, widen scope, or make the PR body or documentation declaration inaccurate is not authorised by this pass.

The outcome step checks that the coder, target check, and architect all succeeded, the local branch and SHA match the PR head, and the working tree is clean. It then dispatches validation of that final head SHA. If any check fails, it labels the issue `agent-blocked` and does not dispatch validation. This role cannot merge, deploy, alter workflow files, or approve a PR. The later independent reviewer remains a separate read-only workflow run.

### Review agent

The review agent (`agent-review.yml`) must, and is configured to:

- Be independent of the implementation step: a separate workflow run, a separate job with its own working tree, conversation, and prompt, and its own job-scoped GitHub permissions, so that it cannot be steered by the implementation agent's own reasoning. It shares only the Anthropic billing credential (see below).
- Review the pull request's current head SHA afresh on every run.
- Evaluate the pull request against the issue's acceptance criteria and exclusions, `AGENTS.md`, `docs/architecture.md`, the validation evidence in the pull request and CI, security, scope, and documentation impact (issue decision versus pull request declaration versus actual diff).
- Produce a written result attached to the pull request: inline comments and exactly one comment-only review whose body carries one verdict line, `VERDICT: CHANGES REQUESTED` or `VERDICT: READY FOR HUMAN REVIEW`, with the blockers listed under it. It never submits an approve or request-changes review.
- **Never** merge, approve on behalf of a human, deploy, modify files, commits, branches, or labels, or edit the pull request or issue.

Its result is advisory. A human still reviews and decides whether to merge.

### Shared billing credential, separate invocations

The implementation, architecture, and review invocations **may share the Anthropic billing credential** (`CLAUDE_CODE_OAUTH_TOKEN`). Independence is provided by separation of invocation, context, and authority, not by separate Anthropic accounts:

- **Separate invocations:** implementation and review are different workflows with separate runs, working trees, prompts, and tokens. The architecture pass is a second invocation within the implementation job, deliberately sharing its feature checkout and job-scoped token so it can update that PR branch. All three editing invocations (implementation, architecture and repair) may remove tracked files in project directories with `git rm -- <path>` when in scope; they cannot remove `.github/` files through that permission. The agent workflow contract test rejects broader deletion patterns. A human starts implementation with `agent-ready`; the initial review is requested automatically, by `agent-implement.yml`'s dispatcher applying `agent-review` and successful validation dispatching the review. After a human starts a repair, successful exact-SHA validation may dispatch another independent review under that existing label.
- **Separate contexts:** each run has its own checkout, prompt, conversation, and tool configuration. The reviewer receives the head SHA, the pull request, and the issue, not the implementation agent's transcript or reasoning.
- **Separate, job-scoped GitHub permissions:** the implementation job (including its architecture pass) has write access to contents, pull requests, and issues; the review job has read-only access to code and may only comment or review. Each job passes its own `GITHUB_TOKEN`, scoped to those permissions and expiring with the job.

The Anthropic credential only meters usage; it grants no authority over the repository. Sharing it is therefore acceptable. Sharing a GitHub credential, a working tree, or a conversation between implementation and review is not.

### CI

CI (`validate.yml`) validates pull requests independently of the agent's own validation run, starting with the pull request's documentation impact declaration. Dispatched exact-SHA validation publishes `agent-validation` on the exact head SHA; event-driven merge-result validation publishes `merge-validation`. For a successful repair validation it may dispatch the already-authorised review, but it does not modify code or labels, start a repair, approve, merge, release, or deploy.

### Deployment workflows

The deployment workflows run only on a push to `main`. They are not invoked by agents, review steps, or CI. Their credentials are repository/environment secrets and variables that are not available to the implementation or review agent.

## Task states and labels

The labels below exist in the repository's label settings, where a human created them. No file in this repository creates labels. `agent-implement.yml` is the only file that applies or removes a label, and only on the issue it was started from.

| Label | Meaning | Applied by |
| --- | --- | --- |
| `agent-ready` | A human has reviewed the issue, confirmed the acceptance criteria are complete and testable and the documentation impact decision is correct, and authorises an implementation agent to start. Applying it starts `agent-implement.yml`, whose read-only preflight first validates the documentation impact declaration; if that fails, the label is left in place and nothing else runs until a human edits the issue and re-applies it. | **Human only** |
| `agent-working` | An implementation agent has started and owns a feature branch for this issue. | `agent-implement.yml`, at the start of its run (replaces `agent-ready`) |
| `agent-review` | On an **issue**: a pull request is open and awaiting independent review. On a **pull request**: authorises the (now automatic) initial independent review and fresh review after any later human-requested repair. `agent-repair.yml` accepts the repository owner's `@claude repair` comments while the label remains present; a pushed repair is validated and reviewed again automatically. | Issue: `agent-implement.yml` when its coder and architecture stages complete with a verified PR head. Pull request: `agent-implement.yml`'s `dispatch-validation` job, as a deterministic step, when it dispatches validation for that PR; a human may also apply it manually (for example to re-request review outside a repair) |
| `agent-blocked` | The agent stopped because validation/review failed after the permitted repair attempts, or because a human decision or permission is required. The issue or PR must state the exact blocker. | `agent-implement.yml` when the coder, architecture target check, or architect fails to finish with a verified PR head; otherwise human (including after the second failed repair) |
| `risk:low` | See risk classification. | Human at triage |
| `risk:medium` | See risk classification. | Human at triage |
| `risk:high` | See risk classification. | Human at triage |

Rules:

- A human applies `agent-ready`. Submitting the issue form does not apply it, and the form does not reference it.
- An agent must not start from an issue that has not been reviewed and labelled `agent-ready` by a human.
- An agent must not apply `agent-ready` to any issue, including one it drafted. Claude itself is denied `gh issue edit`, `gh pr edit`, and `gh label` in every agent workflow; the only label transitions are the deterministic steps in `agent-implement.yml` listed above.
- The state labels are mutually exclusive: an issue is in at most one of `agent-ready`, `agent-working`, `agent-review`, or `agent-blocked`.
- Removing `agent-blocked` and returning an issue to `agent-ready` is a human decision. Re-applying `agent-ready` starts a new implementation run.

State flow:

```text
(new issue) → [human review] → agent-ready → agent-working → agent-review → [human merge decision]
                                     ↑              │              │
                                     └── agent-blocked ←───────────┘
```

## Risk classification

Every agent task issue proposes a risk level, and a human confirms it before applying `agent-ready`. When a task touches more than one area, use the highest applicable classification.

### Low risk

- Documentation.
- Tests that do not alter production behaviour.
- Localised cleanup with no runtime behaviour change (for example renaming a private helper with full test coverage).

### Medium risk

- Normal API or UI behaviour changes.
- Non-financial business features (for example supplier or category maintenance, list filtering, presentation improvements).
- Refactoring that is covered by existing contracts and tests and does not change a public API.

### High risk

The following are always high risk and require explicit human scrutiny of both the issue and the pull request:

- Financial or profit calculations (sales, fees, GST, commissions, gross/direct/net profit, margins).
- Inventory quantity or historical costing rules (`QuantityInStock`, `CostingQuantity`, `InventoryValue`, AVCO, COGS provenance, rebuilds).
- Database schema or EF Core migrations.
- Data backfills or destructive data operations.
- Authentication or authorization.
- Secrets or environment configuration.
- GitHub Actions, Azure, or deployment changes.
- Any Nayax or other external-integration change, whether it reads, writes, imports, synchronises, maps errors, changes authentication, or handles remote payloads.
- Imports or reconciliation behaviour (transaction, product, and reimbursement imports; status/payment classification; settlement matching).
- Public API contract changes.
- File upload or filesystem security.
- Production-impacting operations of any kind.

The detailed invariants for these areas are defined in `AGENTS.md` and `docs/architecture.md`; a high-risk change must cite the invariants it touches in its pull request.

### Merge policy by risk

**During the initial automation phases (1–5), all merges remain human-controlled regardless of risk.** Risk level affects how much scrutiny a human applies and, in later phases, whether a task is eligible for automation at all. Agents never merge, whatever the risk level.

## Failure and retry policy

- An implementation agent may make **at most two** repair attempts for a pull request failure (a failing CI check or a rejecting review). The original implementation does not count as an attempt.
- **Repair counting is human-controlled.** A repair attempt happens only when the repository owner comments `@claude repair` (optionally followed by instructions) on the pull request while it carries `agent-review`; that starts `agent-repair.yml`. The workflow does not count attempts, does not start a repair from a failed check or review, and never chains into another repair. If the repair pushes a new head, deterministic validation and the already-authorised independent review follow automatically. The human who requests a repair is responsible for not requesting a third one. Mechanical enforcement of the limit is future work and is **not implemented**.
- After the second failed repair attempt, the human labels the issue `agent-blocked` and takes over. There is no third attempt and no implementation/review loop without a human in between.
- The agent must stop **immediately**, without attempting a repair, when the acceptance criteria conflict with each other, with `AGENTS.md`, with `docs/architecture.md`, or with existing tests, or when the requirements are materially ambiguous.
- When that stop happens before a feature branch exists, the implementation prompt requires the agent to write the runner-local `.agent-run-status` marker with exactly `blocked`. The architecture-target step accepts this marker only while the checkout is still on `develop`; it records a clean blocked outcome, skips the architecture pass and exact-SHA validation, and moves the issue to `agent-blocked`. A successful Claude action that stays on `develop` without that exact marker is still an error, so unexpected missing branches or pull requests are not silently treated as intentional blockers.
- The agent must never weaken, skip, or delete a failing test merely to obtain a green build. If an established rule is intentionally changing, that must be stated in the issue, and all affected tests and documentation are updated together as described in `AGENTS.md`.
- Environmental failures (missing SDK, network failure, package registry outage, runner timeout) must be reported exactly as they occurred, with the failing command. They must not be reported as passing, and they do not consume a repair attempt when the change itself was not at fault, but the task still stops until a human decides how to proceed.
- A blocked task must state the exact decision or permission needed from a human: which requirement is ambiguous, which rule conflicts, which command failed and how, or which approval is required. "Blocked" without that statement is not an acceptable outcome.

## Traceability

Every automated change must maintain an unbroken, inspectable chain:

| Record | Where it lives | Requirement |
| --- | --- | --- |
| Issue | GitHub issue using the agent task form | Defines outcome, acceptance criteria, exclusions, and risk. |
| Feature branch | Git branch from `develop` | One branch per issue; name references the issue or its purpose. |
| Commit | Git history | Focused commits; message describes the change; no history rewriting. |
| Pull request | GitHub PR targeting `develop` | Uses the PR template; links the issue; describes validation, risk, impact, and the documentation impact decision with evidence. |
| Validation result | Agent's PR description, dispatched run, and `agent-validation` status | Exact head SHA, actual commands, and actual results; never claimed without running. |
| Agent run | GitHub Actions run of `agent-implement.yml`, `agent-review.yml`, or `agent-repair.yml`, linked from the closing comment | Full log of what the agent read, ran, and changed. |
| Review result | PR review/comments | Review-agent comment review with its `VERDICT:` line, plus human review. |
| Human merge decision | PR merge by a human | Records who accepted the change into `develop`. |
| Release/deployment record | `develop` → `main` release PR and the workflow run | Applies when the change reaches production. |

A change whose chain is broken (for example a PR without an issue, or a validation claim without evidence) is not eligible for automated handling and must be reviewed as an ordinary human change.

## Branch and release policy

- Work branches start from the latest `develop`.
- Normal feature and fix pull requests target `develop`.
- `develop` is the integration branch. A push to `develop` runs backend build and tests (`vm-manager.yml` build job) but does not deploy.
- Production releases use a **separate** pull request from `develop` to `main`.
- A merge or push to `main` deploys the API and the frontend to Azure through the existing workflows. It does not apply EF Core migrations: outside Development and `Testing`, API startup applies no schema change and fails closed if one is pending (see [Push or merge to `main`](#push-or-merge-to-main)).
- An agent may prepare or update a `develop` to `main` release pull request only when a human explicitly requests it. Permission to implement a feature never grants permission to create a release pull request. Agents never merge or deploy: a human reviews and merges the release pull request, and the existing workflow performs the deployment.
- Production deployments and production migrations require human control. There is no automated path from an issue to production.

## Recommended repository protection checklist

> **Manual configuration checklist.** These are GitHub repository settings, not repository files. This document recommends them; it does not enable them and does not verify that they are enabled. A maintainer must configure and confirm each item in the GitHub UI or API.

| Setting | Recommendation |
| --- | --- |
| Pull requests required | Require a pull request before merging into `develop` and `main`. |
| Required status check | Two stable statuses exist once this workflow is present on both branches and its live test has passed: `merge-validation`, published by normal same-repository `pull_request` validation of every PR, and `agent-validation`, published only by trusted exact-SHA dispatch for agent PRs. Requiring `merge-validation` gates human PRs and, for agent PRs, requires a human to approve the bot-created duplicate `pull_request` run; requiring `agent-validation` gates only agent PRs. Choose the combination deliberately: a human decides which statuses branch protection requires, and this document does not verify the setting. |
| Force pushes | Block force pushes to `develop` and `main`. |
| Branch deletion | Block deletion of `develop` and `main`. |
| Conversation resolution | Require all review conversations to be resolved before merging, where available. |
| Bypass | Do not allow automation credentials (GitHub Apps, tokens used by agents, `GITHUB_TOKEN`) to bypass protected-branch rules. The agent workflows rely on branch protection as the final guard against a push to `develop` or `main`. |
| Deployment credentials | Keep Azure deployment secrets/variables and the `VmInventoryApi_Env` environment unavailable to implementation and review agents. The agent workflows request none of them. |
| Agent credential | Keep `CLAUDE_CODE_OAUTH_TOKEN` a repository secret used only by the agent workflows; rotate it by regenerating it with `claude setup-token`. Do not make it available to forks (GitHub already withholds secrets from fork pull requests). |
| Workflow branches | The agent workflows must exist on `main` (where `issues` and `issue_comment` workflows run from) and on `develop` (where `pull_request` workflows targeting `develop` run from). Keep them identical on both. The documentation impact preflight in `agent-implement.yml` takes effect when it reaches `main`; backfill issues #87–#92 before then (see [Documentation impact gate](#documentation-impact-gate)). |
| Production approval | Configure a human-controlled required reviewer on the production deployment environment, when available on the repository's plan. |
| Review count | Require at least one human approving review on `main`; consider the same for `develop`. |

`docs/architecture.md` already notes that branch protection and required-check configuration live in repository settings and must be enabled separately from source-controlled workflows.

## Implementation phases

Each phase is delivered as its own pull request and must be proven reliable before the next begins.

| Phase | Scope | Status |
| --- | --- | --- |
| 1 | Automation documentation and templates: this document, the agent task issue form, the PR template, and `AGENTS.md`/`README.md` updates. | **Implemented** |
| 2 | Issue label to implementation-agent PR creation: labels, an implementation agent connected to `agent-ready` issues, and `agent-working`/`agent-review`/`agent-blocked` transitions. No merge or deploy authority. | **Implemented** with Claude Code (`agent-implement.yml`, `CLAUDE.md`). Labels were created by a human in repository settings. Exact-SHA validation is dispatched automatically from trusted `main`; see [Workflow-token behaviour](#workflow-token-behaviour). |
| 3 | Independent automated review and bounded repair: a separate review step, its written result, and the two-attempt repair limit enforced mechanically. | **Partially implemented.** The independent review step exists (`agent-review.yml`) and its initial run is dispatched automatically once validation succeeds (`agent-implement.yml`'s dispatcher applies `agent-review` to the pull request as a deterministic step); a human-invoked repair step exists (`agent-repair.yml`), and a pushed repair automatically receives exact-SHA validation followed by fresh review under the same label. The [documentation impact gate](#documentation-impact-gate) (templates, preflight, validation, review comparison) is **implemented**. Mechanical enforcement of the two-attempt limit is **not implemented**: repair attempts are requested and counted by a human. |
| 4 | Staging deployment and smoke tests: a non-production environment deployed from `develop` with automated smoke checks. No staging environment exists today. | Proposed |
| 5 | Controlled release PR and production approval: a `develop` → `main` release PR process with human environment approval. | Proposed |
| 6 | Production monitoring and proposed issue creation: monitoring that can draft issues for humans to review; it must not apply `agent-ready`. | Proposed |
| 7 | Possible low-risk auto-merge, only after the earlier phases have proven reliable and only for `risk:low` tasks, with a human able to disable it at any time. Any such merge would be performed by repository automation configured by a human, never by the implementation or review agent, and would require an explicit update to this document. | Proposed, not committed |

Phases 4–7, and the mechanical repair-limit enforcement of phase 3, are not implemented by this repository at the time of writing. Any claim that one of them exists must be backed by a workflow or configuration file in this repository and a corresponding update to this document.
