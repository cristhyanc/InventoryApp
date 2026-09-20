# InventoryApp automated development lifecycle

## Purpose

This document defines how InventoryApp intends to run a controlled, automated software-development system:

```text
GitHub issue → implementation agent → feature branch → pull request → CI validation
→ independent review → human approval → develop → release PR → main → Azure deployment → monitoring
```

It describes the lifecycle, the authority of each participant, task states, risk classification, failure handling, traceability, branch policy, and the incremental phases in which the system will be built.

It deliberately separates three things:

| Category | Meaning |
| --- | --- |
| **Exists now** | Behaviour implemented by files in this repository today: the validation and deployment workflows, the Claude Code implementation, review, and repair workflows (`agent-implement.yml`, `agent-review.yml`, `agent-repair.yml`), the validation scripts, `AGENTS.md`, `CLAUDE.md`, the agent task issue form, and the pull request template. |
| **Proposed for future pull requests** | Automation that is designed here but **not implemented**: mechanical enforcement of the two-attempt repair limit, staging, release automation, monitoring, and any auto-merge. |
| **Human-controlled** | Decisions that stay with a human regardless of how much automation is added: applying `agent-ready`, requesting a review, authorising and counting repair attempts, approving, merging, releasing, deploying. |

Nothing in this document creates an automation capability by itself. Where a capability is described as future work, it does not exist until a later pull request implements it and this document is updated.

The agent provider is **Claude Code**, run through the `anthropics/claude-code-action` GitHub Action. `CLAUDE.md` at the repository root directs Claude to read and obey `AGENTS.md`, this document, `docs/architecture.md` where relevant, and the linked issue's acceptance criteria and exclusions.

`AGENTS.md` is the authoritative engineering and safety policy, and `docs/architecture.md` is the authoritative architectural description. This document does not restate their financial, inventory, database, Nayax, or security invariants; it refers to them.

## Desired lifecycle

The target lifecycle for one automated change is:

1. A human creates or refines an agent task issue using the **Agent task** issue form.
2. A human confirms that the acceptance criteria are complete, testable, and bounded by explicit exclusions.
3. A human applies the `agent-ready` label.
4. An implementation agent creates a feature branch from the latest `develop`.
5. The agent implements only the approved scope defined by the issue's acceptance criteria and exclusions.
6. The agent runs the complete repository validation (`scripts/validate.sh` or `scripts/validate.ps1`).
7. The agent opens a pull request targeting `develop`, using the pull request template.
8. CI independently validates the pull request.
9. A separate reviewer (an independent review agent, then a human) evaluates requirements, architecture, security, tests, validation evidence, and scope.
10. If validation or review fails, a human may request a repair; the implementation agent may make **no more than two** repair attempts per pull request.
11. If the pull request still fails, or the requirements are materially ambiguous or conflicting, the task becomes blocked and returns to a human with a precise statement of the decision needed.
12. A human decides whether to merge into `develop`.
13. A separate release pull request from `develop` to `main` controls production deployment.
14. Production deployment and any production migration remain human-controlled.

