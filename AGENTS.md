# InventoryApp agent instructions

These instructions apply to the entire repository. More specific `AGENTS.md` files may refine them for a subtree, but may not weaken the financial, inventory, security, or deployment safeguards in this file.

## Mission

InventoryApp manages a real vending-machine business. Treat inventory quantities, historical cost, sales, fees, commissions, reimbursements, GST fields, and profitability as financial data. Prefer a small, reviewable, tested change over a broad rewrite.

The repository is also being prepared for reliable AI-assisted engineering. Every change must be understandable from its issue, diff, tests, and validation output.

## Repository map

```text
backend/InventoryApi/                 ASP.NET Core .NET 10 API and composition root
  Controllers/                        HTTP boundary
  DTOs/                               Current API/report contracts
  Bootstrap/                          Startup schema decision and the human-invoked commands; they call Infrastructure services and own no persistence adapter (issue #309)
  Adapters/Mapping/                   Response-DTO projections for the controllers
backend/Inventory.Domain/             Deterministic domain rules and calculations
backend/Inventory.Application/        Use cases and their narrow ports; new use-case/domain logic goes here, never into InventoryApi
backend/Inventory.Infrastructure/     Adapters behind those ports: Nayax Lynx client and catalog snapshot (Nayax/), report CSV/XLSX export (Reporting/), site names (Sites/), document storage, imported-file readers, clock/calendar, backups
  PlatformDiagnostics/                The read-only SQLite diagnostics adapter and its authorizer (issue #336); the only cross-business read path
  Data/AppDbContext.cs                EF Core model, mappings and tenant query filters (issue #307)
  Data/BusinessOwnershipEnforcer.cs   The SaveChanges tenant-ownership enforcement
  Data/EfNayaxSalesQueries.cs         The completed-sale query predicate every sales query filters on (issue #308)
  Migrations/                         SQLite schema history
  Models/                             Current EF entities and enums
  Persistence/                        Every other EF adapter behind an Application persistence port (issue #309); InventoryApi owns none
  Reporting/Persistence/              The reporting EF fact providers and their shared queries (issue #308)
backend/Inventory.UnitTests/          xUnit tests of Inventory.Domain/Inventory.Application only (references nothing else)
backend/Inventory.IntegrationTests/   xUnit database, API, adapter, migration, bootstrap and architecture tests
frontend/inventory-app/               Angular 19 standalone application
.github/workflows/                    Validation, Claude Code and Copilot agent, and Azure deployment workflows
CLAUDE.md                             Claude Code entry point: read and obey this file and the docs
docs/architecture.md                  Current and target architecture
docs/automation.md                    Automated development lifecycle and authority model
.github/ISSUE_TEMPLATE/agent-task.yml Agent task issue form
.github/pull_request_template.md      Pull request template
.editorconfig                         Repository-wide formatting, naming and diagnostic severities
Directory.Build.props                 Shared .NET build quality settings (nullable, analyzers, warnings-as-errors)
frontend/inventory-app/eslint.config.js    Angular/TypeScript ESLint flat configuration
scripts/validate.ps1                  Complete Windows validation
scripts/validate.sh                   Complete Bash validation
scripts/validate-agent-workflows.mjs  Agent workflow and template contract checks (with .test.mjs)
scripts/validate-deployment-workflows.mjs  Production deployment workflow contract checks (with .test.mjs)
scripts/deployment-migration-preflight.mjs Deploy Production migration preflight (with .test.mjs)
scripts/validate-documentation-impact.mjs  Documentation-impact declaration parser for issues and PRs (with .test.mjs)
scripts/run-agent-evals.mjs           InventoryApp Agent Evals runner (with .test.mjs)
scripts/run-agent-model-evals.mjs     On-demand model-decision evals over a bounded critical set (with .test.mjs)
evals/agent/                          Agent Evals corpus, schema and baseline report (see evals/agent/README.md)
```

Read the nearest relevant production code and tests before changing behavior. For financial or inventory changes, also read the corresponding models, migrations, import logic, and reporting tests.

## Required workflow

1. Work on a feature branch created from the latest `develop`. Never commit directly to `develop` or `main`.
2. Restate the issue's acceptance criteria, its explicit exclusions, and its documentation impact decision, and identify affected domain rules.
3. Inspect existing implementations and tests before proposing a design.
4. Make the smallest coherent change. Do not mix unrelated cleanup with feature work.
5. For new or changed behavior with a clear expected result, write a focused failing test first, run it to confirm the intended failure, implement the smallest change that passes, then refactor with tests green. Prioritize authorization and tenant isolation, financial and inventory rules, imports, regressions, and API contracts. Add meaningful edge cases. For exploratory UI work, configuration, migrations, or changes where a useful test cannot be written first, explain why in the PR and add relevant verification before completion. Avoid tests that merely mirror implementation details.
5a. Follow the issue's documentation impact decision. When it says documentation changes are required, update every listed documentation file and section in the same change. When it says none are required but the change nevertheless makes documentation inaccurate, update that documentation anyway and state the discrepancy in the pull request. Never leave documentation that the change contradicts.
6. Run the complete repository validation from the repository root:

   - Windows: `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1`
   - PowerShell 7: `pwsh -File scripts/validate.ps1`
   - macOS/Linux/Git Bash: `bash scripts/validate.sh`

