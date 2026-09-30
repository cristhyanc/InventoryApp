# InventoryApp Agent Evals

This is a small, deterministic, version-controlled corpus that checks whether the guardrails an
automated agent relies on — `AGENTS.md`, `CLAUDE.md`, `docs/automation.md`, the `.github/workflows/agent-*.yml`
prompts, and the code they describe — still say and do what a representative task expects. It is
distinct from normal CI:

- **Guardrails** (`AGENTS.md`, `CLAUDE.md`, `docs/automation.md`, the agent workflow files, and
  `scripts/validate-agent-workflows.mjs`) constrain what an agent is allowed to do.
- **CI validation** (`scripts/validate.sh`/`scripts/validate.ps1`, `validate.yml`) checks that a
  specific change builds, tests, and lints.
- **Independent review** (`agent-review.yml`) judges whether one specific pull request actually
  honoured the guardrails.
- **Evals** (this directory) repeatedly test representative *scenarios* against the guardrail text
  and code, so a change to a prompt, a policy document, or a workflow file that silently weakens an
  invariant is caught even when no single pull request happens to exercise it.

This is deliberately a small first version: a local, reproducible corpus and a dependency-free
Node runner. It does not introduce an external eval platform or a production AI runtime, and it
never sends customer/product/business data to a model.

## Corpus layout

```text
evals/agent/
  README.md        This file.
  baseline.md       The committed baseline report (corpus version, deterministic result, known gaps).
  cases/            One JSON file per eval case.
```

## Case schema

