# InventoryApp automated development lifecycle

## Purpose

This document defines how InventoryApp intends to run a controlled, automated software-development system:

```text
GitHub issue → implementation agent (Claude) → feature branch → pull request → read-only architecture check by Copilot
→ implementer fixes findings → CI validation → final review by Copilot → human approval → develop → release PR → main (releasable) → human-started Deploy Production → monitoring
```

It describes the lifecycle, the authority of each participant, task states, risk classification, failure handling, traceability, branch policy, and the incremental phases in which the system will be built.

It deliberately separates three things:

| Category | Meaning |
| --- | --- |
| **Exists now** | Behaviour implemented by files in this repository today: the validation and deployment workflows, the Claude Code implementation, Copilot architecture-check and review, repair and updated-head scheduling workflows (`agent-implement.yml`, `agent-architecture.yml`, `agent-review.yml`, `agent-review-request.yml`, `agent-repair.yml`, `agent-head-update.yml`; see [Cross-review: Claude implements, Copilot reviews](#cross-review-claude-implements-copilot-reviews)), the validation scripts, the workflow-contract and documentation-impact validators in `scripts/`, `AGENTS.md`, `CLAUDE.md`, the agent task issue form, the pull request template, the [documentation impact gate](#documentation-impact-gate), and the deterministic [Agent Evals](#guardrails-ci-validation-review-and-evals) corpus/runner in `evals/agent/`. |
| **Proposed for future pull requests** | Automation that is designed here but **not implemented**: mechanical enforcement of the two-attempt repair limit, staging, release automation, monitoring, and any auto-merge. |
| **Human-controlled** | Decisions that stay with a human regardless of how much automation is added: choosing the route by applying exactly one readiness label (`agent-ready-claude` by default, or the `agent-ready-full-claude` fallback label), authorising and counting repair attempts (and the fresh review that follows one), approving, merging, releasing, deploying. The *initial* final review is requested automatically once validation succeeds; it is no longer a separate human decision. |

Nothing in this document creates an automation capability by itself. Where a capability is described as future work, it does not exist until a later pull request implements it and this document is updated.

The agent provider is **Claude Code**, run through the `anthropics/claude-code-action` GitHub Action. `CLAUDE.md` at the repository root directs Claude to read and obey `AGENTS.md`, this document, `docs/architecture.md` where relevant, and the linked issue's acceptance criteria and exclusions.

`AGENTS.md` is the authoritative engineering and safety policy, and `docs/architecture.md` is the authoritative architectural description. This document does not restate their financial, inventory, database, Nayax, or security invariants; it refers to them.

## Guardrails, CI validation, review, and evals

Four distinct mechanisms keep an automated change safe, and it matters which one caught a
problem:

| Mechanism | Question it answers | Where it lives |
| --- | --- | --- |
| **Guardrails** | What is an agent allowed to do at all? | `AGENTS.md`, `CLAUDE.md`, this document, the `.github/workflows/agent-*.yml` prompts and tool permissions, `scripts/validate-agent-workflows.mjs`. |
| **CI validation** | Does this specific change build, test, and lint? | `scripts/validate.sh`/`scripts/validate.ps1`, `validate.yml`. |
| **Final review** | Did this specific pull request actually honour the guardrails? Independent (the other provider) on the cross routes; same-provider, and so not independent, on a full-provider fallback. | `agent-review.yml`. |
| **Evals** | Do representative scenarios still resolve the way the guardrails say they should, across changes to the guardrails themselves? | `evals/agent/` (corpus and deterministic runner in `scripts/run-agent-evals.mjs`; on-demand model-decision runner in `scripts/run-agent-model-evals.mjs`; see `evals/agent/README.md`). |

Evals are the odd one out: they do not validate a specific pull request's diff, and today's
deterministic runner does not simulate an agent's decision on a scenario at all. It checks that
the guardrail text/workflow permission/distinguishing code each eval case cites is still present,
so a change to a prompt or a policy document that silently weakens an invariant is caught as a
failing eval case even when no single application pull request happens to exercise it.

**Model-decision evals** (`node scripts/run-agent-model-evals.mjs --provider claude-cli`, issue #249)
add a second, on-demand layer: six critical synthetic scenarios (direct stock overwrite,
MachineRefill as COGS, tenant-filter bypass, an unexpected migration, a conflicting high-risk
requirement, and a safe low-risk change as the `proceed` control) are sent to a model, which must
answer with a structured `proceed`/`reject`/`stop` decision. The runner grades that answer
deterministically against each case's hidden expected decision and forbidden-outcome checks; the
model never sees the rubric and never grades itself, and a failed critical case fails the run.
Boundaries:

- It runs only when a person types the command. `--provider` has no default; `scripts/validate.sh`
  and `scripts/validate.ps1` run only its unit tests (fixture responses, no model call), and no
  workflow runs it.
- The only live provider reuses the Claude Code CLI already installed and signed in on the machine
  running it. Each call is tool-less and runs in an empty temporary directory, so the model sees
  only the prompt: `AGENTS.md`, `CLAUDE.md`, the cited policy sections and the synthetic scenario.
  No repository files, case answers, credentials or business data reach it.
- It adds no secret, credential, GitHub permission, workflow or provider. Running it in GitHub
  Actions, or with a new key or provider, is a separate change that needs human review first.
- Results are advisory and carry their reproducibility metadata (eval and prompt version, git
  SHA, corpus hash, CLI version and reported models, timestamp). They never replace the
  deterministic evals, CI, the final review, or human approval, merge and deployment.

`node scripts/run-agent-evals.mjs --mode model` still prints prompts for the hand-graded
`requiresModelEvaluation` cases (`AUTH-006`, `INV-005`, `INV-006`), which are not in the
model-decision set yet. See `evals/agent/README.md` § Model-decision evals.

**When a pull request changes an agent policy or prompt file** — `AGENTS.md`, `CLAUDE.md`, this
document, or a prompt/instruction block inside `agent-implement.yml`, `agent-architecture.yml`, `agent-review.yml`, or
`agent-repair.yml` — re-run `node scripts/run-agent-evals.mjs` (already part of
`scripts/validate.sh`/`scripts/validate.ps1`) and read its result deliberately rather than treating
it as routine backend/frontend noise. A failing case means the change altered or removed something
an eval case relies on: either update the affected case(s)/`policyReferences` in the same pull
request because the guardrail is intentionally changing (and say so, per `AGENTS.md` § Tests
required by change type), or treat the failure as a regression and fix the guardrail file instead.

## Desired lifecycle

### Project tracking

The optional [Project status sync](project-status-sync.md) mirrors agent labels
and current-head validation/review results into the Status field of existing
InventoryApp issue cards in user Project #4. It is one-way display automation:
moving a card does not grant readiness, request a repair, close an issue, merge
or deploy. Parent epics remain manual. It requires a dedicated Projects credential
and explicit activation after the workflow reaches main; see the linked setup,
precedence, race limitations and board-rule requirements.

The target lifecycle for one automated change is:

1. A human creates or refines an agent task issue using the **Agent task** issue form, including its documentation impact decision and details.
2. A human confirms that the acceptance criteria are complete, testable, and bounded by explicit exclusions, and that the documentation impact decision is correct.
3. A human applies a readiness label, which chooses the route: normally `agent-ready-claude` (Claude implements, Copilot reviews), or the `agent-ready-full-claude` fallback label when Copilot is unavailable (see [Provider roles and readiness labels](#provider-roles-and-readiness-labels)). A read-only preflight job validates that the issue carries a meaningful documentation impact declaration; when it does not, nothing else runs and the issue is left unchanged.
4. An implementation agent creates a feature branch from the latest `develop`.
5. The agent implements only the approved scope defined by the issue's acceptance criteria and exclusions, and updates the documentation the issue's decision requires.
6. The agent runs the complete repository validation (`scripts/validate.sh` or `scripts/validate.ps1`).
7. The agent opens a pull request targeting `develop`, using the pull request template, with an accurate `## Documentation impact` declaration. The route's checker (the other provider, or a separate read-only invocation of the same provider on a full-provider fallback) then checks the PR's architecture read-only, a deterministic job reads the open SonarCloud issues on the same head, and the implementer fixes any correct, in-scope findings and SonarCloud issues with narrowly scoped, behavior-preserving edits on the same feature branch. The architecture stage must finish before validation is dispatched.
8. CI independently validates the pull request, starting with the documentation impact declaration.
9. A separate reviewer (the other provider, or a separate same-provider invocation on a full-provider fallback, then a human) evaluates requirements, architecture, security, tests, validation evidence, scope, and whether the documentation impact declaration matches the issue and the diff.
10. If validation or review fails, a human may request a repair; the implementation agent may make **no more than two** repair attempts per pull request.
11. If the pull request still fails, or the requirements are materially ambiguous or conflicting, the task becomes blocked and returns to a human with a precise statement of the decision needed.
12. A human decides whether to merge into `develop`.
13. A separate release pull request from `develop` to `main` makes the change releasable. Merging it deploys nothing.
14. Production deployment is a separate human decision: a human starts the **Deploy Production** workflow for an exact `main` commit (issue #343). The resulting schema migration, if any, is then applied automatically by the API on its next Production startup (issue #201), not by a human command, and the workflow reports the expected migrations before it deploys.

Today: steps 1–3 are human, except that the preflight in step 3 is the deterministic first job of `agent-implement.yml`. Steps 4–7 are split across two workflows: `agent-implement.yml` performs coding, full validation, persistence and PR creation, then its trusted dispatcher invokes `agent-architecture.yml` from `main` for the exact published head SHA. The architecture workflow independently checks out that SHA, runs the architecture agent and publishes any narrowly scoped structural commit to the same branch. For step 8, only the architecture workflow's deterministic finalizer moves the issue/PR to `agent-review` and invokes trusted `validate.yml` from `main` for the exact final head SHA with `dispatch_review: true`; validation publishes the stable `agent-validation` commit status. For step 9, the initial final review by the route's reviewer (see [Provider roles and readiness labels](#provider-roles-and-readiness-labels)) is then dispatched automatically once `agent-validation` succeeds; no human action requests it. Step 10 exists only as a human-invoked repair (`agent-repair.yml`, started by an `@claude repair` comment on the pull request); the two-attempt limit is counted by the human, not by a workflow. When a repair actually pushes a new head, its separate dispatcher invokes exact-SHA validation, and successful validation dispatches a fresh review while the `agent-review` label (applied automatically at step 8, or by a human if ever reapplied) remains present. Any other new commit on an eligible labelled agent pull request (for example a human push or an "Update branch" merge) is scheduled the same way by `agent-head-update.yml`; see [Updated-head scheduling](#updated-head-scheduling). Step 11 is partly automatic (the implementation workflow labels the issue `agent-blocked` when implementation or architecture cannot complete with a verified PR head) and otherwise human. Steps 12–14 are human.

## Provider roles and readiness labels

Claude is the only implementer and Copilot is the normal independent reviewer. **Copilot no longer implements.** The owner retired the Copilot implementation route (the Copilot coding agent, its `agent-ready-copilot` and `agent-ready-full-copilot` labels, and the workflows that assigned Copilot and handed its pull requests to Claude); Copilot keeps only its reviewing role, through the Copilot CLI. A human picks the route per issue by applying exactly one readiness label:

| Route | Label | Implementer | Architecture check and final review | Status |
| --- | --- | --- | --- | --- |
| Default | `agent-ready-claude` (or `-low` / `-high`) | Claude | Copilot (independent, cross-provider) | Active. Use this whenever Copilot is available. |
| Single-provider fallback | `agent-ready-full-claude` | Claude | Separate read-only Claude invocations (same-provider review, not independent) | Fallback when Copilot is unavailable or out of credits. |

The full-provider label is a human-selected fallback for when Copilot is unavailable or out of credits; it is not a preferred route. Same-provider review is weaker than cross-provider review: the checker and reviewer share the implementer's provider, model family and blind spots, even though each runs as a fresh read-only invocation with no implementation conversation state and no publishing credentials. Their prompts, the architecture record on the pull request and the published review all say "same-provider", never "independent". Model triage stays on Claude Haiku on both routes (`agent-model-selection.yml` with `CLAUDE_CODE_OAUTH_TOKEN`). Nothing detects provider credits or switches provider automatically. When the selected provider fails (quota, credential or outage), the stage fails closed and the task becomes `agent-blocked` for a human decision. See [Single-provider fallback route](#single-provider-fallback-route).

The trusted parser in `scripts/agent-mode.mjs` (re-exported by `scripts/select-implementation-model.mjs`: `parseReadinessLabel`, `READINESS_LABEL_PATTERN`, `ROUTES`) maps each label to its implementer and reviewer; nothing comes from model output. The retired Copilot labels are not readiness labels any more: they start nothing, they do not count as a claim, and a `copilot/*` pull request is refused at every boundary (`verifyCopilotImplementationRetired` in `scripts/validate-agent-workflows.mjs` keeps the removed workflows and labels out). Exactly one readiness label may authorise a run: applying the full label together with any other readiness label makes the run fail before model triage or any label change. `FULL_PROVIDER_EXECUTION_ENABLED` in `scripts/agent-mode.mjs` is the kill switch for the fallback. It is `true` since #340; setting it to `false` makes every full-provider claim fail closed at every boundary, and the workflow contract checks then also require removing the full label from the implementation trigger. A human creates the full label in repository settings; creating it does nothing until the workflows that route it are on `main`.

### Provider mode provenance

The route a human chose must still be known after the readiness label is removed. It is never taken from a comment, from labels still on the issue, or from pull request text, because agents can write comments as `github-actions[bot]` with the job's `GITHUB_TOKEN`. It comes from the issue's own label history, which GitHub records and nobody can edit or delete:

- **Claim.** The claiming step (`Mark issue as agent-working` in `agent-implement.yml`) consumes readiness with one edit, `gh issue edit --remove-label <readiness label> --add-label agent-working`, as `github-actions[bot]`. It is the only workflow step that adds `agent-working` (contract-checked). No agent can change issue labels: every agent's tool list denies `gh issue edit` and `gh api`, and model triage has no shell. The claim comment that follows ("Provider mode: …") is for people only and is never read back.
- **Verify.** Each boundary that acts on an agent pull request downloads `scripts/agent-mode.mjs` from its own trusted workflow commit (`GITHUB_WORKFLOW_SHA`, through the contents API, never from a checkout or the pull request) and runs `verify-pr`. It reads the pull request (head branch, closing issue, creation time) and the issue's `labeled`/`unlabeled` events. The boundaries are the architecture dispatch in `agent-implement.yml`, the context job of `agent-architecture.yml`, exact-SHA validation and its review dispatch in `validate.yml`, the context and the pre-publication recheck of `agent-review.yml`, and both steps of `agent-repair.yml`. `agent-review-request.yml` and `agent-head-update.yml` are pure dispatchers that read only the shared eligibility guard (below), not the mode, so they rely on the workflow they dispatch, which verifies the mode before doing anything. The existing exact-head SHA checks are unchanged, so a new head is always revalidated and re-reviewed under the same mode, and the review publisher suppresses a verdict when the mode changed while the review ran.
- **Rules.** The newest claim (the newest `agent-working` added by `github-actions[bot]`) decides. It must have removed exactly one readiness label within a few seconds, and that label must have been applied before the claim by a person: GitHub's actor type must be `User`, so any bot (including the automation App) or a missing identity fails closed. The pull request must be Claude's `agent/issue-<n>-*` branch of the same issue, and it must have been opened after that claim, so a pull request left over from an earlier claim, or from an older run, is refused. A missing claim, an ambiguous claim, a claim that consumed a retired Copilot label, or a full-provider claim while that route is disabled fails closed. A provider is never switched automatically.
- **Why a later run cannot overwrite the choice.** The claim workflow uses the per-issue concurrency group `agent-implementation-issue-<n>` and refuses to claim while the issue carries an active state (`agent-working`, `agent-review`, `agent-blocked`). So claims are serialized, a retry or re-run of a claim step finds the label already consumed and adds no second claim, and only the claim's own run can open the pull request after it. Label events are ordered by GitHub, not by whoever writes last.
- **Tasks started before this change** have the same label history (readiness label applied by a person, swapped for `agent-working` by the claim step), so they resolve the same way, without any separate fallback.
- **Blocked recovery.** After `agent-blocked`, a human decides. To retry the same pull request, fix the cause and use the existing manual paths (re-apply `agent-review`, or `@claude repair` on the pull request); the claim is unchanged. To start over, possibly on the other route, close the old pull request first, then remove `agent-blocked` and apply one readiness label. The new claim is newer than the old pull request, so the old one is refused at every boundary from then on instead of being silently re-routed.

## Cross-review: Claude implements, Copilot reviews

On the default route Claude implements and Copilot checks the architecture read-only and does the final review, so the implementer never reviews its own work. The single-provider fallback is described separately in [Single-provider fallback route](#single-provider-fallback-route). A human still merges.

```text
agent-ready-claude
    ↓
Claude implements (agent-implement.yml)
    ↓
Copilot architecture check, read-only (Copilot CLI, agent-architecture.yml)
    ↓
Claude fixes the findings, if any
    ↓
Exact-SHA validation (validate.yml)
    ↓
Copilot final review (Copilot CLI, agent-review.yml)
    ↓
Human merge
```

| Stage | `agent-ready-claude` |
| --- | --- |
| Implementer, branch, PR author | Claude Code; `agent/issue-<n>-*`; the agent GitHub App bot (`AGENT_AUTOMATION_APP_BOT_LOGIN`) |
| Read-only architecture check | Copilot CLI in the `copilot-check` job of `agent-architecture.yml` (it runs only when the verified mode is `cross-claude`): `contents: read` only, no persisted checkout credentials, `--deny-tool='write'`, only `git diff/log/show` shell tools and the two read-only Nayax documentation tools (see [Nayax documentation access](#nayax-documentation-access)); the job fails if the working tree or `HEAD` changed. It ends with `ARCHITECTURE: CLEAN` or `ARCHITECTURE: FINDINGS`. |
| Fixing findings | Claude's `architecture` job runs only when Copilot reported findings. Claude verifies each finding, fixes the correct in-scope ones, and explains any it declines in a PR comment. With a clean result the job is skipped and the unchanged head goes to validation. |
| Final review | After successful exact-SHA validation, `validate.yml` dispatches `agent-review.yml`. Its `copilot-review` job runs the pinned Copilot CLI read-only (`--deny-tool='write'`, read-only `git` and `gh` shell tools, the two read-only Nayax documentation tools, read-only permissions; the job fails if the tree or `HEAD` changed) with the same review instructions and blocking rules as Claude. Copilot returns a marked JSON review that the job checks against the same contract as Claude's `--json-schema`; anything else fails the job. |
| Verdict | The deterministic publish job re-verifies the live PR and its provider mode, takes the review only from the job of the reviewer the verified route selected, binds the review to the exact SHA, posts a comment-only review headed with the reviewer's name and the review type (cross-provider or same-provider), and sets the per-SHA `agent-review-verdict` status (`success` for `READY FOR HUMAN REVIEW`, `failure` for `CHANGES REQUESTED`, `error` when suppressed). |
| Repairs | The owner comments `@claude repair` (`agent-repair.yml`); the repaired head is validated and Copilot reviews it again. |

Setup a human must do once (these are repository settings and credentials, which no agent may change):

- Create the label `agent-ready-claude`. The old `agent-ready` label no longer starts anything. The fallback label `agent-ready-full-claude` is created the same way, by a human, following [Rolling out, recovering and verifying provider routes](#rolling-out-recovering-and-verifying-provider-routes).
- Add repository secret `COPILOT_CLI_TOKEN`: a fine-grained personal access token whose only permission is Copilot Requests. It is the only Copilot credential, given only to the Copilot CLI architecture check and final review, which check out pull request code; both also set `COPILOT_AUTO_UPDATE=false`. Copilot code review is not used; the Copilot final review runs through the Copilot CLI.
- The Copilot CLI version used by the architecture check and final review is pinned by `.github/copilot-cli/package-lock.json`, read from the trusted workflow commit and installed with `npm ci --ignore-scripts`; change that lockfile to upgrade it.
- The architecture check, validation dispatches and both review jobs all run from trusted `main` through `workflow_dispatch`.

Every one of these workflows runs from `main`, so a change to them takes effect only after a `develop` → `main` release.

### Single-provider fallback route

`agent-ready-full-claude` runs the same stages, gates and state transitions as the default route, with every AI-assisted step on Claude. The verified provider mode (see [Provider mode provenance](#provider-mode-provenance)) selects each checker and reviewer job; the Copilot jobs are skipped, so Copilot is never invoked and its secret is never needed.

| Stage | `agent-ready-full-claude` |
| --- | --- |
| Model triage | Claude Haiku (`agent-model-selection.yml`), as on the default route |
| Implementation | Claude Code (`agent-implement.yml`), `agent/issue-<n>-*` |
| Read-only architecture check | `claude-check` job of `agent-architecture.yml`: a fresh Claude invocation with read-only permissions and tools, schema-validated `CLEAN`/`FINDINGS` output bound to the checked SHA, and a prompt that calls it a same-provider check. `copilot-check` is skipped. |
| Architecture record | The finalizer's PR comment is headed "Same-provider Claude architecture check" and states that it is not an independent provider. |
| Fixing findings and SonarCloud issues | Claude's `architecture` job, exactly as on the default route |
| Exact-SHA validation | Unchanged |
| Final review | `review` job of `agent-review.yml`: a fresh read-only Claude invocation told that it is a same-provider review, not an independent one. `copilot-review` is skipped. |
| Published verdict | `Reviewer: Claude (separate read-only invocation)`, `Review type: Same-provider review (full-claude fallback): not independent` |
| Repairs | `@claude repair` (`agent-repair.yml`) |
| Credentials used | `CLAUDE_CODE_OAUTH_TOKEN`, the automation App, `GITHUB_TOKEN`, `SONAR_TOKEN`; no Copilot secret |

Every new head is validated and reviewed again under the same verified mode, and the review publisher suppresses a verdict if the mode changed while the review ran. When the selected provider fails, the job fails closed: the architecture finalizer moves the task to `agent-blocked`, or the review publisher records an `error` `agent-review-verdict`. Nothing falls back to the other provider, weakens a check or skips a review. To switch route, a human follows the blocked-recovery steps in [Provider mode provenance](#provider-mode-provenance).

#### Limits of same-provider review

A same-provider check and review are a fallback, not an equivalent of the default route:

- The implementer, the checker and the reviewer share one provider, model family and billing credential, so they tend to share blind spots. A mistake the provider makes systematically is likely to be missed by its own review.
- Separation is by invocation only: a fresh job, working tree, prompt and conversation, read-only tools and permissions, and no publishing or App credential. That prevents the reviewer being steered by the implementation, but it does not make the review independent.
- A `READY FOR HUMAN REVIEW` verdict with `Review type: Same-provider review (...): not independent` therefore deserves more human scrutiny than a cross-provider verdict, especially on `risk:high` tasks. For a high-risk task that can wait, prefer waiting for Copilot and using the default route.
- The verdict stays advisory on every route. It never approves, merges, releases or deploys, and it does not change branch protection or the human merge gate.

### Rolling out, recovering and verifying provider routes

Every agent workflow runs from `main` and downloads `scripts/agent-mode.mjs` from its own trusted workflow commit, so the workflows and the trusted scripts always come from the same commit; there is no separate script rollout.

Rollout of a routing change:

1. Merge the reviewed pull request into `develop`. On `develop` nothing changes for live tasks, because the agent workflows run from `main`.
2. Release `develop` → `main` with one normal release pull request. Never copy single workflow files or scripts to `main`: a `main` with a label in its triggers but without the routing jobs, or the reverse, is not a supported state. The contract checks require the full label in the implementation trigger exactly when `FULL_PROVIDER_EXECUTION_ENABLED` is `true`, and the routing jobs are on the same commit.
3. After that release reaches `main`, confirm the setup in the checklist below. A human creates `agent-ready-full-claude` in repository settings. Creating the labels does not enable anything by itself: the fallbacks work only because the released `main` routes them. A label created before the release only lets a run fail closed on the older `main`.
4. Tasks claimed before the release keep their route: their label history resolves the same way (see [Provider mode provenance](#provider-mode-provenance)), and a pull request already in review keeps its reviewer.

Existing pull requests and blocked runs:

- To retry the same pull request, fix the cause and use the manual paths: re-apply `agent-review`, or comment `@claude repair` on the pull request. The claim, and so the route, is unchanged.
- To start over, or to move a task to the other route, close the old pull request first, then remove `agent-blocked` and apply exactly one readiness label. Never re-apply a readiness label while the old pull request is open: that starts a second implementation, and the old pull request is then refused at every boundary.
- Never add a second readiness label to change route while a task is active, and never remove `agent-working` or `agent-review` by hand to make a new claim possible. Both start a duplicate run or leave the old pull request without a valid route. Only the claim step and the deterministic finalizers move these labels.

Verification checklist, for one low-risk test issue per route after a release (a human applies the label and reads the results):

| Check | `agent-ready-claude` | `agent-ready-full-claude` |
| --- | --- | --- |
| Claim | The readiness label is replaced by `agent-working`, and the claim comment says `Provider mode: cross-claude` | The same, with `full-claude` |
| Architecture check | `copilot-check` runs; `claude-check` is skipped | `claude-check` runs, and the PR record is headed "Same-provider Claude architecture check"; `copilot-check` is skipped |
| Exact-SHA validation | `agent-validation` succeeds on the head SHA the review names | The same |
| Final review | `copilot-review` runs; `Review type: Cross-provider review (Claude implemented, Copilot reviewed)` | `review` runs; `Review type: Same-provider review (full-claude fallback): not independent` |
| Verdict | One comment-only review and an `agent-review-verdict` status on the same SHA | The same |
| Credentials | `CLAUDE_CODE_OAUTH_TOKEN`, `COPILOT_CLI_TOKEN`, the automation App, `SONAR_TOKEN` | No Copilot secret is read (the Copilot jobs are skipped) |

Also confirm on either route that a test failure or provider error ends in `agent-blocked` or an `error` verdict and never in the other provider running. The pipeline refuses any task that changes `.github/workflows/**` on every route, so workflow changes, including any fix found by this checklist, go through a normal human-reviewed pull request into `develop`.

Rollback:

- To turn the fallback off, open a reviewed pull request that sets `FULL_PROVIDER_EXECUTION_ENABLED = false` in `scripts/agent-mode.mjs` and removes `agent-ready-full-claude` from the triggers of `agent-implement.yml` (the contract checks require both together), then release it to `main`. Every full-provider claim then fails closed at every boundary, including tasks already in flight, which a human recovers as above. The default route is unaffected. A human may also delete the label.
- To undo a routing change, revert its pull request through a normal reviewed pull request and release again. Nothing in a routing change touches secrets, branch protection, deployment or data.

## Nayax documentation access

Issue #192 gives the agents read-only access to Nayax's official developer documentation through its MCP server, so that Nayax request/response contracts are verified against the documentation instead of inferred. The binding rule for when and how to use it, and how to fail safely, is `AGENTS.md` § Nayax contract verification; every covered prompt repeats it in one paragraph.

- **Server:** `https://devzone.nayax.com/mcp`, an HTTP MCP server configured under the name `nayax`. It is configured without any credential and no secret is added; if the server ever required one, the agents would simply be unable to reach it and Nayax work would report that verification could not be completed. Only two of its tools are granted: `search_nayax_developer_portal` and `query_docs_filesystem_nayax_developer_portal`, both read-only queries over the public documentation and OpenAPI specs. Its `submit_feedback` tool, which would post to Nayax, is explicitly denied.
- **Claude invocations** (`agent-implement.yml` implementation, `agent-architecture.yml` architecture fix and same-provider check, `agent-repair.yml` repair, and the `review` job of `agent-review.yml`): `claude_args` adds `--mcp-config` with only that server, adds `mcp__nayax__search_nayax_developer_portal` and `mcp__nayax__query_docs_filesystem_nayax_developer_portal` to `--allowedTools`, and adds `mcp__nayax__submit_feedback` to `--disallowedTools`. Every other allowed and disallowed tool is unchanged; `WebFetch` and `WebSearch` stay disallowed.
- **Copilot CLI analysis steps** (the `copilot-check` job of `agent-architecture.yml` and the normal `copilot-review` invocation in `agent-review.yml`): `--additional-mcp-config` adds the same server with its `tools` filter limited to the two documentation tools, `--allow-tool='nayax(<tool>)'` approves exactly those two, and `--deny-tool='nayax(submit_feedback)'` denies feedback. The existing shell and `--deny-tool='write'` rules are unchanged.
- **Excluded:** model triage (`agent-model-selection.yml`) and the format-only Copilot JSON repair in `agent-review.yml` get no Nayax MCP configuration and perform no lookup. The repair keeps `--disable-builtin-mcps`, `--deny-tool='shell'`, `--deny-tool='write'` and `--deny-tool='url'`. The repository deliberately has no workspace `.mcp.json` or `.github/mcp.json`, which the Copilot CLI would otherwise load for every invocation, including these two.
- **Availability:** the server is consulted only for Nayax work. If it is unreachable, a non-Nayax run continues normally; a Nayax run must report that authoritative verification could not be completed instead of guessing contract fields.
- **Enforcement:** `verifyNayaxDocumentationAccess` in `scripts/validate-agent-workflows.mjs` requires the exact configuration, tool grants, denial and prompt paragraph in each covered invocation, and rejects any Nayax MCP access in model triage or the format-only repair.
- **Delivery:** like every change to `.github/workflows/**`, this configuration is not delivered through the agent pipeline (which refuses workflow changes) and takes effect only after a `develop` → `main` release, because the agent workflows run from `main`.

## Implementation model tiers

These tiers apply only to the initial implementation invocation. Architecture checks, architecture/Sonar fixes, final reviews, format recovery and human-requested repairs retain their existing models. In the rest of this document, the unsuffixed readiness labels also describe their corresponding low/high implementation paths unless a particular trigger is stated.

| Human readiness label | Implementation selection |
| --- | --- |
| `agent-ready-claude-low` | Claude `haiku`, at most 300 turns; no triage |
| `agent-ready-claude` | A short read-only Haiku triage chooses `haiku` (300 turns), `sonnet` (150 turns) or `opus` (400 turns) |
| `agent-ready-claude-high` | Claude `opus`, at most 400 turns; no triage |

The existing labels remain the default tier. Apply exactly one readiness label per issue (the three labels above and the `agent-ready-full-claude` fallback label all count; the full label has no fixed tier and uses the same Haiku triage as the unsuffixed label); remove an existing readiness/state label before choosing another. The default tier selects low, standard or high automatically, choosing the least expensive tier adequate for the task; high is chosen for complex cross-cutting design, broad structural changes, or difficult concurrency, security or data correctness. Needing a stronger model is not a reason to stop. Triage returns `clarification-required` only when the requirements, acceptance criteria or exclusions are materially ambiguous; the selection job then fails before implementation or label mutation, and the run log gives the reason. Clarify the issue, then remove and re-apply the readiness label. Use the explicit `-low` or `-high` label to skip triage. A low-tier agent that cannot finish within its model must report the limitation and stop for a human; it cannot upgrade itself. Model tier does not lower the task's risk classification, testing requirements or review gate.

`agent-model-selection.yml` is a reusable read-only job called only after the existing documentation preflight succeeds. It checks out trusted workflow policy at `github.workflow_sha`, snapshots the live issue and validates readiness. Explicit low/high labels use a deterministic mapping with no AI invocation. Default labels make one Haiku invocation (at most eight turns, Read only, no shell/edit/web/sub-agent tools), using only the issue title and body as untrusted data. Preparation rejects missing, blank or non-string title/body values before invoking a model. The complete snapshot is JSON-encoded on one step-output line and supplied directly in the triage prompt; no task-file read is needed, and issue text is never interpolated into shell commands. Its strict output selects a tier, never a model ID or CLI argument. Missing/malformed output fails closed with the issue unchanged. Selection is validated and mapped by `scripts/select-implementation-model.mjs`; the chosen tier/model/reason is recorded in the run summary and, once implementation starts, the issue comment.

The implementation workflow uses the queued `agent-implementation-issue-<n>` concurrency group. Closed issues, conflicting readiness labels (including a standard label alongside the full-provider label) and active/blocked state labels are rejected. The selection job and the pre-claim recheck call `scripts/select-implementation-model.mjs`. Before claiming the task, the implementation step rechecks the live title/body fingerprint and readiness; an edit requires reapplying readiness. This is a best-effort pre-mutation check, not an atomic GitHub issue lock. Claude reruns of the same workflow may retain `agent-working` so the existing checkpoint-resume mechanism continues working; a new run cannot claim an active issue.

Claude receives an explicit `--model`, an `availableModels` allowlist containing only that alias, and the selected turn limit. Provider/model access failures must be surfaced, never retried on a more expensive model. Claude aliases resolve to the provider's supported family version. Adjust mappings through a reviewed change to the trusted script if model availability changes.

Rollout: merge into `develop`, then release the workflows to `main` before using the new labels. A maintainer creates the two labels in repository settings: `agent-ready-claude-low` and `agent-ready-claude-high`. For example, run `gh label create <label> --repo cristhyanc/InventoryApp --description "Human-approved implementation model tier"` for each. No workflow creates or self-applies readiness labels. The existing `CLAUDE_CODE_OAUTH_TOKEN` is reused; no new secret is required. This reduces implementation usage but does not cap total subscription credits or change Opus repairs.

## Current repository behaviour

This section is derived from the triggers, conditions, and jobs in `.github/workflows/` and from `scripts/`, as inspected when this document was written. If a workflow changes, update this section in the same pull request.

### Pull-request validation

`.github/workflows/validate.yml` ("Validate pull request"):

- Triggers on `pull_request` events of type `opened`, `synchronize`, `reopened` and `edited` whose base branch is `develop` or `main` (`edited` is included so that editing the pull request description reruns the documentation impact gate), and on `workflow_dispatch` with required `pr_number` and `head_sha` inputs plus an optional `dispatch_review` boolean.
- Runs in one of two **validation modes**, which never share a concurrency group or a commit-status context (see [Validation modes](#validation-modes)):
  - For a normal `pull_request` event, preserves **merge-result validation**: it checks out the event's merge commit and publishes the `merge-validation` commit status on same-repository PR heads.
  - For a `workflow_dispatch`, performs **exact-SHA validation**: the trusted workflow definition runs from `main`, verifies that the PR is open, non-draft, targets `develop`, is a Claude implementation (a same-repository `agent/issue-*` branch authored by the dedicated App bot named in `AGENT_AUTOMATION_APP_BOT_LOGIN`), still has the supplied exact head SHA, and does not change `.github/workflows/**`; it then checks out only that SHA and publishes the authoritative `agent-validation` commit status.
- Each status is linked to its run and is `pending` before validation and `success` or `failure` afterward. The context job resolves the status context once, from `github.event_name`, and refuses to continue if the mode and the context disagree; the status job publishes only that resolved context.
- The context job also obtains the pull request body in both modes (from the `pull_request` event payload, or with `gh pr view --json body` for a dispatch) and passes it to the validation job as a base64-encoded job output. The body is untrusted text: it travels through an environment variable, never through an expression in a `run:` script, and no GitHub token is given to the job that checks out pull request code merely to retrieve it.
- Runs **"Backend tests and frontend build"** in a separate job with only `contents: read`, `persist-credentials: false`, no GitHub token, and no Anthropic or deployment secret. It installs .NET 10 and Node.js 20, decodes the body into a file under `$RUNNER_TEMP` (outside the checkout) and runs `node scripts/validate-documentation-impact.mjs --pr-body` on it **before** anything else, so an invalid `## Documentation impact` declaration fails the job and therefore the normal `merge-validation` or `agent-validation` status; then runs `node scripts/validate-agent-workflows.mjs` to enforce the dispatcher/permission/guard/concurrency/status/documentation-gate contract, `node --test scripts/validate-agent-workflows.test.mjs` and `node --test scripts/validate-documentation-impact.test.mjs` (the deterministic contract and parser tests), and then `bash scripts/validate.sh`.
- `scripts/validate.sh` and `scripts/validate.ps1` perform the same steps: `dotnet restore`, `dotnet build --configuration Release`, `dotnet test`, `npm ci`, and `npm run build` for the Angular application. The frontend has no configured test or lint script; its gate is the production build.
- When a dispatched validation (from the implementation, repair or updated-head dispatcher) succeeds with `dispatch_review: true`, a separate job with `actions: write` and no checkout reverifies the current SHA and guards, requires `agent-review`, and dispatches `agent-review.yml` from `main`, which runs the route's reviewer (the Copilot CLI on the default `cross-claude` route). It holds only `actions: write`, `pull-requests: read` and `issues: read`, changes no label, and holds no Copilot credential. When `agent-review` is absent it records a notice and dispatches nothing.
- Concurrency is scoped by validation mode **and** PR number (`validation-<workflow>-<event_name>-<pr_number>`): a newer run of the same mode for the same PR cancels the superseded one, while `pull_request` and `workflow_dispatch` validation of the same PR run independently and cannot cancel each other, and another PR's validation is never affected.
- Does not approve, merge, release, deploy, change labels, or start a repair.

### Push to `develop`

`.github/workflows/vm-manager.yml` ("Build and test API") triggers on `push` to `develop` and to `main`. Its only job restores, builds, and tests the backend solution `backend/InventoryApi/InventoryApi.slnx` in Release configuration, so both test projects (`backend/Inventory.UnitTests` and `backend/Inventory.IntegrationTests`, issue #311) run, with coverage collected. It has no publish, Azure login or deploy step, and only `contents: read`.

A push to `develop` therefore runs backend build and tests but **does not deploy** the API or the frontend.

### Push or merge to `main`

A merge of a pull request into `main` is a push to `main`. It runs `vm-manager.yml`'s backend build and tests, exactly as a push to `develop` does, and **deploys nothing** (issue #343). `main` means approved, releasable code; production changes only when a human starts [Deploy Production](#deploy-production).

### Deploy Production

`.github/workflows/deploy-production.yml` ("Deploy Production") is the only workflow that changes production. It has only a `workflow_dispatch` trigger, so it runs only when a human starts it from the Actions tab. It takes two optional inputs: `sha`, the full 40-character `main` commit to deploy (empty means the `main` commit the run starts from), and `baseline_sha` (see the migration preflight below). One production deployment runs at a time; a second waits rather than cancelling the first.

The jobs run in this order, and each stops everything after it when it fails:

1. **Resolve release commit** (`contents: read`, `deployments: read`). Refuses any run not started from `refs/heads/main` before it checks anything out, requires a full commit SHA, and verifies that the SHA is contained in `main`. That one SHA is the release for the whole run; every later job checks out or downloads artifacts named by it, never a branch tip.
2. **Validate release commit** (`contents: read` only, no environment, no secrets). Runs `bash scripts/validate.sh` on the exact SHA. Pull-request validation validates the PR merged with its base, which is a different commit from the `main` merge commit, so the release is validated again here.
3. **Migration preflight** (`contents: read`). Production keeps its data in SQLite on the App Service, and a runner cannot read its applied-migration history, so the preflight derives the *expected* pending migrations from the repository: the EF Core migrations in the release commit minus those in the **baseline**, the last backend commit production is known to run healthily (`scripts/deployment-migration-preflight.mjs`). The baseline is the newest production release this workflow recorded after a successful health check (a GitHub deployment on `VmInventoryApi_Env` with task `deploy:production-backend`); before the first recorded release, the operator supplies `baseline_sha`, and the run fails closed when neither exists or when both exist and differ. The run also fails closed when the scan finds no migrations, or when the baseline contains migrations the release lacks (older code over a newer schema; startup never rolls a migration back). When the release adds migrations, the run summary lists them and the log warns before any deployment step. The preflight never connects to production and never applies a migration.
4. **Build release artifacts** (`contents: read`). Publishes the API (`dotnet publish` of `InventoryApi.csproj`; the whole publish output is deployed, so other publish content such as a WebJob ships with it) and builds the Angular bundle once, from the release SHA, as artifacts named `api-<sha>` and `frontend-<sha>`.
5. **Deploy API** (environment `VmInventoryApi_Env`, `contents: read` and `id-token: write`). Repeats the migration warning, re-checks that the SHA is still contained in `main` immediately before Azure authentication (environment approval can come much later than the run started), logs in to Azure with OIDC and deploys the `api-<sha>` package to `vm-manager`.
6. **Verify API health** (no permissions). Polls `GET /health/ready` on the deployed API for up to about 15 minutes, because startup may be applying migrations. The URL comes from the non-secret repository variable `PRODUCTION_API_BASE_URL` when it is set, otherwise from the deploy step's `webapp-url`. The API does not expose which build is answering, so this proves the API is ready, not which commit it runs.
7. **Record production backend release** (`deployments: write` only). Runs only after the health check succeeded, and records the SHA as the next run's baseline. A successful package upload alone is never recorded: when the API then fails to start, fails a migration or never becomes healthy, the baseline stays at the last healthy release. The next run then lists this release's migrations again even if startup already applied some of them, which over-reports rather than hides them, and the run summary says the unhealthy package was not recorded.
8. **Deploy frontend** (environment `VmInventoryApi_Env`, `contents: read`). Runs only after the API deployed, passed its health check and was recorded. Re-checks the SHA against `main`, then uploads the prebuilt `frontend-<sha>` bundle to Azure Static Web Apps with the app build skipped, so the deployed bundle is the one built from the release SHA. It authenticates with the existing Static Web Apps deployment token (a repository secret; Static Web Apps upload does not use Azure OIDC), and then checks that the site answers.
9. **Deployment summary** (always runs, no permissions). Publishes the release SHA, baseline SHA, validation, preflight and migration result, build, backend deployment, release record, health check and frontend result.

Both deploy jobs use the `VmInventoryApi_Env` environment, so a required reviewer on that environment approves the API and the frontend separately. A failed run changes nothing else: it never reverts or merges code, never changes a label, and never starts an agent or repair. Recovery is a human decision, normally fixing forward through a new release, or restoring a verified backup when a migration must be undone.

Deploying the API restarts the process, and that restart applies its database schema (issue #54, revised by issue #201). **Normal Production startup applies pending migrations automatically**, the same as Development and `Testing`, and fails closed with a `DatabaseMigrationFailedException` naming the environment and the pending migrations if the attempt does not succeed — the API never starts and never serves requests against a schema its code does not match. A non-Production environment that is neither Development nor `Testing` (an ephemeral integration or Staging database, for example) still applies nothing by default and fails closed with a `PendingMigrationsException` instead, unless `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` is `true` for that disposable database. The explicit `migrate-database` command shipped inside the published application (`dotnet InventoryApi.dll migrate-database --dry-run`, then `--apply`, after taking a verified backup — see `docs/tenant-rollout.md`) remains available; it is no longer mandatory before a normal Production deployment, but is still how an operator inspects what a pending deployment will apply, or applies a high-risk migration ahead of a deployment window under review. Recovery from a failed or unwanted apply — automatic or manual — is a database restore from a verified backup, not a further automated migration: neither startup nor `migrate-database` ever rolls back a migration, and re-running either only applies what is still pending.

### Implementation workflow

`.github/workflows/agent-implement.yml` ("Agent implementation"):

- Triggers only on `issues` / `labeled`, and its jobs run only when the added label is `agent-ready-claude`, `agent-ready-claude-low` or `agent-ready-claude-high` (and the entity is an issue, not a pull request). No other event, comment, mention, or label starts it. Like every `issues`-triggered workflow, GitHub runs the copy on the default branch (`main`).
- A separate **`preflight`** job runs first with only `contents: read` and `issues: read`. It checks out trusted `develop` with `persist-credentials: false`, reads the issue body with `gh issue view --json body`, and runs `node scripts/validate-documentation-impact.mjs --issue-body` on it. It has no Anthropic credential, runs no Claude step, creates no branch, and changes no label or comment. When it fails, it emits an `::error` annotation stating exactly what to fix (edit the issue's documentation impact decision/details, then remove and re-apply `agent-ready-claude`), and the implementation job is skipped because it depends on both `preflight` and the separate read-only `model` selection job, with successful results required from both. The check is deliberately not a step inside the implementation job: that job's always-running outcome step would otherwise relabel the issue `agent-blocked` after a preflight failure.
- The implementation job permissions are `contents: read`, `pull-requests: write`, and `issues: write`. Claude receives only the job-scoped `GITHUB_TOKEN` for read/comment operations; checkout persists no Git credential. A deterministic post-Claude step separately receives the short-lived App token for push/PR creation. The implementation job has no `actions: write`; a separate no-checkout dispatcher job holds `actions: write` only long enough to invoke trusted `agent-architecture.yml` from `main`.
- Checks out `develop` at its latest commit, installs .NET 10 and Node.js 20, and moves the issue from `agent-ready-claude` to `agent-working`.
- Runs Claude Code with a fixed prompt that requires it to read the issue, restate the acceptance criteria, exclusions and documentation impact decision/details, create `agent/issue-<n>-<slug>` from the checked-out `develop`, make the smallest change with tests, follow the issue's documentation impact decision (update every listed documentation file, and any documentation the change would otherwise contradict), perform all inspection and implementation in the single foreground invocation without Claude sub-agents or the `Agent` tool, run `bash scripts/validate.sh` synchronously in the foreground and wait for its exit result, commit the completed branch locally and prepare validated untracked PR title/body scratch files. Tool permissions allow file edits, `dotnet`, `npm --prefix frontend/inventory-app`, exact foreground `bash scripts/validate.sh`, the documentation-impact validator, read-only Git inspection, scoped `git rm --`/`git restore --staged --`/`mkdir -p` under normal project directories, local branch creation, `git add`/`commit`, and read/comment GitHub operations. Claude itself cannot push, create/edit a PR, edit labels, call `gh api`/`gh workflow`, reset/rebase, switch to protected branches, use web access, or delegate to another Claude agent. After Claude exits, deterministic steps verify the local package, mint the short-lived App token, push the branch, create the PR, and verify that the App-authored PR head matches the local HEAD. The separate architecture workflow applies the same foreground-only execution rule to its own Claude invocation.
- When the coder succeeds and the local branch, clean working tree and commit match a verified open pull request targeting `develop` from an `agent/issue-<n>-*` branch, the implementation job outputs its PR number and implementation head SHA and leaves the issue in `agent-working`. If coding or publication fails, it moves the issue to `agent-blocked`. The separate architecture workflow owns the next state transition: success moves the issue and pull request to `agent-review`; failure moves the issue to `agent-blocked`.
- A separate `dispatch-architecture` job has `actions: write`, `pull-requests: read`, and `issues: write`, but no checkout and no Anthropic or deployment secret. It reverifies the open, non-draft, same-repository, bot-authored `agent/issue-*` PR, exact current SHA, `develop` base, issue-number branch identity and workflow-file exclusion, then dispatches `agent-architecture.yml` from trusted `main`. It cannot label the PR or dispatch validation; its issue-write authority exists only so that any failure before the dispatch succeeds (a failed guard, an unavailable GitHub API, or the dispatch itself) moves the issue from `agent-working` to `agent-blocked` with a comment, instead of leaving it stuck.
- `agent-architecture.yml` has five jobs: a read-only context guard, the read-only Copilot architecture check (`copilot-check`), the read-only SonarCloud read (`sonar`, see [SonarCloud issues in the architecture stage](#sonarcloud-issues-in-the-architecture-stage)), the Claude fix job (`architecture`, which runs only when Copilot reported findings or SonarCloud reported open issues), and a deterministic finalizer that records Copilot's findings and the SonarCloud issues on the PR. The findings travel from the checker to the fix job and the finalizer as a run-bound artifact (see the architecture findings transport bullet in [Review workflow](#review-workflow)). The context guard verifies the exact PR head, App-bot author, `develop` base, same-repository `agent/issue-<n>-*` branch, linked `agent-working` issue and workflow-file exclusion. The architecture job checks out the exact SHA with no persisted credentials, may commit narrowly scoped structural edits locally, and only a post-Claude GitHub App step can push them. The finalizer re-fetches the current PR head; only after architecture succeeds does it apply `agent-review` to both PR and issue and dispatch `validate.yml` from trusted `main` with `dispatch_review: true`. The finalizer runs even when the context guard rejects the target, and any failure before validation is dispatched (a rejected target, a failed architecture job, a PR head that moved, or a failed dispatch) moves the issue to `agent-blocked` with a comment and removes any `agent-review` it applied. It only blocks an issue that is still `agent-working` or that it labelled itself, so a stale or mistaken manual dispatch cannot block an issue already in review. Like the repair dispatcher, it skips the dispatch when the final SHA already has an `agent-validation` status, because the architecture push can also reach `agent-head-update.yml`; it therefore also needs `statuses: read`. `scripts/agent-architecture-handoff.test.mjs` runs both trusted steps against a fake `gh` to prove these paths. It also runs each checker's real package step and the fix job's real store step together, and the finalizer against missing, tampered and mismatched findings artifacts.
- Concurrency group `agent-implementation-issue-<n>` uses `cancel-in-progress: false` so a newer label event cannot interrupt the persistence boundary. `agent-architecture.yml` similarly serializes one PR with `cancel-in-progress: false`.
- Does not merge, deploy, apply an `agent-ready-*` label, or push to `develop` or `main`.

### Review workflow

`.github/workflows/agent-review.yml` ("Agent review"):

- Runs the reviewer the verified provider mode selects: the `copilot-review` job (pinned Copilot CLI, read-only) for `cross-claude`, and the `review` job (a separate read-only Claude invocation) for the `full-claude` fallback. The context job records the implementer, the mode and the reviewer; the publish job re-derives the implementer and mode from the live pull request, suppresses the result if either differs, and accepts only the route's reviewer, so the cross route never publishes a review by Claude and the full route never publishes Copilot's (see [Cross-review: Claude implements, Copilot reviews](#cross-review-claude-implements-copilot-reviews) and [Single-provider fallback route](#single-provider-fallback-route)). On the full route both the prompt and the published review say it is a same-provider review, not an independent one. Both prompts tell the reviewer that a separate read-only architecture check ran before validation, and require it to re-verify the architecture and confirm the earlier findings were resolved on the exact SHA; an architecture violation or unresolved finding is a blocker.
- Triggers only on `workflow_dispatch` with required `pr_number` and `head_sha` inputs, and its context job fails unless the run is a `workflow_dispatch` of `main`. It has no `pull_request` trigger: for a `pull_request` event GitHub sets `github.sha` to the pull request's merge commit, so the workflow and the pinned Copilot CLI manifest it installs (`.github/copilot-cli/`, read at `github.sha`) could come from the pull request. The automatic initial, post-repair and updated-head reviews are dispatched by `validate.yml` after successful exact-SHA validation; a manual re-review is dispatched by `agent-review-request.yml` (below), always with `--ref main`. `synchronize` deliberately does not trigger review directly, so a push cannot start review before its new SHA is validated; since issue #268 a new commit is reviewed again only through `agent-head-update.yml`, which schedules exact-SHA validation first (see [Updated-head scheduling](#updated-head-scheduling)). A deliberate human re-request, including a fresh review of a head that was already reviewed, goes through `.github/workflows/agent-review-request.yml`: a `pull_request_target` `labeled` workflow (so it always runs from `main`) with no checkout and only `actions: write`, `pull-requests: read`, `statuses: read` and `contents: read` (to fetch the shared eligibility guard from its own commit). When a human applies `agent-review`, it re-verifies the open, non-draft, same-repository agent pull request, its implementer author, its current head SHA, the workflow-file exclusion and a successful `agent-validation` status on that exact SHA, then dispatches `agent-review.yml --ref main`. Labels applied by automation with `GITHUB_TOKEN` start no workflow, so only a human's label reaches it.
- A dispatched post-repair review uses the trusted workflow from `main` and additionally requires an open, non-draft, same-repository `agent/issue-*` PR authored by `AGENT_AUTOMATION_APP_BOT_LOGIN` and targeting `develop`, the supplied current head SHA, the existing `agent-review` label, no `.github/workflows/**` change, and a successful latest `agent-validation` status on that exact SHA.
- The workflow has three jobs: `context` (resolve and verify), `review` (Claude, read-only) and `publish` (deterministic, no Claude). Review-job permissions are `contents: read`, `pull-requests: read`, `issues: read`, `actions: read`, `checks: read`, and `statuses: read`: the model job holds no write permission of any kind, so it cannot publish, comment, push, change labels, dispatch, approve, or merge even by another route. The publish job has only `pull-requests: write`, `statuses: write` and the read permissions it needs (`contents: read`, `issues: read`, and `actions: read` to read this run's review artifact), no checkout, and no Anthropic credential. The action keeps `allowed_bots: "github-actions[bot]"` because the automatic review is started by a trusted `workflow_dispatch` whose triggering actor is the Actions bot. That setting authorizes the workflow actor only; PR authenticity is checked separately against `AGENT_AUTOMATION_APP_BOT_LOGIN` via the REST pull-request author.
- The context job resolves one PR number, base, and exact head SHA. The review job checks out that SHA with persisted credentials disabled and passes it into the prompt. Every run is a fresh review of that SHA; it is instructed to disregard earlier review rounds except to check that earlier findings are resolved.
- The prompt requires it to read `CLAUDE.md`, `AGENTS.md`, this document, `docs/architecture.md` where relevant, the pull request and its diff, the linked issue's acceptance criteria and exclusions, the pull request's validation evidence, and the actual CI state, then evaluate requirements, invariants, architecture, tests, evidence, hygiene, risk classification, and documentation impact. For documentation impact it must compare three things: the issue's documentation impact decision and details, the pull request's `## Documentation impact` declaration, and the actual diff, verifying from the diff rather than from filenames alone. Missing, inaccurate or incomplete required documentation, or a declaration that contradicts the diff or silently ignores the issue decision, is a blocker. The gate adds no write authority to the review job.
- **Review judgment.** The prompt's blocking rules require an explicit `met`, `not met` or `not verified` assessment with evidence for every acceptance criterion; a criterion that could not be verified is `not verified`, never `met`. `VERDICT: READY FOR HUMAN REVIEW` is allowed only when every criterion is `met` and there are no blockers. A known unmet criterion or violated financial, inventory/costing, tenant or security invariant is a blocker and cannot be waived because legacy behaviour is worse, the failure seems unlikely, the tests pass, or Sonar or another gate is green. A materially ambiguous high-risk requirement becomes a blocker starting "Human decision required:" rather than an invented policy. For any stale-state or concurrency guarantee the reviewer must inspect the authoritative read, the expected-state comparison and the mutation as one operation (a transaction around only the write is not evidence of atomicity) and require a regression test in which the state changes between the read and the mutation.
- **Structured output, not publication.** The model returns its result only as schema-validated structured output (`--json-schema`): the reviewed head SHA, the verdict, the blockers, the per-criterion assessments, non-blocking suggestions, the validation evidence it confirmed, and inline comments (`path`, `line`, `body`). Its tool permissions allow read-only `gh`/`git` commands and the CI log tools; `gh pr review` (any form), the inline-comment tool, `gh pr comment`, file editing, `git push`/`commit`, `gh pr merge`/`edit`, label changes, `gh api`, `gh workflow`, and web access are denied.
- **Review result transport (PR #558).** The review never travels to the publisher as a job output. GitHub withholds a job output that contains any value it masks (`Skip output 'structured_output' since it may contain secret`), and on PR #558 (run 38050149174) that silently delivered an empty result for a review that had succeeded. Each reviewer job now ends with a `Package review result` step that runs `scripts/agent-review-transport.mjs`, fetched from the trusted workflow commit (`GITHUB_WORKFLOW_SHA`), never from the checked-out pull request head. It checks the review against the contract and the reviewed SHA, then redacts in three layers. First it removes the credentials the job actually holds by exact value: the step receives the job's `GITHUB_TOKEN` and its provider token (`CLAUDE_CODE_OAUTH_TOKEN` or `COPILOT_CLI_TOKEN`) as `REVIEW_REDACT_*` variables, compares against them without printing them, and removes their plain, base64, base64url and URL-encoded forms. These are the values GitHub masks in that job, and packaging fails closed if any of them would still be stored. Second it removes known credential formats (GitHub, Anthropic and other API tokens, private keys, JWTs, connection-string passwords and keys, bearer credentials and credentials in URLs). Third it removes unrecognised generated-looking tokens: 32 or more characters mixing upper case, lower case and digits, with high entropy and not built from words. It then wraps the review in an envelope bound to the repository, pull request number, head SHA, provider route, implementer, reviewer, workflow run id and run attempt. The pinned `actions/upload-artifact` uploads that one file as `agent-review-result-<reviewer>-<run id>` with one-day retention. Only the artifact id and the envelope's SHA-256 leave the job as outputs. The publish job then lists this run's artifacts through the Actions API and requires exactly one with that name. It must match the reported id and belong to this run, and it must be unexpired and within the size limit (1 MiB archive, 512 KiB envelope). The job downloads it, requires the archive to hold only the envelope, checks the digest, and re-verifies every binding, the contract and the reviewed SHA. It applies the redaction again and only then hands the review to the guards below. A missing, malformed, mismatched, stale, oversized or unverifiable artifact is a transport failure. That failure is recorded as an `error` status whose description starts `Review result not delivered:`, which keeps it distinct from a review that requested changes (`failure`). Nothing is published. When values were redacted, the published review says how many. The workflow `.github/workflows/agent-review-transport-check.yml` repeats the hand-off on real runners with a fixture, on any pull request that changes the transport script or `agent-review.yml`. It needs no secret or model and only read permissions. Its fixture keeps three values apart. A non-secret transport marker is masked only so that GitHub withholds the job output; its arrival proves the delivery. A fake shapeless "held" credential, passed as `REVIEW_REDACT_*`, must be absent from the stored artifact; this is the security guarantee. A fake `ghs_` token must be removed by format. **Limitation:** a secret the reviewer job does not hold and that has no recognisable shape, such as a short password the model read somewhere, cannot be detected. Such a value would be kept in the one-day artifact, which only repository members can read, and could be published. The exact-value layer covers every credential the review jobs are given.
- **Architecture findings transport.** `agent-architecture.yml` passes its checker's findings the same way, because they are model text too and a masked value would make GitHub withhold them. The checker job (`copilot-check` or `claude-check`) keeps only its verdict (`clean` or `findings`) as a job output. A `Package architecture findings` step runs the transport script's `package-findings` command from the trusted workflow commit (`git show "$TRUSTED_SHA:scripts/agent-review-transport.mjs"`, which the full-history checkout already holds). It redacts the text in the same three layers, using the credentials that checker gives its model (`COPILOT_CLI_TOKEN`, or `GITHUB_TOKEN` and `CLAUDE_CODE_OAUTH_TOKEN`), then cuts it to the same 60,000 characters as before, only after redaction. It binds the text to the repository, pull request, head SHA, route, implementer, checker (which must be the one the route names), run id and run attempt, and uploads it as `agent-architecture-findings-<checker>-<run id>` with one-day retention. Only the artifact id and the envelope digest become job outputs. The Claude fix job (which gains `actions: read`) and the finalizer fetch it with `fetch-findings`, which applies the same artifact checks as the review publisher, also requires the envelope's verdict to match the checker job's output, and redacts again. A finding that cannot be delivered or verified fails the fix job or the finalizer, so the task becomes `agent-blocked`; nothing is fixed, recorded or dispatched from it. The live transport check also round-trips fixture findings, and runs on pull requests that change `agent-architecture.yml`. SonarCloud's issue list still travels as a job output: it is SonarCloud's own text, not model text, and is left for a later change.
- **Guarded publication.** The `publish` job runs with `if: always()` after the review job, so it also runs when the review failed or was cancelled. It publishes nothing unless every check passes, and otherwise records why:
  1. The review job succeeded, its result arrived through the verified review artifact (above), and it is well formed, names exactly the reviewed SHA, and is internally consistent: a ready verdict with any blocker or any criterion not `met` is refused, as is a changes-requested verdict without a blocker or any second `VERDICT:` line.
  2. Immediately before publishing it re-fetches the pull request and re-applies every eligibility guard: open, not draft, `develop` base, same repository, `agent/issue-*` branch, App-bot author, `agent-review` label still present, no `.github/workflows/**` change, head still exactly the reviewed SHA, and a successful latest `agent-validation` status on that SHA. A failure to read any of this stops the job before anything is posted (fail closed).
  3. It renders the review body itself (reviewed SHA, the single verdict line, blockers, criteria, suggestions, evidence, a statement that the verdict applies only to that SHA, and the advisory sentence) and publishes one comment-only review through the REST reviews API with `commit_id` set to the reviewed SHA. If GitHub rejects an inline comment position, it publishes once more with the inline comments listed in the body. It never approves or requests changes: agent pull requests are authored by the dedicated App bot and GitHub rejects approve/request-changes reviews from a pull request's own author, so the verdict line carries the outcome.
- **Per-SHA outcome.** Every run that passed the context job ends with an `agent-review-verdict` commit status on the reviewed SHA only: `success` (ready for human review, advisory), `failure` (changes requested), or `error` (superseded or not published: the head moved, the pull request closed, became draft or ineligible, validation is no longer valid, the output was invalid, the review failed or was cancelled, the review result could not be delivered (`Review result not delivered: ...`), or publication failed closed; when the review was already posted and only the status write failed, the description says the review was posted). A stale or superseded run therefore never produces a ready result for the current head, and because both the review (`commit_id`) and the status are bound to one commit, a later push leaves the new head with no verdict until its own validation and review finish. The status is informational; this change does not add it to branch protection, which remains a repository setting under human control. Human approval and branch protection remain the merge gate.
- Concurrency group `agent-review-pr-<n>` with `cancel-in-progress: true` covers both event and dispatched runs: a newer review for the same PR cancels an older one, so at most one review of a pull request is active, and the cancelled run's `publish` job records `error` on the SHA it was reviewing.
- It does not use the multi-agent code-review plugin.

### Repair workflow

`.github/workflows/agent-repair.yml` ("Agent repair"):

- Triggers only on `issue_comment` / `created`. The job runs only when the comment is on an open pull request that carries `agent-review`, the comment body starts with `@claude repair`, and the commenter is the repository owner (`github.event.comment.user.login == github.repository_owner`; the owner/member/collaborator association check is kept as well). Widening this to other collaborators is a deliberate later change. Before checking anything out it verifies through the API that the pull request targets `develop`, is not a draft, and that its head branch is in this repository and is not `develop` or `main`.
- The repair job permissions are `contents: read`, `pull-requests: write`, `issues: write`, `actions: read`, `checks: read`, and `statuses: read`. Claude commits locally using only the job-scoped `GITHUB_TOKEN`; a deterministic post-Claude step mints the App token only when a new local commit must be pushed. It has no `actions: write`.
- Checks out the pull request's head branch, installs .NET 10 and Node.js 20, and runs Claude Code with the human's request comment, the review findings, and the CI state as input. The prompt requires a repair that changes behaviour, contracts, architecture, configuration, automation, deployment, operations or user workflows, or that addresses a documentation finding, to update the affected documentation on the head branch in the same repair. The agent cannot edit the pull request description: when the `## Documentation impact` declaration is no longer accurate, its closing comment must state exactly what the human must correct, and the `edited` trigger of `validate.yml` revalidates the description once it is changed. Tool permissions allow file edits, scoped `git rm --` for tracked files in `backend/`, `frontend/`, `docs/` and `scripts/` (never `.github/`), validation and build commands, `git add`/`commit`, and `git push origin <that head branch>` only; force pushes, `git checkout`/`switch`/`rebase`/`merge`, `gh pr create`/`merge`/`review`/`edit`, label changes, and `gh api` are denied.
- Always posts a closing comment on the pull request with the outcome and human next steps. It compares the starting and ending head SHAs. When the head changed, it outputs the new SHA for a follow-up job; when unchanged or unverifiable, it does not dispatch validation or review and does not claim that it did.
- A separate `dispatch-validation` job has `actions: write` and `pull-requests: read`, but no checkout and no Anthropic or deployment secret. It reverifies the open, non-draft, bot-authored, same-repository `agent/issue-*` PR, exact current SHA, `develop` base, existing `agent-review` label, and workflow-file exclusion, then dispatches `validate.yml` from trusted `main` with `dispatch_review: true`, unless the SHA already has an `agent-validation` status (the repair push also triggers `agent-head-update.yml`; whichever dispatches first owns the SHA). It therefore also needs `statuses: read`. Validation dispatches review only after it succeeds.
- **Repair counting is human-controlled.** The workflow does not count attempts and never starts on its own. The human who comments `@claude repair` is responsible for the two-attempt limit in the [failure and retry policy](#failure-and-retry-policy) and for labelling the issue `agent-blocked` after the second failed attempt.
- Concurrency group `agent-repair-pr-<n>` with `cancel-in-progress: true`.

### Updated-head scheduling

`.github/workflows/agent-head-update.yml` ("Agent head update"), added by issue #268:

- Triggers only on `pull_request_target` / `synchronize` for pull requests targeting `develop`. `pull_request_target` always runs the workflow definition from the default branch (`main`), never from the pull request, and the workflow never checks out or executes pull request code, so a pull request cannot change what its `actions: write` token does. A `pull_request`-triggered job would run the pull request's own copy of the file and must never hold dispatch authority.
- A job-level condition skips human, fork, draft and unlabelled pull requests without a failing check. The job (`actions: write`, `pull-requests: read`, `statuses: read`, and `contents: read` to fetch the shared eligibility guard from its own commit) then runs the same guard as the other dispatchers: open, not draft, `develop` base, same repository, an `agent/issue-*` branch with the App-bot author, `agent-review` label, no `.github/workflows/**` change, and the event's SHA still being the current head. An ineligible or already superseded pull request is skipped with a notice; a failure to read the live state fails the job without dispatching.
- **Deduplication.** If the SHA already has an `agent-validation` status (pending, success or failure), another path owns it (the implementation dispatcher, the repair dispatcher, or an earlier run) and nothing is dispatched. Otherwise it dispatches the trusted `validate.yml` from `main` with `dispatch_review: true`; validation publishes `agent-validation` and dispatches a fresh review only if it succeeds. The implementation dispatcher is unaffected: its pushes happen before it applies `agent-review`, so the scheduler skips them. A check-then-dispatch race between two dispatchers for the same SHA is collapsed by the per-PR `validate.yml` dispatch concurrency group, so only one validation, and therefore one review, completes.
- **Superseding.** Concurrency group `agent-head-update-pr-<n>` with `cancel-in-progress: true`. When pushes arrive in quick succession, the event for an older push sees that the head has moved and stands down; the newest head is validated, a newer exact-SHA validation of the same pull request cancels an older one, and a review still running for an older head is either cancelled by the newer review or suppressed by the publish job's head check.
- **Manual retries.** A human can still request a fresh review of an already validated head by removing and re-applying `agent-review` (which starts `agent-review-request.yml`), including after an earlier review completed; the concurrency group keeps it to one active review per pull request. Re-running validation for a SHA that already has a status is a manual `workflow_dispatch` of `validate.yml`; the automatic paths never re-dispatch it.
- Workflow-changing pull requests stay excluded from automated validation and review everywhere, including this scheduler; they are validated and reviewed manually.
- It does not review, approve, merge, label, push, or deploy.

`scripts/validate-agent-workflows.mjs` (`verifyReviewPublicationAndScheduling`) enforces the review prompt's blocking rules, the read-only review job and its denied publishing tools, the publish job's guards, commit binding and fail-closed outcome, and this workflow's trusted trigger, absence of checkout, guards and duplicate check; `scripts/validate-agent-workflows.test.mjs` proves each by mutation. `scripts/agent-review-publication.test.mjs` runs the actual shell of the publish step, this dispatcher and the repair dispatcher against a fake `gh` for an unchanged head, a head that moved during review, closed/draft/unlabelled/ineligible pull requests, missing, failed or pending validation, invalid or inconsistent model output, failed or cancelled reviews, rapid pushes, duplicate dispatch paths, and unreadable state (fail closed). It also runs the real `Package review result` step of both reviewer jobs and feeds the artifact it produces to the real publish step. That covers a review containing a masked, secret-like value (the PR #558 case), empty, invalid and wrong-SHA output, and a review that succeeded without an artifact. It also covers artifacts with the wrong id, digest, run, name count, expiry, size, archive contents, pull request, head, route, reviewer, repository, run or attempt. `scripts/agent-review-transport.test.mjs` unit-tests the redaction, the contract, the envelope and the command line. `verifyReviewTransport` in the contract checks rejects a reviewer job output carrying the review, a transport script run from the checkout, an unpinned or long-lived artifact, and a publisher without the artifact path. These are deterministic tests of the trusted workflow code, not evidence of how the model judges a pull request; the review-judgment eval cases in `evals/agent/` cover that separately.

**Activation.** Every workflow in this chain that matters for trust runs from `main`: `pull_request_target`, the `workflow_dispatch` of `validate.yml` and `agent-review.yml`. A change to them, including this one, takes effect only after a human merges the `develop` → `main` release pull request; until then the previous behaviour (including direct publication by the review model) remains active.

### Credentials used by the agent workflows

- **Copilot:** `COPILOT_CLI_TOKEN` (Copilot Requests permission only) is the only Copilot credential, used only by the read-only Copilot CLI architecture check and final review. See [Cross-review: Claude implements, Copilot reviews](#cross-review-claude-implements-copilot-reviews).
- **Anthropic:** every Claude invocation (implementation, architecture fix or check, Claude review, repair) authenticates to Anthropic with the repository secret `CLAUDE_CODE_OAUTH_TOKEN` (a long-lived Claude Code OAuth token) through the action's `claude_code_oauth_token` input. The Copilot CLI jobs never receive it. Claude invocations are permitted to share this billing credential; see [Shared billing credential, separate invocations](#shared-billing-credential-separate-invocations).
- **GitHub:** only the deterministic remote mutations that publish implementation, architecture and repair commits use a dedicated GitHub App installation token created just-in-time with `actions/create-github-app-token`. The App is installed only on this repository and is limited to Contents read/write and Pull requests read/write (Metadata read is implicit). Issue labels/comments stay on the job-scoped `GITHUB_TOKEN`, so the App does not need Issues permission. The token is short-lived, masked and revoked automatically by the action at job end. Deterministic workflow bookkeeping, status publication and workflow dispatch continue to use the job-scoped `GITHUB_TOKEN`; those dispatcher/status jobs never expose the App private key or token. The review agent remains read-only (its job token has no write permission) and the separate publish job uses its own job-scoped `GITHUB_TOKEN`. No Azure credential is introduced; the only personal access token is `COPILOT_CLI_TOKEN` above, which the Copilot CLI requires because it cannot run with an installation token.
- **GitHub App configuration:** repository variable `AGENT_AUTOMATION_APP_CLIENT_ID` contains the App client ID, repository variable `AGENT_AUTOMATION_APP_BOT_LOGIN` contains the canonical bot login (for example `inventoryapp-agent-automation[bot]`), and repository secret `AGENT_AUTOMATION_APP_PRIVATE_KEY` contains the private key generated by GitHub for that App. The App must be installed only on `cristhyanc/InventoryApp`; grant only Contents read/write and Pull requests read/write (Metadata read is implicit), and do not grant Issues, Actions, Administration, Secrets, Deployments, Environments or workflow-management permissions.
- **SonarCloud:** the architecture stage reads the public SonarCloud project without a credential. An optional repository secret `SONAR_TOKEN` (a SonarCloud token with Browse permission only) and optional variable `SONAR_PROJECT_KEY` are passed only to the read-only `sonar` jobs.
- **Pinned actions:** every third-party action in the agent workflows is pinned to a full commit SHA with the corresponding release tag in a comment: `anthropics/claude-code-action` v1.0.231, `actions/create-github-app-token` v3.2.0, `actions/checkout` v4.4.0, `actions/setup-dotnet` v4.3.1, `actions/setup-node` v4.4.0.

### Workflow-token behaviour

GitHub treats pull requests created or updated with the repository `GITHUB_TOKEN` specially: their `pull_request` runs for `opened`, `synchronize`, and `reopened` start in an approval-required state. InventoryApp avoids that manual gate by using the dedicated repository-scoped GitHub App token for the mutations that create or update agent pull requests.

- The implementation Claude creates and validates the feature branch commit locally but never receives the App credential. After Claude exits, a deterministic workflow step mints a fresh App token, pushes the branch and opens the pull request. Normal `pull_request` validation therefore starts automatically instead of waiting for **Approve workflows to run**.
- The architecture and repair Claude invocations likewise commit only locally. When either produces a new verified local commit, a deterministic step mints a fresh App token immediately before pushing it, so the resulting `synchronize` event starts normal PR validation automatically without exposing the App credential to Claude or risking expiry during a long agent run.
- `workflow_dispatch` remains the trusted exact-SHA path: no-checkout dispatcher jobs use `GITHUB_TOKEN` with `actions: write` only after verifying the PR number, current head SHA, base, author, head repository/branch and workflow-file exclusion.
- **One shared eligibility guard.** Every dispatcher and the review publisher check an agent pull request with the same trusted script, `scripts/agent-pr-guard.mjs`, instead of their own inline copies. The guarded steps are the dispatched context and the review dispatcher in `validate.yml`, the context and the publisher in `agent-review.yml`, `agent-head-update.yml`, `agent-review-request.yml`, the architecture dispatcher in `agent-implement.yml`, the context and the finalizer in `agent-architecture.yml`, and the validation dispatcher in `agent-repair.yml`. Each fetches the script from its own workflow commit (`GITHUB_WORKFLOW_SHA`, through the contents API, never from a checkout) and runs `check <pr> <expected sha>`, adding `--issue <n>` where the branch must belong to that issue. The script reads the live pull request and refuses, in this order, a pull request that is not open (`closed`), a draft (`draft`), one not targeting `develop` (`base`), one from another repository (`fork`), a branch that is not `agent/issue-*` or not the issue's (`branch`), a missing `AGENT_AUTOMATION_APP_BOT_LOGIN` (`config`), another author (`author`), a head that is no longer the expected SHA (`stale`), and any change under `.github/workflows/` (`workflow-files`). A live state it cannot read is `unavailable`. On success it prints the branch, head, implementer and labels as JSON; on a refusal it prints one `code: message` line and exits 2. Each workflow keeps its own response: most fail the job; `agent-head-update.yml` skips an ineligible pull request quietly but fails on `config`, `unavailable` or any guard fault; the publisher suppresses an ineligible result as an `error` status but stops without any status when the state cannot be read. Provider-mode verification, label, issue-state and validation-status checks stay in each workflow. `scripts/agent-pr-guard.test.mjs` covers every refusal and the command line, and the workflow contract rejects an inline copy of any check the guard owns. The creation check in `agent-implement.yml`, which verifies the pull request it has just opened, and the pre-repair check in `agent-repair.yml` are separate and unchanged.
- **The bot-author check reads the canonical login from the REST pull request endpoint.** The shared guard resolves the author with `gh api "repos/$GITHUB_REPOSITORY/pulls/<number>" --jq '.user.login // empty'` and compares it for exact equality with repository variable `AGENT_AUTOMATION_APP_BOT_LOGIN`. It must not use `gh pr view --json author`, whose GraphQL actor representation is not the canonical bot login.
- The dispatched workflow definition always comes from `main`. The validation job then checks out the separately verified PR SHA with a read-only token and persisted credentials disabled.
- The `agent-validation` status linked to the dispatched run remains the authoritative exact-SHA validation result for an agent-created or agent-updated PR. Normal `pull_request` validation independently publishes `merge-validation`.
- The `agent-review` label still authorises review; successful exact-SHA validation dispatches the route's final review automatically, for the initial head, a repaired head, or any later head scheduled by `agent-head-update.yml`. Human approval and merge remain required.

The one-time prerequisite is the dedicated GitHub App plus the two repository variables and one repository secret described above. App installation tokens expire after about an hour, so they are deliberately created only after an agent invocation has finished and immediately before the remote mutation. No PAT is used.

### Validation modes

`validate.yml` serves two callers that can fire for the same pull request at the same time: GitHub's own `pull_request` event (for every PR, including a human approving the duplicate run on an agent PR) and the trusted `workflow_dispatch` from the implementation and repair dispatchers. Before this contract existed, both shared one concurrency group and one `agent-validation` status context, so the later event could cancel the earlier run and the last status written, whichever mode produced it, became the "authoritative" exact-SHA result that `agent-review.yml` trusts. The contract is now:

| | `pull_request` event | `workflow_dispatch` |
| --- | --- | --- |
| Mode | Merge-result validation | Exact-SHA validation |
| Workflow definition | The PR's base branch | Trusted `main` |
| Checkout | The event's merge commit (`github.sha`) | The verified current head SHA only |
| Guards | Same-repository check before publishing a status | Open, non-draft, `develop` base, same repository, an `agent/issue-*` branch with author exactly matching `AGENT_AUTOMATION_APP_BOT_LOGIN`, exact current head SHA, no `.github/workflows/**` change |
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
| Decide whether the declaration is correct | Final review (`agent-review.yml`), then the human reviewer | Compares the issue decision, the pull request declaration and the actual diff. Missing, inaccurate or incomplete required documentation is a blocker. |
| Approve and merge | Human | Confirms the decision when applying a readiness label and again when approving and merging. Automation never approves, merges or edits a declaration. |

`scripts/validate-agent-workflows.mjs` enforces, and `scripts/validate-agent-workflows.test.mjs` proves by mutation, that both templates keep the contract, that the read-only preflight job exists before the implementation job and gates it, that `validate.yml` obtains the body in the trusted context job, hands it over base64-encoded and invokes the validator before repository validation without a GitHub token, that the implementation, architecture, review and repair prompts keep their documentation requirements, that the architecture pass cannot be skipped before validation dispatch, and that no reviewer or repair write authority (in particular `gh pr edit`) is added. `scripts/validate-documentation-impact.test.mjs` covers both valid decisions, missing and duplicate sections, invalid decisions, empty evidence, the HTML template placeholders, and generic answers.

**Backfill of issues #87–#92.** Those agent task issues predate the gate and do not contain the two documentation impact fields. The preflight runs from the copy of `agent-implement.yml` on the default branch, so it becomes active the moment this change reaches `main`. Before that, or at least before an `agent-ready-*` label is next applied to any of them, a human must edit each issue to add a `Documentation impact decision` section containing exactly one of the two options and a `Documentation impact details` section with specific content. The parser accepts both the form's `### Heading` rendering and the `## Heading` style those issues already use. Applying an `agent-ready-*` label to an un-backfilled issue fails the preflight, changes nothing on the issue, and reports the required edit in the run log. This repository change does not edit those issues.

### Which workflows can deploy

| Workflow | Deploys | Trigger that deploys |
| --- | --- | --- |
| `validate.yml` | Nothing | — |
| `agent-implement.yml` | Nothing | — |
| `agent-architecture.yml` | Nothing | — |
| `agent-review.yml` | Nothing | — |
| `agent-review-request.yml` | Nothing | — |
| `agent-review-transport-check.yml` | Nothing (read-only fixture check of the review and architecture findings artifact hand-offs) | — |
| `agent-head-update.yml` | Nothing | — |
| `agent-repair.yml` | Nothing | — |
| `vm-manager.yml` | Nothing (backend build and tests on `push` to `develop` and `main`) | — |
| `deploy-production.yml` | API to Azure App Service, then frontend to Azure Static Web Apps | `workflow_dispatch` started by a human from `main` only |

`scripts/validate-deployment-workflows.mjs` (run by `validate.yml`, with its tests) enforces this table: no workflow other than `deploy-production.yml` may contain an Azure login, App Service or Static Web Apps deploy step, the Static Web Apps token or the production environment, and Deploy Production must keep its manual-only trigger, `main` gate, single release SHA, job ordering and permission boundaries.

There is no staging environment and no automated path from an issue to `main`.

### Where agents stop today

`AGENTS.md` requires every agent to work on a feature branch, run complete validation, and open a pull request targeting `develop`. After that, the agent may update only its feature branch, for at most two permitted repair attempts in response to CI or review failures, each explicitly requested by a human, and then stops and returns control to a human. Agents do not merge and do not deploy. Because production changes only through the human-started Deploy Production workflow, and agents can neither merge to `main` nor start that workflow, this boundary is a safety control, not a convention, and the workflow permissions above are chosen so that the agents cannot cross it even if instructed to.

## Roles and authority

The authority matrix below applies to every phase. The implementation agent is the agent the issue's label chose: Claude Code run by `agent-implement.yml` (and, for human-requested repairs, `agent-repair.yml`) for `agent-ready-claude` and `agent-ready-full-claude`; Copilot no longer implements. On the default cross route the review agent is the Copilot CLI, run by `agent-review.yml`. On the full-claude fallback it is a separate, read-only Claude invocation, recorded as a same-provider review. The same split applies to the read-only architecture check.

| Capability | Human owner/maintainer | Implementation agent | Review agent | CI (`validate.yml`) | Deployment workflows |
| --- | --- | --- | --- | --- | --- |
| Create or refine an agent task issue | Yes | No (may propose in a comment) | No | No | No |
| Apply any readiness label (`agent-ready-*`: standard, tiered or full-provider) | Yes | **No** | No | No | No |
| Read repository files | Yes | Yes | Yes | Yes | Yes |
| Create a feature branch from `develop` | Yes | Yes | No | No | No |
| Modify files within the approved issue scope | Yes | Yes | No | No | No |
| Add or update tests | Yes | Yes | No | No | No |
| Run repository validation | Yes | Yes | No (reads the PR's evidence and CI results instead) | Yes | Build/test steps only |
| Commit and push the feature branch | Yes | Yes | **No** (read-only code access) | No | No |
| Open and update a pull request | Yes | Yes | Returns a review as structured output; a separate guarded job publishes it as a comment-only review; may not edit the PR | No | No |
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
| Deploy an environment | Yes, by starting Deploy Production for a `main` commit | **No** | **No** | No | Yes, only `deploy-production.yml` and only when a human starts it from `main` |
| Run production migrations | Yes, via the human-invoked `migrate-database --apply` command, for diagnostics or ahead of a deployment window | **No** | **No** | No | Indirectly: the API process applies pending migrations automatically on normal Production startup after a deploy (issue #201); the deployment workflow itself does not run migration commands; it reports the expected pending migrations before deploying (see [Deploy Production](#deploy-production)) |
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

- Mark its own issue with any readiness label (`agent-ready-*`).
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

The architecture stage is a read-only check by Copilot (on the full-claude fallback, by a separate read-only Claude invocation recorded as a same-provider check), followed by fixes from Claude. It runs once during initial implementation; a later human-requested repair does not restart it.

For `agent-ready-claude`, `agent-implement.yml` verifies the PR and dispatches `agent-architecture.yml` from `main` for the exact implementation SHA. Its `copilot-check` job runs the Copilot CLI read-only against `AGENTS.md` and `docs/architecture.md` on the touched code: controller and use-case boundaries, domain versus adapters, dependency direction, duplicated logic and testability. Only when Copilot reports findings does the Claude `architecture` job run. Claude verifies each finding, fixes the correct in-scope ones with no observable behavior, API, data or acceptance-criteria change, reruns complete validation after edits, and commits locally. Only a deterministic GitHub App step pushes the commit. Claude comments on the PR with each finding and whether it fixed or declined it. An edit that would require a migration, widen scope, or make the PR body or documentation inaccurate is reported for human review instead of being made.

#### SonarCloud issues in the architecture stage

SonarCloud analyses every push automatically and comments on the pull request, but its quality gate can pass while new issues remain open (pull request #285 passed with one). The architecture workflow therefore has a `sonar` job that hands those issues to the implementer in the same fix pass as the architecture findings:

- The job is deterministic and read-only (`contents: read`, `checks: read`, no model, no agent or App credential). It checks out only the trusted workflow commit (`github.sha` on `main`) and runs `scripts/sonar-new-issues.mjs`, never code from the pull request.
- The script waits up to 15 minutes for a completed `SonarCloud Code Analysis` check run from the `sonarqubecloud` app on the exact head SHA, then reads the pull request's unresolved issues from the SonarCloud Web API (`api/issues/search` with `pullRequest`). That search is keyed only by pull request number, so the script binds it to the head through GitHub. It does not use SonarCloud's analysis revision, because automatic analysis records the default branch's commit there, never the pull request head (pull requests #293 and #314 were both reported `unavailable` for that reason). Instead it reads the pull request's `refs/pull/<n>/head` before and after the issue search and hands issues on only when both equal the exact head SHA, and it requires the number of issues read to equal the "New issues" count in that head's check run summary, re-reading briefly while SonarCloud catches up. A newer push, a head that moves during the read, an issue list that still does not match the check run's count, or a check run whose conclusion shows no analysis (cancelled, timed out, stale, skipped) is reported as `unavailable`. A failed quality gate is still a completed analysis. Issues a human marked accepted or false positive in SonarCloud are resolved there, so they are not handed on. It works without a token for the public SonarCloud project; an optional `SONAR_TOKEN` secret and `SONAR_PROJECT_KEY` variable (default `<owner>_<repo>`) cover a private project or a different key.
- The read fails open: no analysis in time, an API error or a crashed job counts as zero issues and is recorded as `unavailable`. SonarCloud is a third party, so its outage never blocks a task.
- The Claude fix job runs when the check reported findings or SonarCloud reported issues. Claude receives the issue list as a file under `.git`, never interpolated into its prompt, verifies each issue, fixes the correct in-scope ones, and declines false positives with a reason. It must not silence an issue with `NOSONAR`, `#pragma warning disable`, `[SuppressMessage]`, `.editorconfig` or analyzer-configuration changes. The finalizer records the issues on the PR.
- The stage runs once. Issues introduced by the fix push itself, or by a later repair, show in SonarCloud's own pull request comment for the reviewer and the human; they are not fixed automatically.

The finalizer first re-verifies the current PR head. What follows depends on the path and the result:

- On success, on `agent-ready-claude` and `agent-ready-full-claude` alike, the finalizer labels the PR and issue `agent-review` and dispatches exact-SHA validation of the final head, which then dispatches the final review.

If any stage fails, the finalizer labels the issue `agent-blocked` and dispatches nothing. Neither architecture role can merge, deploy, alter workflow files, or approve a PR.

### Review agent

The review agent (`agent-review.yml`) is the reviewer the verified route selects: the Copilot CLI on `agent-ready-claude` (and its tiers), and a separate read-only Claude invocation on the `agent-ready-full-claude` fallback. It must, and is configured to:

- Be separate from the implementation step: a separate workflow run, a separate job with its own working tree, conversation, and prompt, and its own job-scoped GitHub permissions, so that it cannot be steered by the implementation agent's own reasoning. Each reviewer runs with only the model credential it needs: `CLAUDE_CODE_OAUTH_TOKEN` for Claude, or `COPILOT_CLI_TOKEN` for the Copilot CLI. A model credential only meters usage (see below). On the cross route the reviewer is also a different provider, so the review is independent. On the full-claude fallback it is the same provider, so the published review says `Review type: Same-provider review (...): not independent` (see [Limits of same-provider review](#limits-of-same-provider-review)).
- Review the pull request's current head SHA afresh on every run.
- Evaluate the pull request against the issue's acceptance criteria and exclusions, `AGENTS.md`, `docs/architecture.md`, the validation evidence in the pull request and CI, security, scope, and documentation impact (issue decision versus pull request declaration versus actual diff).
- Produce a written result for the exact reviewed SHA as structured output, which the separate deterministic `publish` job turns into inline comments and exactly one comment-only review bound to that commit, whose body carries one verdict line, `VERDICT: CHANGES REQUESTED` or `VERDICT: READY FOR HUMAN REVIEW`, with the blockers listed under it, and into the per-SHA `agent-review-verdict` status. Only when the pull request is still eligible and still at that SHA; otherwise the result is recorded as superseded and not published. Neither job submits an approve or request-changes review.
- Apply the blocking rules: every acceptance criterion assessed with evidence, no ready verdict with any criterion `not met` or `not verified`, no waiver of an unmet criterion or invariant, a human decision instead of an invented policy for ambiguous high-risk requirements, and read, comparison and mutation inspected as one operation for concurrency guarantees.
- **Never** merge, approve on behalf of a human, deploy, modify files, commits, branches, or labels, or edit the pull request or issue.

Its result is advisory. A human still reviews and decides whether to merge.

Whenever the Copilot CLI reviews (the `cross-claude` route), it cannot return schema-enforced structured output, so the `copilot-review` job asks for the review as one JSON object between `BEGIN_REVIEW_JSON` and `END_REVIEW_JSON` lines, takes the last such block, and checks it with `jq` against the same contract as Claude's `--json-schema` (exact keys, typed values, allowed verdicts and criterion statuses). Output that does not match fails the job, and the publish job then records an `error` verdict status instead of a review. Valid output goes to the same `Package review result` step and review artifact as Claude's review.

### Shared billing credential, separate invocations

Invocations of the same model **may share that model's billing credential**: every Claude invocation uses `CLAUDE_CODE_OAUTH_TOKEN`, and both Copilot CLI invocations (architecture check and final review) use `COPILOT_CLI_TOKEN`. No job receives the other model's credential. Independence is provided by separation of invocation, context, and authority, not by separate accounts:

- **Separate invocations:** implementation, architecture and review are separate workflows with separate runs, working trees, prompts, and tokens. The architecture pass no longer shares the implementation runner: it is dispatched from trusted `main`, checks out the exact implementation PR SHA in a fresh runner, and only a deterministic post-agent step may publish a structural commit back to that same feature branch. All three editing invocations (implementation, architecture and repair) may remove tracked files in project directories with `git rm -- <path>` when in scope; they cannot remove `.github/` files through that permission. The implementation and architecture agents may also recover from an accidentally staged project file with the narrowly scoped `git restore --staged -- <project-path>` permission; broad restore/reset and `.github/` cleanup remain forbidden. Both may create directories with `mkdir -p` only under `backend/`, `frontend/`, `docs/` and `scripts/`. The implementation, architecture and repair Claude steps each set `BASH_DEFAULT_TIMEOUT_MS` and `BASH_MAX_TIMEOUT_MS` to 30 minutes so the bare, allowlisted `bash scripts/validate.sh` can finish in the foreground; with the short default timeout an agent backgrounded or redirected validation, the literal-command allowlist refused it, and the run ended with uncommitted work (implementation run 36774979794, issue #240; repair run 37202054752 on PR #376, issue #361). The agent workflow contract tests reject broader deletion, staging-cleanup and directory-creation patterns and require both timeouts. A human starts Claude implementation with `agent-ready-claude`; the initial review is requested automatically, by `agent-implement.yml`'s dispatcher applying `agent-review` and successful validation dispatching the review. After a human starts a repair, successful exact-SHA validation may dispatch another independent review under that existing label.
- **Separate contexts:** each run has its own checkout, prompt, conversation, and tool configuration. The reviewer receives the head SHA, the pull request, and the issue, not the implementation agent's transcript or reasoning.
- **Separate, job-scoped GitHub permissions:** the implementation and repair jobs use repository-scoped GitHub App installation tokens only for branch/PR mutations and agent GitHub calls; deterministic bookkeeping/dispatch continues to use each job's `GITHUB_TOKEN`. The review job has read-only access to code and the pull request and cannot publish; only the separate deterministic publish job may post a comment-only review and a commit status, with its own `GITHUB_TOKEN`. The App private key is never exposed to the review or validation jobs.

Neither model credential grants authority over the repository: `CLAUDE_CODE_OAUTH_TOKEN` only meters Anthropic usage, and `COPILOT_CLI_TOKEN` carries only the Copilot Requests permission. Sharing them across invocations of the same model is therefore acceptable. Sharing a GitHub credential, a working tree, or a conversation between implementation and review is not.

### CI

CI (`validate.yml`) validates pull requests independently of the agent's own validation run, starting with the pull request's documentation impact declaration. Dispatched exact-SHA validation publishes `agent-validation` on the exact head SHA; event-driven merge-result validation publishes `merge-validation`. For a successful dispatched validation (initial, repair or updated head) it may dispatch the already-authorised review, but it does not modify code or labels, start a repair, approve, merge, release, or deploy.

### Deployment workflows

The only deployment workflow, `deploy-production.yml`, runs only when a human starts it from `main`. It is not invoked by agents, review steps, CI, or a push. Their credentials are repository/environment secrets and variables that are not available to the implementation or review agent.

## Task states and labels

A human creates the labels below in the repository's label settings; no file in this repository creates labels. `agent-ready-claude` must exist before the cross-review workflows are used (see [Cross-review: Claude implements, Copilot reviews](#cross-review-claude-implements-copilot-reviews)); `agent-ready-full-claude` is created only as described in [Rolling out, recovering and verifying provider routes](#rolling-out-recovering-and-verifying-provider-routes). The retired Copilot labels (`agent-ready-copilot`, `agent-ready-copilot-low`, `agent-ready-copilot-high`, `agent-ready-full-copilot` and `agent-architecture-fix`) start nothing any more and can be deleted. Only the deterministic steps of `agent-implement.yml` and `agent-architecture.yml` apply or remove labels, and only on the issue they were started from and its pull request.

| Label | Meaning | Applied by |
| --- | --- | --- |
| `agent-ready-claude` | A human has reviewed the issue, confirmed the acceptance criteria are complete and testable and the documentation impact decision is correct, and authorises Claude to implement it, with Copilot as architecture checker and final reviewer. Applying it starts `agent-implement.yml`, whose read-only preflight first validates the documentation impact declaration; if that fails, the label is left in place and nothing else runs until a human edits the issue and re-applies it. | **Human only** |
| `agent-ready-claude-low` / `agent-ready-claude-high` | The same reviewed Claude task authority, with the explicit implementation model tier described above. | **Human only** |
| `agent-ready-full-claude` | The same human confirmation for the single-provider fallback: Claude implements, and separate read-only Claude invocations check the architecture and do the final review (same-provider, not independent; see [Single-provider fallback route](#single-provider-fallback-route)). Applying it starts `agent-implement.yml` like `agent-ready-claude`; applying it alongside another readiness label blocks the run. A human creates this label; it does nothing until the release that routes it reaches `main`. | **Human only** |
| `agent-working` | An implementation agent has started and owns a feature branch for this issue. | `agent-implement.yml` (replaces the readiness label), at the start of the run |
| `agent-review` | On an **issue**: a pull request is open and awaiting the route's final review. On a **pull request**: authorises the (now automatic) initial final review and a fresh review after any later repair or other new commit. `agent-repair.yml` accepts the repository owner's `@claude repair` comments while the label remains present; a pushed repair, and any other new commit, is validated and reviewed again automatically. Removing the label stops further automatic reviews and makes the publish job suppress a review still in progress. | Issue and pull request: the `agent-architecture.yml` finalizer after a successful architecture stage and exact current-head recheck; a human may also apply it manually (for example to re-request review outside a repair) |
| `agent-blocked` | The agent stopped because validation/review failed after the permitted repair attempts, or because a human decision or permission is required. The issue or PR must state the exact blocker. | `agent-implement.yml` for coding/publication failure; `agent-architecture.yml` for architecture or architecture-to-validation handoff failure; otherwise human (including after the second failed repair) |
| `risk:low` | See risk classification. | Human at triage |
| `risk:medium` | See risk classification. | Human at triage |
| `risk:high` | See risk classification. | Human at triage |

Rules:

- A human applies `agent-ready-claude` (the default, or one of its tiers) or, when Copilot is unavailable, the `agent-ready-full-claude` fallback label. Submitting the issue form does not apply any readiness label; the form only explains that a human must apply one after review.
- An agent must not start from an issue that has not been reviewed and given a readiness label by a human.
- An agent must not apply an `agent-ready-*` label to any issue, including one it drafted. Claude itself is denied `gh issue edit`, `gh pr edit`, and `gh label` in every agent workflow, and the read-only Copilot CLI reviewer has no write permission; the only label transitions are the deterministic workflow steps listed above.
- The state labels are mutually exclusive: an issue is in at most one of the readiness labels above (the full-provider label included), `agent-working`, `agent-review`, or `agent-blocked`.
- Removing `agent-blocked` and returning an issue to an `agent-ready-*` label is a human decision. Re-applying one starts a new implementation run with the agent it names, so first follow the blocked-recovery steps in [Provider mode provenance](#provider-mode-provenance): close the old pull request when starting over, and never re-apply a readiness label just to retry an open one.

State flow:

```text
(new issue) → [human review] ─┬→ agent-ready-claude       ─┬→ agent-working ──────────────────────────────────────────→ agent-review → [human merge decision]
                              └→ agent-ready-full-claude  ─┘

Any stage that fails closed → agent-blocked → (human decision) → exactly one readiness label (any route)
```

## Risk classification

Every agent task issue proposes a risk level, and a human confirms it before applying a readiness label. When a task touches more than one area, use the highest applicable classification.

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
- When that stop happens before a feature branch exists, the implementation prompt requires the agent to write the runner-local `.agent-run-status` marker with exactly `blocked`. The implementation result step accepts this marker only while the checkout is still on `develop`; it records a clean blocked outcome, skips PR creation and therefore never dispatches the separate architecture workflow, and moves the issue to `agent-blocked`. A successful Claude action that stays on `develop` without that exact marker is still an error, so unexpected missing branches or pull requests are not silently treated as intentional blockers.
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
| Agent run | GitHub Actions run of `agent-implement.yml`, `agent-architecture.yml`, `agent-review.yml`, or `agent-repair.yml`, linked from the closing comment | Full log of what the agent read, ran, and changed. |
| Review result | PR review/comments | Review-agent comment review with its `VERDICT:` line, plus human review. |
| Human merge decision | PR merge by a human | Records who accepted the change into `develop`. |
| Release/deployment record | `develop` → `main` release PR, then the Deploy Production run (its summary and the `deploy:production-backend` deployment record) | The release PR makes the change releasable; the Deploy Production run records the exact SHA that reached production. |

A change whose chain is broken (for example a PR without an issue, or a validation claim without evidence) is not eligible for automated handling and must be reviewed as an ordinary human change.

## Branch and release policy

- Work branches start from the latest `develop`.
- Normal feature and fix pull requests target `develop`.
- `develop` is the integration branch. A push to `develop` runs backend build and tests (`vm-manager.yml` build job) but does not deploy.
- Production releases use a **separate** pull request from `develop` to `main`.
- A merge or push to `main` deploys nothing: `main` is approved, releasable code. A human deploys it by starting [Deploy Production](#deploy-production) for an exact `main` commit, which deploys the API, verifies its health, then deploys the frontend from the same commit. The deployment workflow itself does not apply EF Core migrations; it reports the expected ones, and the API process applies them automatically on its next Production startup after the deploy (issue #201), failing closed if that attempt does not succeed. Outside Development, `Testing`, and Production, API startup still applies no schema change and fails closed if one is pending.
- An agent may prepare or update a `develop` to `main` release pull request only when a human explicitly requests it. Permission to implement a feature never grants permission to create a release pull request. Agents never merge or deploy: a human reviews and merges the release pull request, and a human starts Deploy Production.
- Production deployments require human control: a human starts each one. A failed deployment never gives an agent authority to merge, revert, roll back or deploy. There is no automated path from an issue to production. The schema migration a deployment may trigger is applied automatically by the API itself, not by any agent or workflow; an agent must still never invoke `migrate-database --apply` against Production, and never deploy, on its own initiative.

## Recommended repository protection checklist

> **Manual configuration checklist.** These are GitHub repository settings, not repository files. This document recommends them; it does not enable them and does not verify that they are enabled. A maintainer must configure and confirm each item in the GitHub UI or API.

| Setting | Recommendation |
| --- | --- |
| Pull requests required | Require a pull request before merging into `develop` and `main`. |
| Required status check | Two stable statuses exist: `merge-validation`, published by normal same-repository `pull_request` validation of every PR, and `agent-validation`, published only by trusted exact-SHA dispatch for agent PRs. Keep `merge-validation` required for branch protection. Agent-created/updated PRs use the dedicated GitHub App token, so their normal `pull_request` runs start automatically rather than waiting for manual workflow approval. |
| Force pushes | Block force pushes to `develop` and `main`. |
| Branch deletion | Block deletion of `develop` and `main`. |
| Conversation resolution | Require all review conversations to be resolved before merging, where available. |
| Bypass | Do not allow automation credentials (GitHub Apps, tokens used by agents, `GITHUB_TOKEN`) to bypass protected-branch rules. The agent workflows rely on branch protection as the final guard against a push to `develop` or `main`. |
| Deployment credentials | Keep Azure deployment secrets/variables and the `VmInventoryApi_Env` environment unavailable to implementation and review agents. The agent workflows request none of them. |
| Agent credential | Keep `CLAUDE_CODE_OAUTH_TOKEN` a repository secret used only by the agent workflows; rotate it by regenerating it with `claude setup-token`. Do not make it available to forks (GitHub already withholds secrets from fork pull requests). |
| Workflow branches | The agent workflows must exist on `main` (where `issues` and `issue_comment` workflows run from) and on `develop` (where `pull_request` workflows targeting `develop` run from). Keep them identical on both. The documentation impact preflight in `agent-implement.yml` takes effect when it reaches `main`; backfill issues #87–#92 before then (see [Documentation impact gate](#documentation-impact-gate)). |
| Production approval | Configure a human-controlled required reviewer on the production deployment environment (`VmInventoryApi_Env`), when available on the repository's plan. Both Deploy Production deploy jobs use it, so the reviewer approves the API and then the frontend. |
| Production deployment branch | Restrict `VmInventoryApi_Env`'s deployment branches to `main`. Deploy Production refuses other refs itself, but that check lives in the workflow file, which someone could edit on another branch; the environment rule is the boundary that cannot be edited from a branch. Also restrict who may run workflows (write access) to people trusted to deploy: `workflow_dispatch` can also be started through the GitHub API by anyone with that access, so the environment's required reviewer is the actual human approval barrier, and should be configured before the first production run. |
| Review count | Require at least one human approving review on `main`; consider the same for `develop`. |

`docs/architecture.md` already notes that branch protection and required-check configuration live in repository settings and must be enabled separately from source-controlled workflows.

## Implementation phases

Each phase is delivered as its own pull request and must be proven reliable before the next begins.

| Phase | Scope | Status |
| --- | --- | --- |
| 1 | Automation documentation and templates: this document, the agent task issue form, the PR template, and `AGENTS.md`/`README.md` updates. | **Implemented** |
| 2 | Issue label to implementation-agent PR creation: labels, an implementation agent connected to `agent-ready-claude` issues (since #340 also the `agent-ready-full-claude` fallback; the former Copilot coding agent route is retired), and `agent-working`/`agent-review`/`agent-blocked` transitions. No merge or deploy authority. | **Implemented** with Claude Code (`agent-implement.yml`, `CLAUDE.md`). Labels were created by a human in repository settings. After implementation PR creation, trusted `agent-architecture.yml` runs first; its successful finalizer dispatches exact-SHA validation from `main`; see [Workflow-token behaviour](#workflow-token-behaviour). |
| 3 | Independent automated review and bounded repair: a separate review step, its written result, and the two-attempt repair limit enforced mechanically. | **Partially implemented.** The final review step exists (`agent-review.yml`) and its initial run is dispatched automatically once validation succeeds (`agent-architecture.yml`'s deterministic finalizer applies `agent-review` before exact-SHA validation); a human-invoked repair step exists (`agent-repair.yml`), and a pushed repair automatically receives exact-SHA validation followed by fresh review under the same label. The [documentation impact gate](#documentation-impact-gate) (templates, preflight, validation, review comparison) is **implemented**. Mechanical enforcement of the two-attempt limit is **not implemented**: repair attempts are requested and counted by a human. |
| 4 | Staging deployment and smoke tests: a non-production environment deployed from `develop` with automated smoke checks. No staging environment exists today. | Proposed |
| 5 | Controlled release PR and production approval: a `develop` → `main` release PR process with human environment approval. | Proposed |
| 6 | Production monitoring and proposed issue creation: monitoring that can draft issues for humans to review; it must not apply an `agent-ready-*` label. | Proposed |
| 7 | Possible low-risk auto-merge, only after the earlier phases have proven reliable and only for `risk:low` tasks, with a human able to disable it at any time. Any such merge would be performed by repository automation configured by a human, never by the implementation or review agent, and would require an explicit update to this document. | Proposed, not committed |

Phases 4–7, and the mechanical repair-limit enforcement of phase 3, are not implemented by this repository at the time of writing. Any claim that one of them exists must be backed by a workflow or configuration file in this repository and a corresponding update to this document.



## Implementation durability and recovery (issue #250)

The trusted implementation workflow captures the starting `develop` SHA before Claude runs. Immediately after the implementation invocation, an `always()` persistence step uses a fresh, contents-only GitHub App token to preserve meaningful committed work **before** clean-tree, PR metadata, PR creation, architecture and review processing. Claude receives no publishing credential or push permission. The publisher checks the exact expected HEAD, the issue-specific feature branch and ancestry from the trusted base. It checks every intervening commit (including merge parents and reverted files) for `.github/**` and tracked agent scratch files; protected changes require human recovery and are never published through this path.

Successful invocations are saved on `agent/issue-<issue>-checkpoint-<run>-<attempt>`; unsuccessful invocations are saved on `agent/recovery-<issue>-<run>-<attempt>`. These are storage references, not normal completed implementation PRs. Existing refs must point to the exact intended SHA or publication fails, and pushes never force or rewrite history. The branch and SHA are recorded in the run summary and issue. No new commit means no recovery branch. Uncommitted edits are not persisted.

The persistence step does not establish that validation passed or acceptance criteria were met. A failed agent action stays failed and cannot advance to normal PR creation, architecture or validation dispatch. Later failures leave the checkpoint intact. Human approval, exact-SHA validation, architecture review, merge gates, deployment controls and `.github/**` protections remain required. The normal PR branch is still published only after implementation postconditions pass.

Git pushes performed with freshly minted GitHub App installation tokens use a bounded authorization-propagation retry: one immediate attempt, then retries after 2, 5 and 10 seconds only when GitHub returns the observed transient `403` / `Permission ... denied` response. Other Git failures fail immediately. The retry helper never force-pushes or rewrites history and is used for checkpoint persistence, normal implementation publication and standalone architecture-head publication.

Rerunning the same workflow run selects the most recent checkpoint from an earlier attempt and continues on a fresh issue-specific feature branch at its exact SHA. Claude inspects existing work and completes validation and metadata instead of reimplementing it. Resume is allowed only when current `develop` is an ancestor of the saved commit; a changed base requiring integration returns to a human. A new label-triggered run has a different run ID and does not automatically adopt old or ambiguous recovery work: a maintainer must select and reconcile that branch explicitly.

Runs for one issue are queued rather than cancelled by a newer invocation, so a new label event cannot interrupt the persistence boundary. Runner termination, hard timeout, credential/API/network failure, and work never committed can still prevent remote persistence; this is not an absolute guarantee against infrastructure loss. Persistence errors remain visible as workflow failures. Deterministic tests use real temporary repositories and a bare remote to exercise downstream failures, failed-agent recovery, no-commit behavior, SHA/ancestry rejection, protected history, collision rejection and exact-SHA resume.

## Copilot review output recovery

The independent Copilot review validates its marked JSON against the same strict field contract used by the Claude reviewer. If extraction or validation fails, the workflow logs which contract checks passed or failed, including nested criterion and inline-comment checks, plus the top-level field types (booleans and types only, never raw review content), and makes exactly one format-only Copilot invocation with shell, write and URL tools denied and the built-in MCP servers disabled. The same check report is logged again if the repaired output is also invalid. That invocation receives the original instructions and output, must preserve the SHA, verdict, findings and evidence, and must not invent missing evidence, remove blockers or mark unverified criteria met. Its output passes through the unchanged strict validator. A second invalid result fails the review job and the guarded publisher records an error; it never substitutes an approval. Valid first-attempt output receives no retry. The working tree and reviewed head are checked again after the format repair. Regression coverage is in `scripts/copilot-review-contract.test.mjs` (`node --test scripts/copilot-review-contract.test.mjs`), which the `validate.yml` validate job runs on every pull request; `scripts/validate-agent-workflows.mjs` rejects a workflow that drops it.

## Retired Copilot implementation route

Copilot implemented issues only until commit 354147a, which retired the route (`agent-ready-copilot` and its tiers, `agent-ready-full-copilot`, `agent-architecture-fix` and the workflows that assigned Copilot and handed its pull requests to Claude). Issue #194's trial evidence for that route, formerly `docs/copilot-trial-evidence.md`, described only that retired route and was removed; it remains in the repository history at commit 354147a. What still applies today:

- **The retirement is enforced, not just documented.** The retired labels start nothing and never count as a claim, a `copilot/*` pull request is refused at every boundary, and `verifyCopilotImplementationRetired` in `scripts/validate-agent-workflows.mjs` fails validation if a retired workflow file or label comes back. The tests that prove these refusals stay in place.
- **Usage/credit measurement is unavailable, not estimated.** Neither provider exposes per-run token, request or premium-request consumption anywhere this repository or its public GitHub history can read, so usage is reported as **unavailable** rather than inferred from turn counts, commit counts or elapsed time.
- **Provider/tier authorization stays distinct from cost.** The tiered readiness labels in [Implementation model tiers](#implementation-model-tiers) select the implementation model only; they do not cap or imply the cost of the architecture check, review, triage or repair stages. No document here proposes an automatic provider-routing algorithm.
