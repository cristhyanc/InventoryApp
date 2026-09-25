# InventoryApp architecture

## Purpose

InventoryApp is a modular full-stack application for operating a vending-machine business. It manages products, suppliers, purchasing documents, physical stock, supplier orders, machine replenishment, Nayax sales/imports, historical cost, fees, commissions, expenses, reconciliation, and management/accounting reports.

This document describes the repository at the `main` baseline inspected on 17 September 2026 and the intended incremental architecture. It is a guide for maintainers and coding agents; existing tests remain the executable specification.

## Runtime stack

| Area | Technology |
| --- | --- |
| Backend | ASP.NET Core Web API on .NET 10 |
| Persistence | EF Core 10, SQLite, code-first migrations |
| External integration | Nayax Lynx HTTP API and imported reimbursement/workbook data |
| Documents | Purchase and operating-expense files behind `IDocumentStorage`, with metadata in SQLite. `DocumentStorage:Provider` selects the implementation: `FileSystem` (the default) keeps them under the content root's `protected-files/`, outside the web root, with a legacy `wwwroot/{category}` read fallback; `AzureBlob` keeps them in a private Azure container under `tenants/{businessId}/...`, authenticated with a managed identity. Static-file middleware is disabled and no SAS or public URL is ever generated, so no document has an anonymous URL |
| Frontend | Angular 19 standalone components, TypeScript, RxJS, Tailwind-based styling |
| Backend tests | xUnit, Moq, EF Core InMemory and SQLite |
| Hosting | Azure App Service API and Azure Static Web Apps frontend |
| Automation | GitHub Actions |

### SQLite operating assumptions and scale strategy (issue #53)

The production database is the single SQLite file EF Core opens through
`ConnectionStrings:DefaultConnection` (`backend/InventoryApi/appsettings.json`), which defaults to
`Data Source=inventory.db` — a path relative to the API process's working directory
(`backend/InventoryApi/Program.cs`). No committed `appsettings.Production.json` overrides that
default; a different value can only come from an App Service application setting
(`ConnectionStrings__DefaultConnection`), which is Azure configuration outside this repository and
not verifiable from source.

**Where the file lives and what persists.** Azure App Service keeps exactly one directory durable
across process restarts and reboots, and shared across every instance if the plan ever scales to
more than one: `/home` (Windows: `d:\home`), mounted from the platform's own storage rather than
the ephemeral per-instance disk. A normal zip/package deployment through `azure/webapps-deploy@v2`
(`.github/workflows/vm-manager.yml`) extracts the published output under `/home/site/wwwroot`, so
the default relative `inventory.db` path resolves inside that persistent directory today, and the
file survives ordinary app restarts.

That default is fragile for two reasons this repository cannot verify from source, because they
are Azure App Service configuration, not code:

1. **Run-From-Package.** If the App Service is configured with `WEBSITE_RUN_FROM_PACKAGE=1` (a
   common App Service deployment mode), `wwwroot` is mounted **read-only** from the deployed
   package, and a relative `inventory.db` path would not be writable at all. Confirm this setting
   for the live app before relying on the default relative path.
2. **Deployment overwrite.** Even where `wwwroot` is writable, it is also the deployment target.
   Placing the database file inside the same directory the deployment pipeline writes to is an
   avoidable risk, independent of exactly how a given deployment happens to behave.

**Operational recommendation** (an App Service configuration change, not a code change — this
issue does not alter any production setting): point `ConnectionStrings__DefaultConnection` at an
absolute path outside `wwwroot` but still under the persistent `/home` mount, for example
`Data Source=/home/data/inventory.db`, and confirm `WEBSITE_RUN_FROM_PACKAGE` is unset or `0` for
this app. A human operator must make and verify this against the live App Service; it is not
something an automated agent may apply (`AGENTS.md` reserves Azure/App Service configuration
changes for humans).

**Concurrency and locking limits.** The API opens the database with `Microsoft.Data.Sqlite`'s
default journal mode (rollback journal, not WAL — nothing in `Program.cs` configures
`journal_mode`) and the driver's default busy timeout (`Default Timeout=30`, i.e. 30 seconds).
SQLite allows unlimited concurrent readers but only one writer at a time; a second writer blocks
for up to the busy timeout and then fails with `SQLITE_BUSY` rather than queuing indefinitely. In
this single-process deployment that is a soft limit on request latency under write-heavy
concurrent load (for example simultaneous imports and sale-costing writes), not a hard outage —
but it does not extend to multiple concurrently writing processes: **the App Service plan for this
app must stay pinned to a single instance (no scale-out) while SQLite remains the store.** SQLite's
file locking is unreliable over the kind of shared, network-backed storage `/home` becomes once
more than one instance mounts it, so running two instances against the same database file is not a
supported configuration.

**Monitoring signals that indicate SQLite pressure** — none of these are wired into automated
alerting today (that is future work, not part of this change), but are the observable evidence
worth watching for:

- `SQLITE_BUSY`/timeout exceptions in the application log
  (`Microsoft.Data.Sqlite.SqliteException` with `SqliteErrorCode == 5`), especially clustered
  around import or reconciliation jobs.
- API request latency growth specifically on write endpoints (imports, sale costing, purchases,
  stock adjustments) that does not correlate with CPU/memory pressure — a symptom of writers
  queued behind the single-writer lock rather than genuine compute cost.
- Database file size approaching the App Service plan's storage quota, or backup/restore
  durations (see below) growing enough to threaten the business's recovery-time expectations.
- Any deliberate move to more than one App Service instance for this app, which SQLite's
  single-writer, file-locking model does not support safely.

**Measurable triggers to migrate to a server database (Azure SQL or PostgreSQL).** Any one of
these is a concrete, evidence-based reason to plan the migration, not a speculative one:

1. The App Service plan needs more than one concurrently running instance (for availability or
   throughput) — SQLite cannot be safely shared across instances at all.
2. Recurring `SQLITE_BUSY` write-timeout errors under normal (non-incident) load, observed rather
   than anticipated.
3. A verified backup/restore cycle (see below) exceeds the business's acceptable recovery time
   because the file has grown large enough that a full-file backup and restore no longer fits the
   maintenance window.
