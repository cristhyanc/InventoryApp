# InventoryApp agent instructions

These instructions apply to the entire repository. More specific `AGENTS.md` files may refine them for a subtree, but may not weaken the financial, inventory, security, or deployment safeguards in this file.

## Mission

InventoryApp manages a real vending-machine business. Treat inventory quantities, historical cost, sales, fees, commissions, reimbursements, GST fields, and profitability as financial data. Prefer a small, reviewable, tested change over a broad rewrite.

The repository is also being prepared for reliable AI-assisted engineering. Every change must be understandable from its issue, diff, tests, and validation output.

## Repository map

```text
backend/InventoryApi/                 ASP.NET Core .NET 10 API
  Controllers/                        HTTP boundary
  DTOs/                               Current API/report contracts
  Data/AppDbContext.cs                EF Core model and mappings
  Integrations/Nayax/                 Nayax Lynx HTTP integration
  Migrations/                         SQLite schema history
  Models/                             Current entities and enums
  Services/                           Current application/domain logic
  Services/Interfaces/                Current service contracts
backend/InventoryApi.Tests/           xUnit backend tests
frontend/inventory-app/               Angular 19 standalone application
.github/workflows/                    Validation, Claude Code agent, and Azure deployment workflows
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
scripts/validate-documentation-impact.mjs  Documentation-impact declaration parser for issues and PRs (with .test.mjs)
```

Read the nearest relevant production code and tests before changing behavior. For financial or inventory changes, also read the corresponding models, migrations, import logic, and reporting tests.

## Required workflow

1. Work on a feature branch created from the latest `develop`. Never commit directly to `develop` or `main`.
2. Restate the issue's acceptance criteria, its explicit exclusions, and its documentation impact decision, and identify affected domain rules.
3. Inspect existing implementations and tests before proposing a design.
4. Make the smallest coherent change. Do not mix unrelated cleanup with feature work.
5. Add or update tests for the behavior and meaningful edge cases.
5a. Follow the issue's documentation impact decision. When it says documentation changes are required, update every listed documentation file and section in the same change. When it says none are required but the change nevertheless makes documentation inaccurate, update that documentation anyway and state the discrepancy in the pull request. Never leave documentation that the change contradicts.
6. Run the complete repository validation from the repository root:

   - Windows: `powershell -ExecutionPolicy Bypass -File scripts/validate.ps1`
   - PowerShell 7: `pwsh -File scripts/validate.ps1`
   - macOS/Linux/Git Bash: `bash scripts/validate.sh`

