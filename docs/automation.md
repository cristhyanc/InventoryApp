# InventoryApp automated development lifecycle

## Purpose

This document defines how InventoryApp intends to run a controlled, automated software-development system:

```text
GitHub issue → implementation agent → feature branch → pull request → CI validation
→ independent review → human approval → develop → release PR → main → Azure deployment → monitoring
```

It describes the lifecycle, the authority of each participant, task states, risk classification, failure handling, traceability, branch policy, and the incremental phases in which the system will be built.

It deliberately separates four things:

| Category | Meaning |
| --- | --- |
| **Exists now** | Behaviour implemented by files in this repository today (workflows, scripts, instructions). |
| **Added by this document's pull request** | Governance contracts and templates: this document, the agent task issue form, the pull request template, and `AGENTS.md`/`README.md` updates. |
| **Proposed for future pull requests** | Automation that is designed here but **not implemented**: labels, issue-to-PR automation, automated review, bounded repair, staging, release automation, monitoring. |
| **Human-controlled** | Decisions that stay with a human regardless of how much automation is added. |

Nothing in this document creates an automation capability by itself. Where a capability is described as future work, it does not exist until a later pull request implements it and this document is updated.

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
9. A separate reviewer (initially a human; later a review agent plus a human) evaluates requirements, architecture, security, tests, and scope.
10. If validation or review fails, the implementation agent may make **no more than two** automated repair attempts.
11. If the pull request still fails, or the requirements are materially ambiguous or conflicting, the task becomes blocked and returns to a human with a precise statement of the decision needed.
12. A human decides whether to merge into `develop`.
13. A separate release pull request from `develop` to `main` controls production deployment.
14. Production deployment and any production migration remain human-controlled.

Today, steps 1, 2, 5, 6, 7, 8, 9 (human review), 12, 13, and 14 are performed by humans or by existing CI. Steps 3, 4, 10, and 11 describe conventions and future automation; the labels and automated transitions do not exist yet.

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

### Which workflows can deploy

| Workflow | Deploys | Trigger that deploys |
| --- | --- | --- |
| `validate.yml` | Nothing | — |
| `vm-manager.yml` | API to Azure App Service | `push` to `main` only |
| `azure-static-web-apps-red-island-0c128c000.yml` | Frontend to Azure Static Web Apps | `push` to `main` only |

There is no staging environment, no automated review workflow, and no issue-to-pull-request automation in this repository.

### Where agents stop today

`AGENTS.md` requires every agent to work on a feature branch, run complete validation, open a pull request, and stop. Agents do not merge and do not deploy. Because a merge to `main` deploys production, this boundary is a safety control, not a convention.

## Roles and authority

The authority matrix below applies to every phase. "Future" marks a participant that does not exist yet.

| Capability | Human owner/maintainer | Implementation agent | Review agent (future) | CI (`validate.yml`) | Deployment workflows |
| --- | --- | --- | --- | --- | --- |
| Create or refine an agent task issue | Yes | No (may propose in a comment) | No | No | No |
| Apply `agent-ready` | Yes | **No** | No | No | No |
| Read repository files | Yes | Yes | Yes | Yes | Yes |
| Create a feature branch from `develop` | Yes | Yes | No | No | No |
| Modify files within the approved issue scope | Yes | Yes | No | No | No |
| Add or update tests | Yes | Yes | No | No | No |
| Run repository validation | Yes | Yes | May run read-only checks | Yes | Build/test steps only |
| Commit and push the feature branch | Yes | Yes | No | No | No |
| Open and update a pull request | Yes | Yes | Comment/review only | No | No |
| Report blockers | Yes | Yes | Yes | Via failed check | Via failed run |
| Broaden acceptance criteria | Yes | **No** | No | No | No |
| Approve a pull request | Yes | No | May recommend | Reports status | No |
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

### Review agent (future)

When an automated review step is introduced, it must:

- Be independent of the implementation step: a different invocation, context, and credentials from the implementation agent, so that it cannot be steered by the implementation agent's own reasoning.
- Evaluate the pull request against the issue's acceptance criteria and exclusions, `AGENTS.md`, `docs/architecture.md`, test evidence, security, and scope.
- Produce a written result attached to the pull request.
- **Never** merge, approve on behalf of a human, deploy, or modify the feature branch.

Until that step exists, review is performed by a human.

### CI

