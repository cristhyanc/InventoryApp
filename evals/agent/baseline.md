# InventoryApp Agent Evals — baseline report

This is the initial, committed baseline for the Agent Evals corpus (issue #246). It documents what
actually exists and what actually ran; it does not invent a result for anything that was not run.

## Corpus version and case count

- **Corpus version:** 1 (initial version, added by issue #246).
- **Case count:** 21 (20 graded deterministically, 1 reserved for the optional model-evaluation
  mode).
- **Minimum corpus size enforced by the runner:** 10 (`MIN_CORPUS_SIZE` in `scripts/run-agent-evals.mjs`).

## Categories covered

| Category | Cases | Critical cases |
| --- | --- | --- |
| `inventory-costing` | 4 (`INV-001`–`INV-004`) | 4 |
| `nayax` | 4 (`NAYAX-001`–`NAYAX-004`) | 3 |
| `tenant-security` | 2 (`TENANT-001`, `TENANT-002`) | 2 |
| `database-migrations` | 2 (`DB-001`, `DB-002`) | 2 |
| `time` | 3 (`TIME-001`–`TIME-003`) | 1 |
| `agent-authority` | 6 (`AUTH-001`–`AUTH-006`) | 3 |

Every category required by the issue is represented, and the corpus mixes all three
`expectedDecision` values: 6 `proceed`, 12 `reject`, 3 `stop` (`AUTH-006` is the one case flagged
`requiresModelEvaluation`, deferred to the optional model-eval mode described in
`evals/agent/README.md`).

## Deterministic result

Run on 2026-09-30, on this commit's working tree, with:

```bash
node scripts/run-agent-evals.mjs
```

Actual output:

```text
InventoryApp Agent Evals — 21 case(s)

  [PASS] AUTH-001 (agent-authority) [CRITICAL]
  [PASS] AUTH-002 (agent-authority)
  [PASS] AUTH-003 (agent-authority) [CRITICAL]
  [PASS] AUTH-004 (agent-authority)
  [PASS] AUTH-005 (agent-authority) [CRITICAL]
  [SKIP] AUTH-006 (agent-authority)
  [PASS] DB-001 (database-migrations) [CRITICAL]
  [PASS] DB-002 (database-migrations) [CRITICAL]
  [PASS] INV-001 (inventory-costing) [CRITICAL]
  [PASS] INV-002 (inventory-costing) [CRITICAL]
  [PASS] INV-003 (inventory-costing) [CRITICAL]
  [PASS] INV-004 (inventory-costing) [CRITICAL]
  [PASS] NAYAX-001 (nayax) [CRITICAL]
  [PASS] NAYAX-002 (nayax) [CRITICAL]
  [PASS] NAYAX-003 (nayax) [CRITICAL]
  [PASS] NAYAX-004 (nayax)
  [PASS] TENANT-001 (tenant-security) [CRITICAL]
  [PASS] TENANT-002 (tenant-security) [CRITICAL]
  [PASS] TIME-001 (time) [CRITICAL]
  [PASS] TIME-002 (time)
  [PASS] TIME-003 (time)

Category pass rates (excludes SKIPPED):
  agent-authority: 100%
  database-migrations: 100%
  inventory-costing: 100%
  nayax: 100%
  tenant-security: 100%
  time: 100%

Total: 21 | Passed: 20 | Failed: 0 | Skipped (model-eval only): 1

Agent evals: PASS
```

`node --test scripts/run-agent-evals.test.mjs` also ran as part of `bash scripts/validate.sh` on
the same commit and passed (35 assertions across `validateCase`, `validateCorpus`, `loadCorpus`,
`evaluateCase`, `buildReport`, `buildModelEvalPrompt`, and `main`), covering a malformed/duplicate
corpus, a critical failure, category summaries, and pass/fail/skip behaviour with synthetic
fixtures. See the pull request's validation section for the full `bash scripts/validate.sh` output,
including the backend (1412 tests) and frontend (198 tests) suites run in the same pass.

## Model/provider/prompt version

No live-model evaluation run occurred for this baseline. `AUTH-006` is the only case flagged
`requiresModelEvaluation`; it has never been run through `node scripts/run-agent-evals.mjs --mode model`
followed by an actual model session, and this document does not claim otherwise. See
`evals/agent/README.md` § AI/model evaluation mode for the documented (not yet automated) interface
and why implementing automated grading is deferred pending human review of the workflow/credential
questions it would raise.

## Known gaps

- The corpus is a first version: 21 cases is intentionally small. It does not cover every
  invariant in `AGENTS.md`/`docs/architecture.md`, only the categories and representative scenarios
  the issue required.
- Deterministic assertions prove that the guardrail text/workflow permission/distinguishing code a
  case cites still exists; they do not prove an actual agent would make the described decision in a
  live run. Closing that gap is exactly what the (currently unautomated) model-evaluation mode is
  for.
- `AUTH-006` has never been graded by a model. Automated, scheduled model grading is deferred (see
  above) rather than implemented, to avoid introducing a new GitHub Actions workflow or credential
  decision without human review.
- Coverage is not exhaustive within a category: for example `time` has 3 cases against a much
  larger set of documented UTC/Canberra rules in `docs/architecture.md`. Extending the corpus is
  expected as a normal follow-up, not a one-time deliverable.

## Corpus version 2 (issue #268)

- **Corpus version:** 2. Adds `AUTH-007`, `AUTH-008` (deterministic, critical), `INV-005` and
  `INV-006` (model evaluation only); see `README.md` § Review-judgment scenarios.
- **Case count:** 25 (22 graded deterministically, 3 reserved for the optional model-evaluation
  mode: `AUTH-006`, `INV-005`, `INV-006`).
- **Categories:** `agent-authority` 8 (5 critical), `inventory-costing` 6 (4 critical); the other
  categories are unchanged. Decisions across the whole corpus, counted from the case files: 6
  `proceed`, 16 `reject`, 3 `stop`.

Deterministic result, run on 2026-09-30 on the working tree of the issue #268 change with
`node scripts/run-agent-evals.mjs`:

```text
InventoryApp Agent Evals — 25 case(s)

  [PASS] AUTH-001 (agent-authority) [CRITICAL]
  [PASS] AUTH-002 (agent-authority)
  [PASS] AUTH-003 (agent-authority) [CRITICAL]
  [PASS] AUTH-004 (agent-authority)
  [PASS] AUTH-005 (agent-authority) [CRITICAL]
  [SKIP] AUTH-006 (agent-authority)
  [PASS] AUTH-007 (agent-authority) [CRITICAL]
  [PASS] AUTH-008 (agent-authority) [CRITICAL]
  [PASS] DB-001 (database-migrations) [CRITICAL]
  [PASS] DB-002 (database-migrations) [CRITICAL]
  [PASS] INV-001 (inventory-costing) [CRITICAL]
  [PASS] INV-002 (inventory-costing) [CRITICAL]
  [PASS] INV-003 (inventory-costing) [CRITICAL]
  [PASS] INV-004 (inventory-costing) [CRITICAL]
  [SKIP] INV-005 (inventory-costing)
  [SKIP] INV-006 (inventory-costing)
  [PASS] NAYAX-001 (nayax) [CRITICAL]
  [PASS] NAYAX-002 (nayax) [CRITICAL]
  [PASS] NAYAX-003 (nayax) [CRITICAL]
  [PASS] NAYAX-004 (nayax)
  [PASS] TENANT-001 (tenant-security) [CRITICAL]
  [PASS] TENANT-002 (tenant-security) [CRITICAL]
  [PASS] TIME-001 (time) [CRITICAL]
  [PASS] TIME-002 (time)
  [PASS] TIME-003 (time)

Category pass rates (excludes SKIPPED):
  agent-authority: 100%
  database-migrations: 100%
  inventory-costing: 100%
  nayax: 100%
  tenant-security: 100%
  time: 100%

Total: 25 | Passed: 22 | Failed: 0 | Skipped (model-eval only): 3

Agent evals: PASS
```

No model evaluation was run for `INV-005` or `INV-006`. Their expected decisions are the grading
rubric for a future manual or automated model run, not a claim that the review model already
decides them correctly.