7. Review the final diff for secrets, accidental schema changes, generated files, and unrelated edits.
8. Open a pull request targeting `develop`, using the repository pull request template, with the reason for the change, tests run, financial/data risks, migration impact, any known limitations, and an accurate `## Documentation impact` section (see [Documentation impact gate](#documentation-impact-gate)).
9. After opening the pull request, update only the feature branch, and only for at most two permitted repair attempts in response to CI or review failures. Stop and return control to a human when validation and review succeed, after two failed repair attempts, or when requirements are ambiguous or conflicting. Agents never merge or deploy; a human reviews and merges the pull request.

If complete validation cannot run, state exactly which command failed or was unavailable. Never claim a test or build passed unless it ran successfully.

## Branches and releases

- The default base branch for normal work is `develop`. Create feature branches from the latest `develop`.
- Normal feature and fix pull requests target `develop`, which is the integration branch. A push to `develop` runs backend build and tests but does not deploy.
- Production releases are separate pull requests from `develop` to `main`. A merge to `main` deploys the API and frontend to Azure.
- An agent may prepare or update a `develop` to `main` release pull request only when a human explicitly requests it. Permission to implement a feature never grants permission to create a release pull request. Agents never merge or deploy: a human reviews and merges the release pull request, and the existing workflow performs the deployment.

## Automated task contract

These rules govern any current or future automated agent that implements a GitHub issue. `docs/automation.md` contains the complete lifecycle, authority model, task states, risk classification, and retry policy; this section is the binding summary.

- A human must review an issue and mark it `agent-ready` before an automated agent begins implementation. An agent must not start from an unreviewed issue and must never apply `agent-ready` itself.
- The issue's acceptance criteria and explicit exclusions define the agent's authority. Work outside them is out of scope even when it is nearby, related, or looks like useful cleanup.
- When requirements are materially ambiguous, conflict with each other, or conflict with this file, `docs/architecture.md`, or existing tests, stop and request a human decision. State exactly which decision is needed.
- Do not expand scope, broaden acceptance criteria, or reinterpret exclusions. Propose follow-up work in the pull request instead.
- After a pull request fails validation or review, an agent may make at most two automated repair attempts. After that, or as soon as a repair would require weakening a test or changing an established rule, the task is blocked and returns to a human.
- After opening the pull request, the agent may update its feature branch for at most two permitted repair attempts in response to CI or review failures. It stops and returns control to a human when validation and review succeed, after two failed repair attempts, or when requirements are ambiguous or conflicting. It never merges a feature or release pull request, never pushes to `develop` or `main`, never deploys, and never runs production migrations or modifies production data. An agent prepares a release pull request only on a separate, explicit human request, and only a human approves and merges it.
- High-risk categories require explicit human scrutiny of both the issue and the pull request: financial or profit calculations, inventory quantity or historical costing, database schema or migrations, backfills or destructive data operations, authentication or authorization, secrets or environment configuration, GitHub Actions/Azure/deployment changes, any Nayax or other external-integration change (whether it reads, writes, imports, synchronises, maps errors, changes authentication, or handles remote payloads), imports or reconciliation, public API contract changes, file upload or filesystem security, and any production-impacting operation.
- Every automated change must be traceable through its issue, branch, commits, pull request, validation result, review result, and human merge decision.
- Every agent task issue must carry a documentation impact decision and details, and every pull request must carry a `## Documentation impact` declaration. Automation only checks that these declarations exist and are meaningful; the reviewer decides whether they are truthful. See [Documentation impact gate](#documentation-impact-gate).

## Documentation impact gate

Documentation in this repository (`AGENTS.md`, `CLAUDE.md`, `docs/`, `README.md`, the issue and pull request templates) is part of the product: the agents obey it and the humans rely on it. Every change therefore makes an explicit, checked decision about documentation. The gate has four parts with separate owners:

1. **Templates collect the decision.** The agent task issue form has a required `Documentation impact decision` dropdown with exactly two options, `Documentation changes required` and `No documentation changes required`, and a required `Documentation impact details` field that lists the affected documentation files/sections or explains, for that specific task, why documentation is unaffected. The pull request template has a `## Documentation impact` section with exactly one `Decision:` line, which must be exactly `UPDATED` or `NOT REQUIRED`, and exactly one `Evidence:` entry. `UPDATED` evidence lists each documentation file changed and what changed in it. `NOT REQUIRED` evidence explains why behaviour, contracts, architecture, configuration, automation, deployment, operations and user workflows are unaffected. Bare `None`, `N/A` or `Not applicable`, generic sentences that only restate those categories, and unreplaced template placeholders are invalid in both.
2. **Automation validates that a meaningful declaration exists.** `scripts/validate-documentation-impact.mjs` parses both bodies deterministically. A read-only preflight job in `agent-implement.yml` validates the issue before Claude runs, a branch is created, or a label changes; when it fails, the issue is left unchanged and the run reports what to fix. `validate.yml` validates the pull request body before repository validation, for both the `pull_request` and the exact-SHA `workflow_dispatch` modes, so an invalid declaration fails the normal `merge-validation` or `agent-validation` status; editing the description reruns it. The parser never infers documentation impact from the changed files.
3. **Independent review decides whether the declaration is correct.** `agent-review.yml` compares the issue decision, the pull request declaration and the actual diff. Missing, inaccurate or incomplete required documentation, or a declaration that contradicts the diff, is a blocker. Repairs update affected documentation on the head branch but never edit the pull request description; when the declaration must change, the repair comment states exactly what the human must correct.
4. **Humans remain responsible for approval and merge.** A human confirms the decision when applying `agent-ready` and again when approving and merging.

Issues #87 to #92 were created before this gate and lack the declaration. A human must backfill both fields on each of them (either as the form's `### Documentation impact decision` / `### Documentation impact details` headings or as the `## ...` headings those issues already use) before the preflight becomes active on the default workflow branch, or applying `agent-ready` to them will fail the preflight and leave them untouched.

## Commands

Backend solution:

```bash
dotnet restore backend/InventoryApi/InventoryApi.slnx
dotnet format backend/InventoryApi/InventoryApi.slnx --verify-no-changes --no-restore --exclude backend/InventoryApi/Migrations
dotnet build backend/InventoryApi/InventoryApi.slnx --configuration Release --no-restore
dotnet test backend/InventoryApi/InventoryApi.slnx --configuration Release --no-build --no-restore --collect:"XPlat Code Coverage"
dotnet package list --project backend/InventoryApi/InventoryApi.slnx --vulnerable --include-transitive
```

Frontend:

```bash
npm --prefix frontend/inventory-app ci
npm --prefix frontend/inventory-app run lint
npm --prefix frontend/inventory-app run build
npm --prefix frontend/inventory-app audit
```

Both validation scripts run exactly this pipeline; run the script rather than the individual commands. Notes:

- The backend builds with `TreatWarningsAsErrors`, .NET analyzers and `EnforceCodeStyleInBuild` (see `Directory.Build.props`). A new warning in application code fails the build. The only suppressed compiler diagnostic is `CS8981` on EF Core generated migrations, scoped in `.editorconfig` to `[**/Migrations/*.cs]`. Do not widen that scope and do not add a global `<NoWarn>`.
- `dotnet format` excludes `backend/InventoryApi/Migrations` because an applied migration must not be rewritten.
- Coverage is collected on every test run but has no minimum threshold yet. Coverage output is git-ignored; never commit it.
- The frontend now has a configured `lint` script but still has no `test` script. Do not claim tests ran. `npm run lint` must report zero **errors**; warnings are visible but non-blocking.
- `npm audit` is reported, not enforced: the outstanding high/critical advisories are in the Angular 19 build toolchain and clear only with a major Angular upgrade. Never run `npm audit fix --force`.
- `dotnet package list --vulnerable` always exits 0, so the scripts parse its output. A vulnerable package fails validation; an unreachable nuget.org only warns.

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
- Use `CancellationToken` for asynchronous I/O and pass it through to EF Core and HTTP operations.
- Do not duplicate business formulas in controllers, Angular components, exports, and reports. One authoritative calculation must feed all presentations.

## Database and migrations

- EF Core migrations are the schema source of truth. Startup does **not** apply them outside Development: `DatabaseSchemaStartup` applies migrations automatically only in Development, and in every other environment applies nothing and fails closed when migrations are pending. Production schema changes are applied by a human through the explicit `migrate-database` command (dry run first). Never reintroduce an unconditional `Database.Migrate()` at startup — some tenancy migrations rebuild tables and copy persisted rows, so applying them must never be a deployment side effect.
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
- **Unrestricted access is an explicit opt-in.** Only the human-invoked `migrate-database` and `bootstrap-business` commands may pass `UnscopedBusinessScope.Instance`. No request path, controller, or service may run unrestricted. `IgnoreQueryFilters`, raw SQL, and direct `AppDbContext` construction in a request path are boundary violations.
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
- A receipt-linked restock increases costing quantity/value at its purchase unit cost.
- `MachineRefill` is an internal transfer from storage to a vending machine. It can reduce storage quantity, but must not reduce business costing quantity/value and must not create COGS.
- A completed sale reduces costing inventory and records historical unit cost and COGS.
- Cost-bearing write-offs such as damaged/expired stock must follow their explicit inventory movement semantics and remain auditable.
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
- A push to `main` deploys both the API and frontend. Agents must create a branch and a pull request targeting `develop`. Agents never merge or deploy. Production releases are separate `develop` to `main` pull requests that an agent prepares only on explicit human request and that only a human approves and merges; the existing workflow performs the deployment after that merge.
- Agents never deploy, run production migrations, import production statements, modify production data, or invoke destructive remote operations. These remain human-controlled operations performed outside the agent's authority.
- Do not use `git push --force`, destructive resets, or history rewriting.

## Definition of done

A change is complete only when:

- Acceptance criteria are met without unrelated behavior changes.
- Relevant tests cover success, important edge cases, and regression risk.
- Complete validation succeeds, or the exact environmental blocker is documented.
- Schema/API/configuration changes are documented and backward compatibility is considered.
- The documentation impact decision has been honoured: documentation the issue required is updated, any documentation the change would otherwise contradict is updated, and the pull request's `## Documentation impact` declaration is accurate and passes `scripts/validate-documentation-impact.mjs`.
- Financial and inventory definitions remain internally consistent across API, UI, and exports.
- The diff contains no secret, local database, uploaded business document, generated output, or accidental large file.
- The pull request explains what changed, why, how it was verified, and any data or deployment risk.