CI (`validate.yml`) validates pull requests independently of the agent's own validation run. It reports status; it does not merge, deploy, or apply labels.

### Deployment workflows

The deployment workflows run only on a push to `main`. They are not invoked by agents, review steps, or CI. Their credentials are repository/environment secrets and variables that are not available to the implementation or review agent.

## Task states and proposed labels

The following labels are **proposed**. This document defines their meaning; it does not create them. No file in this repository creates, applies, or transitions labels. Creating the labels and wiring their transitions belongs to the later issue-to-PR automation pull request (phase 2).

| Label | Meaning | Applied by |
| --- | --- | --- |
| `agent-ready` | A human has reviewed the issue, confirmed the acceptance criteria are complete and testable, and authorises an implementation agent to start. | **Human only** |
| `agent-working` | An implementation agent has started and owns a feature branch for this issue. | Future automation |
| `agent-review` | A pull request is open and awaiting independent review. | Future automation |
| `agent-blocked` | The agent stopped because validation/review failed after the permitted repair attempts, or because a human decision or permission is required. The issue or PR must state the exact blocker. | Future automation or human |
| `risk:low` | See risk classification. | Human at triage |
| `risk:medium` | See risk classification. | Human at triage |
| `risk:high` | See risk classification. | Human at triage |

Rules:

- A human applies `agent-ready`. Submitting the issue form does not apply it, and the form does not reference it.
- An agent must not start from an issue that has not been reviewed and labelled `agent-ready` by a human.
- An agent must not apply `agent-ready` to any issue, including one it drafted.
- The state labels are mutually exclusive: an issue is in at most one of `agent-ready`, `agent-working`, `agent-review`, or `agent-blocked`.
- Removing `agent-blocked` and returning an issue to `agent-ready` is a human decision.

Proposed state flow:

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

- An implementation agent may make **at most two** automated repair attempts for a pull request failure (a failing CI check or a rejecting review). The original implementation does not count as an attempt.
- After the second failed repair attempt, the agent stops, the task moves to `agent-blocked`, and a human takes over. There is no third automatic attempt and no implementation/review loop without a human in between.
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
| Review result | PR review/comments | Human review now; review-agent output plus human review later. |
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
| Bypass | Do not allow automation credentials (GitHub Apps, tokens used by agents, `GITHUB_TOKEN`) to bypass protected-branch rules. |
| Deployment credentials | Keep Azure deployment secrets/variables and the `VmInventoryApi_Env` environment unavailable to implementation and review agents. |
| Production approval | Configure a human-controlled required reviewer on the production deployment environment, when available on the repository's plan. |
| Review count | Require at least one human approving review on `main`; consider the same for `develop`. |

`docs/architecture.md` already notes that branch protection and required-check configuration live in repository settings and must be enabled separately from source-controlled workflows.

## Implementation phases

Each phase is delivered as its own pull request and must be proven reliable before the next begins. Only phase 1 is delivered by the pull request that introduces this document.

| Phase | Scope | Status |
| --- | --- | --- |
| 1 | Automation documentation and templates: this document, the agent task issue form, the PR template, and `AGENTS.md`/`README.md` updates. | **This PR** |
| 2 | Issue label to implementation-agent PR creation: create the labels, connect an implementation agent to `agent-ready` issues, and wire `agent-working`/`agent-review`/`agent-blocked` transitions. Provider-neutral; no merge or deploy authority. | Proposed |
| 3 | Independent automated review and bounded repair: a separate review step, its written result, and the two-attempt repair limit enforced mechanically. | Proposed |
| 4 | Staging deployment and smoke tests: a non-production environment deployed from `develop` with automated smoke checks. No staging environment exists today. | Proposed |
| 5 | Controlled release PR and production approval: a `develop` → `main` release PR process with human environment approval. | Proposed |
| 6 | Production monitoring and proposed issue creation: monitoring that can draft issues for humans to review; it must not apply `agent-ready`. | Proposed |
| 7 | Possible low-risk auto-merge, only after the earlier phases have proven reliable and only for `risk:low` tasks, with a human able to disable it at any time. Any such merge would be performed by repository automation configured by a human, never by the implementation or review agent, and would require an explicit update to this document. | Proposed, not committed |

Phases 2–7 are not implemented by this repository at the time of writing. Any claim that one of them exists must be backed by a workflow or configuration file in this repository and a corresponding update to this document.