Today: steps 1–3 are human. Steps 4–7 are performed by the Claude Code implementation workflow (`agent-implement.yml`) when a human applies `agent-ready`. Step 8 is `validate.yml`, with the caveat described under [Workflow-token limitation](#workflow-token-limitation). Step 9 is the Claude Code review workflow (`agent-review.yml`), which a human requests by applying `agent-review` to the pull request, followed by human review. Step 10 exists only as a human-invoked repair (`agent-repair.yml`, started by an `@claude repair` comment); the two-attempt limit is counted by the human, not by a workflow. Step 11 is partly automatic (the implementation workflow labels the issue `agent-blocked` when its run ends without a pull request) and otherwise human. Steps 12–14 are human.

## Current repository behaviour

This section is derived from the triggers, conditions, and jobs in `.github/workflows/` and from `scripts/`, as inspected when this document was written. If a workflow changes, update this section in the same pull request.

### Pull-request validation

`.github/workflows/validate.yml` ("Validate pull request"):

- Triggers on `pull_request` events whose base branch is `develop` or `main`, and on manual `workflow_dispatch`.
- Runs one job, **"Backend tests and frontend build"**, which checks out the repository, installs .NET 10 and Node.js 20, and runs `bash scripts/validate.sh`.
- `scripts/validate.sh` and `scripts/validate.ps1` perform the same steps: `dotnet restore`, `dotnet build --configuration Release`, `dotnet test`, `npm ci`, and `npm run build` for the Angular application. The frontend has no configured test or lint script; its gate is the production build.
- Has `contents: read` permission only and does not deploy anything.
- Cancels an in-progress run for the same pull request when a new run starts.

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

### Implementation workflow

`.github/workflows/agent-implement.yml` ("Agent implementation"):

- Triggers only on `issues` / `labeled`, and its single job runs only when the added label is `agent-ready` (and the entity is an issue, not a pull request). No other event, comment, mention, or label starts it. Like every `issues`-triggered workflow, GitHub runs the copy on the default branch (`main`).
- Job permissions: `contents: write`, `pull-requests: write`, `issues: write`. Nothing else; no `id-token`, no `actions: write`.
- Checks out `develop` at its latest commit, installs .NET 10 and Node.js 20, and moves the issue from `agent-ready` to `agent-working`.
- Runs Claude Code with a fixed prompt that requires it to read the issue, restate the acceptance criteria and exclusions, create `agent/issue-<n>-<slug>` from the checked-out `develop`, make the smallest change with tests, run `bash scripts/validate.sh`, push that branch only, and open one pull request targeting `develop` using the pull request template. Tool permissions enforce the same limits: file edits, `dotnet`, `npm --prefix frontend/inventory-app`, `bash scripts/validate.sh`, read-only `git`, `git add`/`commit`, `git checkout -b agent/issue-<n>-*`, `git push -u origin agent/issue-<n>-*`, `gh issue view`/`comment`, and `gh pr create`/`view`/`list`. Force pushes, resets, rebases, switching to `develop`/`main`, `gh pr merge`/`review`/`edit`, `gh issue edit`, `gh api`, `gh workflow`, `gh run`, and web access are denied.
- When the Claude step succeeds and an open pull request targeting `develop` from an `agent/issue-<n>-*` branch exists, the workflow moves the issue to `agent-review` and comments with the pull request link. When the step fails or no such pull request exists, it moves the issue to `agent-blocked` and comments with the run link. A cancelled (superseded) run only comments.
- Concurrency group `agent-implement-issue-<n>` with `cancel-in-progress: true`: re-applying `agent-ready` while a run is in progress cancels the older run.
- Does not merge, deploy, apply `agent-ready`, or push to `develop` or `main`.

### Review workflow

`.github/workflows/agent-review.yml` ("Agent review"):

- Triggers only on `pull_request` events `labeled`, `synchronize`, and `ready_for_review` for pull requests whose base branch is `develop`. The job additionally requires that the pull request is not a draft, carries `agent-review`, comes from a branch in this repository, and, for `labeled`, that the label just added is `agent-review`. A human applies `agent-review` to the pull request to request a review; pushes to a labelled pull request re-run it.
- Job permissions: `contents: read`, `pull-requests: write`, `issues: read`, `actions: read`, `checks: read`, `statuses: read`. It cannot push, cannot change labels, and cannot approve or merge.
- Checks out the pull request's current head SHA and passes that SHA into the prompt. Every run is a fresh review of that SHA; it is instructed to disregard earlier review rounds except to check that earlier findings are resolved.
- The prompt requires it to read `CLAUDE.md`, `AGENTS.md`, this document, `docs/architecture.md` where relevant, the pull request and its diff, the linked issue's acceptance criteria and exclusions, the pull request's validation evidence, and the actual CI state, then evaluate requirements, invariants, architecture, tests, evidence, hygiene, and risk classification.
- It may post inline comments and exactly one review, either `--comment` ("Ready for human review") or `--request-changes`. Tool permissions allow only those, plus read-only `gh`/`git` commands and the CI log tools; file editing, `git push`/`commit`, `gh pr review --approve`, `gh pr merge`/`edit`, label changes, `gh api`, and web access are denied.
- Concurrency group `agent-review-pr-<n>` with `cancel-in-progress: true`: a newer head cancels a review of an older head.
- It does not use the multi-agent code-review plugin.

### Repair workflow

`.github/workflows/agent-repair.yml` ("Agent repair"):

- Triggers only on `issue_comment` / `created`. The job runs only when the comment is on an open pull request that carries `agent-review`, the comment body starts with `@claude repair`, and the author is an owner, member, or collaborator. Before checking anything out it verifies through the API that the pull request targets `develop`, is not a draft, and that its head branch is in this repository and is not `develop` or `main`.
- Job permissions: `contents: write`, `pull-requests: write`, `issues: write`, `actions: read`, `checks: read`, `statuses: read`.
- Checks out the pull request's head branch, installs .NET 10 and Node.js 20, and runs Claude Code with the human's request comment, the review findings, and the CI state as input. Tool permissions allow file edits, validation and build commands, `git add`/`commit`, and `git push origin <that head branch>` only; force pushes, `git checkout`/`switch`/`rebase`/`merge`, `gh pr create`/`merge`/`review`/`edit`, label changes, and `gh api` are denied.
- Always posts a closing comment on the pull request with the outcome and the human next steps.
- **Repair counting is human-controlled.** The workflow does not count attempts and never starts on its own. The human who comments `@claude repair` is responsible for the two-attempt limit in the [failure and retry policy](#failure-and-retry-policy) and for labelling the issue `agent-blocked` after the second failed attempt.
- Concurrency group `agent-repair-pr-<n>` with `cancel-in-progress: true`.

### Credentials used by the agent workflows

- **Anthropic:** all three workflows authenticate to Anthropic with the repository secret `CLAUDE_CODE_OAUTH_TOKEN` (a long-lived Claude Code OAuth token) through the action's `claude_code_oauth_token` input. Implementation and review are permitted to share this billing credential; see [Shared billing credential, separate invocations](#shared-billing-credential-separate-invocations).
- **GitHub:** all three workflows pass the job-scoped `GITHUB_TOKEN` explicitly through the action's `github_token` input. Each job declares the minimum permissions listed above, so the token an agent operates with is exactly the job's permissions and expires when the job ends. The workflows do not request `id-token: write` and do not use the Claude GitHub App's installation token, a personal access token, or any Azure credential.
- **Pinned actions:** every third-party action in the agent workflows is pinned to a full commit SHA with the corresponding release tag in a comment: `anthropics/claude-code-action` v1.0.231, `actions/checkout` v4.4.0, `actions/setup-dotnet` v4.3.1, `actions/setup-node` v4.4.0.

### Workflow-token limitation

GitHub does not start `pull_request` or `push` workflows for events created with a workflow's own `GITHUB_TOKEN`. Consequences:

- A pull request opened by the implementation workflow, and a push made by the repair workflow, do **not** automatically run `validate.yml` or the review workflow. A human confirms CI (closing and reopening the pull request runs `validate.yml` as a normal `pull_request` event; a human push does too) and applies `agent-review` to request the review. The workflows say this in their closing comments.
- Labels applied by the implementation workflow to the *issue* do not trigger anything. The review trigger is the `agent-review` label on the *pull request*, applied by a human.

This is a deliberate trade-off: it keeps every agent credential job-scoped and short-lived, and it keeps a human between implementation and review. Replacing it with a GitHub App or personal token would be a credential change requiring its own reviewed pull request.

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
| Apply or change labels | Yes | `agent-implement.yml` transitions `agent-working`/`agent-review`/`agent-blocked` on the issue; Claude itself may not | **No** | No | No |
| Report blockers | Yes | Yes | Yes | Via failed check | Via failed run |
| Broaden acceptance criteria | Yes | **No** | No | No | No |
| Request or count a repair attempt | Yes | **No** | No (may request changes) | No | No |
| Approve a pull request | Yes | No | **No** (may recommend "ready for human review") | Reports status | No |
| Merge to `develop` | Yes | **No** | **No** | No | No |
| Prepare or update a `develop` → `main` release PR | Yes | Only when a human explicitly and separately requests it | **No** | No | No |
| Approve or merge a release PR | Yes | **No** | **No** | No | No |
| Deploy an environment | Yes, indirectly, by merging a release PR to `main` | **No** | **No** | No | Yes, on `push` to `main` |
| Run production migrations | Yes (application startup on deploy) | **No** | **No** | No | Indirectly, on deploy |
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

### Review agent

The review agent (`agent-review.yml`) must, and is configured to:

- Be independent of the implementation step: a separate workflow run, a separate job with its own working tree, conversation, and prompt, and its own job-scoped GitHub permissions, so that it cannot be steered by the implementation agent's own reasoning. It shares only the Anthropic billing credential (see below).
- Review the pull request's current head SHA afresh on every run.
- Evaluate the pull request against the issue's acceptance criteria and exclusions, `AGENTS.md`, `docs/architecture.md`, the validation evidence in the pull request and CI, security, and scope.
- Produce a written result attached to the pull request: inline comments and one review that either requests changes or states the change is ready for human review.
- **Never** merge, approve on behalf of a human, deploy, modify files, commits, branches, or labels, or edit the pull request or issue.

Its result is advisory. A human still reviews and decides whether to merge.

### Shared billing credential, separate invocations

The implementation and review workflows **may share the Anthropic billing credential** (`CLAUDE_CODE_OAUTH_TOKEN`). Independence is provided by separation of invocation, context, and authority, not by separate Anthropic accounts:

- **Separate invocations:** implementation and review are different workflows started by different human actions (`agent-ready` on an issue; `agent-review` on a pull request). One never starts the other.
- **Separate contexts:** each run has its own checkout, prompt, conversation, and tool configuration. The reviewer receives the head SHA, the pull request, and the issue, not the implementation agent's transcript or reasoning.
- **Separate, job-scoped GitHub permissions:** the implementation job has write access to contents, pull requests, and issues; the review job has read-only access to code and may only comment or review. Each job passes its own `GITHUB_TOKEN`, scoped to those permissions and expiring with the job.

The Anthropic credential only meters usage; it grants no authority over the repository. Sharing it is therefore acceptable. Sharing a GitHub credential, a working tree, or a conversation between implementation and review is not.

### CI

CI (`validate.yml`) validates pull requests independently of the agent's own validation run. It reports status; it does not merge, deploy, or apply labels. See [Workflow-token limitation](#workflow-token-limitation) for when a human must re-trigger it.

### Deployment workflows

The deployment workflows run only on a push to `main`. They are not invoked by agents, review steps, or CI. Their credentials are repository/environment secrets and variables that are not available to the implementation or review agent.

## Task states and labels

The labels below exist in the repository's label settings, where a human created them. No file in this repository creates labels. `agent-implement.yml` is the only file that applies or removes a label, and only on the issue it was started from.

| Label | Meaning | Applied by |
| --- | --- | --- |
| `agent-ready` | A human has reviewed the issue, confirmed the acceptance criteria are complete and testable, and authorises an implementation agent to start. Applying it starts `agent-implement.yml`. | **Human only** |
| `agent-working` | An implementation agent has started and owns a feature branch for this issue. | `agent-implement.yml`, at the start of its run (replaces `agent-ready`) |
| `agent-review` | On an **issue**: a pull request is open and awaiting independent review. On a **pull request**: a human requests the independent review; `agent-review.yml` runs for the current head and again on later pushes while the label is present, and `agent-repair.yml` accepts `@claude repair` comments. | Issue: `agent-implement.yml` when its run ends with an open PR. Pull request: **human only** |
| `agent-blocked` | The agent stopped because validation/review failed after the permitted repair attempts, or because a human decision or permission is required. The issue or PR must state the exact blocker. | `agent-implement.yml` when its run ends without an open PR; otherwise human (including after the second failed repair) |
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
- **Repair counting is human-controlled.** A repair attempt happens only when a human with write access comments `@claude repair` (optionally followed by instructions) on the pull request while it carries `agent-review`; that starts `agent-repair.yml`. The workflow does not count attempts, does not start on a failed check or review, and does not chain into another run. The human who requests a repair is responsible for not requesting a third one. Mechanical enforcement of the limit is future work and is **not implemented**.
- After the second failed repair attempt, the human labels the issue `agent-blocked` and takes over. There is no third attempt and no implementation/review loop without a human in between.
- The agent must stop **immediately**, without attempting a repair, when the acceptance criteria conflict with each other, with `AGENTS.md`, with `docs/architecture.md`, or with existing tests, or when the requirements are materially ambiguous.
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
| Pull request | GitHub PR targeting `develop` | Uses the PR template; links the issue; describes validation, risk, and impact. |
| Validation result | Agent's PR description and the CI check | Actual commands and actual results; never claimed without running. |
| Agent run | GitHub Actions run of `agent-implement.yml`, `agent-review.yml`, or `agent-repair.yml`, linked from the closing comment | Full log of what the agent read, ran, and changed. |
| Review result | PR review/comments | Review-agent review (comment or request-changes) plus human review. |
| Human merge decision | PR merge by a human | Records who accepted the change into `develop`. |
| Release/deployment record | `develop` → `main` release PR and the workflow run | Applies when the change reaches production. |

A change whose chain is broken (for example a PR without an issue, or a validation claim without evidence) is not eligible for automated handling and must be reviewed as an ordinary human change.

## Branch and release policy

- Work branches start from the latest `develop`.
- Normal feature and fix pull requests target `develop`.
- `develop` is the integration branch. A push to `develop` runs backend build and tests (`vm-manager.yml` build job) but does not deploy.
- Production releases use a **separate** pull request from `develop` to `main`.
- A merge or push to `main` deploys the API and the frontend to Azure through the existing workflows, and the API applies EF Core migrations at startup.
- An agent may prepare or update a `develop` to `main` release pull request only when a human explicitly requests it. Permission to implement a feature never grants permission to create a release pull request. Agents never merge or deploy: a human reviews and merges the release pull request, and the existing workflow performs the deployment.
- Production deployments and production migrations require human control. There is no automated path from an issue to production.

## Recommended repository protection checklist

> **Manual configuration checklist.** These are GitHub repository settings, not repository files. This document recommends them; it does not enable them and does not verify that they are enabled. A maintainer must configure and confirm each item in the GitHub UI or API.

| Setting | Recommendation |
| --- | --- |
| Pull requests required | Require a pull request before merging into `develop` and `main`. |
| Required status check | Require the existing `Validate pull request` / "Backend tests and frontend build" check on both branches. |
| Force pushes | Block force pushes to `develop` and `main`. |
| Branch deletion | Block deletion of `develop` and `main`. |
| Conversation resolution | Require all review conversations to be resolved before merging, where available. |
| Bypass | Do not allow automation credentials (GitHub Apps, tokens used by agents, `GITHUB_TOKEN`) to bypass protected-branch rules. The agent workflows rely on branch protection as the final guard against a push to `develop` or `main`. |
| Deployment credentials | Keep Azure deployment secrets/variables and the `VmInventoryApi_Env` environment unavailable to implementation and review agents. The agent workflows request none of them. |
| Agent credential | Keep `CLAUDE_CODE_OAUTH_TOKEN` a repository secret used only by the agent workflows; rotate it by regenerating it with `claude setup-token`. Do not make it available to forks (GitHub already withholds secrets from fork pull requests). |
| Workflow branches | The agent workflows must exist on `main` (where `issues` and `issue_comment` workflows run from) and on `develop` (where `pull_request` workflows targeting `develop` run from). Keep them identical on both. |
| Production approval | Configure a human-controlled required reviewer on the production deployment environment, when available on the repository's plan. |
| Review count | Require at least one human approving review on `main`; consider the same for `develop`. |

`docs/architecture.md` already notes that branch protection and required-check configuration live in repository settings and must be enabled separately from source-controlled workflows.

## Implementation phases

Each phase is delivered as its own pull request and must be proven reliable before the next begins.

| Phase | Scope | Status |
| --- | --- | --- |
| 1 | Automation documentation and templates: this document, the agent task issue form, the PR template, and `AGENTS.md`/`README.md` updates. | **Implemented** |
| 2 | Issue label to implementation-agent PR creation: labels, an implementation agent connected to `agent-ready` issues, and `agent-working`/`agent-review`/`agent-blocked` transitions. No merge or deploy authority. | **Implemented** with Claude Code (`agent-implement.yml`, `CLAUDE.md`). Labels were created by a human in repository settings. Subject to the [workflow-token limitation](#workflow-token-limitation). |
| 3 | Independent automated review and bounded repair: a separate review step, its written result, and the two-attempt repair limit enforced mechanically. | **Partially implemented.** The independent review step exists (`agent-review.yml`), and a human-invoked repair step exists (`agent-repair.yml`). Mechanical enforcement of the two-attempt limit is **not implemented**: repair attempts are requested and counted by a human. |
| 4 | Staging deployment and smoke tests: a non-production environment deployed from `develop` with automated smoke checks. No staging environment exists today. | Proposed |
| 5 | Controlled release PR and production approval: a `develop` → `main` release PR process with human environment approval. | Proposed |
| 6 | Production monitoring and proposed issue creation: monitoring that can draft issues for humans to review; it must not apply `agent-ready`. | Proposed |
| 7 | Possible low-risk auto-merge, only after the earlier phases have proven reliable and only for `risk:low` tasks, with a human able to disable it at any time. Any such merge would be performed by repository automation configured by a human, never by the implementation or review agent, and would require an explicit update to this document. | Proposed, not committed |

Phases 4–7, and the mechanical repair-limit enforcement of phase 3, are not implemented by this repository at the time of writing. Any claim that one of them exists must be backed by a workflow or configuration file in this repository and a corresponding update to this document.
