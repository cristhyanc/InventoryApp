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
| Documents | Files beneath the API web root with metadata in SQLite |
| Frontend | Angular 19 standalone components, TypeScript, RxJS, Tailwind-based styling |
| Backend tests | xUnit, Moq, EF Core InMemory and SQLite |
| Hosting | Azure App Service API and Azure Static Web Apps frontend |
| Automation | GitHub Actions |

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
│   └── InventoryApi.Tests/
├── frontend/inventory-app/
├── .github/workflows/
├── AGENTS.md
└── scripts/
```

The API is currently a single assembly. Controllers generally call service interfaces, while services use `AppDbContext` and, where required, Nayax or filesystem facilities. `Program.cs` is the composition root and applies EF Core migrations at startup.

```mermaid
flowchart TD
    UI["Angular frontend"] --> API["ASP.NET controllers"]
    API --> Services["Application services"]
    Services --> DB["EF Core / SQLite"]
    Services --> Nayax["Nayax Lynx + imports"]
    Services --> Files["Receipt and expense documents"]
```

## Current strengths

- Most feature controllers already depend on service interfaces.
- The backend has meaningful tests for products, stock, receipts, supplier orders, costing, imports, fees, commissions, machine profitability, reporting, and migrations.
- Nayax transaction status and payment-method classification are centralized.
- Historical sale cost and its provenance are persisted.
- Reconciliation and incomplete-data states are represented explicitly.
- Backend CI restores, builds, and tests before a `main` deployment.

## Current pressure points

- HTTP, use cases, domain calculations, EF Core, Nayax, file storage, and export generation live in one project.
- `ReportingService` owns many report types, shared queries, reconciliation, and CSV/XLSX generation and has grown to roughly 110 KB.
- Receipt, machine, and inventory-cost-transition services combine orchestration and persistence and are large.
- Operating-expense, fee-setting, and site-commission controllers directly access `AppDbContext`; operating expenses also manipulate files.
- `Product` contains persistence state, business calculations, and transient Nayax/UI fields.
- Reporting contracts are concentrated in a large DTO file.
- Several tests use EF Core InMemory where SQLite behavior may be more representative.
- The deployment workflows currently do not provide a complete backend-and-frontend validation gate for every pull request.

These are reasons to improve boundaries, not reasons for a wholesale rewrite.

## Target: pragmatic Clean Architecture with vertical slices

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

Examples include `CreateOperatingExpense`, `RecordCommissionPayment`, `ImportNayaxSales`, `GetBookkeepingReport`, and `RebuildHistoricalCosts`.

Application code determines what must happen. It does not know the physical database, file path, HTTP endpoint, or spreadsheet library used to make it happen.

### Inventory.Infrastructure

Contains adapters and technical implementation:

- `AppDbContext`, entity configurations, migrations, and repositories/read stores.
- Nayax Lynx HTTP client and imported-file parsers.
- Document storage for receipts and operating expenses.
- CSV/XLSX report exporters.
- Clock/timezone adapter if introduced.

Use narrow feature-specific ports. A generic repository that leaks persistence semantics into every feature is not a goal.

### InventoryApi

Contains:

- Controllers and HTTP-specific models.
- Authentication/authorization and middleware.
- OpenAPI configuration.
- Dependency injection and application startup.
- HTTP error/result mapping.

Controllers do not implement accounting, inventory, persistence, or filesystem rules.

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

## Data flow

### Product purchase and restock

1. A receipt and receipt items record the source purchase.
2. Receipt-linked restock movements add physical and costing inventory at purchase cost.
3. Delivery/package amounts remain identifiable for whole-business reporting.
4. Supplier-order allocations are reconciled without fabricating purchase quantities.

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

## Incremental migration plan

Each step is a separate, passing pull request. Existing endpoints stay operational throughout.

1. **Safety baseline**
   - Add repository instructions, architecture documentation, and cross-platform validation scripts.
   - Correct documentation/CI drift in focused follow-up changes.

2. **Project skeleton**
   - Add Domain, Application, and Infrastructure projects and dependency-registration extensions.
   - Move no complex feature merely to populate the projects.

3. **Nayax fee settings slice**
   - Move validation/use cases out of `SettingsController`.
   - Prove persistence port, result mapping, DI, and test patterns.

4. **Operating expenses slice**
   - Extract use cases, persistence, and `IDocumentStorage`.
   - Preserve atomic replacement/cleanup and upload validation behavior.

5. **Products and stock slice**
   - Move reorder and inventory-movement rules to Domain.
   - Preserve supplier-order projection and low-stock semantics.

6. **Purchasing and costing slice**
   - Migrate receipts, supplier orders, stock ledger, AVCO, rebuilding, and sale costing as one coherent area.

7. **Reporting slices**
   - Split bookkeeping, daily, reconciliation, machine/product profitability, GST, dashboard, and transactions into separate query handlers.
   - Split CSV/XLSX formatting from report calculation.

8. **Remove legacy structure**
   - Only after every feature is migrated and tests prove equivalent behavior.

## Testing architecture

Use three complementary levels:

1. **Pure unit tests** for calculations, policies, classification, and domain transitions.
2. **SQLite integration tests** for EF queries, constraints, transactions, ordering, migrations, and repositories.
3. **API/adapter tests** for HTTP contracts, Nayax mapping, file storage, imports, and report exports.

EF Core InMemory tests remain useful for fast service checks but must not be the only evidence for relational behavior.

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

## Build and delivery

The canonical local validation entry points are `scripts/validate.ps1` and `scripts/validate.sh`. They restore, build, and test the backend and install/build the frontend.

Current production delivery is triggered from `main`:

- `.github/workflows/vm-manager.yml` builds/tests and deploys the API.
- `.github/workflows/azure-static-web-apps-red-island-0c128c000.yml` builds/deploys the Angular frontend.

Consequently, automated engineering agents stop at a pull request. Merge and production deployment remain human-controlled.

## Architectural decision rules

Use this order when considering new structure:

1. Can an existing feature/use case own the change?
2. Can a pure calculation or domain type express the rule?
3. Is an external port needed because the code crosses a database, HTTP, time, or filesystem boundary?
4. Is a new dependency measurably simpler than a local implementation?

Do not use Clean Architecture as a reason to introduce layers without behavior, duplicate models mechanically, or move a large class unchanged into a differently named project. The objective is explicit domain rules, replaceable external boundaries, reliable tests, and small changes that humans and agents can understand.