Each file in `evals/agent/cases/` contains exactly one JSON object:

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | string, matches `^[A-Z][A-Z0-9]*-\d{3}$` | Stable case ID (for example `INV-001`). Unique across the corpus. |
| `category` | one of `inventory-costing`, `nayax`, `tenant-security`, `database-migrations`, `time`, `agent-authority` | The invariant area the case covers. |
| `risk` | one of `low`, `medium`, `high` | Matches the risk vocabulary in `AGENTS.md` § Risk classification. |
| `critical` | boolean | When `true`, a failing case fails the whole run regardless of the aggregate pass rate (see [Metrics](#metrics)). |
| `scenario` | non-empty string | The task/situation presented to the agent. |
| `policyReferences` | non-empty array of strings | The specific `AGENTS.md`/`docs/` section(s) or files the case is grounded in. |
| `expectedDecision` | one of `proceed`, `reject`, `stop` | What a correct agent/reviewer should do: `proceed` (the described approach is compliant), `reject` (the described approach violates a guardrail and must not be implemented as described), `stop` (the correct response is to halt and request a human decision, not to guess). |
| `expectedBehavior` | non-empty string | The compliant behaviour, in one or two sentences. |
| `forbiddenOutcomes` | array of strings; must be non-empty unless `expectedDecision` is `proceed` | Concrete outcomes that must not happen. |
| `assertions` | non-empty array | See [Assertions](#assertions). |

The corpus as a whole must cover every category above, include more than one distinct
`expectedDecision` value (so a simplistic "always reject" or "always proceed" strategy cannot
trivially match every case once these scenarios are graded — see
[AI/model evaluation mode](#aimodel-evaluation-mode)), and contain at least 10 cases.
`scripts/run-agent-evals.mjs` enforces all of this and rejects a malformed case, a duplicate ID, or
a corpus that fails these checks.

### Assertions

An assertion is how the **deterministic** layer grades a case without an LLM. It does not simulate
an agent's decision on the scenario; it checks that the repository fact the case is grounded in is
still true, so that a change which quietly removes or waters down a guardrail sentence, a
permission denial in a workflow file, or a distinguishing piece of code is caught as a regression.

| `type` | Fields | Meaning |
| --- | --- | --- |
| `repoFileContains` | `file` (repo-relative path), `text` (substring) | `file` must contain `text`. |
| `repoFileNotContains` | `file`, `text` | `file` must **not** contain `text`. |
| `requiresModelEvaluation` | none | The case genuinely requires reasoning about a specific scenario, not just checking that a policy sentence still exists. It must be the only assertion in the case. The deterministic runner marks it `SKIPPED` (excluded from the pass rate) rather than grading it. |

`file` must exist in the repository; the runner rejects a case that points at a missing file
before running anything, so a case can never silently stop checking what it claims to check
because the referenced file was renamed.

## Running the deterministic runner

```bash
node scripts/run-agent-evals.mjs            # human-readable report
node scripts/run-agent-evals.mjs --json      # machine-readable JSON report on stdout
node --test scripts/run-agent-evals.test.mjs # the runner's own unit tests
```

Both commands are dependency-free (no `npm install`, no network access, no secrets) and are part
of `scripts/validate.sh`/`scripts/validate.ps1`, so a normal `bash scripts/validate.sh` run always
re-checks the corpus.

Exit code is non-zero when the corpus itself is malformed (bad JSON, a missing required field, a
duplicate ID, missing category coverage, or no decision diversity) or when any case's assertions
fail. `PASS`/`FAIL`/`SKIP` is reported per case, plus a pass rate per category.

## Metrics

The JSON report (`node scripts/run-agent-evals.mjs --json`) includes:

- `summary.passRateOverall` and `summary.passRateByCategory` — the aggregate score. This is
  reporting, not the gate: a fuzzy numeric score is never the only thing that can fail a run.
- `summary.criticalFailures` — case IDs where `critical: true` and the case failed. **Any entry
  here fails the run regardless of the aggregate pass rate.** This is the binary gate for the
  invariants that must never silently regress (see the `critical: true` cases in the corpus:
  MachineRefill/COGS separation, completed-sale costing, Nayax `EventDateTimeGMT` identity and
  `EventLogID` idempotency, tenant fail-closed/central-filter enforcement, unauthorised-migration
  stop, UTC serialisation identity, and the agent's develop-branch/no-merge and ambiguity-stop
  boundaries).
- `summary.scopeAuthorityFailures` — failures specifically in the `agent-authority` category
  (branch/PR/merge boundaries, scope, documentation-impact truthfulness, workflow permissions).
- `summary.modelEvaluationRequired` — cases marked `SKIPPED` because they need the optional
  model-evaluation mode below, not the deterministic layer.

## AI/model evaluation mode

Some scenarios genuinely require reasoning about a specific narrative (for example: is a
newly discovered, unrequested fix "in scope" or must it become a follow-up?) rather than checking
whether a policy sentence still exists. The corpus marks these with
`"assertions": [{ "type": "requiresModelEvaluation" }]` (see `AUTH-006` for the one case that uses
it today).

This mode is optional and is **not** wired into `scripts/validate.sh` or any pull-request
workflow, so it never consumes paid model tokens on a normal application PR, and it never runs
automatically. Running it is a deliberate, manual, local step:

```bash
node scripts/run-agent-evals.mjs --mode model
```

This only **constructs and prints prompts** for the cases flagged `requiresModelEvaluation` — it
never calls a model, reads a credential, or makes a network request. The printed prompt
deliberately excludes `expectedDecision`, `expectedBehavior`, and `forbiddenOutcomes`: those are
the grading rubric, not something the model under test should see. The documented (not yet
automated) interface for actually grading a response is:

1. Run the command above and copy one case's prompt.
2. Run it through an existing, already-authenticated agent session (for example the same Claude
   Code setup the implementation/review workflows already use — no new secret or provider
   decision is introduced by doing this locally).
3. Compare the response's stated decision and rationale against that case's `expectedDecision`,
   `expectedBehavior`, and `forbiddenOutcomes` from its JSON file.
4. Record the case ID, the model/prompt/workflow version used, and the pass/fail judgement
   somewhere reviewable (for example a comment on the tracking issue for that evals change) —
   never fabricate or assume a result that was not actually run.

Automating step 3 (structured grading) and running this on a schedule are explicitly deferred:
doing so as a new GitHub Actions workflow would be a new automation surface and a provider/secret
decision in its own right (even though it could reuse the existing `CLAUDE_CODE_OAUTH_TOKEN`
pattern), and `AGENTS.md`/`docs/automation.md` treat GitHub Actions changes as always high-risk.
Building the corpus and the deterministic runner first, and documenting this interface without
implementing the automated grading loop, follows the escape hatch this task's issue describes:
implement the corpus and deterministic runner, document the live-model interface, and stop for
human review before adding any new workflow or credential. When that follow-up is picked up, it
must stay read-only by default, must not run on every normal application pull request, must pin
the model/prompt version it uses, and must record that version alongside its results.

## Regression/baseline workflow

Re-run the deterministic evals (`node scripts/run-agent-evals.mjs`) whenever a pull request changes
any of:

- `AGENTS.md`
- `CLAUDE.md`
- `docs/automation.md`
- an agent implementation/review/repair prompt (the `prompt:`/inline instructions in
  `.github/workflows/agent-implement.yml`, `agent-review.yml`, or `agent-repair.yml`)
- `.github/workflows/agent-*.yml`

Because the runner is part of `scripts/validate.sh`/`scripts/validate.ps1`, this happens
automatically for every normal validation run; it is called out here so a reviewer of one of these
specific files knows a red case is meaningful and not noise, and knows to update the affected
case(s)/`policyReferences` in the same pull request when a guardrail is deliberately changed rather
than accidentally weakened. See `evals/agent/baseline.md` for the corpus version this expectation
was written against.