7. Review the final diff for secrets, accidental schema changes, generated files, and unrelated edits.
8. Open a pull request targeting `develop`, using the repository pull request template, with the reason for the change, tests run, financial/data risks, migration impact, any known limitations, and an accurate `## Documentation impact` section (see [Documentation impact gate](#documentation-impact-gate)).
8a. After the implementation workflow publishes the initial PR, it dispatches the separate `agent-architecture.yml` workflow, which runs an architecture agent invocation on the same feature branch at the exact published head and before exact-SHA validation. It may make focused, behavior-preserving structural edits within the approved issue, run full validation, commit, and push the branch. It may report an architectural concern without editing. It must not broaden scope, change data or API behavior, create another PR, edit the PR description, or change workflows, migrations, secrets, or deployment. If its edit would make the PR description or documentation-impact declaration inaccurate, it reports the finding for human review without making that edit. Its failure, or any failure to hand off to or from it, blocks validation dispatch and marks the task `agent-blocked`. Independent review and human merge remain separate.
9. After the architecture pass, update only the feature branch, and only for at most two permitted repair attempts in response to CI or review failures. Stop and return control to a human when validation and review succeed, after two failed repair attempts, or when requirements are ambiguous or conflicting. Agents never merge or deploy; a human reviews and merges the pull request.

If complete validation cannot run, state exactly which command failed or was unavailable. Never claim a test or build passed unless it ran successfully.

## Branches and releases

- The default base branch for normal work is `develop`. Create feature branches from the latest `develop`.
- Normal feature and fix pull requests target `develop`, which is the integration branch. A push to `develop` runs backend build and tests but does not deploy.
- Production releases are separate pull requests from `develop` to `main`. A merge to `main` deploys nothing: `main` is approved, releasable code. Production changes only when a human starts the **Deploy Production** workflow (`.github/workflows/deploy-production.yml`) for an exact `main` commit; it validates that commit, reports the EF Core migrations production startup is expected to apply, deploys the API, verifies its health, and then deploys the frontend from the same commit (see `docs/automation.md` § Deploy Production).
- An agent may prepare or update a `develop` to `main` release pull request only when a human explicitly requests it. Permission to implement a feature never grants permission to create a release pull request. Agents never merge or deploy: a human reviews and merges the release pull request, and a human starts Deploy Production.

## Automated task contract

These rules govern any current or future automated agent that implements a GitHub issue. `docs/automation.md` contains the complete lifecycle, authority model, task states, risk classification, and retry policy; this section is the binding summary.

- A human must review an issue and mark it `agent-ready-claude` (the default: Claude implements, Copilot reviews) or, as an explicit override, `agent-ready-copilot` (the Copilot coding agent implements, Claude reviews) before an automated agent begins implementation. When one provider is unavailable, a human may instead apply a single-provider fallback label, `agent-ready-full-claude` or `agent-ready-full-copilot`: that provider implements, and separate read-only invocations of the same provider check the architecture and do the final review. These are same-provider reviews, never independent ones, and are recorded as such. Model triage uses Claude Haiku on every route, including `agent-ready-full-copilot`. The full labels count toward the one-readiness-label rule. See `docs/automation.md` § Provider roles and readiness labels and § Single-provider fallback routes. The chosen route is taken from the issue's label history (the deterministic claim step that swaps the readiness label for `agent-working`), never from comments or pull request text, and every later workflow verifies it against the pull request; a missing, conflicting, stale or disabled mode fails closed and never switches provider (`docs/automation.md` § Provider mode provenance). An agent must not start from an unreviewed issue and must never apply either label itself.
- Implementation readiness has three model tiers per provider: the existing `agent-ready-claude` / `agent-ready-copilot` labels use a cheap triage that selects low, standard or high automatically; `agent-ready-claude-low` / `agent-ready-copilot-low` choose a cheap model; `agent-ready-claude-high` / `agent-ready-copilot-high` select a high model directly. Apply exactly one readiness label; a standard label combined with any other readiness label, including a full-provider label, is rejected before any model or provider is invoked. All variants require the same human review, acceptance criteria, documentation-impact declaration and invariants. Default triage may choose low, standard or high; it stops for a human only when the requirements need clarification. Only a human applies readiness labels or changes tiers. An implementation agent must not switch models or delegate to bypass the selected tier; if inadequate, report the limitation and stop. These labels do not change architecture, review or repair models. See `docs/automation.md` § Implementation model tiers.
- Review routing: the verified provider mode alone selects the read-only architecture checker and the final reviewer; the implementer fixes the findings and does human-requested repairs on every route. On the cross routes the provider that did not implement the change checks and reviews it: Claude's review never runs on a `cross-claude` pull request (the Copilot CLI reviews it), and the Copilot CLI never reviews a `cross-copilot` one. On a full-provider fallback a fresh, read-only invocation of the implementing provider checks and reviews, the other provider is never invoked, and the prompts and published review say it is a same-provider review, not an independent one. Claude never repairs a Copilot-implemented `copilot/*` pull request's code by editing it. A provider failure fails closed and never switches provider. All final reviews publish the same per-SHA `agent-review-verdict` status. See `docs/automation.md` § Cross-review: Claude and Copilot and § Single-provider fallback routes.
- The issue's acceptance criteria and explicit exclusions define the agent's authority. Work outside them is out of scope even when it is nearby, related, or looks like useful cleanup.
- When requirements are materially ambiguous, conflict with each other, or conflict with this file, `docs/architecture.md`, or existing tests, stop and request a human decision. State exactly which decision is needed.
- Do not expand scope, broaden acceptance criteria, or reinterpret exclusions. Propose follow-up work in the pull request instead.
- After a pull request fails validation or review, an agent may make at most two automated repair attempts. After that, or as soon as a repair would require weakening a test or changing an established rule, the task is blocked and returns to a human.
- After the initial architecture pass, the implementation agent may update its feature branch for at most two permitted repair attempts in response to CI or review failures. It stops and returns control to a human when validation and review succeed, after two failed repair attempts, or when requirements are ambiguous or conflicting. It never merges a feature or release pull request, never pushes to `develop` or `main`, never deploys, and never runs production migrations or modifies production data. An agent prepares a release pull request only on a separate, explicit human request, and only a human approves and merges it.
- High-risk categories require explicit human scrutiny of both the issue and the pull request: financial or profit calculations, inventory quantity or historical costing, database schema or migrations, backfills or destructive data operations, authentication or authorization, secrets or environment configuration, GitHub Actions/Azure/deployment changes, any Nayax or other external-integration change (whether it reads, writes, imports, synchronises, maps errors, changes authentication, or handles remote payloads), imports or reconciliation, public API contract changes, file upload or filesystem security, and any production-impacting operation.
- Every automated change must be traceable through its issue, branch, commits, pull request, validation result, review result, and human merge decision.
- The final review on every route (independent on the cross routes, same-provider and not independent on a full-provider fallback) is advisory, is bound to one exact validated head SHA, and must enforce the issue: every acceptance criterion is assessed `met`, `not met` or `not verified` with evidence, and `VERDICT: READY FOR HUMAN REVIEW` requires every criterion `met` and no blockers. A known unmet criterion or violated financial, inventory/costing, tenant or security invariant is a blocker (`VERDICT: CHANGES REQUESTED`) and is never waived because legacy behaviour is worse, the failure seems unlikely, tests pass, or a quality gate is green. A materially ambiguous high-risk requirement is a "Human decision required:" blocker, not an invented policy. For stale-state or concurrency guarantees, the authoritative read, expected-state comparison and mutation are judged as one operation, with a regression test for a change between the read and the mutation.
- The review model publishes nothing itself. A separate trusted, deterministic job re-verifies eligibility and the current head immediately before publishing a comment-only review bound to the reviewed commit, and records the per-SHA `agent-review-verdict` status; a stale, superseded or ineligible result is recorded as not published and never as ready for the current head. Every new commit on an eligible `agent-review` pull request is re-validated at its exact SHA and, only on success, reviewed afresh; duplicate dispatch paths for one SHA collapse to one. See `docs/automation.md` § Review workflow and § Updated-head scheduling.
- Every agent task issue must carry a documentation impact decision and details, and every pull request must carry a `## Documentation impact` declaration. Automation only checks that these declarations exist and are meaningful; the reviewer decides whether they are truthful. See [Documentation impact gate](#documentation-impact-gate).

## Documentation impact gate

Documentation in this repository (`AGENTS.md`, `CLAUDE.md`, `docs/`, `README.md`, the issue and pull request templates) is part of the product: the agents obey it and the humans rely on it. Every change therefore makes an explicit, checked decision about documentation. The gate has four parts with separate owners:

1. **Templates collect the decision.** The agent task issue form has a required `Documentation impact decision` dropdown with exactly two options, `Documentation changes required` and `No documentation changes required`, and a required `Documentation impact details` field that lists the affected documentation files/sections or explains, for that specific task, why documentation is unaffected. The pull request template has a `## Documentation impact` section with exactly one `Decision:` line, which must be exactly `UPDATED` or `NOT REQUIRED`, and exactly one `Evidence:` entry. `UPDATED` evidence lists each documentation file changed and what changed in it. `NOT REQUIRED` evidence explains why behaviour, contracts, architecture, configuration, automation, deployment, operations and user workflows are unaffected. Bare `None`, `N/A` or `Not applicable`, generic sentences that only restate those categories, and unreplaced template placeholders are invalid in both.
2. **Automation validates that a meaningful declaration exists.** `scripts/validate-documentation-impact.mjs` parses both bodies deterministically. A read-only preflight job in `agent-implement.yml` validates the issue before Claude runs, a branch is created, or a label changes; when it fails, the issue is left unchanged and the run reports what to fix. `validate.yml` validates the pull request body before repository validation, for both the `pull_request` and the exact-SHA `workflow_dispatch` modes, so an invalid declaration fails the normal `merge-validation` or `agent-validation` status; editing the description reruns it. The parser never infers documentation impact from the changed files.
3. **The final review decides whether the declaration is correct.** `agent-review.yml` compares the issue decision, the pull request declaration and the actual diff. Missing, inaccurate or incomplete required documentation, or a declaration that contradicts the diff, is a blocker. Repairs update affected documentation on the head branch but never edit the pull request description; when the declaration must change, the repair comment states exactly what the human must correct.
4. **Humans remain responsible for approval and merge.** A human confirms the decision when applying a readiness label and again when approving and merging.

Issues #87 to #92 were created before this gate and lack the declaration. A human must backfill both fields on each of them (either as the form's `### Documentation impact decision` / `### Documentation impact details` headings or as the `## ...` headings those issues already use) before the preflight becomes active on the default workflow branch, or applying an `agent-ready-*` label to them will fail the preflight and leave them untouched.

## Commands

Backend solution:

```bash
dotnet restore backend/InventoryApi/InventoryApi.slnx
dotnet format backend/InventoryApi/InventoryApi.slnx --verify-no-changes --no-restore --exclude backend/Inventory.Infrastructure/Migrations
dotnet build backend/InventoryApi/InventoryApi.slnx --configuration Release --no-restore
dotnet test backend/InventoryApi/InventoryApi.slnx --configuration Release --no-build --no-restore --collect:"XPlat Code Coverage"
dotnet package list --project backend/InventoryApi/InventoryApi.slnx --vulnerable --include-transitive
```

Frontend:

```bash
npm --prefix frontend/inventory-app ci
npm --prefix frontend/inventory-app run lint
npm --prefix frontend/inventory-app run test
npm --prefix frontend/inventory-app run build
npm --prefix frontend/inventory-app audit
```

Agent Evals (see `evals/agent/README.md`):

```bash
node scripts/run-agent-evals.mjs
node --test scripts/run-agent-evals.test.mjs
node --test scripts/run-agent-model-evals.test.mjs
```

Both validation scripts run exactly this pipeline; run the script rather than the individual commands. Notes:

- The backend builds with `TreatWarningsAsErrors`, .NET analyzers and `EnforceCodeStyleInBuild` (see `Directory.Build.props`). A new warning in application code fails the build. The only suppressed compiler diagnostic is `CS8981` on EF Core generated migrations, scoped in `.editorconfig` to `[**/Migrations/*.cs]`. Do not widen that scope and do not add a global `<NoWarn>`.
- The solution holds two test projects (issue #311), and `dotnet test` on it runs both: `backend/Inventory.UnitTests` references only `Inventory.Domain` and `Inventory.Application` and holds the pure Domain/Application tests; `backend/Inventory.IntegrationTests` holds everything that touches Infrastructure, `InventoryApi`, EF Core or a database, including the architecture tests. A test belongs in the unit project only if it and every helper it uses compile against those two projects alone; never add an Infrastructure or API reference to it. Test doubles both projects need live in the unit project and are compiled into the integration project as linked source files, so neither test project references the other. To run one suite: `dotnet test backend/Inventory.UnitTests/Inventory.UnitTests.csproj` or `dotnet test backend/Inventory.IntegrationTests/Inventory.IntegrationTests.csproj`.
- `dotnet format` excludes `backend/Inventory.Infrastructure/Migrations` because an applied migration must not be rewritten. Both validation scripts hold that path in one variable (`migrations_dir` / `$MigrationsRelativePath`); keep them in step if the migrations ever move again.
- Coverage is collected on every test run but has no minimum threshold yet. Coverage output is git-ignored; never commit it.
- The frontend has a configured `lint` script (`ng lint`) and a `test` script (`jest`, via `jest-preset-angular`). `npm run lint` must report zero **errors**; warnings are visible but non-blocking. `npm run test` runs the Jest suite once (no watch mode) and must exit zero.
- `npm audit` is reported, not enforced: the outstanding high/critical advisories are in the Angular 19 build toolchain and clear only with a major Angular upgrade. Never run `npm audit fix --force`.
- `dotnet package list --vulnerable` always exits 0, so the scripts parse its output. A vulnerable package fails validation; an unreachable nuget.org only warns.
- `scripts/run-agent-evals.mjs` is a dependency-free Node runner over the version-controlled corpus in `evals/agent/cases/`; both validation scripts run it (and its own tests) before the backend restore. It checks that the guardrail text/code each case cites is still present, not what a model would decide; see `evals/agent/README.md`. The validation scripts also run the unit tests of `scripts/run-agent-model-evals.mjs`, but never the model-decision evals themselves: those call a model, run only when a person invokes them with an explicit `--provider`, and reuse the locally signed-in Claude Code CLI without adding any credential (see `evals/agent/README.md` § Model-decision evals).

## Architecture rules

The current backend is one project with a service layer. Its target is an incremental, pragmatic Clean Architecture described in `docs/architecture.md`.

- Keep the application a modular monolith; do not introduce microservices.
- New controllers must be thin: bind/validate transport input, invoke a use case, and map its result to HTTP.
- Do not inject `AppDbContext`, `IWebHostEnvironment`, filesystem APIs, or Nayax HTTP clients into new controllers.
- Existing direct-access controllers should be migrated feature by feature, not rewritten together.
- Organize new application code by business feature/use case, not only by technical type.
- Keep domain calculations deterministic and free of EF Core, ASP.NET Core, HTTP, filesystem, ClosedXML, and configuration dependencies.
- Define narrow ports for external behavior such as `INayaxClient`, document storage, or report export. Avoid a generic `IRepository<T>` abstraction.
- Do not add MediatR, an event bus, mapping frameworks, or other architectural machinery without an issue that justifies the dependency.
- Preserve public API contracts unless the issue explicitly permits a breaking change.
- There is exactly one authorized cross-business request path, the platform diagnostics API (issue #336), and it is narrowly bounded: `GET /api/admin/diagnostics/access` and `POST /api/admin/diagnostics/query`, behind the named `PlatformDiagnostics` authorization policy, which is satisfied only by a separately configured Entra `(tid, oid)` pair held outside the database — never by a business role, a membership row, an email address, or anything a request supplies. It is read-only: one statement, a 5-second ceiling, 500 rows, 1 MiB of serialized response, 16 KiB of submitted SQL, and a documented table/column allowlist enforced inside SQLite (read-only connection, `PRAGMA query_only`, zero `SQLITE_LIMIT_ATTACHED`, and an authorizer callback), not by pattern-matching the SQL. Every query emits one structured `ILogger` audit event with the actor, a normalized query-shape SHA-256 fingerprint (never the statement), duration, row count and outcome. Do not widen the allowlist, raise a limit, add a second cross-business path, or reach this surface from any other endpoint. **Any future data repair must be a separately reviewed, named maintenance operation with a preview/dry-run step and explicit verification — the shape `bootstrap-business`, `migrate-documents` and `InventoryCostRepair` already use. A repair must never be reachable by submitting SQL, and the diagnostics API must never become its execution path.** See `docs/architecture.md` § Platform diagnostics.
- Use `CancellationToken` for asynchronous I/O and pass it through to EF Core and HTTP operations.
- Do not duplicate business formulas in controllers, Angular components, exports, and reports. One authoritative calculation must feed all presentations.

## Database and migrations

- EF Core migrations are the schema source of truth. `DatabaseSchemaStartup` decides per environment whether startup may apply them. **Normal Production startup applies pending migrations automatically** (issue #201), the same as Development and `Testing`, and fails closed if the attempt fails — it logs a critical error and does not start, rather than serve requests against a schema its code does not match. Any other non-Production environment (an ephemeral Staging-like database, for example) still applies nothing by default and refuses to start with pending migrations unless `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` is `true`. The explicit `migrate-database` command (dry run first) remains available for diagnostics and manual use — it is no longer mandatory before a normal Production deployment, but is still the right tool to inspect what a deployment will apply, or to apply migrations ahead of a deployment window. Some tenancy migrations rebuild tables and copy persisted rows (the NayaxSales re-key, for example); automatic Production migration does not change how those migrations are written or reviewed, only who triggers applying them.
- Never replace migrations with `EnsureCreated()`.
- Never delete or rewrite an applied migration merely to simplify a change.
- Do not edit an existing migration unless the issue explicitly concerns an unapplied migration and a human confirms it is safe.
- Add a new migration for schema changes and test upgrade behavior from the prior schema where practical.
- Never commit `inventory.db`, local databases, uploaded receipts, imported production files, build output, or credentials.
- Prefer relational SQLite tests for behavior that depends on constraints, transactions, SQL translation, ordering, or migrations. EF Core InMemory tests do not prove relational behavior.
- Do not run destructive production data operations, mass backfills, or irreversible corrections automatically at startup.
- A backfill/rebuild must be explicit, idempotent or safely restartable, observable, and covered by regression tests.

## Tenant ownership and data isolation

Business data is owned by an application-owned `Business` (issue #64). These are repository-wide invariants, not feature-local choices. `docs/architecture.md` § Tenant ownership describes the design; `docs/tenant-rollout.md` describes the rollout.

- **Ownership is central, never ad hoc.** Tenant-owned reads are scoped by the global query filters in `AppDbContext`, and every write goes through `BusinessOwnershipEnforcer` on `SaveChanges`. Do not add per-controller or per-service `Where(x => x.BusinessId == ...)` clauses: they are redundant, and they make the real boundary look optional. Fix the central mechanism instead.
- **A new persisted entity is tenant-owned by default.** Implement `IBusinessOwned` and it is filtered, indexed, and stamped automatically. An entity that genuinely is not owned must be added to `DeliberatelyGlobalEntities` in `BusinessOwnershipCoverageTests` with the structural reason. That test fails if a new table arrives with no owner.
- **Never accept a business or tenant ID from client input.** It is resolved from the authenticated actor's membership and stamped by the enforcer. No route, query, form, JSON, or header value may choose an owner, and no DTO carries one.
- **Fail closed.** An unresolved business reads nothing and writes nothing. Never treat "no current business" as "no filter"; that turns a resolution bug into a cross-business data leak.
- **Unrestricted access is an explicit opt-in.** Only the human-invoked `migrate-database`, `bootstrap-business` and `migrate-documents` commands may pass `UnscopedBusinessScope.Instance`. No request path, controller, or service may run unrestricted. `IgnoreQueryFilters`, raw SQL, and direct `AppDbContext` construction in a request path are boundary violations. The platform diagnostics API (issue #336) is not an exception to this rule and must not become one: a diagnostics request carries a **denied** `BusinessScope`, so the query filters and `BusinessOwnershipEnforcer` stay fully in force and it reads nothing at all through `AppDbContext`. Its cross-business read happens on a separate, read-only SQLite connection owned by `Inventory.Infrastructure.PlatformDiagnostics`, restricted to an allowlisted table/column surface by SQLite's own authorizer. See § Architecture rules and `docs/architecture.md` § Platform diagnostics.
- **The membership requirement is bypassed for exactly one endpoint family, and only after a policy check.** `BusinessScopeMiddleware` lets a request past the `BusinessMembership` requirement only when the endpoint carries `PlatformDiagnosticsEndpointAttribute` **and** the middleware's own re-evaluation of the `PlatformDiagnostics` policy succeeds on that request. Do not infer the bypass from pipeline ordering, do not mark another endpoint with that attribute, and do not add a second bypass. Every other endpoint keeps its current membership behaviour.
- **Ownership is immutable and relationships stay inside one business.** A record cannot be moved between businesses, and a foreign key between tenant-owned entities may not cross one.
- **Uniqueness is per business.** Constraints over externally supplied values — Nayax transaction IDs, import file hashes, site agreements, fee effective dates — are scoped by business, because two businesses may legitimately hold the same external value.
- **Claims parsing stays at the API boundary.** Domain and Application must not reference ASP.NET claims or principals; use the Application current-business abstraction.
- **Isolation changes need two-business tests.** Any change to ownership, filtering, or enforcement requires relational tests with two synthetic businesses proving reads, writes, ID lookups, relationships, reports, imports, and document access cannot cross.

## Core bookkeeping and reporting invariants

These rules come from the application's established bookkeeping design. Changing one requires an explicit issue, updated tests, and a clear migration/recalculation plan.

### Sales, payment methods, and statuses

- Gross vending sales are not the same as a Nayax payout or bank deposit.
- Keep total sales, card sales, and cash sales distinct. Cash sales contribute to vending revenue but are excluded from Nayax reimbursement/settlement calculations.
- Use `PaymentMethodClassifier` as the central card/cash/unknown classification. Do not add report-specific string matching.
- Only status ID `12` is an approved/completed sale.
- Status IDs `55` and `80` are pending and are not final sales.
- Status ID `62` is refunded.
- Status IDs `26`, `28`, `31`, and `250` are cancelled or declined.
- Null or unrecognised statuses remain unknown. Pending, refunded, cancelled/declined, and unknown rows must remain visible in data-quality/status reporting; never silently treat them as completed or discard them.
- Avoid double counting imported transactions. A duplicate or conflicting transaction must be surfaced and handled deliberately.

### Reimbursements and reconciliation

- Reconcile completed card transactions to Nayax-reported gross card sales. Never reconcile total sales, including cash, to a Nayax transfer.
- Match a reimbursement to its `ReimbursementStartDate`/`ReimbursementEndDate` sales period, not its payout/import/bank date.
- Preserve the distinction between gross amount, reimburse-by-Nayax amount, non-reimbursed amount, processing fee, fee GST, other fees, adjustments, expected net reimbursement, actual net reimbursement, and amount transferred.
- A period is reconciled only when the absolute difference is at most `$0.01`.
- Do not force an overlapping or partial reimbursement into a different requested reporting period. Surface pending or unmatched periods.
- When imported data cannot support adjustments or machine-level allocation, expose that limitation through data-quality fields/notes instead of inventing an allocation.

### Nayax processing fees and GST

- Keep processing fee excluding GST, fee GST, and fee including GST as separate values.
- Imported reimbursement fee data is authoritative for the dates it covers.
- Estimate fees only for completed card transactions on dates not covered by authoritative imported fee data, using the effective-dated configured rate.
- Never charge or estimate a Nayax processing fee for cash transactions.
- Never double count actual and estimated fees for the same covered date.
- If no effective rate exists, report missing-rate transactions and make affected profit results provisional/unavailable as appropriate. Do not assume zero.
- GST-inclusive sales GST is `inclusive amount / 11` under the current Australian GST assumption. GST on an exclusive fee is `exclusive amount * 10%`. Reuse the central calculation functions.
- Do not infer unsupported purchase GST. Missing GST classification remains a data-quality limitation.

### Site commissions

- Commissions are based on effective-dated `SiteCommissionAgreement` records.
- Supported bases are gross sales, card sales, and sales excluding GST. Preserve their existing meanings in `SiteCommissionCalculator`.
- No agreement is a valid zero-commission case. Incomplete coverage and overlapping agreements are configuration problems and must make affected reports provisional rather than selecting one silently.
- A payment cannot exceed commission due beyond the established `$0.01` tolerance.

### Profit calculations

- Calculate gross profit only when COGS is complete: `gross sales - COGS`.
- Missing COGS is `null`/unknown, never zero. Preserve partial COGS, uncosted transaction count, and uncosted sales amount separately.
- Direct profit subtracts applicable COGS, Nayax fees including GST, site commission, and direct operating expenses from sales.
- Whole-business net profit additionally includes shared receipt delivery/package costs and other operating expenses. Do not present whole-business net profit for a machine-filtered report when shared overhead is unallocated.
- Margin percentages must use the same authoritative profit/cost calculation and must handle zero sales without division errors.
- Dashboard, detail reports, transaction reports, CSV, and XLSX must use the same calculation engine. A presentation/export must not reimplement financial formulas.

### Reporting dates and filters

- Australian financial years run from 1 July through 30 June.
- Reporting ranges are inclusive calendar dates. Preserve the distinction between sale/authorization date, reimbursement coverage dates, and payout date.
- The business reporting timezone is `Australia/Sydney`. Store true instants consistently and do not introduce server-local or browser-local date shifts. Any timezone behavior change requires boundary tests, including daylight-saving transitions.
- Machine, site, product, payment type, status, COGS status, date, financial-year, search, paging, and sorting filters must not silently change totals or reconciliation scope.
- CSV/XLSX exports must represent the same filters, definitions, data-quality state, and totals as their API/UI report.

## Inventory and historical costing invariants

- `QuantityInStock` represents physical storage/home stock used for replenishment planning.
- `CostingQuantity` and `InventoryValue` represent business-owned inventory for perpetual weighted-average costing; they are not synonyms for storage quantity.
- `Product.UnitPrice` is the catalog default/list selling price, synced one-way from the Nayax product catalog's `ProductDefaultRetailPrice` field by `Inventory.Application.Imports.ImportNayaxProductCatalog`. It is a display/default value only: no calculation in `Inventory.Application`/`Inventory.Domain` reads it, and `Inventory.Application.Products.UpdateProduct`/the product edit UI intentionally treat it as Nayax-managed and read-only. Never conflate it with `AverageUnitCost`/AVCO, historical sale cost, the Nayax `ProductCostPrice`/`NayaxProductCostPrice` cost field, or `Product.MachinePrice` (the machine-specific live price sourced from the per-machine Nayax `RetailPrice`). That operator-catalogue JSON field name is **confirmed** (issue #363): a human confirmed from a live `GET /v1/operators/{OperatorID}/products` response that the product selling price field is `ProductDefaultRetailPrice`, matching the published Nayax contract; see `docs/architecture.md` § Product selling price for the confirmation detail. Products already imported with `UnitPrice` of `0` under the previous, unconfirmed mapping are not backfilled by issue #363.
- A receipt-linked restock increases costing quantity/value at its purchase unit cost.
- `MachineRefill` is an internal transfer from storage to a vending machine. It can reduce storage quantity, but must not reduce business costing quantity/value and must not create COGS.
- A completed sale reduces costing inventory and records historical unit cost and COGS.
- Cost-bearing write-offs such as damaged/expired stock must follow their explicit inventory movement semantics and remain auditable.
- A costing repair (`InventoryCostRepair`, issue #359) is the only supported way to restore costing history that was never recorded, and it is costing-only: it increases `CostingQuantity`/`InventoryValue` and recosts later sales, and never changes `QuantityInStock`, machine quantities, `StockAdjustment` rows, MachineRefill history or a transition baseline. Repairs are explicit, auditable, positive-only historical events: quantity greater than zero, a non-negative unit cost, a specific reason, the creating identity and the effective instant recorded, and append-only - there is no update, delete or reversal path. Never infer one from a machine-refill gap, a Nayax event, the product's current cost, or anything else, and never let one stand in for a real purchase, correction or write-off. A repair that leaves a fatal data-quality issue must persist nothing.
- Never infer historical COGS from the product's current cost or selling price.
- Historical sale-cost precedence is:
  1. persisted internal AVCO/ledger cost when the ledger is reliable;
  2. otherwise, the transaction-level Nayax `Product Cost Price` persisted on that sale;
  3. otherwise, leave the sale uncosted.
- Persist `UnitCostAtSale`, `CostOfGoodsSold`, `CostingStatus`, and `CostSource` on the sale. Do not recalculate old reports from today's product cost.
- Rebuild inventory and sale costs chronologically and deterministically. Never fabricate purchases or opening costs to make a report balance.
- Cost rebuilds must preserve provenance and visibly report data-quality failures.
- Product reorder behavior must continue to account for physical stock, machine replenishment need, outstanding supplier orders, low-stock threshold, and restock target.

## Nayax integration

- Treat remote Nayax identifiers as external identities; do not repurpose local entity IDs.
- Do not call Nayax once per row when a batched read is available.
- Pass cancellation tokens and handle partial/unavailable remote data without corrupting local state.
- Never log API tokens, authorization headers, connection strings, raw credentials, or sensitive imported payloads.
- Keep raw imported facts separate from derived accounting values so calculations can be rerun and audited.

### Nayax contract verification

Nayax's developer documentation, not the code or a guess, is the authority on a Nayax API contract. The automated agents can read it through the official Nayax documentation MCP server (`https://devzone.nayax.com/mcp`, server name `nayax`), limited to its two read-only tools `search_nayax_developer_portal` and `query_docs_filesystem_nayax_developer_portal`; see `docs/automation.md` § Nayax documentation access.

- When implementing, repairing, architecture-checking or reviewing code that calls the Nayax API or models a Nayax request or response, look up the relevant endpoint contract in that documentation **before** defining, changing or accepting request/response DTOs or any assumption about field names, types, nullability, identifiers, timestamps or endpoint semantics.
- Never invent a Nayax response property when the authoritative contract can be retrieved. A field the documentation does not confirm stays unconfirmed.
- If the documentation tools are unavailable, or the contract cannot be found, say so explicitly: in a pull request body, comment or review, state that authoritative Nayax verification could not be completed and which contract it affects, and treat that contract as unverified (a reviewer reports it as `not verified` or a blocker, never `met`). Never silently fall back to guessed fields for a contract-sensitive change.
- Work that does not touch the Nayax integration needs no lookup, and an unreachable documentation server must not block it.
- The documentation tools grant no other web access: `WebFetch`/`WebSearch` stay disabled, and the server's `submit_feedback` tool is denied. Model triage and the format-only Copilot review JSON repair have no Nayax documentation access and perform no lookup.

## Files and attachments

- Validate extension, content type, size, and generated storage name on the server. Do not trust the uploaded filename or client MIME type alone.
- Prevent path traversal and never allow a request to choose an arbitrary filesystem path.
- Keep database changes and file replacement/deletion behavior consistent when an operation fails.
- Do not expose physical server paths through API responses or logs.

## Frontend rules

- The frontend is Angular 19 using standalone components and TypeScript.
- Use `ConfigService` for the API base URL; do not hardcode production or local API URLs in feature code.
- Keep money as numeric values through the API/client boundary and format it only for display.
- Preserve nullable financial values. Do not convert unavailable profit/COGS to `0` in TypeScript or templates.
- Display provisional, incomplete, pending, unknown, and reconciliation-warning states instead of hiding them.
- Keep calculations on the backend unless a UI-only display calculation is explicitly safe and tested.
- Maintain accessible labels, keyboard behavior, loading states, empty states, and actionable error messages.
- Page/detail components (the ones routed directly in `app.routes.ts`) are primarily composition/orchestration boundaries: route/query parameters, the page's own loading/error/selection state, and laying out child components. When a change adds a distinct workflow with its own substantial UI plus its own state/actions/loading or error lifecycle, implement it as a dedicated feature component composed back in through `@Input`/`@Output`, not grown directly inside the page component. See `docs/architecture.md` § [Page composition boundary](docs/architecture.md#page-composition-boundary-issue-191) before adding new page-level UI, and its automated `frontend/inventory-app/src/app/architecture/page-composition.guard.ts` check, which fails if a routed page's own template authors dialog markup (`role="dialog"`) directly instead of delegating to a child component.

## Tests required by change type

- Domain formula/rule: focused unit tests, including boundaries and failure cases.
- EF query or persistence: relational SQLite integration test where SQL/transactions/constraints matter.
- Migration: upgrade test from the previous migration and verification of preserved data.
- Nayax import/status/payment behavior: representative imported rows plus unknown/duplicate/missing cases.
- Financial report: totals, card/cash split, COGS-complete and incomplete paths, fee source, commission coverage, reconciliation, filters, and export parity as applicable.
- File operation: valid file, invalid extension/type/size, replacement failure, cleanup, and path safety.
- API contract: success response plus validation/not-found/conflict cases.
- Regression fix: a test that fails before the fix and passes after it.

Do not weaken or delete a failing test merely to obtain a green build. If an established rule intentionally changes, explain it in the PR and update all affected tests and documentation together.

## Security and deployment safeguards

- Never add secrets to source, test fixtures, logs, screenshots, documentation, issues, pull-request text, build output, or API responses. No participant, human or agent, may expose a production secret; humans access production secrets only through approved secure platform administration when required.
- Do not alter GitHub/Azure credentials, environment variables, production CORS origins, deployment environments, or infrastructure unless explicitly requested.
- A push to `main` deploys nothing; only the human-started Deploy Production workflow deploys the API and frontend. Agents must create a branch and a pull request targeting `develop`. Agents never merge, deploy, or start Deploy Production. Production releases are separate `develop` to `main` pull requests that an agent prepares only on explicit human request and that only a human approves and merges; a human then starts Deploy Production. A failed production deployment never gives an agent authority to merge, revert, roll back or deploy; recovery is a human decision. Do not add Azure login, deploy steps, the production environment or deployment secrets to any other workflow; `scripts/validate-deployment-workflows.mjs` rejects it.
- Agents never deploy, run production migrations, import production statements, modify production data, or invoke destructive remote operations. These remain human-controlled operations performed outside the agent's authority.
- Do not use `git push --force`, destructive resets, or history rewriting.

## Definition of done

A change is complete only when:

- Acceptance criteria are met without unrelated behavior changes.
- Relevant tests cover success, important edge cases, and regression risk; for test-first changes the PR states which test initially failed and why.
- Complete validation succeeds, or the exact environmental blocker is documented.
- Schema/API/configuration changes are documented and backward compatibility is considered.
- The documentation impact decision has been honoured: documentation the issue required is updated, any documentation the change would otherwise contradict is updated, and the pull request's `## Documentation impact` declaration is accurate and passes `scripts/validate-documentation-impact.mjs`.
- Financial and inventory definitions remain internally consistent across API, UI, and exports.
- The diff contains no secret, local database, uploaded business document, generated output, or accidental large file.
- The pull request explains what changed, why, how it was verified, and any data or deployment risk.