4. A second live business is onboarded whose Nayax integration needs to run concurrently with the
   first (see [Tenant ownership](#tenant-ownership-issue-64): the Nayax client is single-tenant
   today) in a way that increases concurrent write load beyond what one SQLite writer sustains.

None of these are currently met; the current single-business, single-instance deployment is within
SQLite's supported envelope. This issue does not migrate the database (explicitly out of scope for
issue #53); it defines what would justify doing so.

**Where a server-database migration would live.** If one of the triggers above is met, the target
is a Clean Architecture change, not a drop-in connection-string swap. `AppDbContext` is currently
API-owned in `InventoryApi/Data` as a documented transitional state (see [Backend target: pragmatic
Clean Architecture with vertical slices](#backend-target-pragmatic-clean-architecture-with-vertical-slices)
below), not the intended permanent location. A provider migration belongs in
`Inventory.Infrastructure` — landing there directly, or moving there together with `AppDbContext`
if that relocation has not happened first — behind the same narrow persistence ports this document
already describes for migrated features, so `Inventory.Domain` and `Inventory.Application` stay
unaware of which relational engine is in use. Do not treat the current `InventoryApi`-owned
location of `AppDbContext` as where a server-database provider belongs.

**Backup and restore procedure.** SQLite's Online Backup API — the same engine feature the
`sqlite3` CLI's `.backup` command and `Microsoft.Data.Sqlite`'s
`SqliteConnection.BackupDatabase` both call — copies a consistent snapshot of the database to a
new file without requiring the source connection, or the API process holding it, to stop.
`backend/InventoryApi.Tests/Operations/SqliteBackupRestoreTests.cs` proves this mechanically: it
backs up a live SQLite file while the source connection that created it stays open (mirroring a
running API process) and confirms the resulting copy passes `PRAGMA integrity_check` and contains
exactly the committed rows, then proves a second backup taken later, still without closing that
connection, reflects the writes committed in between.

The equivalent operator procedure, using the `sqlite3` CLI (the standard SQLite tool;
<https://sqlite.org/cli.html>) against the App Service's persistent database path:

1. **Prefer a quiet window, but do not rely on stopping the app.** The Online Backup API produces
   a consistent snapshot even while writes continue; stopping the App Service first (or scaling to
   zero) removes any residual risk but is not required for backup correctness. Do not run a plain
   filesystem `cp` of the `.db` file while the app is running — unlike the backup API, a raw copy
   can capture a database file mid-write and is not guaranteed consistent.
2. **Take the backup:**

   ```bash
   sqlite3 /home/data/inventory.db ".backup '/home/data/backups/inventory-$(date +%Y%m%dT%H%M%S).db'"
   ```

3. **Verify the backup immediately**, before trusting it:

   ```bash
   sqlite3 /home/data/backups/inventory-<timestamp>.db "PRAGMA integrity_check;"
   ```

   A result other than a single `ok` row means the backup is not trustworthy; take it again.
4. **Store the verified backup somewhere other than the App Service's own `/home` mount** (for
   example downloaded to a workstation, or uploaded to separate storage) — a backup that lives only
   next to the database it protects does not survive whatever destroys the database.
5. **Restore** by stopping the API, replacing the live database file with the verified backup (or
   pointing the connection string at the restored file), and starting the API again:

   ```bash
   sqlite3 /home/data/backups/inventory-<timestamp>.db ".backup '/home/data/inventory.db'"
   ```

   Restoring does overwrite live data and must only be run deliberately, by a human, after
   confirming the target file is the one meant to be replaced — this is the one step in the
   sequence that is not safe to automate or run against a live app.

This procedure, and the non-destructive local check in the README, do not touch any production
connection string, credential, or data; every example above uses a placeholder path that a human
operator supplies for their own environment.

## Current repository structure

```text
InventoryApp/
├── backend/
│   ├── InventoryApi/
│   │   ├── Controllers/
│   │   ├── Data/
│   │   ├── DTOs/
│   │   ├── Integrations/Nayax/
│   │   ├── Migrations/
│   │   ├── Models/
│   │   ├── Services/
│   │   ├── Program.cs
│   │   └── InventoryApi.csproj
│   ├── Inventory.Domain/            NayaxFeeSettings rule, reporting policies/calculations (Inventory.Domain.Reporting.<Feature>), Purchases.PurchaseTotalValidationPolicy; other features not yet migrated
│   ├── Inventory.Application/       NayaxFeeSettings use cases/ports, reporting use cases/contracts (Inventory.Application.Reporting.<Feature>), Purchases.ComputePurchaseTotalValidation, Documents.IDocumentStorage, shared Inventory.Application.Time.IClock/IBusinessCalendar; other features not yet migrated
│   ├── Inventory.Infrastructure/    SystemClock/SydneyBusinessCalendar adapters (Inventory.Infrastructure.Time), FileSystemDocumentStorage and AzureBlobDocumentStorage (Inventory.Infrastructure.Documents); other features not yet migrated
│   └── InventoryApi.Tests/
├── frontend/inventory-app/
│   ├── src/app/
│   │   ├── components/          Feature pages and shared UI
│   │   ├── models/              Shared TypeScript contracts
│   │   ├── services/            API clients and UI services
│   │   ├── app.config.ts        Angular providers and startup
│   │   └── app.routes.ts        Application routes
│   ├── src/assets/config.json       Runtime API configuration
│   ├── proxy.conf.json              Local API proxy
│   └── package.json
├── .github/workflows/
├── AGENTS.md
└── scripts/
```

The Angular application uses standalone components. `app.config.ts` registers the router, HTTP client, and a startup initializer that loads the API base URL. Routes load their page components lazily with `loadComponent`, except the public `/auth` Entra redirect callback, which stays eagerly imported (see [Routing and loading](#routing-and-loading)). Pages keep their own view state and call singleton services, which use `HttpClient` to reach the API.

The API's production dependency skeleton (`Inventory.Domain`, `Inventory.Application`, `Inventory.Infrastructure`) is wired into the `InventoryApi` composition root through `AddApplicationServices()`/`AddInfrastructureServices()` extension methods. The Nayax fee-settings slice (GET/POST `api/settings/nayax-processing-fee-rates`) is the first feature moved into this shape: `Inventory.Domain.NayaxFeeSettings.NayaxFeeRate` validates the configured rate, `Inventory.Application.NayaxFeeSettings` holds the `ListNayaxFeeRates`/`SaveNayaxFeeRate` use cases and the `INayaxFeeRateStore` port (`SaveNayaxFeeRate` also takes the shared `Inventory.Application.Time.IClock` port, promoted out of this feature slice into a shared Application abstraction — see [Time](#time)), `Inventory.Infrastructure.Clock.SystemClock` implements `IClock`, and `SettingsController` only binds HTTP input and maps the use-case result. Because `AppDbContext` and its EF entities still live in `InventoryApi`, `INayaxFeeRateStore` is implemented by `InventoryApi.Adapters.Persistence.EfNayaxFeeRateStore` — a deliberately temporary API-owned adapter, registered directly in `Program.cs` rather than through `AddInfrastructureServices()`, so that `Inventory.Infrastructure` does not need to reference `InventoryApi`. It must move into `Inventory.Infrastructure` once `AppDbContext` and the shared persistence models relocate there. The bookkeeping report (GET `api/reports/bookkeeping`) is the second feature moved into this shape, following the same pattern: `Inventory.Domain.Reporting.Bookkeeping.BookkeepingProfitPolicy` computes profit/margin/GST/net-settlement from already-aggregated facts, `Inventory.Application.Reporting.Bookkeeping.GetBookkeepingReport` is the use case, `IBookkeepingReportFactsProvider` is its narrow port, and `InventoryApi.Adapters.Persistence.EfBookkeepingReportFactsProvider` is its temporary API-owned EF adapter (also composing the still-legacy `INayaxProcessingFeeService`/`ISiteCommissionService`). `ReportsController` calls `GetBookkeepingReport` directly for that endpoint; at that point in the migration, the legacy `ReportingService.GetBookkeepingAsync` delegated to the same use case so CSV/XLSX export and the GST report (which reuses bookkeeping's result) stayed on one authoritative implementation, until issue #92 removed `ReportingService` entirely (see below). The daily report (GET `api/reports/daily`) is the third feature moved into this shape, following the same pattern: `Inventory.Domain.Reporting.Daily.DailyRowPolicy` computes each day's profit/margin/reconciliation status from already-aggregated facts, reusing the shared `Inventory.Domain.Reporting.ReconciliationStatusPolicy` (placed there, alongside `ReportingCalculations`, so the still-legacy reconciliation report can reuse the same policy once it migrates instead of reimplementing it), `Inventory.Application.Reporting.Daily.GetDailyReport` is the use case, `IDailyReportFactsProvider` is its narrow port, and `InventoryApi.Adapters.Persistence.EfDailyReportFactsProvider` is its temporary API-owned EF adapter. Its completed-sale cost query and period-level imported-reimbursement summary are shared with `EfBookkeepingReportFactsProvider` through `InventoryApi.Adapters.Persistence.EfReportingSharedQueries` rather than duplicated a third time; its per-date reimbursement grouping is specific to daily and has no bookkeeping equivalent. `ReportsController` calls `GetDailyReport` directly for that endpoint; at that point in the migration, the legacy `ReportingService.GetDailyAsync` delegated to the same use case so CSV/XLSX export stayed on one authoritative implementation, until issue #92 removed `ReportingService` entirely (see below). The reconciliation report (GET `api/reports/reconciliation`) is the fourth feature moved into this shape, following the same pattern: `Inventory.Domain.Reporting.Reconciliation.ReconciliationPeriodPolicy` computes each period's (and the totals row's) gross/settlement difference and status from already-aggregated facts, reusing the shared `Inventory.Domain.Reporting.ReconciliationStatusPolicy` daily also calls, `Inventory.Application.Reporting.Reconciliation.GetReconciliationReport` is the use case, `IReconciliationReportFactsProvider` is its narrow port, and `InventoryApi.Adapters.Persistence.EfReconciliationReportFactsProvider` is its temporary API-owned EF adapter. Its completed and all-status sales queries are shared with `EfBookkeepingReportFactsProvider`/`EfDailyReportFactsProvider` through `EfReportingSharedQueries`; its per-reimbursement-period `Include` graph and card-gross fallback cascade are specific to reconciliation and have no bookkeeping or daily equivalent. `ReportsController` calls `GetReconciliationReport` directly for that endpoint; at that point in the migration, the legacy `ReportingService.GetReconciliationAsync` delegated to the same use case so CSV/XLSX export stayed on one authoritative implementation, until issue #92 removed `ReportingService` entirely (see below). The machine and product profitability reports (GET `api/reports/machine-profitability` and GET `api/reports/product-profitability`) are the fifth and sixth features moved into this shape, following the same pattern: `Inventory.Domain.Reporting.Profitability.ProfitabilityRowPolicy` computes the per-machine/per-product cost/gross-profit/margin gate shared by both reports, and `Inventory.Domain.Reporting.Profitability.MachineDirectProfitPolicy` computes machine profitability's direct-profit completeness rule (COGS complete, no missing Nayax fee rates, complete commission coverage), reusing the shared `ReportingCalculations`. `Inventory.Application.Reporting.MachineProfitability.GetMachineProfitabilityReport` and `Inventory.Application.Reporting.ProductProfitability.GetProductProfitabilityReport` are the use cases; `IMachineProfitabilityReportFactsProvider`/`IProductProfitabilityReportFactsProvider` are their narrow ports; `InventoryApi.Adapters.Persistence.EfMachineProfitabilityReportFactsProvider`/`EfProductProfitabilityReportFactsProvider` are their temporary API-owned EF adapters, reusing `EfReportingSharedQueries`' completed-sale query. The machine profitability adapter also composes the still-legacy `INayaxProcessingFeeService`/`ISiteCommissionService`; its site-commission resolution is shared with `EfBookkeepingReportFactsProvider` through `EfReportingSharedQueries.GetMachineCommissionsAsync`/`GetSiteCommissionAsync` rather than duplicated a third time, while its per-machine operating-expense breakdown has no equivalent in the already-migrated adapters and stayed local. Nayax product matching (`NayaxProductMatcher`, previously `InventoryApi.Services.NayaxProductMatcher` only) is deterministic Domain business logic and moved to `Inventory.Domain.Reporting.ProductMatching.ProductMatcher`, operating on a Domain-owned `ProductMatchCandidate(Id, Name)` rather than the persistence `Product` entity; the product profitability use case calls it directly on its own catalogue projection, and the EF adapter never calls it (matching stays out of the persistence adapter). `InventoryApi.Services.NayaxProductMatcher` (used by machine service, sale costing, inventory cost rebuild, import, and site commissions — outside this migration's scope) became a thin wrapper delegating to the same Domain implementation, so both stay on one authoritative matching algorithm instead of two. `ReportsController` calls `GetMachineProfitabilityReport`/`GetProductProfitabilityReport` directly for those endpoints; at that point in the migration, the legacy `ReportingService.GetMachineProfitabilityAsync`/`GetProductProfitabilityAsync` delegated to the same use cases so CSV/XLSX export and the dashboard report (which reuses product profitability's result) stayed on one authoritative implementation, until issue #92 removed `ReportingService` entirely (see below). GST, dashboard, and transactions have since moved too (see the reporting migration track below); every individual report family has migrated, and issue #92 completed the final shared-query audit: it found no further duplication to consolidate (every already-migrated adapter already shared what could be shared through `EfReportingSharedQueries`) and removed the legacy `InventoryApi.Services.ReportingService`/`IReportingService`. `Inventory.Application.Reporting.Export.GetReportExportRows` is now the one authoritative export-row-building step for every report, called directly by `ReportsController`'s single export endpoint; `InventoryApi.Adapters.Export.ReportExportFileWriter` is its only remaining CSV/XLSX byte-encoding adapter (ClosedXML stays out of Application). Every other feature implementation still lives in `InventoryApi`: controllers generally call service interfaces, while services use `AppDbContext` and, where required, Nayax or filesystem facilities. `Program.cs` is the composition root. It does not apply EF Core migrations as a matter of course: startup delegates to `DatabaseSchemaStartup`, where Development and `Testing` auto-migrate a throwaway database (as may another non-Production environment under an explicit override), Production never auto-migrates whatever the configuration says, and a Production database with pending migrations fails startup rather than serving requests against a schema its code does not match. Those migrations are applied explicitly by a human with the `migrate-database` command (dry run first).

```mermaid
flowchart TD
    Router["Angular router"] --> UI["Feature pages"]
    Config["Runtime config"] --> Clients["Angular API services"]
    UI --> Clients
    Clients --> API["ASP.NET controllers"]
    API --> Services["Application services"]
    Services --> DB["EF Core / SQLite"]
    Services --> Nayax["Nayax Lynx + imports"]
    Services --> Files["Receipt and expense documents"]
```

## Current strengths

- Most feature controllers already depend on service interfaces.
- The backend has meaningful tests for products, stock, purchases, supplier orders, costing, imports, fees, commissions, machine profitability, reporting, and migrations.
- Nayax transaction status and payment-method classification are centralized.
- Historical sale cost and its provenance are persisted.
- Reconciliation and incomplete-data states are represented explicitly.
- The frontend has a centralized runtime API configuration, typed services, reusable report-page behavior, and shared toast/confirmation UI.
- Standalone Angular components keep feature code independent of NgModule structure.
- Backend CI restores, builds, and tests before a `main` deployment.

## Current pressure points

- HTTP, use cases, domain calculations, EF Core, Nayax, file storage, and export generation live in one project for every feature area still pending migration (purchases, machine services, inventory-cost transition, and the remaining direct-`AppDbContext` controllers/services). Reporting is no longer part of this pressure point: its use cases live in `Inventory.Application.Reporting.<Feature>` and its calculations in `Inventory.Domain.Reporting.<Feature>`; only its temporary EF/Nayax adapters, HTTP controller, and CSV/XLSX byte encoding remain in `InventoryApi`. `Purchases.PurchaseTotalValidationPolicy`/`ComputePurchaseTotalValidation` are the first pieces of the purchase slice to move out (see the [Purchase rename plan](#purchase-rename-plan)); the purchase upload/update/delete orchestration itself is still in `InventoryApi`.
- `PurchaseService`, the machine services, and the inventory-cost-transition services each combine orchestration and persistence, and are large.
- Operating-expense and site-commission controllers directly access `AppDbContext`; operating expenses also manipulate files. Fee-setting no longer does (see the Nayax fee-settings slice above), except through its temporary API-owned persistence adapter.
- `Product` contains persistence state, business calculations, and transient Nayax/UI fields.
- Several tests use EF Core InMemory where SQLite behavior may be more representative.
- Frontend contracts are split between a broad `models.ts` file and service-local report interfaces. `reporting.service.ts` is already a large multi-report API client.
- Some page components, especially administration and reporting pages, contain substantial orchestration and presentation logic.
- Report state is locally managed, but date-range logic and financial formatting can accidentally erase `null`/unknown meaning if reused without care.
- The frontend package has no automated test or lint command; its current validation gate is a production build.
- Branch protection and required-check configuration live in GitHub repository settings and must be enabled separately from source-controlled workflows.

These are reasons to improve boundaries, not reasons for a wholesale rewrite.

## Backend target: pragmatic Clean Architecture with vertical slices

The application remains a single deployable modular monolith. The intended projects are:

```text
backend/
├── InventoryApi/                    HTTP host and composition root
├── Inventory.Application/          Feature use cases and external ports
├── Inventory.Domain/               Business rules and domain types
├── Inventory.Infrastructure/       EF, Nayax, files, imports, exports
├── Inventory.UnitTests/            Pure domain/application tests
└── Inventory.IntegrationTests/     Database, API and adapter tests
```

```mermaid
flowchart TD
    Api["InventoryApi"] --> Application["Inventory.Application"]
    Api --> Infrastructure["Inventory.Infrastructure"]
    Infrastructure --> Application
    Infrastructure --> Domain["Inventory.Domain"]
    Application --> Domain
```

### Inventory.Domain

Contains stable business language and deterministic rules:

- Products, reorder policy and inventory movements.
- Costing quantity/value and weighted-average calculations.
- Sale-cost status/source and costing results.
- Commission agreement semantics and calculations.
- Financial-year/date-range value types where appropriate.
- Pure reporting calculations such as GST extraction, gross profit, and margins.

It must not reference ASP.NET Core, EF Core, HTTP, filesystem APIs, ClosedXML, configuration, or concrete Nayax clients.

### Inventory.Application

Contains use cases grouped by feature, request/response contracts, validation, orchestration, and narrow ports:

```text
Inventory.Application/
├── Products/
├── Stock/
├── Purchases/
├── SupplierOrders/
├── Expenses/
├── Commissions/
├── Imports/
└── Reporting/
```

Examples include `CreateOperatingExpense`, `RecordCommissionPayment`, `ImportNayaxSales`, `GetBookkeepingReport`, `RebuildHistoricalCosts`, and `ComputePurchaseTotalValidation`.

Application code determines what must happen. It does not know the physical database, file path, HTTP endpoint, or spreadsheet library used to make it happen.

### Inventory.Infrastructure

Contains adapters and technical implementation:

- `AppDbContext`, entity configurations, migrations, and repositories/read stores.
- Nayax Lynx HTTP client and imported-file parsers.
- Document storage for purchase documents and operating-expense attachments (`Inventory.Infrastructure.Documents.FileSystemDocumentStorage` and `AzureBlobDocumentStorage`, behind the Application's `Documents.IDocumentStorage` port; see [Document storage](#document-storage)).
- CSV/XLSX report exporters.
- Clock/timezone adapter (`Inventory.Infrastructure.Clock.SystemClock`, `Inventory.Infrastructure.Time.SydneyBusinessCalendar`; see [Time](#time)).

Use narrow feature-specific ports. A generic repository that leaks persistence semantics into every feature is not a goal.

### InventoryApi

Contains:

- Controllers and HTTP-specific models.
- Authentication/authorization and middleware.
- OpenAPI configuration.
- Dependency injection and application startup.
- HTTP error/result mapping.

Controllers do not implement accounting, inventory, persistence, or filesystem rules.

#### Temporary API-owned exception and its enforcement (issue #145)

`InventoryApi/Services` (import, costing, commissions, machine/site/product/purchase/stock
orchestration) is use-case/domain logic that predates the `Inventory.Domain`/`Inventory.Application`
split and has not migrated yet. `InventoryApi/Adapters/{Persistence,Export,Nayax}` hold the
temporary, API-owned adapters (`EfNayaxFeeRateStore`, the `Ef<Feature>ReportFactsProvider` family,
`ReportExportFileWriter`, `NayaxCatalogSnapshotProvider`, ...) that implement `Inventory.Application`
ports until `AppDbContext` and its persistence models move into `Inventory.Infrastructure` - see the
per-slice detail under [Backend migration track](#backend-migration-track). Both are deliberate,
temporary exceptions to "controllers are thin and InventoryApi holds no use-case/domain logic", not
places for new business logic to land. Removing them entirely is tracked by issues #153/#154, after
the feature-by-feature migrations in #146-#151; this issue does not migrate any of them.

What issue #145 adds is enforcement that the `InventoryApi/Services` side of the exception stops
growing silently. `ProjectDependencyDirectionTests.Only_the_documented_legacy_services_remain_in_InventoryApi_Services`
(`backend/InventoryApi.Tests/Architecture/`) freezes the exact, named set of git-tracked files this
migration found already in that folder; the moment a file is added, removed, or renamed there, the
test fails and names the mismatch. A new slice's use-case or domain logic must go into
`Inventory.Application`/`Inventory.Domain` instead of extending the legacy folder; growing the
exception is still possible, but only as a conscious, reviewed edit to both that allow-list and this
paragraph, never as a silent side effect of an unrelated change. `InventoryApi/Adapters/*` is not
frozen the same way: unlike `Services`, adding a new temporary EF/Nayax/export adapter there for a
migrating slice (mirroring `EfNayaxFeeRateStore`) is the established, expected pattern for this
migration track, not scope creep - it implements an `Inventory.Application`-owned port rather than
containing use-case logic itself.

`CleanArchitectureDependencyTests` (same directory) is the complementary, compiled-assembly side of
the boundary: `Domain_must_not_depend_on_Application_Infrastructure_or_Api`,
`Application_must_not_depend_on_Infrastructure_or_Api`, and `Infrastructure_must_not_depend_on_Api`
cover the `InventoryApi` reference direction; `Infrastructure_must_not_depend_on_ASP_NET_HTTP_types`
(added by issue #145) keeps the ASP.NET Core web-host surface (`HttpContext`, middleware, MVC types)
out of `Inventory.Infrastructure` now that it is a real target for adapters, alongside the existing
rules keeping ASP.NET Core, EF Core, `HttpClient`, and ClosedXML types out of `Inventory.Domain` and
`Inventory.Application`. `Inventory.Infrastructure` may still depend on EF Core and outbound HTTP
clients (`System.Net.Http`) - those are exactly what an adapter is for - it is only the ASP.NET Core
web-request-pipeline surface that must stay confined to `InventoryApi`.

### Authentication and authorization

Authentication/authorization is an `InventoryApi`/frontend boundary concern (issue #38). Identity-provider types stay confined to that boundary:

- **Backend.** `Program.cs` registers `AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))` and calls `UseAuthentication()` before `UseAuthorization()`. Every controller carries `[Authorize]` plus `[RequiredScope("access_as_user")]` (`Microsoft.Identity.Web.Resource`), so a request without a bearer token is rejected `401 Unauthorized` and a request whose token lacks the delegated `access_as_user` scope is rejected `403 Forbidden`, both by ASP.NET Core's authentication/authorization middleware before any controller action runs. The non-secret `AzureAd` configuration (`Instance`, `TenantId`, `ClientId`, `Scopes`) lives in `appsettings.json`; the `ClientId` is the API app registration's public application ID, used only to validate the token audience, never a client secret. `Microsoft.Identity.Web`/`Microsoft.AspNetCore.Authorization`/JWT types are used only in `InventoryApi` (`Program.cs` and controllers) and must never appear in `Inventory.Domain` or `Inventory.Application`; if a use case ever needs the caller's identity, define a narrow neutral Application port instead of exposing Microsoft identity-provider types across that boundary.
- **Frontend.** The Angular SPA authenticates through MSAL (`@azure/msal-angular`, `@azure/msal-browser`). `frontend/inventory-app/src/app/auth-config.ts` defines the SPA/API Entra application IDs, the delegated `access_as_user` scope (`loginRequest`), and `buildProtectedResourceMap(apiBaseUrl)`, which keys MSAL's protected-resource map off `ConfigService.apiBaseUrl` rather than a hard-coded host. `app.config.ts` wires `MsalInterceptor` (attaches `Authorization: Bearer <token>` to matching requests), `MsalGuard` (redirect-based route protection), and `MSAL_INTERCEPTOR_CONFIG` (built from that dynamic map), so the bearer token is attached correctly whether `ConfigService.apiBaseUrl` resolves to the local dev proxy (`/api`) or the deployed Azure API's absolute URL — see [Runtime configuration and API contracts](#runtime-configuration-and-api-contracts). `app.routes.ts` applies `MsalGuard` to every application route except the public `/auth` callback route (`AuthCallbackComponent`), which must stay reachable without authentication so the Entra redirect can complete. `AppComponent` drives sign-in/sign-out (`MsalService.loginRedirect`/`logoutRedirect`) and reflects the active account in the header.
- **Protected documents.** Static-file middleware does not run controller authorization, so an uploaded document under `wwwroot` would be downloadable by anyone who knew its generated file name no matter what `[Authorize]` says. `Program.cs` therefore registers no static-file middleware at all — the API serves no public assets, since the Angular application is a separate Azure Static Web App — and `Inventory.Infrastructure.Documents.FileSystemDocumentStorage` stores purchase documents and operating-expense supporting documents under `{ContentRoot}/protected-files/{category}/`, outside the web root. The only way to read one is `GET /api/purchases/{id}/file` or `GET /api/operating-expenses/{id}/attachment` — the sole, canonical OperatingExpense attachment route; the legacy `GET /api/operating-expenses/{id}/receipt` alias was removed (issue #61) once verification confirmed no in-repository or external caller used it, with no deprecation period. Documents uploaded before this rule still sit in `wwwroot/{category}` and stay readable and deletable through the same endpoints (the adapter falls back to that location) but no longer have an anonymous URL. Because these endpoints require a bearer token, the frontend must fetch them through `HttpClient` (`PurchaseService.getFile`, `OperatingExpenseService.getAttachment`, both `responseType: 'blob'`) and render them from an object URL; an `<a href>`/`<img src>` pointing straight at the endpoint is a plain browser request that carries no token and gets `401`.
- **Authentication is not ownership.** Accepting users from multiple Microsoft Entra tenants (`TenantId: "common"`) establishes *who* the caller is. *What they may see* is decided separately by business ownership, described below.

### Tenant ownership (issue #64)

Authentication answers "who is this?". Tenant ownership answers "whose data is this?", and the two are deliberately not the same question. An Entra directory tenant is not a vending business: the mapping between them is application-owned data, so the business a caller belongs to is a decision this application makes and can audit, not one inherited from the token.

**Identity and membership.** The actor is identified by the validated `(tid, oid)` claim pair — never by email, display name, or the Entra directory tenant alone, all of which are mutable or shared. `BusinessMembership` rows map an actor to one application-owned `Business`. `BusinessMembershipResolutionPolicy` (Domain) decides the outcome: exactly one active membership on an active business resolves; none, or several, denies. Ambiguity is denied rather than resolved by picking one, because silently choosing a business is how data ends up in the wrong ledger.

**Layering.** Claims parsing stays at the API boundary (`EntraActorIdentityAccessor`, the only implementation of the Application's `IAuthenticatedActorAccessor` port). `ICurrentBusinessProvider` is the Application-facing abstraction; `Inventory.Domain` and `Inventory.Application` never reference ASP.NET claims or principals. EF query filters and `SaveChanges` enforcement are persistence concerns and live with the `AppDbContext` adapter.

**Per-request resolution.** `BusinessScopeMiddleware` runs after authentication and before the endpoint, resolves membership once, and publishes the answer into the request-scoped `BusinessScope`. An authenticated caller with no usable membership gets `403` and never reaches an action; the response carries no detail about why, because telling an unrecognised caller whether a business exists is information they have not earned. `IBusinessScope` exists as a separate synchronous port because query filters and `SaveChanges` cannot await a membership lookup.

**Fail closed.** `BusinessScope` starts denied and can only move forward to a resolved business, once. A denied scope yields a `null` business ID, and the filters compare with `==`, so an unresolved caller matches no row. "No current business" must never be read as "no filter" — that would convert a resolution bug into a cross-business leak. A scope cannot be repointed mid-request.

**Tenant-owned versus global.** Everything persisted is tenant-owned — products, categories, suppliers, purchases and items, stock adjustments, supplier orders and allocations, sales, imports and their children, expenses, commissions, fee rates, costing transition records and report facts — except three structural exceptions: `Business` (the boundary itself), `BusinessMembership` (what resolves the boundary, so filtering it would be circular), and `BusinessBackfillAudit` (operational evidence about the rollout, which must stay readable precisely when a run assigned rows to the wrong business). There is no "global reference data": the enum-like constants live in code as C# enums, not tables. `BusinessOwnershipCoverageTests` enforces this — a new entity with no ownership fails the build rather than quietly arriving unfiltered.

**Central read enforcement.** `AppDbContext.ConfigureBusinessOwnership` walks the model rather than naming entities: every `IBusinessOwned` type gets a required business key, an index leading with it, and a global query filter. A new tenant-owned entity is protected the day it implements the interface. There is no per-entity list to forget and no controller `Where` clause that could be omitted on one endpoint — which is why adding redundant filters in controllers or services is discouraged rather than merely unnecessary.

**Central write enforcement.** Query filters protect reads only. `BusinessOwnershipEnforcer` runs on both `SaveChanges` overloads and enforces four rules: new rows are **stamped** with the caller's business (callers never supply it); inserts, updates, and deletes of another business's row are **rejected**, which is what stops a detached entity attached by ID from slipping through to an `UPDATE`; the business key is **immutable**, so a record cannot be moved between businesses; and every foreign key between tenant-owned entities must **stay inside one business**. The relationship rule is driven by EF model metadata, not a hand-written list, so a new foreign key is covered as soon as it is mapped. Resolving a principal's owner deliberately uses `IgnoreQueryFilters`, because a principal in another business must be reported as a boundary violation rather than mistaken for "does not exist".

**Tenant-scoped uniqueness.** Constraints over externally supplied values are scoped by business, because two businesses may legitimately hold the same external value: Nayax transaction IDs, import file hashes, site commission agreements, fee effective dates, and costing baselines. `NayaxSales` consequently has its own local key with `TransactionID` unique *per business* — a remote identifier is an external identity, not a primary key. This also keeps import de-duplication correct: one business must never be told its own import is a duplicate because another imported the same bytes first, and a shared remote ID must never cause one business's import to update another's row.

**Protected documents.** A document's bytes live outside the database, so hiding the row is not enough. Retrieval always resolves the tenant-owned parent record first — `GET /api/purchases/{id}/file` and `GET /api/operating-expenses/{id}/attachment` both go through the filtered `DbSet` — and the stored file name is read from that record, never from the request. No endpoint accepts a file name or path as input, and `FileSystemDocumentStorage` reduces any stored name with `Path.GetFileName` and then proves the result is inside the category folder, so a crafted value cannot escape it. Knowing another business's purchase ID, attachment ID, stored file name, and on-disk path therefore yields nothing.

**The unrestricted-context rule.** `new AppDbContext(options)` is fail-closed. Unrestricted, all-business access requires passing `UnscopedBusinessScope.Instance` explicitly, so every such place is greppable. Outside tests it exists only in the three human-invoked commands — `migrate-database`, `bootstrap-business` and `migrate-documents`, the last of which reads every business's document metadata to migrate it (see [Document storage](#document-storage)). No controller, service, or request path may run unrestricted; a composition-root test pins down that the DI container never produces an unscoped context.

**Schema and data are separate, human-controlled steps.** `DatabaseSchemaStartup` decides per environment: Production never migrates automatically regardless of configuration and fails closed when migrations are pending; Development and `Testing` migrate automatically; any other non-Production environment does so only under the `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` override, which is read only after Production has been ruled out. Migrations never assign ownership. The backfill is exclusively `bootstrap-business`: deterministic, idempotent (it touches only unassigned rows), restartable, transactional, dry-runnable, and verified by before/after counts and financial totals, with a `BusinessBackfillAudit` record of what it did. `TenantOwnershipReadiness` reports at startup whether ownership has actually been bootstrapped, so "all my data is gone" cannot be the first symptom of an unfinished rollout.

**Known limits of this rollout.** One business is live. The Nayax client still uses a single operator/token configuration, so remote identifiers and imports are not partitioned per business; a second live business must wait until they are. The database foreign keys from `BusinessId` to `Businesses` are a deliberate, still-outstanding deferral — see `docs/tenant-rollout.md`. Issue #39 (document storage) consumes this ownership key and must not introduce blob storage before it.

### Document storage

Uploaded business documents reach storage through one Application port,
`Inventory.Application.Documents.IDocumentStorage` (issue #39, checkpoint 1). It takes a
`DocumentCategory` — `PurchaseDocument` or `ExpenseAttachment` — and the server-generated stored
file name already held on the tenant-owned purchase or operating-expense record, and offers
`SaveAsync`, `OpenReadAsync` and `DeleteAsync`. It exposes no filesystem path, no container or
URL, no `IWebHostEnvironment`, and no business identifier: ownership is resolved before a call
reaches the port, by loading the parent record through the tenant-filtered `AppDbContext`, so the
#64 boundary is what decides whether a document may be touched at all. `PurchaseService` and
`OperatingExpensesController` compose `SaveAsync` and `DeleteAsync` around their own database
work to replace a document, which is what keeps cleanup-on-failure ordering visible at the call
site rather than hidden in storage.

`Inventory.Infrastructure.Documents.FileSystemDocumentStorage` writes new documents to
`{ContentRoot}/protected-files/{category}/`, reads them back from there or, failing that, from
the legacy `{WebRoot}/{category}/` location, and refuses to overwrite an existing document or to
leave a partially written one behind. Category folders keep their legacy names (`receipts`,
`expenses`) because persisted metadata refers to documents by stored file name alone.

`Inventory.Infrastructure.Documents.AzureBlobDocumentStorage` (issue #39, checkpoint 2) is the
second implementation. It keys every document by the business that owns it:

```text
tenants/{businessId}/purchases/{storedFileName}
tenants/{businessId}/expenses/{storedFileName}
```

The `businessId` is read from `IBusinessScope` — the trusted per-request scope issue #64
publishes from the authenticated actor's membership — and never from a route, query, form,
header, JSON body or file name; `IDocumentStorage` deliberately has no parameter that could
carry one. A denied scope and the deliberate `Unscoped` opt-out both refuse: a document belongs
to exactly one business, so there is no prefix that could stand in for "all of them", and
cross-business maintenance work needs its own explicit path rather than a mode of the request
path. `BlobDocumentPath` reduces an untrusted stored name to its last path segment before it can
reach the service, because the Blob service canonicalises nothing and a `..` in a name would
otherwise be a real blob under a different prefix. Uploads use `overwrite: false`, so the
service itself refuses to replace an existing document; a blob is readable only once its upload
is committed, so a failed upload leaves nothing behind and the adapter deliberately does not
delete on failure. The container is private and no SAS or public URL is generated.

The SDK sits behind a small seam, `IDocumentBlobContainer`, implemented by `AzureBlobContainer`.
That is what lets the adapter's rules — tenant-scoped names, no overwrite, missing blob means
`null` — be tested without Azure credentials or a network.

**Migrating documents to Blob storage.** `migrate-documents`
(`InventoryApi/Bootstrap/DocumentMigrationCommand`) copies the documents already on disk into the
Blob container, as a human-invoked command run with `--dry-run` to inspect and `--apply` to copy.
Exactly one of the two must be given: unlike the other commands, a bare invocation is refused
rather than treated as a dry run. The web host never performs it.

Ownership comes from the database and nowhere else: each document's business is read from the
persisted purchase or operating-expense record that owns it, never from a folder name, a file
name, a blob name or the command line. A record whose `BusinessId` is unusable is reported and
left alone rather than filed under a default. The command therefore reads through
`UnscopedBusinessScope.Instance` — it must see every business at once — but it is emphatically
not a request: it uses `IDocumentMigrationDestination`, a separate Infrastructure abstraction
that takes an explicit `BusinessId`, rather than `IDocumentStorage`, which has no such parameter
and whose Azure adapter correctly refuses to run unscoped. Keys are built with the same
`BlobDocumentPath` the running application uses, so a migrated document lands where the API will
later look for it.

Sources are resolved exactly as a live download resolves them — protected storage first, then the
legacy web root — by reusing `FileSystemDocumentStorage`. Each document is fingerprinted by
streaming SHA-256 and size: an absent destination is written create-if-absent and then read back
and verified before it counts as `Migrated`; a destination already holding the same bytes is
`AlreadyPresent`, which is what makes a rerun safe; a destination holding *different* bytes is a
`Collision`, never overwritten, renamed or deleted. Missing sources, unusable business ids and
failed verifications are reported per document with the record type, id, business, stored name
and reason — and nothing about the document's contents. The command requires
`DocumentStorage:Provider=AzureBlob` and refuses to run against the filesystem default, which
would otherwise copy every document from the filesystem back to the filesystem and report
success. It exits `0` when nothing is unresolved (a dry run's pending work is not a problem) and
`1` when any document is missing, collided, unowned or failed, so an apply can be gated on a
clean dry run.

A failure reaching the destination — container missing, authorization refused, account disabled,
service unavailable — aborts the whole run rather than becoming a few documents' `Failed`. Those
failures are about the destination rather than the document being copied, and reporting them per
item would print the most misleading summary the command could produce. Recovery is to fix the
destination and run again, which is safe because the migration is idempotent. A document's
`Failed` therefore always means something narrower and specific to it: its copy could not be read
back, or did not match its source.

It never deletes or modifies a source document, and never writes to the database: the stored file
name is the link between record and document, and moving bytes is not a reason to change it.
Retiring the filesystem copies and the legacy fallback is a separate human decision, after a
verified migration. [docs/document-storage-rollout.md](document-storage-rollout.md) is the
operational runbook for the whole rollout — prerequisites, the dry-run review gate, verification,
the separate runtime switch, and the rollback.

**Configuration and provider selection.** `Program.cs` binds the non-secret `DocumentStorage`
section and passes it, with the host's content and web roots, to
`AddDocumentStorage`:

```text
DocumentStorage__Provider=AzureBlob
DocumentStorage__BlobServiceUri=https://<storage-account>.blob.core.windows.net
DocumentStorage__ContainerName=business-documents        # business-documents-dev for development
```

An absent provider means `FileSystem`, which is what every environment ran before the setting
existed. An unrecognised provider, or an `AzureBlob` provider with a missing, non-absolute or
plaintext endpoint, a missing container, or an endpoint carrying a query string or credentials
(what a SAS token or an embedded key looks like), throws while the container is being built.
There is deliberately no fallback from `AzureBlob` to the filesystem: an application that could
not reach its configured storage would otherwise look healthy while writing business documents
to a local disk nobody backs up. Authentication is `DefaultAzureCredential` throughout — the App
Service's system-assigned managed identity in Azure, the developer's own Azure sign-in locally —
so no account key, SAS token, client secret or storage connection string is ever configured.
`FileSystemDocumentStorage` stays registered as a concrete type under either provider, because
switching the provider does not move the documents already on disk.

### API documentation policy

`Swashbuckle.AspNetCore` (`AddSwaggerGen`/`UseSwagger`/`UseSwaggerUI` in `Program.cs`) is intentionally retained to give local developers an interactive view of the API surface. It is registered only behind `app.Environment.IsDevelopment()`, so it never runs, and never exposes `/swagger`, outside the Development environment; its `SwaggerDoc` metadata carries only a title, version, and description, with no authentication scheme or configuration values. `dotnet-tools.json` keeps the matching `swashbuckle.aspnetcore.cli` local tool so a developer can export `swagger.json` manually with `dotnet swagger tofile` if needed.

No generated OpenAPI client is adopted (see [Runtime configuration and API contracts](#runtime-configuration-and-api-contracts)): the frontend uses manually maintained TypeScript contracts, and no `nswag.json` or `OpenApiReference` item exists anywhere in the repository. `Microsoft.Extensions.ApiDescription.Client` and `NSwag.ApiDescription.Client` were unused package references with no consumer and have been removed; re-add them only alongside an issue that deliberately adopts a generated client.

### External integration errors

External service failures are represented by a typed integration exception rather than by a raw transport exception or a silently empty result. `NayaxLynxClient` validates every Nayax response in one place and throws `NayaxUpstreamException`, which carries only the operation name, HTTP method, relative endpoint, and upstream status code.

The HTTP boundary maps that exception centrally. `NayaxUpstreamExceptionHandler` is an `IExceptionHandler` registered with `AddProblemDetails()` and `UseExceptionHandler()`; it returns `502 Bad Gateway` as `application/problem+json` with the current trace ID, and returns `false` for every other exception so unrelated failures keep their normal pipeline behaviour. Controllers and services do not catch Nayax transport errors individually.

Tokens, authorization headers, and raw upstream response bodies must never be logged or returned. A failed call logs the operation, method, endpoint, and numeric upstream status only; the public response carries a fixed title and detail and no exception information. An upstream failure must never be disguised as an empty collection, and caller cancellation must stay cancellation rather than becoming a `502`.

### Domain and application error mapping

Issue #59 replaced ad hoc, per-controller exception handling with a small typed strategy and two more centrally registered `IExceptionHandler`s, alongside the untouched Nayax handler above.

`Inventory.Application.Exceptions` defines two framework-neutral exception types with no ASP.NET Core reference, matching the Clean Architecture rule that a Domain/Application failure must not know about HTTP: `DomainValidationException` for a deliberate business-rule/input check, and `DomainConflictException` for a request that cannot proceed because of the current state of the data (an overlapping agreement, a concurrent change). Their messages are written for the caller and must never carry an internal path, identifier, or another actor's data.

`InventoryApi/Http/DomainExceptionHandler.cs` publishes an exception's own message to the caller, so it may only claim exception types whose contract guarantees that message is caller-safe. Exactly three qualify: `DomainValidationException` and `InsufficientStockException` become `400 Bad Request`, and `DomainConflictException` becomes `409 Conflict`. `InsufficientStockException` qualifies because its message is a fixed sentence plus an available-stock count the caller is already entitled to see. Every `ProblemDetails` response carries the current trace ID and a `message` extension equal to `detail`, kept for the existing frontend error handling that already reads `error.message` for this migration (see `machine-detail.component.ts`). A validation outcome is not logged; a conflict is logged once at `Warning` with the trace ID, because it may indicate a real race between two callers, but it is still an expected outcome rather than a server error.

**The handler must never claim a framework-wide base type such as `ArgumentException` or `InvalidOperationException`.** Because `UseExceptionHandler()` is registered ahead of the rest of the request pipeline, a type-based rule here applies to the whole application, not only to the controllers that were migrated. `ArgumentException` and `InvalidOperationException` are thrown throughout the BCL, EF Core, and this application's own infrastructure — `BusinessScope.Resolve`'s tenant-scope double-resolve guard, `BusinessId.From`, `EffectiveFinancialConfiguration.ResolveAgreement`, `GetReportExportRows`, `InventoryCostDataQualityException` — with messages written for a developer, not an API client. Claiming them would turn unrelated internal failures into public `400` responses that echo an internal message and are never logged, and would silently downgrade a tenant-isolation bug from a loud `500` to a quiet `400`. They therefore fall through to `GlobalExceptionHandler` as logged, generic `500`s, and `DomainExceptionHandlerTests` locks that in for `ArgumentException`, `ArgumentNullException`, `ArgumentOutOfRangeException`, `InvalidOperationException`, and an `InvalidOperationException` subclass.

The consequence for a throw site is explicit: **a check whose message is meant for the caller must throw `DomainValidationException` or `DomainConflictException` itself.** Migrating a controller to the centralized mapping therefore means converting the deliberate validation throws in the service behind it, not widening the handler. Anything else stays an ordinary framework exception and stays a generic `500`.

`InventoryApi/Http/GlobalExceptionHandler.cs` is the catch-all last resort, registered after the other two. Anything neither handler claims is logged exactly once at `Error` with the exception and the same trace ID returned to the client, and answers with a fixed, generic `500` `ProblemDetails` that carries no exception message, type name, or stack trace. Caller cancellation is excluded from that logging and left to ASP.NET Core's normal handling, mirroring the same principle already documented above for Nayax upstream cancellation.

Not-found handling is unchanged by this work: controllers continue to return `NotFound()` directly for a missing resource, and this issue introduces no typed not-found exception.

Migrating a controller to the centralized mapping never changes its status code or message; the only observable difference is that the response body for a migrated action becomes a `ProblemDetails` object (`application/problem+json`) instead of a bare JSON string, which is why the `message` extension above exists. `StockController.Adjust`, `InventoryCostTransitionsController` (all four actions), and `SupplierOrdersController.Create` were migrated this way, and the deliberate validation throws in `StockService.Adjust`, `InventoryCostTransitionService`, and `SupplierOrderService.Create` were converted from `ArgumentException`/`InvalidOperationException` to `DomainValidationException` so those actions keep the exact `400` and message they returned before. `SiteCommissionsController.SaveAgreement`'s overlapping-agreement check moved from returning `Conflict(...)` directly to throwing `DomainConflictException`, still producing `409` with the same message. `ProductsController.Update`, `PurchasesController`, `ImportsController.ImportNayaxSales`, and `OperatingExpensesController` still catch their own exceptions and were intentionally left for a later, separate change; `OperatingExpensesController` additionally deletes an uploaded file from its catch block, so migrating it also needs a way to run that cleanup from outside the controller.

## Frontend architecture

The frontend is an application boundary in its own right. It owns navigation, interaction state, accessibility, presentation, and communication with the API. It does not own inventory or accounting truth.

### Current composition

| Area | Current responsibility |
| --- | --- |
| `app.component.*` | Application shell, primary navigation, report menu, router outlet, and toast host |
| `app.routes.ts` | Product, stock, supplier, machine, site, purchase, report, expense, and administration routes |
| `components/` | Routed feature pages plus a small set of shared components |
| `services/` | Typed HTTP calls, runtime configuration, toast state, and feature-specific client behavior |
| `models/models.ts` | Shared inventory, purchase, site, and machine contracts |
| Report base/classes | Common report filters, loading/error state, financial-year presets, and export behavior |
| `assets/config.json`, `api-base-url.ts` | Deployed API base URL and the local-vs-deployed resolution rule applied before the application starts |
| `auth-config.ts`, `auth/auth-callback.component.ts` | MSAL configuration, delegated `access_as_user` scope, protected-resource map, and the public Entra redirect callback route |

The application currently uses component-local state and RxJS-backed singleton services. That is appropriate for its present size. Do not introduce a global state library merely to reorganize files. Add one only when there is demonstrated cross-feature state, cache invalidation, or event-coordination complexity that local state and focused services cannot handle clearly.

```mermaid
flowchart TD
    Route["Route"] --> Page["Feature page"]
    Page --> View["Feature/shared UI"]
    Page --> Client["Typed API client"]
    Config["ConfigService"] --> Client
    Client --> API["Backend API"]
```

### Target feature boundaries

Keep Angular standalone and migrate incrementally toward feature-local code:

```text
src/app/
├── core/
│   ├── config/                    Runtime configuration
│   └── http/                      Cross-cutting HTTP concerns only
├── layout/                            Application shell and navigation
├── shared/
│   ├── ui/                        Reusable presentation components
│   └── formatting/                Presentation-only helpers
├── features/
│   ├── products/
│   │   ├── pages/
│   │   ├── components/
│   │   ├── data-access/
│   │   └── models/
│   ├── stock/
│   ├── purchases/
│   ├── machines/
│   ├── sites/
│   ├── expenses/
│   ├── admin/
│   └── reports/
│       ├── shared/                Filters and report-page behavior
│       ├── bookkeeping/
│       ├── reconciliation/
│       └── profitability/
├── app.config.ts
└── app.routes.ts
```

This is a direction, not a required big-bang move. Move a feature when it is being changed, keep each move behavior-preserving, and avoid empty abstraction folders.

Use these ownership rules:

- **Pages** read route/query parameters, coordinate loading and mutation state, and compose the view.
- **Presentational components** receive typed inputs and emit user intent. They do not fetch unrelated application data.
- **Feature data-access services** own HTTP calls and transport mapping for one feature. They do not calculate financial results already supplied by the API.
- **Feature models** describe API contracts and view-specific types for that feature. Promote a type to `shared` only when multiple features genuinely use the same meaning.
- **Core services** are limited to application-wide infrastructure such as configuration and HTTP concerns. `core` is not a home for miscellaneous business logic.
- **The backend** remains authoritative for stock transitions, historical COGS, fees, commissions, reconciliation, and report calculations.

### Routing and loading

Routes are declared centrally in `app.routes.ts`. Every top-level route loads its component with `loadComponent` (issue #65), except the public `/auth` Entra redirect callback, which stays eagerly imported because it is the landing route for an in-progress authentication redirect, not a migrated feature area. This keeps initial bundles smaller and creates an enforceable feature boundary without introducing NgModules. Preserve route URLs, guards, and parameters when adding or changing a route.

The static host must rewrite unknown application paths to `index.html`; otherwise refreshing a deep link such as `/reports/bookkeeping` or the Entra redirect landing on `/auth` will bypass Angular and return a host-level 404. `frontend/inventory-app/src/staticwebapp.config.json` (copied to the deployed output root by the `assets` build option) declares that Azure Static Web Apps `navigationFallback`, rewriting unmatched paths to `/index.html` while excluding `/assets/*` and static file extensions.

### Runtime configuration and API contracts

`ConfigService` resolves the API base URL through an application initializer, before feature services issue requests, and is the single source of truth for it. On `localhost`/`127.0.0.1` it resolves to `/api`, which `proxy.conf.json` forwards to `http://localhost:5000/`; on any other origin it uses `apiBaseUrl` from `/assets/config.json`, falling back to `/api` if that load fails or yields nothing usable (`api-base-url.ts` holds that framework-free resolution rule). No one therefore edits the tracked `assets/config.json` to move between local development and deployment. Do not hard-code API hosts in components or feature services.

`ConfigService` fetches `/assets/config.json` with a dedicated `HttpClient` built on the raw `HttpBackend`, bypassing the interceptor chain. This is an ordering requirement, not a preference: `MSAL_INTERCEPTOR_CONFIG` is built from `ConfigService.apiBaseUrl` when `MsalInterceptor` is first constructed, which an intercepted configuration request would trigger before the initializer had finished, permanently freezing the protected-resource map on the fallback value. MSAL's protected-resource map is built from that same resolved value (`auth-config.ts`'s `buildProtectedResourceMap`) rather than a fixed host, so the bearer token attaches correctly in both local and deployed environments.

Backend contract changes are full-stack changes. When an endpoint changes:

1. update the backend request/response DTO and its tests;
2. update the matching frontend type and feature client;
3. preserve optionality and `null` explicitly rather than coercing missing financial data to zero;
4. update the page, exports, and error/empty states that consume the contract;
5. verify both the API tests and Angular production build.

Until a generated OpenAPI client is deliberately adopted, keep manually maintained TypeScript contracts close to their feature client. Do not introduce a generated client incidentally in an unrelated feature.

### Financial presentation rules

The UI may format and explain backend results, but it must not recreate authoritative accounting formulas. In particular:

- Display sales, fees, commission, COGS, and profit values returned by the API.
- Preserve quality/status fields so partial, estimated, unmatched, or uncosted results remain visible.
- Use an unavailable/unknown presentation for nullable COGS or profit. A generic formatter that turns `null` into `$0.00` is unsafe for these fields.
- Keep card and cash amounts visibly distinct where settlement is discussed.
- Keep ex-GST, GST, and GST-inclusive fee amounts distinct.
- Send explicit inclusive date filters. Business-period interpretation remains based on `Australia/Sydney`, not the browser's accidental timezone.
- Keep on-screen reports and downloaded exports aligned to the same backend calculation path.

### Interaction and presentation

Every routed page should provide intentional loading, empty, error, and success states. Use shared toast notifications for transient mutation outcomes and inline errors when a report or form cannot be understood without the message. Confirm destructive actions and keep server validation details available to the user without exposing stack traces.

New UI must remain keyboard-operable, associate labels with controls, expose meaningful button/link names, and not rely on color alone for reconciliation or quality status.

Tailwind classes in templates, `src/styles.scss`, and component styles are the styling sources. `npm run build:styles` generates `src/styles.css` before Angular builds, so do not make a manual fix only in the generated CSS. Keep production bundle and component-style budgets in `angular.json` passing.

## Domain model and financial boundaries

### Sales and settlement

The application models distinct flows:

```mermaid
flowchart TD
    Sale["Completed vending sale"] --> Revenue["Card or cash revenue"]
    Revenue --> Cogs["Persisted historical COGS"]
    Revenue --> Fees["Nayax fees for card sales"]
    Revenue --> Commission["Effective site commission"]
    Fees --> Settlement["Expected Nayax reimbursement"]
    Commission --> Profit["Direct / net profit"]
    Cogs --> Profit
```

Gross sales and Nayax reimbursement are not interchangeable. Cash is business revenue but is outside the Nayax cashless settlement. Reimbursement reconciliation uses the covered transaction period rather than payout date. The accepted monetary tolerance is `$0.01`.

Nayax fee data is effective-dated and has two sources:

1. imported reimbursement fees, authoritative for covered dates;
2. configured fee-rate estimates for uncovered completed card transactions.

The sources must not overlap for the same day. Ex-GST, GST, and GST-inclusive amounts remain separate.

Site commissions use effective-dated agreements and one of three bases: gross sales, card sales, or sales excluding GST. Missing coverage and overlaps remain visible quality/configuration failures. The absence of any agreement for a site is a valid zero-commission state.

### Historical inventory cost

Physical storage stock and costing inventory answer different questions:

- `QuantityInStock`: stock physically held in storage/home and available to refill machines.
- `CostingQuantity`: total business-owned quantity still carrying inventory value.
- `InventoryValue`: remaining value used with `CostingQuantity` to derive AVCO.

A machine refill is an internal location transfer: it changes physical storage but does not consume business inventory value or create COGS. Completed sales and explicit cost-bearing write-offs consume costing inventory.

Historical sale cost precedence is:

1. persisted internal AVCO/ledger cost when reliable;
2. transaction-level Nayax Product Cost Price captured on that sale;
3. uncosted/unknown.

Current product cost and selling price are never substitutes for historical cost. Reports preserve partial COGS and quality counts and do not turn missing cost into zero.

### Profit levels

- Gross profit requires complete COGS and equals sales minus COGS.
- Direct profit also subtracts Nayax fees including GST, site commission, and directly attributable operating expenses.
- Whole-business net profit also includes shared costs such as receipt delivery/package costs and unallocated operating expenses.
- A machine-filtered view must not claim whole-business net profit when shared overhead has no allocation rule.

All report, dashboard, transaction-detail, CSV, and XLSX paths must call the same authoritative calculations.

### Time

- Australian financial year: 1 July to 30 June.
- Report date ranges are inclusive.
- Sale/authorization, reimbursement coverage, import, and payout dates are separate concepts.
- Business reporting timezone: `Australia/Sydney`.

Timezone migration is not part of an incidental feature. Changes require explicit boundary and daylight-saving tests.

Time acquisition and timezone conversion are external boundaries, not pure calculations, so their port lives in `Inventory.Application` and their implementation lives in `Inventory.Infrastructure` (issue #44): `Inventory.Application.Time.IClock` (promoted from the NayaxFeeSettings-scoped port the first Clean Architecture slice introduced) is the narrow port for the current UTC instant, implemented by `Inventory.Infrastructure.Clock.SystemClock`. `Inventory.Application.Time.IBusinessCalendar` converts a UTC instant to its `Australia/Sydney` business calendar date (`ToBusinessDate`) and resolves the UTC instant of the start of a Sydney business day (`StartOfBusinessDayUtc`), so a caller can derive inclusive-date-range UTC boundaries without ever touching `TimeZoneInfo` itself; `Inventory.Infrastructure.Time.SydneyBusinessCalendar` implements it using `TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")`, which resolves the platform's IANA timezone database and therefore already accounts for daylight-saving transitions. `Inventory.Domain` still owns only the deterministic, timezone-free date-range/financial-year rules (`AustralianFinancialYear`, `ReportingRangeResolver`) and must not reference `TimeZoneInfo`, server-local time, or an infrastructure clock implementation. `SiteCommissionService`'s commission-due "Overdue" determination is the first use of `IBusinessCalendar` outside the clock's original NayaxFeeSettings feature, replacing a server-local `DateTime.Today` comparison with the injected Sydney business date. Storage keeps true UTC instants (`MachineAuthorizationTime`, `CreatedAt`/`UpdatedAt`, and similar timestamp columns); `IBusinessCalendar` is what turns a stored instant into the Sydney calendar date a report or a due-date comparison actually means, and no historical timestamp is reinterpreted or rewritten by this abstraction. Several other server-local `DateTime.Today`/`DateTime.Now` reads remain outside this slice's bounded scope — for example the machine/site dashboard week-to-date and month-to-date aggregates in `InventoryApi.Services.MachineService`/`SiteService` — and are a known follow-up rather than part of this change.

## Data flow

### Product purchase and restock

1. A purchase and its purchase items record the source purchase.
2. Purchase-linked restock movements add physical and costing inventory at purchase cost.
3. Delivery/package amounts remain identifiable for whole-business reporting.
4. Supplier-order allocations are reconciled without fabricating purchase quantities.

#### Purchase rename plan

The canonical internal business term is **Purchase**/**PurchaseItem**, not Receipt/ReceiptItem
(issue #60). The rename touched entity/service/component/DTO naming and the corresponding source file
names; purchase accounting/inventory behaviour and the database schema are unchanged (verified with
`dotnet ef migrations has-pending-model-changes`, which reports no pending changes).

The source files were renamed with `git mv` alongside their identifiers, so file names and type names
now agree:

| Renamed from | Renamed to |
| --- | --- |
| `backend/InventoryApi/Models/Receipt.cs` | `backend/InventoryApi/Models/Purchase.cs` |
| `backend/InventoryApi/Models/ReceiptItem.cs` | `backend/InventoryApi/Models/PurchaseItem.cs` |
| `backend/InventoryApi/Services/Interfaces/IReceiptService.cs` | `backend/InventoryApi/Services/Interfaces/IPurchaseService.cs` |
| `backend/InventoryApi/Services/ReceiptService.cs` | `backend/InventoryApi/Services/PurchaseService.cs` |
| `backend/InventoryApi/Controllers/ReceiptsController.cs` | `backend/InventoryApi/Controllers/PurchasesController.cs` |
| `backend/InventoryApi.Tests/Services/ReceiptServiceTests.cs` | `backend/InventoryApi.Tests/Services/PurchaseServiceTests.cs` |
| `frontend/.../services/receipt.service.ts` | `frontend/.../services/purchase.service.ts` |
| `frontend/.../components/receipts/` | `frontend/.../components/purchases/` |
| `frontend/.../components/purchases/receipt-list.component.{ts,html}` | `.../purchase-list.component.{ts,html}` |
| `frontend/.../components/purchases/receipt-upload.component.{ts,html}` | `.../purchase-upload.component.{ts,html}` |

The `receipts` upload folder name, the database tables and migration history stay on their legacy
names; see the compatibility table below. (That upload folder has since moved out of `wwwroot` to
`{ContentRoot}/protected-files/receipts` for the authorization boundary described in
[Authentication and authorization](#authentication-and-authorization); only its location changed, not
its name.)

Because InventoryApp is a single-user application with no supported external API client, issue #127
removed the compatibility surfaces that existed only to preserve old bookmarks and generated clients:
the HTTP route, JSON wrapper key, and published OpenAPI names are now canonical Purchase language
end to end. Database/table/migration compatibility, listed in the table below, is a separate,
deliberately preserved concern and was not touched.

##### OpenAPI documentation

`PurchasesController` no longer pins an explicit legacy route; its `[Route("api/purchases")]`
publishes the canonical route, and Swashbuckle's default schema-id/tag derivation from CLR/controller
names is used unmodified — there is no `LegacyOpenApiCompatibility`/`UseLegacyReceiptNames` step in
`SwaggerServiceCollectionExtensions.AddInventoryApiSwagger` any more. The published document therefore
carries the `Purchase`/`PurchaseItem`/`PurchaseResponseDto`/`PurchaseValidationDto` schema ids and the
`Purchases` tag, with no remaining `Receipt*` schema id or `Receipts` tag.
`InventoryApi.Tests.Swagger.PurchaseOpenApiContractTests` (schema ids, tag, and the schemas the
purchase operations reference) and `InventoryApi.Tests.Controllers.PurchasesControllerRouteTests`
(the effective `api/purchases` base route and its GET/POST/PUT/DELETE/file endpoints, read from the
MVC API explorer) cover this contract.

| Layer | Canonical Purchase language | Left as a legacy/compatibility surface | Why |
| --- | --- | --- | --- |
| `Inventory.Domain` | `Purchases.PurchaseTotalValidationPolicy` | — | New pure calculation; the one authoritative total-mismatch formula. |
| `Inventory.Application` | `Purchases.ComputePurchaseTotalValidation` | — | Thin use case wrapping the Domain policy; `InventoryApi.Services.PurchaseService` calls it instead of duplicating the formula. |
| `InventoryApi.Models` | CLR types and files `Purchase.cs`, `PurchaseItem.cs` | DbSet properties `Receipts`/`ReceiptItems`, table names `Receipts`/`ReceiptItems` (mapped explicitly with `ToTable`), `PurchaseItem.ReceiptId` column/property, `StockAdjustment.ReceiptItemId`/`ReceiptItem`, `SupplierOrderReceiptAllocation` (type and its `ReceiptItemId`/`ReceiptItem` members) | Schema/migration history must not change; these are persistence compatibility, not client/API compatibility, and stay out of scope until the purchasing/costing slice moves this persistence into `Inventory.Infrastructure`. |
| `InventoryApi.Services` | `PurchaseService : IPurchaseService` (files `PurchaseService.cs`/`IPurchaseService.cs`) | Physical upload folder keeps the name `receipts` (`FileSystemDocumentStorage.PurchaseDocumentsFolderName`), now under `{ContentRoot}/protected-files/` rather than `wwwroot/` | Already-uploaded purchase document scans must stay reachable by their stored file name; the storage adapter still falls back to the old `wwwroot/receipts` location. Renaming the on-disk category needs its own verified file-migration. |
| `InventoryApi.Controllers` | `PurchasesController` (file `PurchasesController.cs`), `[Route("api/purchases")]` | — | The route is now canonical; there is no supported external client left to preserve `api/receipts` for. |
| `InventoryApi.DTOs` | `PurchaseItemDto`, `PurchaseCreateMetaDto`, `PurchaseValidationDto`, `PurchaseResponseDto` (JSON keys `purchase`/`validation`) | — | The `receipt`/`validation` wrapper existed only for old clients; `PurchaseResponseDto`'s property is now named `Purchase`. |
| Frontend `models.ts`/`purchase.service.ts` | `Purchase`, `PurchaseItem`, `PurchaseValidation`, `PurchaseResponse` (`purchase` field), `PurchaseService` (canonical `/purchases` base URL), `PurchaseUploadPayload`/`PurchaseItemPayload`/`PurchaseUpdatePayload` | JSON-bound field `receiptId` on `PurchaseItem` | `receiptId` matches the backend `PurchaseItem.ReceiptId` persistence/JSON contract above, which is out of this issue's scope. |
| Frontend routing | `/purchases` and `/purchases/new` are the only supported purchase routes | — | The `/receipts` and `/receipts/new` redirect aliases were removed; there is no supported bookmark to preserve. |
| Supporting documents | Not renamed: `Purchase.FileName`/`StoredFileName`/`ContentType`/`FileSizeBytes`, the "Receipt or invoice" upload copy, `OperatingExpense` receipt-attachment naming | — | A purchase's attached scan/photo, and an operating expense's attachment, are supporting *documents*, a distinct concept from the Purchase business record. |

Out of scope for the Purchase/Products contract cleanup (per issues #60 and #127): changing purchase
accounting/inventory behaviour, the purchase GST/BAS model, any destructive migration/table rename,
the on-disk purchase-document category, and the `InventoryCostTransitionBaseline` workflow. The
separate OperatingExpense `/{id}/receipt` alias was removed by issue #61; see the "Protected
documents" bullet under
[Authentication and authorization](#authentication-and-authorization) for the current OperatingExpense
attachment route. The persistence compatibility surfaces listed above are deliberate and stay as they are;
renaming any of them would be a schema
change needing its own issue, ideally combined with the rest of the Purchasing and costing slice
(item 6 above).

### Machine refill

1. A refill moves units out of storage.
2. The movement is linked to the machine when known.
3. It does not create an expense or COGS and does not reduce costing inventory/value.

### Sale import and costing

1. Imported transaction facts are persisted using the Nayax transaction identity.
2. Status and payment type are classified centrally.
3. Only completed sales enter sales/profit calculations.
4. Historical cost is persisted on each sale with its status and source.
5. Unknown products, statuses, payment methods, or missing costs remain visible.

### Reimbursement import and reconciliation

1. Preserve imported period, gross, device, fee, GST, adjustment, and net facts.
2. Match completed card sales within the reimbursement coverage period.
3. Compare both gross/count and expected/actual settlement.
4. Mark reconciled only within the `$0.01` tolerance.
5. Surface pending, unmatched, partial, unknown, or unsupported data.

### Nayax catalog source-state reconciliation

Local catalog data and live Nayax data can drift: a product or machine can be renamed, remapped, or
stop being returned by Nayax entirely. `GET api/data-quality/nayax-catalog-reconciliation` (issue
#55) compares the two and reports a source state per identity for human review; it never deletes or
changes a local record on the basis of what Nayax currently returns.

`Inventory.Domain.CatalogReconciliation.SourceReconciliationState` defines five states, resolved
deterministically by `CatalogReconciliationPolicy` in priority order so at most one state applies to
an identity:

1. **ConflictingIdentity** - the identity is genuinely ambiguous, either because Nayax returns more
   than one entry for the identifier in one snapshot, or because local history records more than one
   name for it at its most recent observation, so no single current local name can be determined.
2. **MissingRemotely** - a local record exists but Nayax no longer returns the identifier. This is a
   data-quality signal, not deletion: the local product/machine history is untouched.
3. **Added** - Nayax returns the identifier but there is no local record of it yet.
4. **MappingChanged** - both sides have the identifier but disagree on their *current* name (a rename
   or remap), after stripping a parenthetical Nayax price/code suffix the same way
   `ProductMatcher.NormalizeName` already does for sale-to-product matching, so formatting-only
   differences are not reported as changes.
5. **Present** - local and remote agree on identity and current name.

Only the latest reliable local name takes part in the comparison. Earlier names are carried
separately as `HistoricalLocalNames` on every reported row (most recently used first) and appended to
the row's note as context; they never decide a state on their own. A completed historical rename is
therefore ordinary history: once the latest local name agrees with Nayax again the identity is
`Present`, and a later disagreement still surfaces as `MappingChanged` instead of being masked by a
permanent conflict.

Products are compared directly (`Product.Id` is the persisted Nayax product identifier; see
`ImportService.ImportProductsAsync`, which already never removes a local product Nayax stops
returning). Machines have no persisted entity at all - `MachineService` builds machines as a live
Nayax view - so a machine's local history is derived from its recorded `NayaxSales` rows: the
`MachineName` on the most recent `MachineAuthorizationTime` is the latest reliable local name, any
other distinct `MachineName` for the same `MachineID` becomes a historical name, and the current name
counts as ambiguous - the only local `ConflictingIdentity` evidence - only when that most recent
authorization time itself carries more than one distinct `MachineName`.

This is a vertical slice on the current dependency skeleton: `SourceReconciliationState`,
`CatalogReconciliationPolicy`, and the plain `RemoteCatalogEntry`/`LocalCatalogEntry` value types are
deterministic `Inventory.Domain` rules with no Nayax or EF Core dependency.
`Inventory.Application.CatalogReconciliation.GetNayaxCatalogReconciliation` is the use case, reading
through the narrow `INayaxCatalogSnapshotProvider`/`ILocalCatalogSnapshotProvider` ports.
`InventoryApi.Adapters.Nayax.NayaxCatalogSnapshotProvider` (backed by `INayaxLynxClient`) and
`InventoryApi.Adapters.Persistence.EfLocalCatalogSnapshotProvider` (backed by `AppDbContext`) are
temporary API-owned adapters, following the same pattern as `EfNayaxFeeRateStore`, because
`INayaxLynxClient` and `AppDbContext` still live in `InventoryApi`. `DataQualityController` only binds
the request and returns the use case's `CatalogReconciliationReportDto`.

## Incremental migration plan

Each step is a separate, passing pull request. Existing endpoints stay operational throughout.

Backend and frontend tracks can progress independently when their contracts do not change. A vertical feature change that touches both sides should still be delivered as one coherent, tested pull request.

### Backend migration track

1. **Safety baseline**
   - Add repository instructions, architecture documentation, and cross-platform validation scripts.
   - Correct documentation/CI drift in focused follow-up changes.

2. **Project skeleton** — done.
   - Added `Inventory.Domain`, `Inventory.Application`, and `Inventory.Infrastructure` projects, the allowed reference directions, no-op dependency-registration extensions wired into `InventoryApi`, and architecture tests that fail on a prohibited reverse dependency.
   - The boundary is enforced from two directions, both in `backend/InventoryApi.Tests/Architecture/`: `ProjectDependencyDirectionTests` reads the `.csproj` files, so it catches a forbidden `ProjectReference` that is declared but not yet used (the compiler would trim it from assembly metadata); `CleanArchitectureDependencyTests` uses NetArchTest against the compiled assemblies, so it catches a forbidden dependency that arrives without a new project reference - through a transitive package or a shared source file - and also keeps ASP.NET Core, EF Core, `HttpClient` and ClosedXML types out of `Inventory.Domain` and `Inventory.Application`.
   - No feature was moved; every controller, service, model, and adapter still lives in `InventoryApi`.

3. **Nayax fee settings slice** — done.
   - Moved validation and use cases out of `SettingsController` into `Inventory.Domain.NayaxFeeSettings`/`Inventory.Application.NayaxFeeSettings`.
   - Proved the persistence port (`INayaxFeeRateStore`), a temporary API-owned EF adapter (`InventoryApi.Adapters.Persistence.EfNayaxFeeRateStore`), result mapping, DI registration, and the unit/Application/SQLite/API test pattern this migration will reuse.
   - The EF adapter remains temporarily in `InventoryApi` until `AppDbContext` and its persistence models move into `Inventory.Infrastructure`.

4. **Operating expenses slice**
   - Extract use cases and persistence.
   - **`IDocumentStorage` done** (issue #39, checkpoint 1): the storage port and its filesystem
     adapter are in place for both purchase documents and operating-expense attachments; see
     [Document storage](#document-storage). Checkpoint 2 added the tenant-scoped Azure Blob
     adapter behind the same port and the `DocumentStorage:Provider` selection; migrating the
     documents already on disk is still to come. The operating-expense use cases and persistence
     themselves remain in `OperatingExpensesController`/`AppDbContext`.
   - Preserve atomic replacement/cleanup and upload validation behavior.

5. **Products and stock slice**
   - Move reorder and inventory-movement rules to Domain.
   - Preserve supplier-order projection and low-stock semantics.

6. **Purchasing and costing slice**
   - Migrate purchases, supplier orders, stock ledger, AVCO, rebuilding, and sale costing as one coherent area.
   - **Receipt-to-Purchase internal rename done** (issue #60), ahead of the full slice migration above,
     **and its client/API compatibility shims removed** (issue #127). See
     [Purchase rename plan](#purchase-rename-plan) for the entity/service/component/DTO and source-file
     renames, the canonical `api/purchases` contract, and the persistence compatibility surfaces
     intentionally left on their legacy names. The rest of this slice — moving the purchase
     upload/update/delete orchestration itself, and the supplier-order/stock-ledger/AVCO code it
     touches, into `Inventory.Application`/`Inventory.Infrastructure` — remains future work.

7. **Reporting slices**
   - Split bookkeeping, daily, reconciliation, machine/product profitability, GST, dashboard, and transactions into separate query handlers.
   - Split CSV/XLSX formatting from report calculation.
   - **Contract placement done.** Report request/result contracts moved from `InventoryApi/DTOs/ReportingDtos.cs` into `Inventory.Application.Reporting.<Feature>` namespaces (`Shared`, `Bookkeeping`, `Daily`, `Reconciliation`, `MachineProfitability`, `ProductProfitability`, `Gst`, `Dashboard`, `Transactions`), with no JSON/API contract change.
   - **Bookkeeping slice done** (issue #43). `GetBookkeepingReport` (`Inventory.Application.Reporting.Bookkeeping`) and `BookkeepingProfitPolicy` (`Inventory.Domain.Reporting.Bookkeeping`) are the one authoritative implementation for `GET api/reports/bookkeeping`, its CSV/XLSX export, and the GST report that reuses its result. `ReportingCalculations`, `AustralianFyHelper`/`AustralianFinancialYear`, `ReportingRangeResolver`, and `ReportingQuality` moved to `Inventory.Domain.Reporting`/`Inventory.Application.Reporting.Shared` as the shared formulas every report family — migrated or not — now calls, so there is still exactly one implementation of each.
   - **Daily slice done** (issue #86). `GetDailyReport` (`Inventory.Application.Reporting.Daily`) and `DailyRowPolicy` (`Inventory.Domain.Reporting.Daily`) are the one authoritative implementation for `GET api/reports/daily` and its CSV/XLSX export. `ReconciliationStatusPolicy` moved to `Inventory.Domain.Reporting`, alongside `ReportingCalculations`, as the one reconciliation-status formula daily now calls; the reconciliation slice reuses the same policy instead of its own copy. `EfDailyReportFactsProvider`'s completed-sale cost query and period-level imported-reimbursement summary are shared with `EfBookkeepingReportFactsProvider` through `EfReportingSharedQueries` rather than duplicated a third time.
   - **Reconciliation slice done** (issue #87). `GetReconciliationReport` (`Inventory.Application.Reporting.Reconciliation`) and `ReconciliationPeriodPolicy` (`Inventory.Domain.Reporting.Reconciliation`) are the one authoritative implementation for `GET api/reports/reconciliation` and its CSV/XLSX export, for both individual period rows and the totals row (the totals row reuses the same policy over summed period facts rather than a second aggregation formula, since the underlying difference/expected-net formulas are linear). `ReconciliationPeriodPolicy` reuses the shared `Inventory.Domain.Reporting.ReconciliationStatusPolicy` daily also calls for the tolerance/pending/warning classification; its `OverallStatus` rollup of the independent gross and settlement statuses is reconciliation-specific and has no daily equivalent, so it was added alongside rather than folded into the shared policy. `IReconciliationReportFactsProvider` is its narrow port, and `EfReconciliationReportFactsProvider` is its temporary API-owned EF adapter, reusing `EfReportingSharedQueries`' completed and all-status sales queries; its per-reimbursement-period `Include` graph and card-gross fallback cascade (device payments, then account-level payment methods, then device gross, then the reimbursement total) are specific to reconciliation and stayed local to the adapter.
   - **Machine/product profitability slice done** (issue #88). `GetMachineProfitabilityReport`/`GetProductProfitabilityReport` (`Inventory.Application.Reporting.MachineProfitability`/`ProductProfitability`) are the one authoritative implementation for `GET api/reports/machine-profitability` and `GET api/reports/product-profitability` and their CSV/XLSX exports (and for the dashboard report, which reuses product profitability's result). `ProfitabilityRowPolicy` (`Inventory.Domain.Reporting.Profitability`) is the shared per-machine/per-product cost/gross-profit/margin gate both reports call; `MachineDirectProfitPolicy` is machine profitability's own completeness/direct-profit rule (COGS complete, no missing Nayax fee rates, complete commission coverage), mirroring `BookkeepingProfitPolicy`'s machine-filtered branch. `IMachineProfitabilityReportFactsProvider`/`IProductProfitabilityReportFactsProvider` are their narrow ports, and `EfMachineProfitabilityReportFactsProvider`/`EfProductProfitabilityReportFactsProvider` are their temporary API-owned EF adapters, reusing `EfReportingSharedQueries`' completed-sale query; the machine adapter also reuses `EfReportingSharedQueries.GetMachineCommissionsAsync`/`GetSiteCommissionAsync` (moved there from `EfBookkeepingReportFactsProvider`, which now calls the shared version too) rather than duplicating commission resolution a third time. Nayax product matching moved to `Inventory.Domain.Reporting.ProductMatching.ProductMatcher`, a pure algorithm over a Domain-owned `ProductMatchCandidate` rather than the persistence `Product` entity; the product profitability use case calls it directly, never the EF adapter. `InventoryApi.Services.NayaxProductMatcher`, still used by machine service, sale costing, inventory cost rebuild, import, and site commissions (outside this migration's scope), became a thin wrapper delegating to the same Domain implementation instead of a second copy of the algorithm. Dashboard and transactions have since moved too (see below); each was tracked as its own follow-up issue.
   - **GST accounting-aid slice done** (issue #89). `GetGstAccountingAid` (`Inventory.Application.Reporting.Gst`) is the one authoritative implementation for `GET api/reports/gst` and its CSV/XLSX export. It depends on the already-migrated bookkeeping use case through the Application-owned `IGetBookkeepingReport` interface (implemented by `GetBookkeepingReport`) and reuses its GST-on-sales/GST-on-fees figures rather than re-deriving them; `GstAccountingAidPolicy` (`Inventory.Domain.Reporting.Gst`) derives taxable sales, taxable fees, and net GST from those figures. `IGstReportFactsProvider` is its narrow port for the imported-summary data-quality flags (whether any imported rows and any GST/VAT classification cover the period) this report still needs, and `EfGstReportFactsProvider` is its temporary API-owned EF adapter, reusing `EfReportingSharedQueries.ImportedSummaryAsync` rather than duplicating the imported-summary query a further time. At that point in the migration, `ReportingService.GetGstAsync` was a thin delegator to `GetGstAccountingAid`, not a second implementation, until issue #92 later removed `ReportingService` entirely (see below).
   - **Dashboard slice done** (issue #90). `GetDashboardReport` (`Inventory.Application.Reporting.Dashboard`) is the one authoritative implementation for `GET api/reports/dashboard` and its CSV/XLSX export. It depends on the already-migrated bookkeeping and product profitability use cases through the Application-owned `IGetBookkeepingReport`/`IGetProductProfitabilityReport` interfaces (implemented by `GetBookkeepingReport`/`GetProductProfitabilityReport`) and reuses their sales, profit, fee, commission, operating-expense, and unmapped-product figures rather than re-deriving them; only the dashboard-specific reimbursement reconciliation is computed independently. `DashboardReimbursementPolicy` (`Inventory.Domain.Reporting.Dashboard`) derives the expected-versus-actual Nayax reimbursement difference and its "Pending"/"Reconciled"/"Needs Review" status from card sales, fees, and the imported net settlement; it reuses the shared `Inventory.Domain.Reporting.ReconciliationStatusPolicy` tolerance check the daily/reconciliation slices also call, but keeps its own three-state status vocabulary locally because it has no separate "Warning" state. `IDashboardReportFactsProvider` is its narrow port for the summary facts unique to the dashboard (completed-sale transaction/machine/product counts, the imported reimbursement facts, and commission completeness/warnings for its own data-quality notes), and `EfDashboardReportFactsProvider` is its temporary API-owned EF adapter, reusing `EfReportingSharedQueries`' completed-sale query, imported-summary query, and site-commission resolution rather than duplicating them a further time. `ReportsController` calls `GetDashboardReport` directly for that endpoint; at that point in the migration, the legacy `ReportingService.GetDashboardAsync` delegated to the same use case, and the `INayaxProcessingFeeService`/`ISiteCommissionService` dependencies it only needed for that orchestration were removed from `ReportingService`, so CSV/XLSX export stayed on one authoritative implementation, until issue #92 removed `ReportingService` entirely (see below).
   - **Transaction sales slice done** (issue #91), the last individual report family. `GetTransactionSalesReport` (`Inventory.Application.Reporting.Transactions`) is the one authoritative implementation for `GET api/reports/transactions` and its CSV/XLSX export, including the unpaginated export case. `Inventory.Domain.Reporting.Transactions.TransactionRowPolicy` derives each transaction's estimated Nayax fee (effective-dated rate lookup, unavailable when none covers the sale date) and site commission (effective-dated agreement lookup, unavailable when none covers the sale, overlapping when more than one does) and its resulting gross/direct profit, reusing the shared `ReportingCalculations`; `TransactionTotalsPolicy` aggregates those per-row results into the report totals. These per-transaction rules are deliberately separate from (not merged into) the aggregate bookkeeping/machine-profitability commission-completeness rules, since row-level and period-level coverage semantics differ. `Inventory.Application.Reporting.Transactions.GetTransactionSalesReport` resolves the requested date range/machine scope, retrieves facts through the narrow `ITransactionSalesReportFactsProvider` port, matches each raw Nayax product identifier/name to the catalogue through the shared `Inventory.Domain.Reporting.ProductMatching.ProductMatcher` (the same algorithm the product profitability slice uses), invokes the Domain row/totals policies, then applies status/payment/COGS/search filtering, user-selected sorting, pagination, page-size clamping (50/100/250, default 50), and filter-option construction as Application/presentation concerns. `EfTransactionSalesReportFactsProvider` is its temporary API-owned EF adapter; because transactions needs every status (not only completed sales, unlike every other migrated report), it does not reuse `EfReportingSharedQueries`' completed-sale query, and its site-name resolution from the live Nayax machine directory has no equivalent adapter to share it with. `ReportsController` calls `GetTransactionSalesReport` directly for that endpoint. This was the last individual report family in the sequence from issue #43.
   - **Shared-query audit and legacy service removal done** (issue #92), the final item in the sequence. The audit re-examined every `Ef<Feature>ReportFactsProvider` adapter for equivalent EF query helpers that earlier slices had not yet consolidated and found none: `EfReportingSharedQueries` already covers every completed-sale query, cost projection, imported-summary query, and site-commission resolution shared across bookkeeping/daily/reconciliation/machine-profitability/GST/dashboard, and the two helpers that looked similar but are not — `EfBookkeepingReportFactsProvider`'s business-wide receipt/operating-expense totals versus machine profitability's per-machine operating-expense breakdown, and `EfTransactionSalesReportFactsProvider`'s all-status query versus the shared completed-sale query — were deliberately kept separate and documented in place rather than forced into one shape. `Inventory.Application.Reporting.Export.GetReportExportRows` replaced the legacy `ReportingService`'s `ExportCsvAsync`/`ExportXlsxAsync` row-building: it calls the same eight migrated use cases directly and returns already-formatted rows (a `ReportExportTable`), never a re-derived value. `InventoryApi.Adapters.Export.ReportExportFileWriter` is the outer InventoryApi adapter that encodes those rows as CSV or XLSX bytes (ClosedXML stays out of `Inventory.Application`, per the architecture rule). `ReportsController`'s single `{report}/export` action now calls `GetReportExportRows` and `ReportExportFileWriter` instead of `IReportingService`. `InventoryApi.Services.ReportingService`/`Services.Interfaces.IReportingService` are gone: their dependency-injection registration (`Program.cs`), every production and test caller (the controller and every test), and both source files (`InventoryApi/Services/ReportingService.cs`, `InventoryApi/Services/Interfaces/IReportingService.cs`) were removed. An architecture test (`ProjectDependencyDirectionTests.No_other_source_file_references_the_removed_legacy_reporting_service`) proves no source file still references them.
   - **Transaction report streaming and bounded page buffering done** (issue #115). `EfTransactionSalesReportFactsProvider.GetFactsAsync` no longer completes its date/machine-filtered EF query with `ToListAsync` into a full transaction list before returning; `TransactionSalesReportFacts.Transactions` is now an `IAsyncEnumerable<TransactionSalesReportFactsRow>`, and the adapter streams rows one at a time from the EF query (`IQueryable.AsAsyncEnumerable()`) with cancellation propagated through the stream. `GetTransactionSalesReport.Handle` enumerates that stream exactly once: it product-matches and runs `TransactionRowPolicy` per raw row as it arrives, folds matching rows into `Inventory.Domain.Reporting.Transactions.TransactionTotalsAccumulator` instead of building an intermediate `TransactionTotalsRowInputs` list (`TransactionTotalsPolicy.Calculate` now delegates to the same accumulator, so batch and incremental accumulation share one formula path), and accumulates distinct site/product filter-option state in dictionaries rather than retaining every row. Totals, quality facts, and filter options still cover the complete date/machine scope exactly as before — this is a one-pass, full-scope streaming design, not page-size-bounded database work or SQL pagination/filter pushdown (both stay out of scope). For a paginated request (`paginate: true`), only the best `page * pageSize` sorted filtered-row candidates needed to answer that page are retained, using the new `Inventory.Application.Reporting.Shared.BoundedTopSelector<T>` fed a comparer equivalent to the existing `SortRows` ordering; for `paginate: false` (CSV/XLSX export), the complete filtered result set is still collected and sorted as before, since export intentionally returns everything.

8. **Remove legacy structure**
   - Done for reporting (issue #92): `InventoryApi.Services.ReportingService`, `InventoryApi.Services.Interfaces.IReportingService`, their dependency-injection registration, and every production and test caller were removed, and both source files were deleted. Reporting exports now run through `Inventory.Application.Reporting.Export.GetReportExportRows` for row building and `InventoryApi.Adapters.Export.ReportExportFileWriter` for CSV/XLSX byte encoding.
   - Still pending for every other feature area (products, stock, purchasing/costing, and the remaining direct-access controllers/services); only after each is migrated and tests prove equivalent behavior does this step complete overall.

### Frontend migration track

1. **Frontend safety baseline**
   - Add a pinned unit-test runner and lint command in a focused pull request.
   - Test runtime configuration and one representative feature client before moving files.

2. **Feature boundaries**
   - Move one routed area at a time under `features/<feature>`.
   - Keep pages, feature UI, contracts, and data access together and preserve public route URLs.

3. **Lazy top-level routes**
   - Convert migrated features to `loadComponent` or a small feature route file.
   - Verify direct navigation, refresh behavior, and production bundle budgets.
   - **Done for every top-level route (issue #65).** All routes in `app.routes.ts` use `loadComponent`, except the public `/auth` callback, which is not a migrated feature area and stays eagerly imported. This step preceded the file-move step above: components still live under `components/`, not yet under `features/<feature>`, so this reflects route-loading behavior only, not the target `src/app/features/` layout.

4. **Reporting slices**
   - Split the broad reporting client and service-local interfaces by report family.
   - Retain shared filters/export behavior without centralizing every report in another large class.
   - Preserve nullable financial values, quality counts, and backend/export parity.
   - **Contract placement done.** Report interfaces moved from `services/reporting.service.ts` into `features/reports/<feature>/models/` (`shared`, `bookkeeping`, `daily`, `reconciliation`, `machine-profitability`, `product-profitability`, `gst`, `dashboard`, `transactions`); `ReportingService` re-exports them from their new location so existing component imports are unaffected. The service itself, its HTTP calls, and the routed report pages/components have not moved into `features/reports/` yet.

5. **Large page decomposition**
   - Extract cohesive forms, tables, and panels from large administration and report pages.
   - Keep orchestration in the routed page and make child components input/output driven.

6. **Critical workflow coverage**
   - Add browser-level smoke tests for product maintenance, purchase/restock, machine refill, import, and a representative financial report.
   - Run frontend tests and the production build in pull-request validation before changing the deployment gate.

## Testing architecture

### Backend tests

Use three complementary levels:

1. **Pure unit tests** for calculations, policies, classification, and domain transitions.
2. **SQLite integration tests** for EF queries, constraints, transactions, ordering, migrations, and repositories.
3. **API/adapter tests** for HTTP contracts, Nayax mapping, file storage, imports, and report exports.

EF Core InMemory tests remain useful for fast service checks but must not be the only evidence for relational behavior.

Most controller tests instantiate the controller directly and never exercise ASP.NET Core's middleware pipeline. Proving the `[Authorize]`/`[RequiredScope]` HTTP boundary (issue #38) instead requires a real pipeline: `AuthenticationBoundaryTests` (`backend/InventoryApi.Tests/Controllers/`) hosts the app with `WebApplicationFactory<Program>`, swapping `AppDbContext` for a shared open in-memory SQLite connection so `Program.cs`'s startup schema step (`DatabaseSchemaStartup.EnsureSchema`) succeeds, then asserts that an unauthenticated request to a representative protected endpoint — including the receipt and operating-expense document endpoints — returns `401`, and that a file placed in the web root has no anonymous static URL. `Program.cs` exposes a trailing `public partial class Program;` solely so `WebApplicationFactory<Program>` can reference it from the test assembly.

Financial regression tests should cover at least:

- card/cash split and unknown payment type;
- completed, pending, refunded, cancelled/declined, and unknown status;
- complete and incomplete COGS;
- internal AVCO and Nayax fallback provenance;
- actual versus estimated fees and missing rates;
- commission bases, gaps, overlaps, and zero-agreement sites;
- reimbursement period and `$0.01` reconciliation boundary;
- machine-filtered versus whole-business profit;
- date/FY filters and export parity.

### Frontend tests

The current package has no automated test command, so the first frontend testing change must choose, configure, and pin the runner explicitly. The target test mix is:

1. **Pure unit tests** for date presets, display-only transformations, validation, and nullable financial presentation.
2. **HTTP client tests** for endpoint, query-parameter, request-body, response, and error mapping behavior.
3. **Component tests** for loading, empty, error, success, confirmation, and accessibility states.
4. **Router tests** for route parameters, redirects, lazy features, and direct report navigation.
5. **Browser smoke tests** for a small number of business-critical workflows against a controlled API/database.

Do not duplicate backend formula tests in Angular. Frontend assertions should prove that authoritative values and quality states are requested and presented correctly.

## Build and delivery

The canonical local validation entry points are `scripts/validate.ps1` and `scripts/validate.sh`. They restore, build, and test the backend and run a clean install plus production build for the frontend. `.github/workflows/validate.yml` runs the Bash entry point for every pull request targeting `develop` or `main` without deploying. When frontend test and lint scripts are added, these validation entry points and pull-request CI must call them.

Frontend build flow is:

1. `npm ci` installs the locked dependency graph.
2. `npm run build:styles` generates Tailwind output from `src/styles.scss` and template/TypeScript content.
3. `ng build` creates `dist/inventory-app` and enforces the production budgets in `angular.json`.
4. Azure Static Web Apps serves the compiled assets and runtime configuration; the host must provide Angular navigation fallback.

Current production delivery is triggered from `main`:

- `.github/workflows/vm-manager.yml` builds/tests and deploys the API.
- `.github/workflows/azure-static-web-apps-red-island-0c128c000.yml` builds/deploys the Angular frontend.

Consequently, automated engineering agents stop at a pull request. Merge and production deployment remain human-controlled. The branch flow, agent authority model, task states, and risk classification for automated changes are defined in [docs/automation.md](automation.md).

Deploying the API is not a way to change its database schema (issue #54). `InventoryApi/Bootstrap/DatabaseSchemaStartup.EnsureSchema` runs at startup and decides per environment:

- **Production never migrates automatically**, regardless of configuration. It applies nothing and throws `PendingMigrationsException` if any migration is pending, naming the pending migrations and the command to apply them rather than serving requests against a schema its code does not match.
- **Development and `Testing`** apply pending migrations automatically, because their database is disposable.
- **Any other non-Production environment** applies them automatically only when the `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` configuration override is `true` — the opt-in for a disposable non-Development database such as an ephemeral integration or staging environment. The override is checked only after Production has been ruled out by environment name, so it can never reach production data; without it, such an environment fails closed exactly as Production does.

Applying a pending schema change is instead the separate, human-invoked `migrate-database` command (`InventoryApi/Bootstrap/DatabaseMigrationCommand`), run with `--dry-run` to inspect and `--apply` to migrate, after a verified backup. Recovery from a bad apply is restoring that backup; the command does not roll a migration back. See `docs/tenant-rollout.md` for the worked example and `AGENTS.md` § Database and migrations for the invariant.

## Architectural decision rules

Use this order when considering new structure:

1. Can an existing feature/use case own the change?
2. Can a pure calculation or domain type express the rule?
3. Is an external port needed because the code crosses a database, HTTP, time, or filesystem boundary?
4. Is a new dependency measurably simpler than a local implementation?

Do not use Clean Architecture as a reason to introduce layers without behavior, duplicate models mechanically, or move a large class unchanged into a differently named project. The objective is explicit domain rules, replaceable external boundaries, reliable tests, and small changes that humans and agents can understand.

For frontend structure, ask the corresponding questions:

1. Which routed feature owns the behavior?
2. Is the code page orchestration, reusable presentation, API data access, or an API contract?
3. Is sharing based on proven reuse with the same meaning, or only on similar-looking code?
4. Does the API already own this business calculation?

Prefer a complete vertical change over disconnected backend and frontend abstractions. A feature is complete only when its contract, UI states, validation, tests, and any report/export implications agree.
