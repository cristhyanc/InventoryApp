# InventoryApp

InventoryApp is a full-stack operations and bookkeeping system for a vending-machine business. It manages the path from purchasing and stock through machine replenishment and Nayax sales, then turns those records into auditable cost, reconciliation, GST, and profitability reports.

## Capabilities

- Product, category, supplier, and low-stock management, including a per-product supplier price history/comparison (lowest vs. latest actual purchase cost, by supplier and date). A supplier can record an optional **website**, which the Suppliers list opens in a new tab and the application never visits itself (see [A supplier's website](#a-suppliers-website)). A supplier that came from the platform's supplier list keeps that list's name and website, cannot be deleted, and can be marked **Not used** to keep it out of the pickers; a business receives the ones that serve the state it trades in, picks up more when it changes state, and can add any other that serves it in one click from **Available to add** (see [Suppliers from the platform list](#suppliers-from-the-platform-list)). A product itself carries **no supplier**: a supplier is recorded on the purchase you bought in and on the supplier order you placed, and the price comparison shows every supplier a product was actually bought from out of that purchase history (see [A product carries no supplier](#a-product-carries-no-supplier)). It carries **no selling price** either: what it sells for is the machine's own live price (see [A product carries no selling price](#a-product-carries-no-selling-price)). Products and categories can be created, edited and deleted by the business itself, with or without a sales provider, and a product with history is retired rather than deleted — kept out of the product lists, the pickers and the reorder alerts while every record of it stays (see [Products and categories](#products-and-categories)). Reorder alerts and the dashboard's ordering figures work with or without a connected Nayax account (see [Reorder alerts without Nayax](#reorder-alerts-without-nayax)).
- Nayax is an optional add-on: inventory, purchasing, expenses and the Bookkeeping and GST reports work without it, while the features that depend on it — the live Nayax pages, and the five sales reports that need a Nayax connection to exist at all — are shown locked until a provider is connected, with no stored sales removed (see [The menu without a usable provider](#the-menu-without-a-usable-provider)).
- Purchases, their supporting documents, supplier orders, and stock movements. A purchase's receipt is
  **optional**, and one can be added to a purchase that has none later, so running out of somewhere to put a
  receipt never stops you recording what you bought (see [A purchase without a
  receipt](#a-purchase-without-a-receipt)). Recording the final delivery
  against a supplier order that arrived short offers, before anything is saved, a choice between keeping the
  order open and completing it with the remainder undelivered (see [Receiving a supplier order: keep it open,
  or complete it](#receiving-a-supplier-order-keep-it-open-or-complete-it)).
- Sites, vending machines, product assignments, and machine refills.
- A **home dashboard reminder when machine restock sync is behind** (see [Machine restock sync reminder](#machine-restock-sync-reminder)): Sync Restock is something an operator asks for, so the dashboard says which Nayax-linked machines have not had a successful restock sync in the last three business days, or still have restock events waiting to be reviewed.
- Nayax product and transaction imports with explicit payment/status handling.
- Owner-driven Nayax connection setup: test a token, see which permissions it carries, save it and re-test it later (see [Connecting Nayax (Admin page)](#connecting-nayax-admin-page)).
- Historical weighted-average inventory costing and persisted COGS provenance.
- Effective-dated Nayax processing fees and site commission agreements.
- Operating expenses, reimbursement imports, and settlement reconciliation.
- Dashboard, bookkeeping, daily-sales, transaction, GST, commission, machine, and product reports; **Bookkeeping is the one consolidated financial summary report** (issue #806 retired the separate Reporting Dashboard page once Bookkeeping covered its distinct figures; the old address now redirects to Bookkeeping, filters and all). Monthly Bookkeeping reports its own sales volume too — the number of completed transactions in the selected dates and machine scope, and the average sale over them; both figures follow the same date and machine filters as the rest of the report, appear in its CSV/XLSX export, and show their availability state rather than a zero when provider data does not cover the period. Monthly Bookkeeping also reconciles the reimbursement it expects — the period's card sales less the Nayax fees including GST — against the reimbursement an imported Nayax settlement actually recorded for that period, showing the signed difference and a Reconciled, Needs Review or Pending status (pending until a settlement import covers the period), with the same tolerance, filters, CSV/XLSX columns and availability labelling as the rest of the report; the calculated expectation stays separate from the imported actual and from the existing net reimbursement figure.
- CSV/XLSX exports that use the same backend calculations as the UI.
- Microsoft Entra sign-in and delegated-scope API authorization (see [Authentication configuration](#authentication-configuration)).
- Owner-only business settings — name, contact email, ABN, GST registration, time zone and the Australian state or territory the business trades in, with a confirmation before a time zone change (see [Business settings (Admin page)](#business-settings-admin-page)).

## Technology

| Area | Stack |
| --- | --- |
| API | ASP.NET Core Web API, .NET 10 |
| Persistence | EF Core 10, SQLite, code-first migrations |
| Frontend | Angular 19 standalone components, TypeScript, RxJS, Tailwind CSS |
| Authentication | Microsoft Entra ID, `Microsoft.Identity.Web` (API), MSAL (`@azure/msal-angular`/`@azure/msal-browser`, SPA) |
| Tests | xUnit, Moq, EF Core InMemory and SQLite |
| Integrations | Nayax Lynx API and imported reimbursement/workbook data |
| Observability | Azure Monitor OpenTelemetry (`Azure.Monitor.OpenTelemetry.AspNetCore`) to Application Insights, enabled by configuration |
| Hosting | Azure App Service and Azure Static Web Apps |
| Automation | GitHub Actions |

## Architecture

The application is a modular monolith with separate API and browser deployments. The backend's incremental move to pragmatic Clean Architecture with vertical feature slices is complete: `InventoryApi` is the HTTP boundary and composition root, `Inventory.Application` holds the use cases and ports, `Inventory.Domain` the deterministic rules and `Inventory.Infrastructure` the adapters, and architecture tests fail if business logic or persistence returns to the API project. The Angular frontend remains standalone and is moving toward feature-local pages, components, data access, and contracts.

```mermaid
flowchart LR
    UI["Angular UI"] --> Client["Typed API services"]
    Client --> API["ASP.NET API"]
    API --> Data["EF Core / SQLite"]
    API --> External["Nayax and documents"]
```

Business calculations stay authoritative on the backend. The UI owns navigation, interaction state, accessibility, and presentation without recreating inventory or accounting formulas.

See [docs/architecture.md](docs/architecture.md) for the current system, target boundaries, frontend structure, financial rules, and incremental migration tracks, and [docs/automation.md](docs/automation.md) for the controlled automated-development lifecycle.

## Repository map

```text
Inventory App/
├── backend/InventoryApi/          ASP.NET Core API, EF migrations, and solution
├── backend/Inventory.UnitTests/   Pure Domain/Application backend tests
├── backend/Inventory.IntegrationTests/ Database, API, adapter and architecture backend tests
├── frontend/inventory-app/        Angular application
├── docs/architecture.md           Current and target architecture
├── docs/automation.md             Automated development lifecycle and authority model
├── evals/agent/                   Agent Evals corpus, schema and baseline report
├── .github/ISSUE_TEMPLATE/        Agent task issue form
├── .github/pull_request_template.md
├── scripts/validate.ps1           Windows/PowerShell validation
├── scripts/validate.sh            Bash validation
├── scripts/run-agent-evals.mjs    Agent Evals runner
├── scripts/validate-runner-placement.mjs  Every workflow job on the runner the INVENTORY_AGENT_SELF_HOSTED_ALL switch selects
├── AGENTS.md                      Engineering and agent safeguards
└── CLAUDE.md                      Claude Code entry point (points to AGENTS.md and docs)
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20 or later](https://nodejs.org/) and npm

A global Angular CLI installation is not required; the npm scripts use the repository's locked CLI version.

## Run locally

### 1. Start the API

From the repository root:

```bash
dotnet restore backend/InventoryApi/InventoryApi.slnx
dotnet run --project backend/InventoryApi/InventoryApi.csproj
```

The development API listens at <http://localhost:5000>. Swagger UI is available at <http://localhost:5000/swagger> while the API is running in the Development environment.

EF Core migrations are applied automatically at startup in **Development**, **Testing**, and **Production** (issue #201). Development and `Testing` do this because their database is disposable; Production does it as part of a normal deployment restart, and fails closed — logging the failure and refusing to finish starting — rather than serving requests against a schema its code does not match. The default SQLite database is `inventory.db`, resolved from the API process's working directory; local database files must not be committed. Schema migrations never assign tenant ownership or perform the business backfill, so starting the API never triggers one. Some schema migrations do rebuild tables and copy persisted rows.

Any other non-Production environment (an ephemeral integration-test or Staging database, for example) still applies nothing by default and refuses to start while any migration is pending, unless `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` is set to `true` for that disposable database; the setting has no effect in Production, which always migrates automatically regardless. The explicit `migrate-database` command remains available for diagnostics and manual use — inspecting what a pending deployment will apply, or applying a high-risk migration ahead of a deployment window under review — from a source tree with the SDK:

```bash
dotnet run --project backend/InventoryApi -- migrate-database --dry-run
dotnet run --project backend/InventoryApi -- migrate-database --apply
```

or, from the deployed application (`dotnet publish` output, which has no SDK or sources, so `dotnet run --project` is unavailable):

```bash
dotnet InventoryApi.dll migrate-database --dry-run
dotnet InventoryApi.dll migrate-database --apply
```

#### Business/tenant bootstrap

Business data is owned by a `Business` record. Applying the schema creates no application `Business` — but it does not follow that there are no tenant-owned rows: seeded or pre-existing legacy rows carry `BusinessId = 0`, which matches no business, so they are invisible to every caller until the ownership bootstrap assigns them. Either way a signed-in caller sees an empty dataset until the bootstrap runs. This is the intended fail-closed state — the data is unowned, not lost — and the API logs an error at startup while it persists.

The mapping from Microsoft Entra identities to that business is human-supplied and is never committed. `appsettings.json` ships the `BusinessBootstrap` section empty; supply real values through user secrets locally:

```bash
cd backend/InventoryApi
dotnet user-secrets set "BusinessBootstrap:BusinessName" "<business name>"
dotnet user-secrets set "BusinessBootstrap:Members:0:DirectoryTenantId" "<your Entra tid claim>"
dotnet user-secrets set "BusinessBootstrap:Members:0:ObjectId" "<your Entra oid claim>"
```

Then assign ownership. The dry run measures and rolls back; `--apply` must be typed explicitly:

```bash
dotnet run --project backend/InventoryApi -- bootstrap-business --dry-run
dotnet run --project backend/InventoryApi -- bootstrap-business --apply
```

On a deployed application use `dotnet InventoryApi.dll bootstrap-business --dry-run` / `--apply` instead.

The command is restart-safe and idempotent: it only ever touches rows that are still unassigned. See [docs/tenant-rollout.md](docs/tenant-rollout.md) for the reviewed production sequence.

#### Migrating documents to Azure Blob storage

Uploaded purchase and operating-expense documents can be stored on the filesystem (the default)
or in a private Azure Blob container, selected by `DocumentStorage:Provider`. Moving the
documents already on disk into the container is a separate human-invoked command. It requires
`DocumentStorage:Provider=AzureBlob` with `BlobServiceUri` and `ContainerName`, authenticates
with `DefaultAzureCredential` — no account key, SAS token or connection string — and refuses to
run against the filesystem default:

```bash
DocumentStorage__Provider=AzureBlob dotnet run --project backend/InventoryApi -- migrate-documents --dry-run
DocumentStorage__Provider=AzureBlob dotnet run --project backend/InventoryApi -- migrate-documents --apply
```

On a deployed application use `dotnet InventoryApi.dll migrate-documents --dry-run` / `--apply`
instead. Exactly one of the two flags must be given.

The provider is supplied as a process-local override, not by changing the deployed application's
own setting: the running API reads the same `DocumentStorage:Provider`, so switching it
persistently would move live document reads to the container before anything had been copied or
verified.

### Reconciling attachment storage against the records (issue #968)

A document's bytes live outside the database, so storage and records can disagree in three ways
that nothing inside the application can see. `reconcile-attachment-storage` is the operator's read
of that, per business, against whichever storage the deployment is configured with:

```bash
dotnet run --project backend/InventoryApi -- reconcile-attachment-storage --dry-run
dotnet run --project backend/InventoryApi -- reconcile-attachment-storage --apply
```

On a deployed application use `dotnet InventoryApi.dll reconcile-attachment-storage --dry-run` /
`--apply` instead. Exactly one of the two flags must be given; a dry run writes nothing.

It reports, for each business, the records whose file is **missing**, the records whose attachment
size was **never recorded**, and the stored files **no record refers to**. An `--apply` writes one
thing and one thing only: a size filled in from the stored file itself. **It never deletes
anything** — an orphan file is reported for a human to decide about, because deleting a business's
receipt cannot be undone — and it never guesses: a size that cannot be read from storage stays
unknown and is reported as a missing file. It exits `0` when nothing needs attention and `1` when
something does, so an apply can be gated on a clean dry run. Running it again is safe and changes
nothing new.

The dry run writes nothing and reports what an apply would do. An apply copies each document to
`tenants/{businessId}/purchases|expenses/{storedFileName}`, where the business is read from the
record that owns the document, and verifies the copy by size and SHA-256 before counting it as
migrated. Reruns are safe and source documents are never deleted.

**Do not run this against production from this description.**
[docs/document-storage-rollout.md](docs/document-storage-rollout.md) is the canonical procedure:
prerequisites, how to read the report, what must be reviewed before an apply, how to verify, and
why the runtime provider is switched only afterwards.

#### Migrating the Nayax connection into the business

Every Nayax call uses the calling business's own encrypted connection record (issues #518 and #520),
and the credential this application has used so far is in global configuration (`NayaxLynx__*`).
Moving that configured operator id and access token into the existing business's record is a separate
human-invoked command, and until it has run that business has no Nayax connection:

```bash
dotnet run --project backend/InventoryApi -- migrate-nayax-connection --dry-run
dotnet run --project backend/InventoryApi -- migrate-nayax-connection --apply
```

On a deployed application use `dotnet InventoryApi.dll migrate-nayax-connection --dry-run` /
`--apply` instead. `--apply` must be typed explicitly; neither flag is a dry run, and both together
are refused.

The dry run writes nothing at all and reports the business it resolved, the configured operator id,
what is stored today and the change an apply would make. The access token is never printed, logged
or included in a message. An apply stores the credential encrypted and marks the connection
`Ready`, because that token is the one already in production use — the command never calls Nayax.
It is one transaction, committed only once both land, and every run reports a `Database` line
stating whether anything changed, so a failed apply leaves nothing behind and says so.
Re-running changes nothing — including the connection's status, which a re-run leaves exactly as it
is even when it is not `Ready`. It refuses, writing nothing, unless the ownership bootstrap is complete
for exactly one business, that business is the one `bootstrap-business` adopted, no *different*
credential is already stored, and a `NayaxTokenProtection` key is provisioned.

A new business does not need this command: an Owner connects their own Nayax account from the
application, through the wizard on Admin → Integrations (see
[Connecting Nayax (Admin page)](#connecting-nayax-admin-page)). The command exists for the business
whose credential was already in global configuration before that wizard did.

**Do not run this against production from this description.**
[docs/tenant-rollout.md § Migrating the Nayax connection into the business](docs/tenant-rollout.md#migrating-the-nayax-connection-into-the-business-issue-519)
is the canonical procedure, including the cutover window around issue #520, how to verify Nayax
afterwards and when the global settings may be removed.

#### Deciding which product an unresolved sale belongs to

Since issue #758 every stored sale records the local product it belongs to in its own column, and
the release's migration fills that column from the identifiers each sale already carries. It
deliberately refuses to guess in two cases: a sale whose stored number names one product directly
and a *different* one through a recorded provider id, and a sale no unambiguous product name covers.
A human names the product instead:

```bash
dotnet run --project backend/InventoryApi -- resolve-nayax-sale-products --dry-run
dotnet run --project backend/InventoryApi -- resolve-nayax-sale-products \
    --decide 4821=5001 --decided-by "A. Operator" --apply
```

On a deployed application use `dotnet InventoryApi.dll resolve-nayax-sale-products --dry-run` /
`--apply` instead. `--apply` must be typed explicitly; neither flag is a dry run, both together are
refused, and `--decided-by` is required with a decision because who decided is part of the audit
row.

The dry run writes nothing and lists, per business, every unresolved sale with its own key, its
transaction, its machine, its reported product name, both recorded candidate products where there
are any, whether it carries a historical cost today, and whether exactly one product's name
attributes it today. Those last two counts matter: **the application refuses to start while any sale
that is costed today has no product**, because such a sale would otherwise be dropped from every
future cost replay while still carrying COGS, and **while any sale a product's name attributes today
has no product**, costed or not, because every reader gave it that product until this release. The
command applies no migration, so it is runnable exactly when startup is refusing for either reason.

A decision names the sale by the key the dry run prints — there is no business flag, and the
business is the one the stored sale belongs to. Each applied decision fills only a product that is
still absent and appends an audit row with the candidates, the chosen product, who decided and when,
so re-running one writes nothing and can never move a sale off a product it already carries.

**Do not run this against production from this description.** See
[docs/architecture.md § The sale's own product](docs/architecture.md#the-sales-own-product-issue-758)
for the model, the migration steps and what the failure gate guarantees.

#### Grandfathering existing businesses onto Pro

[Business plans](#business-plans-free-starter-and-pro) are enforced only once a human has run this
command and it has recorded its completion marker (issue #966). It gives every business that was
already trading a **Pro grant with no end date**, so switching enforcement on cannot downgrade a
business that is using the application today:

```bash
dotnet run --project backend/InventoryApi -- grandfather-business-plans --dry-run
dotnet run --project backend/InventoryApi -- grandfather-business-plans --apply
```

On a deployed application use `dotnet InventoryApi.dll grandfather-business-plans --dry-run` /
`--apply` instead. `--apply` must be typed explicitly; neither flag is a dry run, both together are
refused, and there is **no flag for a cutoff, a plan or a business** — a command line that could
name a business would be a way to grant Pro to one that was not entitled to it.

- **The cutoff is recorded once and never moves.** The first apply fixes it from the clock before
  any grant is written; every later run reads it back and grants by it. A dry run records nothing
  and reports the instant an apply would fix.
- **Eligible:** a business created before the cutoff whose status at the cutoff was Active or
  Deactivated — trading, or offboarded after having traded. **Not eligible:** a pending or rejected
  application, anything created at or after the cutoff, and an application that was *decided* at or
  after the cutoff, which is how a pending application approved later still starts on Free even when
  the command is run again afterwards.
- **Safe to rerun.** Each business's grant is one transaction that inserts only if that business
  holds no grandfathered grant, with a unique index underneath it, and the completion marker is
  written last. An interrupted run therefore leaves some businesses granted, the cutoff fixed and
  **nothing enforced** — rerunning finishes the rest by the same cutoff. A run after completion
  changes nothing and says so. A grant's end date, if an owner sets one later, survives a rerun
  untouched.
- The dry run lists every business with the rule's decision about it and writes nothing at all; the
  apply lists what it granted, what already held a grant, and whether the completion marker was
  written. Neither prints a business name, a member or a credential.

It is independent of `Subscriptions__EnforcementEnabled` — this is the step run *before* enforcement
is switched on — and it writes nothing but those grants and its own record: no business's status, no
billing subscription and nothing else a business owns.

**Do not run this against production from this description.** See
[docs/architecture.md § Business subscriptions and plan entitlements](docs/architecture.md#business-subscriptions-and-plan-entitlements-issue-966)
for the state model, the entitlement rule and the rollout order.

Granting a plan to **one** business - a trial, a pilot site or a complimentary plan, without billing
it - is the Super Admin's separate action on the Businesses page, described under
[Super Admin businesses API](#super-admin-businesses-api).

#### Recording each business's connected-machine count

The daily job of issue #978. It records today's connected-machine count for every business whose plan
has its machines counted and that has not been counted yet today — the dated evidence the extra-machine
billing of issue #969 is charged from (see
[Connected machines and the Pro allowance](#connected-machines-and-the-pro-allowance-issue-978)):

```bash
dotnet run --project backend/InventoryApi -- snapshot-connected-machines
```

On a deployed application use `dotnet InventoryApi.dll snapshot-connected-machines` instead.

- **It takes no arguments at all** — no business, date, account series, count or limit. Which
  businesses are counted is the plan records' answer, the day comes from the clock, and the account
  series comes from each business's own stored connection.
- **It is bounded and makes at most one Nayax call per business**: at most 100 fleet reads per run,
  and reaching that bound is reported and logged. A business whose plan is not counted, whose Nayax
  connection cannot be used, or that has already been counted today costs no call at all.
- **Safe to run twice.** A second run on the same day records nothing. A business whose read failed is
  logged, nothing is stored for it, and the next run is the retry; its last complete count remains and
  is reported as stale with its age.
- **It applies no migration** and refuses to run against a schema with pending ones. Exit code `0`
  when the run completed, `1` when it was refused or any business's attempt failed.
- **Most counts come from ordinary use of the application**: a complete fleet read any page already
  makes records that day's count, so this job only tops up the businesses nothing read the fleet for.
  **Putting it on a schedule is a deliberate operator step** — the same App Service WebJob mechanism as
  the [database backup](#database-backup-and-restore) — and is not configured by this release.

### 2. How the frontend finds the API

Nothing to configure locally. `ConfigService` resolves the API base URL to `/api` whenever the application is served from `localhost` or `127.0.0.1`, and the development proxy forwards `/api` to <http://localhost:5000>. Any other origin is a deployed one and uses `apiBaseUrl` from `frontend/inventory-app/src/assets/config.json`, which holds the deployed Azure API URL. Do not edit that tracked file to switch between local development and deployment, and do not commit a personal endpoint.

### 3. Start the frontend

In a second terminal, from the repository root:

```bash
npm --prefix frontend/inventory-app ci
npm --prefix frontend/inventory-app start
```

Open <http://localhost:4200>. The `start` script builds the Tailwind stylesheet and starts the Angular development server.

### 4. Sign in

Opening the application signed out shows the public Restockroo landing page at `/` (see [The public landing page](#the-public-landing-page) below); everything else requires a Microsoft Entra sign-in. Click **Sign in** on the landing page or in the header; it redirects to Microsoft, and after sign-in redirects back to `/auth` (`AuthCallbackComponent`) before continuing to the requested page. `/auth` itself is intentionally public (not behind `MsalGuard`) so the redirect can complete; every other route except the landing page requires an authenticated session with the delegated `access_as_user` scope, enforced again by the API (`401` with no token, `403` without the scope).

A deployment that has a customer identity tenant configured offers a choice first, on the public `/sign-in` page: **Continue with Google or email** or **Sign in with a Microsoft account** — see [Signing in with Google](#signing-in-with-google-issue-711) and [Signing in with an email address and password](#signing-in-with-an-email-address-and-password-issue-712) below. With no such tenant configured, which is the state in source control, nothing changes: **Sign in** redirects straight to the workforce authority with no extra page and no extra click.

Since issue #957 `/sign-in` is drawn like the landing page, in the public layout with no application sidebar or header: a Restockroo header linked to `/home`, a **Back to home** link, and one card headed **Sign in to Restockroo**. Google and email stay one action because they are one Microsoft-hosted flow, where creating an account and resetting a password also happen. The card keeps the warning visible that each sign-in method is a separate account, which does not share business access with the others, so an existing user should sign in the way they did before. The longer verification-code and password-reset help is in a **Help with verification codes and passwords** section that opens on demand. Only the presentation changed: the authorities offered, the redirect each action starts, the return destination and the error handling are the same.

## Authentication configuration

Authentication uses Microsoft Entra ID (Azure AD): ASP.NET Core / `Microsoft.Identity.Web` validates bearer tokens on the API, and the Angular SPA signs users in with MSAL (`@azure/msal-angular`, `@azure/msal-browser`). See [docs/architecture.md](docs/architecture.md#authentication-and-authorization) for the full boundary description.

Non-secret configuration already committed to the repository:

- **API** (`backend/InventoryApi/appsettings.json`, `AzureAd` section): `Instance`, `TenantId` (`common`, multi-tenant), `ClientId` (the API app registration's public application ID), and `Scopes` (`access_as_user`). These identify the API for token validation; no client secret is used or required.
- **SPA** (`frontend/inventory-app/src/app/auth-config.ts`): the SPA app registration's client ID, the API's `api://<api-client-id>/access_as_user` scope, and the redirect URI (`<origin>/auth`). The same file holds `externalIdSettings`, the second authority's four identifiers, **empty** in source control — see [Signing in with Google](#signing-in-with-google-issue-711).

Human-controlled Entra portal setup this configuration depends on (not part of this repository, and not something an agent may change):

- App registrations for the API and the SPA, with the API exposing the `access_as_user` scope and the SPA's platform configured for the redirect URIs it actually uses (`http://localhost:4200/auth` for local development, and the deployed Static Web App origin's `/auth` for production).
- The SPA registration's API permissions granting delegated `access_as_user` access to the API app registration.

### Customer identity: a second trusted issuer (issue #710)

The API can also validate bearer tokens from **one** Microsoft Entra External ID tenant beside the workforce sign-in above, which is the token-validation half of the approved design in [docs/identity-provider-decision.md](docs/identity-provider-decision.md) (issue #709, under #708). Everything is in `backend/InventoryApi/Auth/ExternalIdentity/`, and [docs/architecture.md § Trusted issuers and the two bearer schemes](docs/architecture.md#trusted-issuers-and-the-two-bearer-schemes-issue-710) describes the boundary in full.

**What has shipped, and what has not.** The API accepts tokens from a configured External ID tenant and resolves the same validated `(tid, oid)` actor it always has; a customer identity that holds no membership gets `403` and no business data. Since issue #711 the Angular application offers the Google journey against that tenant too, and since issue #712 the local email-and-password journey through the same hosted flow — see [Signing in with Google](#signing-in-with-google-issue-711) and [Signing in with an email address and password](#signing-in-with-an-email-address-and-password-issue-712) below. Identity linking is **deferred** (#713), so the same person signing in a different way is a different actor with no membership and no inherited access. Issue #714 has completed the automated half of the consolidated validation — the provider × account-state × role matrix, identity separation, the threat-model rows and the rollback rehearsal — and recorded every hosted-provider check as **not verified**: see [Rollout verification, staging smoke tests and rollback](#rollout-verification-staging-smoke-tests-and-rollback-issue-714) below. **Enabling the new sign-in in production is still gated on those human staging checks and a recorded go/no-go decision.**

**Configuration, all of it non-secret, none of it committed.** The `ExternalId` section in `appsettings.json` ships with three **empty** identifiers, which is both the fail-closed state and the rollback lever: with them empty the second bearer scheme is not registered at all, every External ID token is rejected, and workforce sign-in is untouched. A half-filled or malformed section means the same thing and is logged as a startup error. Supply the values per environment with `dotnet user-secrets` locally or application settings in Azure:

- `ExternalId__Instance` — the tenant's authority host, `https://<tenant-subdomain>.ciamlogin.com/`
- `ExternalId__TenantId` — the External ID tenant's GUID
- `ExternalId__ClientId` — the GUID application ID of the API app registration **in that tenant**, which is a different registration from `AzureAd:ClientId` because an app registration belongs to one tenant and each scheme validates only its own audience

None of these is a credential: the API validates tokens with the authority's published signing keys and the SPA is a public client using authorization code with PKCE, so neither holds a client secret and no Key Vault reference is needed — the same classification the `PlatformAdmin` pair already has. Reverting to workforce-only sign-in is clearing these settings: no deployment, schema change or data step is involved, and memberships created through the new issuer are retained and work again if the settings are restored.

Human-controlled portal setup this depends on (no agent may perform any of it, and the API deliberately does not check that it was done):

- Create the External ID tenant, register the API application in it with an `access_as_user` delegated scope, and register the SPA as a PKCE public client with exactly the redirect and post-logout URIs in use.
- Configure the Google identity provider in the tenant — its OAuth client secret lives in the provider, never in this repository — and the local email/password sign-up with its verification and password reset.
- Create the sign-up/sign-in **user flow**, enable the intended providers inside it, and associate the SPA application with it. None of those three steps is implied by the registrations above, and the journeys simply do not appear without them.
- Decide each registration's accepted access-token version; the API pins both audience spellings (`<client-id>` and `api://<client-id>`), so either one validates.
- Keep the administration of that tenant separate from the `PlatformAdmin` pair, which stays a workforce identity: the same object ID presented from the customer identity tenant is a different actor and reaches no platform-admin endpoint.

### Signing in with Google (issue #711)

Google sign-in is the **hosted** customer identity journey: the SPA redirects to the configured External ID tenant's own sign-up/sign-in user flow, where a human has enabled Google as a built-in identity provider, and the API receives a token that *tenant* issued. The application never talks to Google, never exchanges a Google ID token, sends no provider hint or query parameter of its own, and reads nothing from the upstream Google subject — the actor is the validated `(tid, oid)` pair exactly as for every other sign-in. Design: [docs/identity-provider-decision.md](docs/identity-provider-decision.md) § 4 journey C and § 9; boundary: [docs/architecture.md § The sign-in choice and the Google journey](docs/architecture.md#the-sign-in-choice-and-the-google-journey-issue-711).

**What a person sees.** With a customer identity tenant configured, a signed-out visitor reaches the public `/sign-in` page instead of being redirected automatically — the authority has to be chosen explicitly, once, before any redirect — and it offers the customer identity action, **Continue with Google or email** (named for both journeys since issue #712 added the local journey to the same hosted flow, and shortened by issue #957), beside **Sign in with a Microsoft account** (a work, school or personal Microsoft account: the `common` authority's app registrations accept all three). The choice is remembered in `localStorage`, so a reload, a new tab and a token refresh go back to the authority that issued the session; signing out forgets it, so the next person chooses again. Each tab pins its own snapshot of that choice for as long as it is open, so a second tab changing it cannot alter the authority this tab is already signing in against. A cancelled or failed hosted sign-in returns to `/sign-in` with the reason and the same two choices and starts no further redirect by itself. The page the visitor originally asked for is carried across in the tab's `sessionStorage` and handed to MSAL as the redirect start page; it comes from the router state, never from a URL parameter, and an absolute, protocol-relative, `/auth` or `/sign-in` destination is refused. **It survives a cancelled or failed attempt**, so pressing an action again returns the person to the page they were going to — an invite acceptance page included — rather than to the dashboard; it is released when a sign-in completes, and trying again never extends its 15-minute window.

**Google sign-in grants nothing by itself.** A Google identity that nobody invited lands on the existing welcome page with `NoMembership`, and that page now says plainly that each way of signing in is a separate account: a member who normally uses their work account and then signs in with Google gets a *new* actor with no membership, no role and no inherited access. Nothing is linked by a matching email address — an address is display-only data here and decides nothing — and Google sign-in on its own never redeems an invite. Joining a business is still the existing invite or onboarding journey.

**Configuration, non-secret, and empty in source control.** The four SPA identifiers live in `externalIdSettings` in `frontend/inventory-app/src/app/auth-config.ts`, empty for the same reason the API's `ExternalId` section is: empty is the fail-closed shipped state and the rollback lever. While any of them is empty or the instance is not an `https` authority, the second authority does not exist — no Google action is rendered, no second MSAL instance can be built, and the application signs in exactly as it did before. Fill them in per deployment (they are build-time values, so changing them is a build):

- `instance` — the tenant's authority host, `https://<tenant-subdomain>.ciamlogin.com/`
- `tenantId` — the External ID tenant's GUID
- `clientId` — the **SPA** app registration in that tenant, which is a different registration from the workforce SPA client id above
- `apiClientId` — the **API** app registration in that tenant, whose `access_as_user` scope the SPA asks for

MSAL partitions its token cache by client id and authority, so those distinct client ids are what keep the two authorities' caches apart; the token request names `openid` and `profile` explicitly, because External ID issues `oid` and `tid` only when `profile` is asked for and they are the whole identity the API reads. None of these values is a credential: the SPA is a PKCE public client and holds no client secret, and the Google OAuth client secret exists only inside the External ID tenant's provider configuration — never in this repository, a pipeline or an application setting.

Human-controlled setup this depends on, in addition to the External ID portal work listed above (no agent may perform any of it):

- Create the Google OAuth client in Google Cloud and register its client id and secret **in the External ID tenant**.
- Enable Google inside the tenant's sign-up/sign-in **user flow** — enabling a provider at tenant level is not the same as offering it in a flow — and associate the SPA application with that flow.
- Register `http://localhost:4200/auth` and the deployed Static Web App origin's `/auth` as redirect URIs, and the origins as post-logout URIs, on that tenant's SPA registration.

**Staging verification before production activation (human, not automated).** Configuring a provider is not the same as a person being able to use it, so a human confirms in staging, with a test identity they created, that: `/sign-in` offers the customer identity action; the hosted page actually offers Google and a new Google user can complete the journey end to end; the SPA returns to `/auth` and acquires an API access token for `access_as_user`; the decoded token carries a GUID `oid` and the External ID `tid`; the API accepts it and the identity resolves **no** membership, so every business endpoint answers `403` and the application shows the welcome page; cancelling on the hosted page returns to `/sign-in` with the cancellation explained and no loop; an invite link opened before signing in still lands on `/invite` afterwards and is redeemable within its window, **including when the first attempt was cancelled and the second one completed**; and existing workforce sign-in is unchanged on the same build. A journey that is configured but not offered, or offered but not completable, is a failed phase. Record the result with the design's § 13 checklist; the consolidated run across both issuers is [docs/identity-rollout-verification.md § 3](docs/identity-rollout-verification.md#3-the-staging-smoke-tests-a-human-runs), which is the checklist to work from.

MSAL's protected-resource map (`buildProtectedResourceMap` in `auth-config.ts`) is built from `ConfigService.apiBaseUrl`, the same value every HTTP call uses (see [above](#2-how-the-frontend-finds-the-api)), so the bearer token attaches correctly to `/api/*` locally and to the deployed absolute API URL in production, without any code or configuration change between environments. `ConfigService` loads `assets/config.json` on the raw `HttpBackend` rather than the intercepted `HttpClient`, so the MSAL interceptor cannot be constructed — and cannot capture a stale API base URL — before that load has finished.

Protected business documents (purchase documents and operating-expense supporting documents) are stored outside the API's web root and are served only by the authenticated `/api/purchases/{id}/file` and `/api/operating-expenses/{id}/attachment` endpoints. The API registers no static-file middleware, so these documents have no anonymous URL; the frontend fetches them through `HttpClient` and renders them from a temporary object URL.

Azure Static Web Apps direct navigation (including the `/auth` redirect landing) is handled by `frontend/inventory-app/src/staticwebapp.config.json`, which rewrites unmatched paths to `/index.html` so the Angular router — not a platform 404 — handles them.

### Signing in with an email address and password (issue #712)

A customer can register and sign in with **any deliverable email address**, a custom company domain included, without holding a Microsoft or Google account. It is the *same* hosted journey as Google sign-in above — the same External ID tenant, the same user flow, the same SPA and API registrations — so there is no extra identifier, flag or setting to fill in, and the one customer identity action on `/sign-in` names both: **Continue with Google or an email address**. Design: [docs/identity-provider-decision.md](docs/identity-provider-decision.md) § 4 journey D and § 16; boundary: [docs/architecture.md § The local email and password journey](docs/architecture.md#the-local-email-and-password-journey-issue-712).

**No credential ever reaches this application.** There is no password field, registration form, verification page or recovery link anywhere in it, and no password, password hash, verification code, reset code or recovery token is received, stored, logged or put in a URL. Creating the account, verifying the mailbox with the one-time code Microsoft emails, signing in, and resetting a forgotten password all happen on the provider's hosted pages; the sign-in page says so, says Inventory Manager never sees a password and cannot reset one, and tells a person whose verification or reset code has expired to start again from `/sign-in`, where the hosted page issues a new one. A refused sign-in shows one generic application-authored sentence and never the provider's own error text, so it does not disclose whether an address has an account here.

**Signing in grants nothing by itself.** A newly verified customer who nobody invited lands on the existing welcome page with `NoMembership` and `403` from every business endpoint, exactly like a Google or workforce stranger; joining a business is still the invite or onboarding journey. Nothing is linked by a matching mailbox: registering an address an existing member already uses creates a separate actor with no membership, no role and no inherited access, a duplicate registration of one address is a second actor again, and an email change or password reset at the provider changes no `(tid, oid)` and therefore cannot move or widen a membership.

#### Provider controls, and what a human must configure

Recorded from the design's § 13 verification (checked against Microsoft's published External ID documentation on 18 October 2026, not against a provisioned tenant). These are the controls to set while configuring the user flow, and the gaps to accept or cover deliberately:

| Control | What the provider offers | Human action |
| --- | --- | --- |
| Mailbox verification | A one-time email code during sign-up, **before the account exists**, so an unfinished registration produces no account and no token. "Email with password" is the default local option | Enable email with password in the user flow and confirm the verification step is on |
| Password handling | Stored and checked entirely by the provider; this application never receives one. **Password policy configurability is unconfirmed** | Check the policy the tenant actually applies and record it |
| Account recovery | Self-service password reset sends a code to the mailbox; SMS reset is available as a paid add-on | Enable self-service password reset in the flow and verify the entry point on the hosted page |
| MFA | Email one-time code (no extra charge), SMS (paid add-on, billed per message by country), and passkeys for local accounts | Decide which methods are enabled, and record the decision with the staging result |
| Lockout / throttling | Smart lockout and basic HTTP protections are on by default. **There is no built-in per-IP throttling setting** | Accept the defaults, or put a WAF in front; raise per-IP limiting as its own issue if the data warrants it |
| Bot / sign-up fraud | **Not built in.** It needs a third party (Arkose Labs, HUMAN Security) or a WAF (Cloudflare, Akamai, Azure WAF) | Decide explicitly before enabling self-service onboarding, which stays `false` (`SelfServiceOnboarding:Enabled`) until then |
| Cost | Free for the first 50,000 monthly active users; add-ons (SMS included) have no free tier | None at the current single-business scale |

**Residual limitations, stated rather than worked around.** Bot and sign-up fraud protection and per-IP throttling are not provided and are not implemented here, so a free identity remains cheap to create — which is why `SelfServiceOnboarding:Enabled` stays off and the per-identity application rate limit is not relied on as an anti-abuse control (design § 11, T6). The password policy in force is whatever the tenant is set to and has not been confirmed. Nothing above is verified against a real tenant by this change: it is the documented capability set a human configures and then confirms in staging.

**Staging verification before production activation (human, not automated).** With a test identity they created, a human confirms in staging that: the hosted page offers the email-with-password option; a new customer can register, receive the verification code and complete sign-up end to end; the SPA returns to `/auth`, acquires an API access token for `access_as_user`, and the decoded token carries a GUID `oid` and the External ID `tid`; the API accepts it and resolves **no** membership, so every business endpoint answers `403` and the application shows the welcome page; signing in again with the same address and password works, and a wrong password shows the provider's own message on its own page and never reveals anything here; **forgot password** reaches the hosted reset journey, a reset code arrives, and an **expired or already-used** code is refused on that page — after which starting again from `/sign-in` issues a new one; cancelling the hosted journey returns to `/sign-in` with the cancellation explained and no loop; an invite link opened before signing up still lands on `/invite` afterwards and is redeemable inside its 15-minute window, which the longer registration round trip makes the case worth timing (extending that window would be its own reviewed decision); the same mailbox used through Google or a workforce Microsoft account produces a *separate* account with no membership and no inherited role; and existing workforce sign-in is unchanged on the same build. A journey that is configured but not offered, or offered but not completable, is a failed phase. Record the result with the design's § 13 checklist; the consolidated run across both issuers is [docs/identity-rollout-verification.md § 3](docs/identity-rollout-verification.md#3-the-staging-smoke-tests-a-human-runs), which is the checklist to work from.

**Authentication identifies a person; business ownership decides what they may see.** Accepting sign-ins from multiple Microsoft Entra tenants (`TenantId: "common"`) only authenticates a user. Data isolation is a separate boundary, established by issue #64: the validated `(tid, oid)` claim pair is mapped to an application-owned **Business** through explicit `BusinessMembership` rows, and every tenant-owned read and write is scoped to that business centrally in `AppDbContext`. A signed-in account with no usable membership receives `403` and no business data — it fails closed rather than falling back to "see everything". The business ID is never accepted from route, query, form, or JSON input. There are exactly three endpoints such an account may still reach, all on a reviewed allowlist the build enforces, and none names an existing business or reads business data. `GET /api/me/account-state` (issue #523) answers only with the state that account is in — `Member`, `NoMembership`, `ApplicationPending`, `ApplicationRejected`, `MembershipRevoked`, `BusinessDeactivated` or `MembershipAmbiguous` — so the application can show the right screen instead of a bare error. `POST /api/invites/redeem` (issue #532) takes an invite token an Owner created and nothing else, and creates that account's **own** membership in the business the invite belongs to, with the role the invite grants; it returns only that role, and an unusable token and an account that already belongs somewhere are both refused with one fixed sentence. `POST /api/onboarding/business-applications` (issue #541) is the self-service application described [below](#starting-a-business-self-service-onboarding), behind a flag that is off by default. The `403` every other endpoint returns is unchanged, for all three of those callers.

**The Angular application reads that state before any business call and shows the screen it calls for** (issue #529). `Member` reaches the page asked for. `NoMembership` goes to a welcome page offering **I have an invite link** — paste the link or the code from it, or open the link directly — and, when `onboardingEnabled` is true, **Start a new business**, which opens the business application form at `/apply` (issue #543): business name, contact email (starting as the signed-in account's own address), optional ABN, a stated yes or no for GST registration, a time zone chosen from the browser's IANA list, and the Australian state or territory the business trades in — one of NSW, VIC, QLD, SA, WA, TAS, ACT or NT, chosen rather than defaulted, because a time zone cannot tell NSW from the ACT (issue #933). The form checks each field with the API's own rules and sentences before sending anything, shows any refusal the API returns (an account that already belongs to a business, a different application already waiting, the rate limit, or onboarding switched off), and after a recorded application re-reads the account state and lands on the pending-approval page. `ApplicationPending` goes to a pending-approval page with a **Check again** button that re-reads the account state; once approved, it refreshes the capability cache too and loads the app as Owner. `ApplicationRejected` goes to a rejected page that shows the Super Admin's own recorded reason when there is one, and offers the same invite-link option plus **Apply again**, which opens the same form, when `onboardingEnabled` is true. `BusinessDeactivated`, `MembershipAmbiguous` and `MembershipRevoked` go to one "access unavailable" page that names no business — it says to contact the business Owner for the first and third, and support for the second. Because a revoked or released person is the one case `GET /api/me/account-state`'s `onboardingEnabled` flag can make eligible, that page also offers **I have an invite link** always and **Start a new business** only when `onboardingEnabled` is true, matching the API's own eligibility rule. A `403` from any other member endpoint shows "you don't have access to this" through the application's toast, except on one of those destination routes or `/invite/:token`, which already explain the situation themselves. None of this is a boundary: `BusinessScopeMiddleware` refuses every business call the same way regardless of what the router shows.

### Rollout verification, staging smoke tests and rollback (issue #714)

Everything above describes what the application does with a customer identity tenant. **Whether this deployment may switch one on in production is a separate question, and it is answered in [docs/identity-rollout-verification.md](docs/identity-rollout-verification.md)** — the consolidated verification matrix, the staging smoke tests a human runs, the phased enablement, the observability signals, the incident and rollback procedure, and the production go/no-go checklist.

**Three kinds of evidence, never pooled.** Synthetic API/JWT tests say what the API accepts, refuses and resolves. Synthetic browser journeys say what the SPA decides, stores and asks MSAL for. Only a human running a hosted journey in staging can say that a tenant exists, that Google or an email address is actually offered in its user flow, and that a real token carries the claims the design assumes. Each row of that matrix declares its kind and is `passed`, `failed` or **`not verified`**, and no agent may record a human row.

**Where it stands (18 October 2026).** Every synthetic row is `passed` and runs inside `scripts/validate.sh`: the provider × account-state × role matrix across both issuers (invitation redemption, applicant pending and rejected, member access, business deactivation, membership revocation, role capabilities and the Super Admin boundary), identity separation in place of the deferred linking feature (identical mailboxes across providers, one object id in two tenants, the upstream Google subject, duplicate registration, the already-a-member conflict), the threat-model rows T1, T2, T3, T6, T8, T9 and T10, the browser journeys (provider switching and cache separation, external return URLs, an interrupted invite journey, a cancellation, a disabled or misconfigured route), and the design's § 15 rollback rehearsal — which retains memberships and live invites, restores access unchanged, keeps the legacy Super Admin recovery route working while the new sign-in is off, and leaves no bearer token, invitation token or mailbox in the logs. Every hosted-provider row is **`not verified`**, because no External ID tenant is provisioned for these deployments and no agent may create one.

**Rollback, in one sentence.** Clear `ExternalId__Instance`, `ExternalId__TenantId` and `ExternalId__ClientId` and restart — a settings edit on the build already deployed, no deployment of its own — and the second bearer scheme is not registered, every External ID token is refused, workforce sign-in is untouched, and the memberships, roles and outstanding invites created through the new issuer are retained and work again the moment the settings are restored. Removing the customer identity action from the browser is the SPA's four identifiers, which are build-time values and therefore a build and a deployment **of the current commit**; it is cosmetic by comparison, because the API half is what stops access. If the code itself must change, that is a focused forward fix against the current base, not a redeployment of an earlier build: this design's lack of a migration says nothing about whether an earlier whole-application build matches today's database, and [docs/identity-rollout-verification.md § 6.2](docs/identity-rollout-verification.md) states what a human must check first. Rollback cannot recover a customer identity user's access — there is no linking, so the answer for somebody who must keep working is an invitation redeemed by a workforce identity they control.

### Starting a business: self-service onboarding

A business is created in one of two ways. The legacy way is the human-invoked `bootstrap-business` command, which adopts the one existing Australian vending operation and creates it already `Active`; see [docs/tenant-rollout.md](docs/tenant-rollout.md). The other, since issue #541, is a **self-service application** behind the `SelfServiceOnboarding:Enabled` flag:

1. Somebody signs in for the first time. `GET /api/me/account-state` reports `NoMembership`, and its `onboardingEnabled` field reports whether this deployment allows an application at all.
2. They `POST /api/onboarding/business-applications` with the business's name, contact email address, optional ABN, GST registration, IANA time zone and state or territory (issue #933 — one of NSW, VIC, QLD, SA, WA, TAS, ACT or NT). All of those except the ABN are required, the GST registration must be *stated* — a missing field is a `400`, never a recorded `false` — and a state outside those eight is a `400` too, because a business's state is recorded rather than inferred from its time zone or address.
3. The server creates a **`Pending`** business with those details and makes the applicant its active **Owner**, in one transaction. It decides everything the request did not: the business's identifier, its status, the applicant's role, and who the applicant is (the validated `(tid, oid)` claim pair of their own token). The response carries only the application's name, status and submission instant — no business identifier.
4. The business is **not usable yet.** Only an `Active` business resolves, so the applicant's very next request is answered with the same `403` as before, and `GET /api/me/account-state` now reports `ApplicationPending` so the application can show them a waiting screen. The new business starts completely empty, with no Nayax connection (`NotConfigured`).
5. Approving or rejecting an application is a Super Admin decision, made through the [Super Admin businesses API](#super-admin-businesses-api) below (issue #544). Approving sets the business `Active`, so its Owner's next request is served; rejecting records a reason, which `GET /api/me/account-state` reports back to that applicant alone as `ApplicationRejected`, and releases the applicant's membership. The rejected application is kept, and the person may apply again, which creates a new business.

Who may apply: an account with **no active membership anywhere** — never a member, released, or previously rejected. An account that already holds an active membership in any business, including a `Pending` or `Deactivated` one, gets `409`. Submitting the identical application again while it is pending returns the application already waiting (`200`, same submission instant) so a lost response cannot create a second business; submitting *different* details is `409`, because changing details an operator is reviewing is a different request. Applications are rate-limited per applicant, and each one emits a structured audit event carrying the actor and the new business id and **no name, email address or ABN**.

**The flag is `false` in source control and must stay off in production until a human has verified the release gate** — see [docs/tenant-rollout.md § Enabling self-service onboarding](docs/tenant-rollout.md#enabling-self-service-onboarding-issue-541). With it off, the endpoint answers `404` and `onboardingEnabled` is `false`.

The journey a second live business needs is now complete end to end. The application form exists (issue #543, `/apply`, above), and the approval **user interface** of issue #510 does too: the Super Admin's own Businesses page (issue #547, see [Super Admin businesses API](#super-admin-businesses-api) above) approves, rejects, deactivates and reactivates a business and releases a member of a deactivated one. The Nayax credential is no longer a blocker either: since issue #520 every Nayax call uses the current business's own encrypted operator id and token, with no global fallback (see [docs/architecture.md § Per-business Nayax credentials](docs/architecture.md#per-business-nayax-credentials-issue-520)). An Owner can test a pasted operator id and token, save the tested pair and re-test the stored one (`/api/settings/nayax-connection/test`, `.../save`, `.../retest`; see [docs/architecture.md § Saving a tested connection and re-testing it](docs/architecture.md#saving-a-tested-connection-and-re-testing-it-issue-537)), and drives it from the connection wizard on Admin → Integrations (see [Connecting Nayax (Admin page)](#connecting-nayax-admin-page)); the connection banners of issue #539 are on the screens that depend on Nayax — the machine list and machine detail, Sync Restock, the site list and a site's products, and the pick list — so a member who cannot reach that wizard still sees why those screens have no Nayax data. The business that is live today had its credential moved in by the human-run [`migrate-nayax-connection`](#migrating-the-nayax-connection-into-the-business) command. See [docs/tenant-rollout.md](docs/tenant-rollout.md) for the bootstrap procedure and [docs/architecture.md](docs/architecture.md#tenant-ownership-issue-64) for the design.

## The public landing page

A visitor who is not signed in sees the **Restockroo landing page** at `/` (issue #954): what the
application does, an example dashboard drawn from clearly labelled sample data, the optional Nayax
integration, a three-step workflow, pricing and FAQs. It makes no request to the API at all, so
opening or refreshing it never asks for a sign-in or reads any business, account or Nayax data.

- **Sign in** uses the existing sign-in: the `/sign-in` chooser when a customer identity authority
  is configured, otherwise a redirect straight to Microsoft that returns to `/`.
- **Get started** opens the business application at `/apply` (see
  [Starting a business](#starting-a-business-self-service-onboarding)), taking the visitor through
  sign-in first. While `SelfServiceOnboarding:Enabled` is off, a new account lands on the welcome
  page instead, exactly as before.
- **Signed in**, `/` is still the dashboard. The landing page stays reachable at `/home`, where the
  **Open app** button returns to `/`.

Core inventory - products, stock, purchases, suppliers and machine records - works without Nayax;
the page presents Nayax only as an optional connection for supported sales data and the reports
built on it. The page has **no Privacy Policy, Terms of Service or Contact link**, because none of
those pages exists yet; add them to its footer once they do. The approved design and every wording
change are recorded in [docs/design/restockroo-landing-page.md](docs/design/restockroo-landing-page.md).

### Pricing on the landing page (issue #971)

A **Pricing** link in the header, mobile menu and footer navigation scrolls to a section before the
FAQs with three static plan cards - `landing-content.ts`'s `LANDING_PLANS`, read only by the page,
never fetched - matching the catalogue [issue #965](https://github.com/cristhyanc/InventoryApp/issues/965)
agreed: **Free** (A$0, 1 GB of purchase/expense attachments, every manual feature - Dashboard,
Machines, Inventory, Purchases, Supplier orders, Suppliers, Expenses and manual Pick List entry -
with no supplier comparisons and no reports), **Starter** (A$7 per business per month; Free plus
20 GB of attachments, daily cheapest-supplier comparisons, and the Bookkeeping and GST reports with
their CSV/XLSX export) and **Pro** (A$15 per business per month; Starter plus the Nayax connection
and everything that needs it today - Sites, Site Commissions, the Daily, Transactions,
Reconciliation, Machines and Products reports, machine latest sales, Sync Restock, dashboard sales
refresh, the Nayax sales file import and the calculated Pick List matrix - with 5 connected
machines included and A$2/month per additional connected machine).

**Coming soon, not purchasable.** Paid enforcement, billing and the plan catalogue API (issue #965's
other child tasks) are not live. Free carries an "Available now" badge; Starter and Pro carry a
"Coming soon" badge. Every plan card's action is the same **Get started**/**Open app** as the rest
of the page - the existing business application journey - so there is no checkout, trial promise or
waitlist, nothing a visitor could mistake for a live purchase. The Pro card also states explicitly
that plan entitlement and a *usable* Nayax connection are separate: Pro will include the Nayax
connection, but connected features still need the business's own working Nayax credentials, exactly
as today.

**Existing copy reconciled with the plan catalogue.** The "Optional sales connection" and "Optional
integration" facts in the Nayax section now say Nayax will be a Pro plan feature once paid plans go
live (it stays open to every business today, and connecting it stays optional even on Pro), and the
former "Inventory and cost reporting" feature card is "Inventory value at cost" naming the dashboard
figure it actually shows, so it does not read as the gated Reports area. The FAQ "Do I need Nayax?"
is unchanged and stays true either way. Three FAQs were added covering when Starter/Pro become
purchasable, what the storage allowances will do once the paid plans launch, and that the extra
connected-machine charge only starts once paid plans and the Nayax connection are both live. GST tax
treatment of the advertised prices is one of #965's open launch decisions and is intentionally not
asserted here until it is decided.

**Plan limits are still described as future behaviour, because none of them applies to a visitor
today.** The attachment quota is implemented (issue #968) but applies only once a deployment
switches plan enforcement on, which the shipped state does not; the extra connected-machine charge
needs billing (issue #969) that is not live. So the section's lead paragraph says the storage
allowances and the connected-machine charge start when the paid plans launch and that nothing is
limited today, the Free card repeats it for its 1 GB allowance, and the storage FAQ describes the
80% warning and the 100% upload pause only as what will happen from launch — which is exactly what
[Attachment storage](#attachment-storage) does once enforcement is on. The page must never present a
limit, a paused upload or a per-machine charge as today's behaviour;
`landing-page.component.spec.ts` holds that as a copy regression. (Recording a purchase without
attaching a document has since shipped, issue #976, and the page claims nothing about it either
way.)

## The dashboard and the menu

Signed in, `/` is the **Dashboard** in the Restockroo design (issue #972). It changes how the page
and the menu look, not what any figure means: no calculation, endpoint, permission or lock rule
changed, and every "Unavailable" still means unavailable, never zero.

- **Four summary cards**: Sales this week, Machines needing refill, Products needing ordering and
  Business-owned inventory at cost. The inventory card says separately that its storage-unit count
  excludes stock in machines.
- **Needs attention** collects what is waiting for you: empty selections, machines needing refill,
  products needing ordering and the [restock sync reminder](#machine-restock-sync-reminder)
  ("Recorded refills haven't been synced yet."). Its count is the number of alerts actually shown,
  and the panel is hidden when there is none. It is two columns on a wide screen and one on a phone,
  and it offers only the two existing actions, **Open pick list** and **View needs ordering**. The
  reminder keeps its 14-day window and its link that opens Sync Restock from the oldest pending date.
- **Site and machine performance** is one card (issue #987): each site is a summary row - its name,
  how many machines it has, its stock level in words as well as in colour, its low and empty
  selections and its sales - and an expand button under the site's name reveals that site's machines
  indented directly beneath it. Sites start expanded, collapsing one leaves the others alone, and a
  refresh keeps whichever groups you closed. Machines with no site, or at a site that was not
  returned, come last under **Other machines**, and a site the fleet read returned no machine for
  stays visible saying so. Every machine appears exactly once.
  - **One period at a time, shared by both layouts.** The Today / This week / Last week / Month to
    date buttons above the table choose the period for every row, and the sales column names it. The
    comparison column shows the published "vs same time last week" figure while This week is
    selected and says there is no comparison for the other periods, because that is the only
    comparison these reads publish. Sites publish sales for Today and This week only; for the other
    two a site row says so instead of showing a figure.
  - **Stock is a site figure.** The stock level and the low/empty counts come from the sites read,
    which is the only place they exist. A machine row shows them as unavailable - never the site's
    percentage repeated, and never a percentage worked out in the browser - and the card says why.
  - **Actions are the existing ones**: **Products** opens that site's products, **Details** opens
    that machine, and a machine this business has no local record of gets no Details link at all
    rather than an invented one.
  - **One totals row for the fleet**, over every machine the read returned, so collapsing a group
    never changes it and no site figure is ever added into it. A period total is shown only when
    every machine's figure for that period is known, and is "Unavailable" otherwise.
  - The sites and the machines are still read separately: if one read fails the card keeps the
    other's results and says which one is unavailable, and a failed read is never shown as an empty
    business. On a phone the same hierarchy is expandable site cards holding machine cards.
- **Reorder priorities** shows the first five products of the reorder list, in the list's own order,
  and a link naming how many there really are ("View all 7 products needing ordering") to Products →
  Needs Ordering. Its columns are product, storage stock, machine need, need to order, the last and
  cheapest purchase, and **Cheapest supplier** (issue #981). Last and cheapest purchase are
  **historical costs from your own purchases**; Cheapest supplier is the lowest price your own
  suppliers **currently list** per unit - observed prices for purchasing guidance, not costs you have
  paid - and the section says both. The supplier column is the same cell and popup as
  [on Needs Ordering](#the-cheapest-supplier-on-needs-ordering): one request for the five products,
  shown to everyone who sees the dashboard, unavailable in a read-only support view, and a failed
  read says so with a **Retry** without affecting the rest of the dashboard.

**The menu** is grouped under four headings: **Operations** (Dashboard, Pick List, Sites,
Machines), **Inventory & purchasing** (Inventory, Purchases), **Finance & reports** (Expenses,
Reports) and **Settings** (Admin). A heading appears only when you have something under it. A
platform administrator also sees a separate **Super admin** heading over Platform Diagnostics,
Businesses and Supplier Presets; nobody else does. Locked entries are shown exactly as before (see
[The menu without a usable provider](#the-menu-without-a-usable-provider)).

**On a phone** a bottom bar offers Dashboard, Pick List, Sites and Inventory — only those your role
already has in its menu — and **More**, which opens the whole menu as a sheet from the bottom (the
top bar's menu button opens the same sheet). While it is open the page behind it cannot be clicked,
focused or reached by a screen reader. The sheet scrolls to its last entry, keeps a **Close** button
at the top, keeps clear of the phone's home indicator, keeps keyboard focus inside itself, closes
with Escape, Close or a tap on the dimmed page, and returns focus to the button that opened it.

## Roles and capabilities

Ownership decides *whose* data a member sees; their **role** decides what they may do with it. Every `BusinessMembership` carries one of three roles (issue #521):

| Role | What it may do |
| --- | --- |
| **Owner** | Everything, including the Nayax integration, data repairs, members, roles and the business record itself |
| **Manager** | Everything operational, plus the financial figures: dashboard financials, reports, operating expenses, imports, site commissions and supplier GST defaults |
| **Operator** | Day-to-day work only: the dashboard without financial figures, the pick list, inventory (products, take inventory, categories, stock history, machines including Sync Restock, sites) and purchasing (purchases, supplier orders, suppliers) |

- A signed-in member reads their own role and the capability names it grants from `GET /api/me/access`, which also returns their business's name and time zone. It describes the caller alone — never another member or another business — and a role change applies on their next request. A platform administrator inside a [support session](#super-admin-support-sessions) is the one caller it answers with no role at all: they hold none, and the capabilities it reports for them are that session's own read-only set.
- A role is first set when the membership is created, by `bootstrap-business` (its `Role` option is optional and defaults to `Owner`; see [docs/tenant-rollout.md](docs/tenant-rollout.md#the-members-role-issue-521)). Existing memberships were backfilled to `Owner`, so no one's access changed when roles arrived. Changing a role afterwards is the Owner-only member management below. The only other place a role arrives from client input is an Owner creating an invite link, which records the role the redemption will grant and changes nobody's access by itself; both check the role's published **name** against the same vocabulary, and no endpoint anywhere accepts a stored role value.
- A stored role the application does not recognise **denies access** rather than falling back to the least privileged role.
- **Every endpoint is enforced by capability** (issues #525 and #526). The capability a role must grant is checked before the request reaches the endpoint, and a refusal is a plain `403` that describes neither the capability nor the role. What each role can reach:
  - **Operator** — the Dashboard (including its sales refresh) and the Pick List; inventory: products, take inventory, categories, stock and stock history, machines including Sync Restock, and sites; purchasing: purchases, supplier orders and suppliers. They may record a purchase but not change a supplier's GST defaults (`Suppliers.ManageGstDefaults`), and they reach no report, expense, import, site commission, setting or maintenance tool at all.
  - **Manager** — everything an Operator reaches, plus a supplier's GST defaults, operating expenses and their attachments, every report and its CSV/XLSX export, Admin → Imports, and the site commission report, agreements and payments. Not the Nayax settings and not any data repair, so a Manager may read a report whose underlying processing-fee rate only an Owner may change.
  - **Owner** — everything a Manager reaches, plus Admin → Integrations (the Nayax connection wizard and the processing fee rates; see [Connecting Nayax (Admin page)](#connecting-nayax-admin-page)), the Nayax catalogue reconciliation, all five maintenance repairs (Costing Repair, Inventory AVCO Transition Baseline, Historical GST Classification, Nayax Sale Timestamp Repair and Nayax Historical Cost Recovery), Admin → Users (below), Admin → Roles and Admin → Business Settings (see [Business settings (Admin page)](#business-settings-admin-page)). Each repair's preview and dry run is as restricted as its apply.
  - **Holding the capability is not the same as the page being usable.** Two Admin pages need the live Nayax fleet read and show the connection-required page without it (issue #869): **Site Commission Agreements**, whose site picker is that read and which a Manager reaches as well as an Owner, and the Owner-only **Inventory AVCO Transition Baseline**, whose preview reads the machines and their products. **Nayax Historical Cost Recovery is deliberately not locked** — it reads the Nayax cost already stored on imported sales and makes no Nayax call — and neither are Costing Repair and Historical GST Classification, which work on the business's own data. See [The menu without a usable provider](#the-menu-without-a-usable-provider).
  - **One Admin option can also be switched off by a Super Admin**, which is a different thing again: **Historical GST Classification** can be hidden from everybody but a Super Admin, who keeps it with an **Off** pill beside its name. That hides the sidebar entry and the Admin card and changes no permission at all — an Owner still holds `Data.Repair`, and the page and its API still work exactly as before. See [Hiding this option from other users](#hiding-this-option-from-other-users-super-admin).
  - **Admin → Roles** (issue #530) is a read-only page, on `Roles.View`, which only an Owner holds: it explains what Owner, Manager and Operator can each access, from the same table above. It edits nothing — no role, no permission and no member — and is rendered from one frontend definition kept from drifting away from the published capability vocabulary by its own test (see [docs/architecture.md § The Roles page](docs/architecture.md#the-roles-page-issue-530)).
  - **The dashboard's financial figures are sent only to a role that may see them** (issue #527). Every role reaches `GET /api/dashboard/summary` — it is the landing page — but a role without `Dashboard.ViewFinancials`, today an Operator, receives the response with no `salesThisWeek` object and no `inventory.inventoryValueAtCost`: both are **absent from the JSON**, not `null` and not `0`, and the week's sales are never calculated for that caller. Everything else in the response is unchanged. The Angular dashboard hides those two cards when the figures are absent and shows the two non-financial cards alone (issue #528), so the fix was never to make the API send `null`.
  - **`Dashboard.ViewFinancials` covers every sales, revenue, profit, margin, profitability-status and trend/comparison figure, and inventory value, on the Dashboard, Sites and Machines pages** (issue #699) — not only the two headline cards above. The Sites list and the home Dashboard's Sites/Machines tables hide their revenue, direct-profit, margin and trend columns without it; Site Products hides Estimated Card Profit (and drops it from the CSV export); the Machines list hides sales, direct profit and the profitability-status badge; and Machine Detail hides its revenue/direct-profit summary cards and the Estimated Card Profit/50% Card Margin Price product columns. Unit cost, site/machine selling price, stock, restock and navigation stay visible to every role. That page-level hide is read from `GET /api/me/access` rather than from each endpoint's response, so on its own it hides a figure rather than protecting it; the API withholds the figures too, for the site endpoints since #697, for the machine and product endpoints since #698, and for the stock-movement and purchasing endpoints since #700 (the next three bullets).
  - **The site endpoints withhold revenue and estimated profit from a role that may not see them** (issue #697). Every role reaches `GET /api/sites` and `GET /api/sites/{id}/products` — stocking and pricing a site is day-to-day inventory work, on `Inventory.Operate` — but a role without `Dashboard.ViewFinancials`, today an Operator, is sent `null` for `todayRevenue`, `currentWeekRevenue` and `previousComparableWeekRevenue` on every site row and for `estimatedCardProfit` on every site product row. Never `0`: a period with no completed sale still reports an honest `0` to a caller who may see it, so `null` means "not yours to see". The revenue and the estimate are never calculated for that caller at all — the completed-sales read behind the revenue and the commission and processing-fee reads behind the estimate are not made. **Unit cost (`averageUnitCost`) and site price (`sitePrice`) stay visible to every role**, together with stock, MDB codes, machine counts and the alert counts, and a Manager's and an Owner's response is unchanged. A platform administrator in a [support session](#super-admin-support-sessions) sees the figures, because that session's fixed capability set grants `Dashboard.ViewFinancials`. The machine and product endpoints are covered separately by #698 (next bullet).
  - **The machine and product endpoints withhold their financial figures too** (issue #698). `GET /api/machines`, `GET /api/machines/{id}`, `GET /api/machines/{id}/products`, `GET /api/products`, `GET /api/products/{id}` and the low-stock list all stay available to an Operator — restocking machines is what they are for — but for a caller without `Dashboard.ViewFinancials` every machine revenue, direct-profit and `profitabilityStatus` field and every product's `inventoryValue` is **`null`, never `0`**: a withheld revenue arriving as zero would be a false statement about the business, and summing `inventoryValue` would have recovered the valuation the dashboard withholds. Machine identity, stock, restock and Sync Restock are untouched, and **unit cost (`averageUnitCost`) and selling price (`machinePrice` and the slot's suggested price fields) stay visible to every role** by the recorded decision — an Operator needs them to restock and price a slot. The catalogue `unitPrice` that decision also named is no longer a question: the field was removed, so no response carries it (see [A product carries no selling price](#a-product-carries-no-selling-price)). A Manager's and an Owner's responses are unchanged. The one gap #698 left open — a product's `stockAdjustments` still carrying each movement's inventory value — is closed by #700 in the next bullet. See [docs/architecture.md § Machine and product financial redaction](docs/architecture.md#machine-and-product-financial-redaction-issue-698).
  - **Every remaining Operator-readable endpoint has been audited, and the last two leaks are closed** (issue #700). Issue #700 walked every endpoint a role without `Dashboard.ViewFinancials` can reach — taken from the capability policies themselves — and classified each restricted figure on each one; the endpoint-by-endpoint table is in [docs/architecture.md § Operator-readable financial disclosure](docs/architecture.md#operator-readable-financial-disclosure-issue-700). Two figures were still being sent. A **stock movement's `inventoryValueAfter`** — the business's inventory value at cost after that movement, so the latest one reports the current valuation — is now `null` for a caller without the capability on `GET`/`POST api/products/{id}/stock`, on `GET api/stock-history`, and nested in a product's `stockAdjustments`. The **catalogue product nested in a purchase line or a supplier-order line** now withholds its `inventoryValue` on the `api/purchases` and `api/supplierorders` responses, the same way `api/products` has since #698 — otherwise listing purchases was a way around that. Never `0`: both fields already mean "no cost" or "no valuation yet" when they are `null`, and a withheld amount arriving as zero would be a false statement about the business. **Nothing else is withheld.** A movement's own `unitCost`, `totalCost` and `averageUnitCostAfter`, a purchase's `totalAmount`, delivery and package charges, each line's `unitCost` and `lineTotal` and its input-GST block, and a supplier order's quantities, `unitPrice` and `outstandingQuantity` all stay visible to every role, because they are purchasing and unit-cost data an Operator enters and has to be able to read back. Every other Operator-readable endpoint — the pick list, categories, take inventory, the price history and restock-cost suggestion, Sync Restock, the machine stock ledger, suppliers and the stored purchase document — carries no restricted figure at all. A Manager's and an Owner's responses are unchanged, and a new revenue, profit, margin, sales or inventory-value property on any of these responses now fails the build until somebody decides what happens to it.
  - **The Angular application renders what the role allows** (issue #528). It reads the caller's capabilities once from `GET /api/me/access` and builds the sidebar from them, so an Operator is offered no Expenses, Reports or Admin entries and a Manager no Integrations or data-repair entries; a route the role does not allow shows an "access not allowed" page, and an action the role cannot use — the supplier GST defaults editor — is not offered. **None of that is the boundary**: the API checks the same capability on every request and refuses it there, so a hidden entry hides a page rather than protecting one, and a capability list that cannot be read hides nothing at all. Since every current member is an Owner, nothing changed for anybody using the application today.
  - The capability vocabulary and the one role → capability table are described in [docs/architecture.md](docs/architecture.md#membership-roles-and-capabilities-issue-521), how they are enforced in [docs/architecture.md § Capability authorization](docs/architecture.md#capability-authorization-issue-525), and the full matrix by page and endpoint in [docs/architecture.md § Capability authorization on the Manager and Owner endpoints](docs/architecture.md#capability-authorization-on-the-manager-and-owner-endpoints-issue-526).

### Managing members: changing a role and revoking access

An Owner manages their own business's members through four endpoints (issue #533). They require `Members.Manage`, which only an Owner holds, so a Manager and an Operator get a plain `403` — the list included, because it is the roster the changes are made from and it shows every member's name and email address. The Admin → Users page (issue #534) calls them: it lists every member with their name, email, role and status, and changes a role, revokes or reinstates a member, each behind a confirmation dialog and showing the API's own refusal sentence unchanged — including the last-Owner and already-a-member-elsewhere refusals below.

```text
GET  /api/members                              display name, email, role, active, joined
PUT  /api/members/{membershipId}/role          { "role": "Owner" | "Manager" | "Operator" }
POST /api/members/{membershipId}/revoke
POST /api/members/{membershipId}/reinstate
```

- **Everything is scoped to the caller's own business.** No endpoint takes a business id, and a membership id belonging to another business is simply **not found** — an Owner holds every capability there is and still reaches nothing of another business's.
- **Changes apply on that member's next request.** A demoted member's next call is answered with their new role, a revoked member's next call is refused, and a reinstated member's next call works again. There is nothing to wait for and no sign-out to force.
- **Revoking keeps the row**, marked inactive, so the revoked approval stays visible on the list, can be reinstated, and no longer occupies that person's one active membership — which is what lets them join another business.
- **The business always keeps at least one active Owner.** Revoking or demoting the last active one is refused with `409` and "A business must keep at least one active Owner. Give another member the Owner role first." — including when the Owner is acting on themselves. An Owner *may* step down or revoke themselves while another active Owner remains. The rule exists because only an Owner may manage members, roles, the integration and the business record, so a business that lost its last Owner could not be repaired from inside the application at all.
- **Reinstating is refused with `409` if that person has since joined another business** ("This person is already a member of a business."), because a second active membership would deny them both. The message names no business, member or role.
- **A revoked membership's role is not edited.** Reinstating restores the role the row still holds, so an Owner reinstates first and then changes the role; both steps are audited.
- **Every change is audited.** One structured log event records who made it, the business, the membership, which change it was, and the old → new role and active flag. It contains **no name and no email address**, and it is written only after the change is committed — a refused change, or a request asking for the state a member already has, writes nothing and is audited as nothing.
- An unsupported role name (including a bare number such as `40`) is rejected with `400` and stores nothing. A declared name is accepted case-insensitively.
- See [docs/architecture.md § Member management](docs/architecture.md#member-management-list-role-revoke-and-reinstate-issue-533) for the design, the write transaction and the concurrency guarantees.

### Adding a member: single-use invite links

Until issue #531 the only way to give somebody access was `bootstrap-business`, run by a human with the new member's Entra ids typed in by hand. An Owner now creates an invite link instead — three endpoints, on the same `Members.Manage` capability as the member management above, so a Manager and an Operator get a plain `403`. The Admin → Users page (issue #534) calls them, composing the invite link from the token the creation response carries and showing it once with a Copy button and its expiry; **redeeming** an invite is a separate page (issue #532/#535), so an invite created here cannot be accepted anywhere but `/invite/:token`.

```text
POST /api/members/invites                 { "role": "Owner" | "Manager" | "Operator", "note": "optional" }
GET  /api/members/invites                 every invite, in every state
POST /api/members/invites/{inviteId}/cancel
```

- **The token comes back exactly once**, in the creation response, and never again: the database stores only its SHA-256 hash, no log or audit event carries it, and no later read can produce it. An Owner either copies the link out of that response or creates another invite. The response carries the token rather than a full URL — the API does not know the web application's address, so the Owner-facing page (issue #534, one slice of #505) composes the link.
- **The token is 256 bits of cryptographic randomness**, URL-safe, and unguessable by design; that is what makes a plain hash the right way to store it rather than a password-style one.
- **An invite lasts seven days**, fixed in code. Nothing in the request chooses the business, the creator, the expiry or the token.
- **Expiry needs no cleanup job.** A pending invite past its expiry is reported as `Expired` with no row having changed, so there is no sweep to run and nothing that could leave an expired link looking usable. The four states are `Pending`, `Used`, `Expired` and `Cancelled`, and the list shows all of them — a used or cancelled invite is part of the record of who was offered access.
- **Everything is scoped to the caller's own business.** No endpoint takes a business id, and an invite id belonging to another business is simply **not found**.
- **Cancelling withdraws an invite that has not been used.** A replay is accepted and changes nothing; a **used** invite is refused with `409` ("This invite has already been used, so it cannot be cancelled.") because the membership it created exists, and an **expired** one is refused too, because a cancellation records a deliberate decision and an invite that ran out was nobody's. The check and the write are one statement, so an invite redeemed a moment earlier is never recorded as cancelled.
- **Every creation and cancellation is audited.** One structured log event records who did it, the business, the invite and the role — and never the token, in any form, nor the Owner's note.
- An unsupported role name (including a bare number such as `40`) or a note longer than 200 characters is rejected with `400` and stores nothing.
- See [docs/architecture.md § Invite links](docs/architecture.md#invite-links-create-list-and-cancel-issue-531) for the design, the derived expiry and the cancellation's concurrency guarantee.

### Accepting an invite

Opening an invite link (`/invite/:token`) goes straight to an Accept button at `/invite`. The token is taken out of the address bar before anything else runs, including sign-in: it waits in that browser tab's session storage, never in the address bar, history or Microsoft's sign-in redirect, and the Accept page removes it the moment it reads it (an unused one expires after 15 minutes). A signed-out visitor is sent through sign-in first and lands back on `/invite` with the invite still waiting. Reloading the page afterwards asks you to open the invite link again rather than dropping you on another page.

- **Who sees the Accept button.** Someone with no membership anywhere, someone a business revoked, and a rejected applicant — the three account states the redemption endpoint admits — see it. A member, a pending applicant, and anyone whose own membership is deactivated or ambiguous are sent to the page their own account state already calls for instead, because the API would only refuse them.
- **Every refusal is shown exactly as the API phrases it**: an expired, used, cancelled or invalid link, already belonging to a business, or needing release by the platform administrator first each show their own clear sentence, and the attempt can be retried.
- **Accepting joins the business immediately.** The application re-checks the signed-in person's access and lands on the dashboard with the role the invite granted — there is nothing further to do and no sign-out to force.
- See [docs/architecture.md § Invite acceptance page](docs/architecture.md#invite-acceptance-page-issue-535) for the design, the token's one accepted residual, and what each account state is shown.

## Business plans (Free, Starter and Pro)

Every business has one **plan**, and a plan says what the business has *bought* — which is a different question from what a member may do (their [role](#roles-and-capabilities)) and from whether Nayax is [connected](#connecting-nayax-admin-page). All three have to be satisfied for a connected feature to work, and no one of them implies another.

**Nothing is enforced until a deployment switches it on.** Issue #966 defines the plan state, the catalogue and the one entitlement rule and publishes the answer; issue #967 is what makes the server withhold a feature (see [How a plan is enforced](#how-a-plan-is-enforced) below), and the attachment storage allowance (issue #968, [below](#attachment-storage)) is the one storage limit, which also applies only once a plan is being enforced. Issue #977 is the same enforcement over the supplier price comparisons and the daily collection behind them; the frontend's own locks (#979) are still separate, later work. With the rollout setting off — which is the shipped state, see [`Subscriptions__EnforcementEnabled`](#configuration-and-secrets) — **every business is unrestricted and behaves exactly as it does today**: nothing is withheld, no endpoint refuses anything, attachments upload as before and no extra query is made.

### The plans

Prices are AUD per business per month, as agreed on issue #965. Whether the advertised price includes GST is an open launch decision; the catalogue records the advertised amount and claims nothing about its tax treatment.

| | **Free** | **Starter** | **Pro** |
| --- | --- | --- | --- |
| Price per month | A$0 | A$7 | A$15 |
| Local features (below) | Yes | Yes | Yes |
| Purchase/expense attachments | 1 GB | 20 GB | 20 GB |
| Daily cheapest-supplier comparisons | — | Yes | Yes |
| Bookkeeping and GST reports, with exports | — | Yes | Yes |
| Nayax connection and everything that needs it (below) | — | — | Yes |
| Connected machines included | — | — | 5, then A$2/month each |

A **connected machine** is a machine on the business's Nayax account, whether or not it is linked in this application; a manual machine is not one. Being above the included allowance is **billed, never blocked**. How that count is taken, how old it may be, and what happens when the Nayax account changes are in [Connected machines and the Pro allowance](#connected-machines-and-the-pro-allowance-issue-978).

Storage limits cover purchase and expense attachments combined — not transaction records — 1 GB means 1,000,000,000 bytes, and attachments are **never deleted automatically** on a downgrade. See [Attachment storage](#attachment-storage) for exactly what happens as a business fills it up.

### What is local and what needs a connected plan

The split is the same one the application already makes for a business with no usable provider, and [The menu without a usable provider](#the-menu-without-a-usable-provider) is the list of what Nayax is behind. Said in plan terms:

- **Local, and on every plan:** the dashboard, Machines (the business's own records), Inventory — products, categories, Take Inventory, stock history — Purchases, Supplier orders, Suppliers, Expenses, **manual pick-list entry** (recording a pick, including [one run across several machines](#entering-one-pick-run-across-several-machines-issue-904)), and the **Track supplier prices** switch itself (see below).
- **Starter adds** the daily cheapest-supplier comparisons ([The cheapest supplier, on Needs Ordering](#the-cheapest-supplier-on-needs-ordering) and [Track supplier prices on a product](#track-supplier-prices-on-a-product)) and the two reports that need no provider: **Bookkeeping** and **GST**, with their CSV/XLSX exports.
- **Pro adds** everything that needs the Nayax connection: Sites, Site Commissions, the **Daily**, **Transactions**, **Reconciliation**, **Machines** and **Products** reports, machine latest sales, Sync Restock, the dashboard's sales refresh, the Nayax sales file import under Admin → Imports, and the **calculated pick-list matrix** (the existing automated connected pick list).

A paid plan and a usable Nayax connection are separate conditions: a Pro business whose credentials are missing or have stopped working sees exactly today's connection-required pages, and a connected Free or Starter business sees the same "no usable provider" behaviour it would without credentials.

### How a plan is enforced

Once the rollout setting is on, the plan is checked on the server in two places and nowhere else.

**Everything that needs the Nayax connection is refused in one place.** Every ordinary Nayax call in the application resolves the business's credentials through one component, and that component now checks the plan **before it reads the stored connection at all**. So a business whose plan does not include the connection makes **no Nayax request**, however valid the credentials it has saved, and every mixed page behaves exactly as it already does for a business with no usable connection:

| Page or feature | What a Free or Starter business sees |
| --- | --- |
| Dashboard | The whole local summary, with the machine refill card marked unavailable |
| Needs Ordering / low-stock alerts | A real count, labelled **storage stock only** |
| Machines | The business's own machine records; the live fleet view is unavailable |
| Pick List | Manual entry, as it does with no connection |
| Sites, Site Commissions | Unavailable, with the stored agreements and payments untouched |
| Sync Restock, and its Dashboard reminder | Unavailable; the reminder is not shown |
| Catalogue review, inventory-cost transition previews | Unavailable, as they are with no connection |
| Purchases, Suppliers, Inventory, Expenses, manual pick-list entry, Take Inventory | **Completely unchanged** |

Each of those answers carries a **distinct reason** — `PlanRequired`, not "Nayax is not connected" — so a screen can eventually say "needs Pro" rather than sending somebody to reconnect credentials that are working. A Pro business whose connection really is unusable keeps today's connection reason.

**What a plan refuses outright answers `403` with the code `plan_required`** and names the plan the business would need. That applies to:

- **Starter and Pro:** Bookkeeping and GST, and their CSV/XLSX exports; and the **supplier price comparisons** — the one read behind the **Cheapest supplier** column on Needs Ordering, All Products and the Dashboard.
- **Pro:** the Daily, Transactions, Reconciliation, Machines, Products and reporting-dashboard reports and their exports; Site Commissions (report, agreements and payments); the Nayax sales file import under Admin → Imports; and the Owner's **Test**, **Save** and **Re-test** connection actions on the Integrations page.

A report and its own download always need the same plan. A refused save or import changes nothing at all — the plan is checked before the request body is read, so no credential is written and no row is imported.

**Supplier price comparisons, in plan terms.** On Free, the **Cheapest supplier** column reads as unavailable and the rest of those pages — the row, the purchase-cost columns beside it and every action — is untouched. The daily collection follows the same line: a product is looked up at the suppliers while **at least one** business on Starter or Pro is tracking it, so a product you and a Starter business both track is still priced and nothing you have tracked is ever deleted. And the **Track supplier prices** switch stays available on every plan: you can turn it on and off on Free, the setting is kept through a downgrade, and the prices appear as soon as the plan includes them. See [Track supplier prices on a product](#track-supplier-prices-on-a-product) and [The cheapest supplier, on Needs Ordering](#the-cheapest-supplier-on-needs-ordering).

**Reading the connection status stays available on every plan**, to every role that can see it today, and makes no Nayax call. That is deliberate: a business that moves to a plan without the connection keeps its saved credentials and their status, and the Integrations page still shows them. Nothing is deleted or changed by a plan change — sales, costs, product links, history, agreements, payments and attachments are all exactly as they were, and they become readable again the moment the plan includes them.

**A plan is not a permission.** The role check happens first and is unchanged: a member whose role does not grant a feature is refused exactly as before, with no mention of plans, and a plan never gives a role access it does not have. See [Roles and capabilities](#roles-and-capabilities).

### How a business's plan is decided

Two records per business, and **neither ever changes the other**:

- a **billing subscription**, written only by verified billing events (issue #969), and
- **manual grants**, written only by a Super Admin (issue #982) or by the grandfathering step below.

From those, one rule decides what the business may use:

- **The rollout setting off → unrestricted.** Today's behaviour, whatever either record says.
- **On, but the grandfathering step has not completed → still unrestricted**, and the API logs a startup error naming the missing command. Enforcement cannot start before the businesses that were already trading hold their grant.
- Otherwise the effective plan is **the higher of** the active manual grant and the active paid plan.
- **Expired, cancelled, missing or unreadable records → Free** for paid features. Free's own local features are never locked.
- Grants and paid periods are **UTC instants**: a period applies from its start instant inclusive and stops at its end instant exclusive.

`GET /api/me/access` publishes the answer beside the capability set — `entitlement.enforced`, the plan, where it came from (`ManualGrant`, `BillingSubscription`, `NoActiveRecord`, or the two unrestricted states), when that record ends, and `entitlement.connectedMachines` with the machine allowance, the last recorded count and how old it is (`null` while nothing is enforced; see [Connected machines and the Pro allowance](#connected-machines-and-the-pro-allowance-issue-978)). It is computed per request with no server cache, there is no separate endpoint and no request names a business. Inside a [support session](#super-admin-support-sessions) it reports the **viewed** business's own plan, and a [role preview](#previewing-the-app-as-a-role-issues-720-and-721) narrows role capabilities only and never changes the plan.

Beside it, `entitlement.attachmentStorage` carries the business's attachment storage: how much it is using, its plan's allowance, and whether that is `Normal`, `Warning` (80% and over) or `Full`. It is `null` while nothing is enforced, because then there is no allowance and nothing is counted.

### Attachment storage

Each plan includes an amount of purchase and expense attachment storage, and that allowance is enforced once plans are being enforced (issue #968). It is about **documents only**: nothing here ever stops a purchase, an expense or any other record being saved.

- **What counts.** The sizes of the attachments the business's purchases and expenses actually refer to, plus any upload it has in progress. A file left in storage that no record refers to is **not** charged to the business, and neither is an attachment whose size was never recorded — both are reported by [`reconcile-attachment-storage`](#reconciling-attachment-storage-against-the-records-issue-968) instead.
- **Deleting frees it immediately.** Deleting a purchase, an expense or an attachment releases its bytes as soon as that change is saved, whether or not the stored file is removed successfully.
- **At 80% the business is warned**, and the warning state travels with the plan in the access read above, so the app can say so before anything is refused.
- **At 100% only new uploads pause.** Viewing, downloading and deleting attachments keep working, and so does recording purchases and expenses — a purchase can be saved [without a receipt](#a-purchase-without-a-receipt) and the document added later. An upload that does not fit is refused with a sentence that says the record can still be saved without it, and nothing is written to storage.
- **Replacing a file when storage is full works, as long as it does not grow.** Replacing an expense's attachment is charged only the **difference**: a smaller or same-size file succeeds even at or over the allowance, and a larger one needs room for the difference alone. The old file's bytes stop counting the moment the record points at the new file.
- **If two people replace the same attachment at once, exactly one wins.** The other is refused with "This attachment was replaced by someone else while the upload was in progress. Reload the record and try again." — its file is removed, nothing about the record changed, and retrying from the reloaded record is safe.
- **An abandoned or crashed upload stops holding storage on its own**, within 30 minutes, without anybody having to clean anything up.
- **After a downgrade a business can be above its new allowance, and nothing is deleted.** Every existing attachment stays viewable, downloadable and deletable; only new uploads that would add bytes are refused, until enough has been freed or the plan is upgraded again.

### Grandfathering, in one sentence

When plan enforcement is being switched on, a human runs [`grandfather-business-plans`](#grandfathering-existing-businesses-onto-pro) once: every business that was already trading (or had traded and been offboarded) before the recorded cutoff is given a **Pro grant with no end date**, every other business starts on **Free**, and the command records the completion marker enforcement waits for. A pending application approved after the cutoff starts on Free, and the command is safe to run again — it changes nothing once it has completed.

## Privacy: the personal data this application stores

The application stores almost no personal data. It holds a vending business's own trading records — products, purchases, sales, expenses and reports — and the only people it records are the business's own members.

- **Who may sign in is recorded as an Entra identity, not as a person.** A `BusinessMembership` row maps the validated `(tid, oid)` claim pair to one business, with a role and a current state. That pair is the identity, and it is the only thing access is ever decided from.
- **A member's display name and email address are stored for display** (issue #524). Both are nullable columns on that membership row, refreshed from the `name` and `preferred_username`/`email` claims of the member's own validated sign-in token, at most once an hour and never more than the latest values. They exist so an Owner can see who their members are instead of a list of object ids.
- **They decide nothing.** No authorization, membership, role, capability or invitation decision reads either value; a name and an email address are mutable, reassignable and not unique across Entra directories. Changing them in Entra changes what is displayed and nothing else — and not on the member's next request: the refresh is claimed at most once an hour per member (issue #524), so the new name or address appears the first time that member signs in or calls the API more than an hour after the last refresh, not immediately. **Role changes are the contrast and are immediate**: a demotion, revocation or reinstatement applies on that member's very next request ([above](#roles-and-capabilities)), because a role is read from the membership row on every request rather than refreshed on a timer.
- **They are never logged.** Neither value appears in a log message, a log property or an error, and neither is reachable through the [platform diagnostics API](#platform-diagnostics-api), whose allowlist excludes the membership table entirely. A request made in a support session (`X-Support-Session`) refreshes nothing, so a supporter's own details can never be written into a member's row.
- **A member who has never signed in has neither value stored**, and no migration, deployment or startup step ever fills them in: the only thing that writes them is that member's own token. Exactly one surface reads them: the Owner-only members list above (issue #533), which shows them to that business's own Owner and reports an explicit `null` for a member who has never signed in. No other API response carries either value, and the audit event for a member change deliberately carries neither.
- **An invite's note is free text an Owner typed** (issue #531), so it may well name the person being invited. It is stored on the invite row, shown back to that business's own Owners on the invite list, and nothing else: it decides nothing, nothing looks an invite up by it, and it is deliberately absent from the invite audit event. An invite stores no name and no email address of the person it is for — only the role it grants and the Entra pair of the Owner who created it.
- **Uploaded business documents** (purchase and expense attachments) may contain whatever the supplier's paperwork contains. They are stored outside the API web root, are readable only through authenticated endpoints, and are never committed to this repository.
- **A platform administrator may be granted a time-limited, audited, read-only look at a business's data** (issues #550 and #551). A [support session](#super-admin-support-sessions) records which business, which administrator, why they needed to look, and when the session ran and ended; it lasts 60 minutes, only one may be open at a time, and the record cannot be edited or deleted by the surface that creates it. A request made inside one reads that business and can change nothing — not a row, not a document — and never uses the business's Nayax credentials; it cannot see the members page at all, so members' names and email addresses stay private. Every such request is audited with the endpoint it reached, never with what it read. **Businesses must be told this is possible**, which v1 does in the terms and privacy text; notifying a business's Owners in the app is a documented follow-up. The reason the administrator typed is stored and logged on purpose: it is what makes such access reviewable afterwards.

See [docs/architecture.md § Member display name and email](docs/architecture.md#member-display-name-and-email-issue-524) for the design and the exact write path.

## Configuration and secrets

Backend configuration follows normal ASP.NET Core precedence. Override local or deployed values with user-secrets, environment variables, or the hosting platform's configuration instead of committing credentials.

Common environment-variable names include:

```text
ConnectionStrings__DefaultConnection
NayaxLynx__BaseUrl
NayaxTokenProtection__ActiveKeyId
NayaxTokenProtection__Keys__<key-id>
PlatformAdmin__DirectoryTenantId
PlatformAdmin__ObjectId
ExternalId__Instance
ExternalId__TenantId
ExternalId__ClientId
PriceCollector__TenantId
PriceCollector__ClientId
PriceCollector__RunExpiryHours
SelfServiceOnboarding__Enabled
Subscriptions__EnforcementEnabled
APPLICATIONINSIGHTS_CONNECTION_STRING
```

`NayaxLynx__BaseUrl` is the only Nayax **connection** setting left: it is non-secret, global, and validated at startup — the API refuses to start with a missing or invalid value rather than failing on the first Nayax call. **The operator id and the access token are not configuration any more** (issue #520): each business's own pair is held encrypted in its connection record and is read per call from the trusted current business, with no fallback to a global value or to another business's. Startup therefore requires neither, and an API with no Nayax credential configured at all starts and serves every non-Nayax feature normally; a business whose record is empty gets a stable "Nayax is not connected" error on its Nayax features and nothing else. See [docs/architecture.md § Per-business Nayax credentials](docs/architecture.md#per-business-nayax-credentials-issue-520).

A still-configured `NayaxLynx__OperatorId`, `NayaxLynx__AccessToken` or legacy `Nayax__Token` is read by exactly one thing: the human-run [`migrate-nayax-connection`](#migrating-the-nayax-connection-into-the-business) command, which moves that pair into the existing business's encrypted record (`NayaxLynx__AccessToken` is preferred over `Nayax__Token` when both are set). Leave them in place until that command has been applied and Nayax has been verified, then remove them as a deliberate human step — [docs/tenant-rollout.md § When to remove the global settings](docs/tenant-rollout.md#when-to-remove-the-global-settings) is the procedure. They are still secrets while they exist: never commit them, and the token is never logged.

`NayaxTokenProtection__ActiveKeyId` and `NayaxTokenProtection__Keys__<key-id>` are the encryption keys a business's **own** stored Nayax access token is protected with (issue #518). Each key is a base64-encoded 256-bit (32-byte) AES key and is a secret: set it with `dotnet user-secrets` locally or Key Vault/App Service configuration in Azure, and never commit it. `ActiveKeyId` names the key new ciphertext is produced with, and the key id is stored beside each ciphertext, so a rotation adds a second `Keys__<key-id>` entry and repoints `ActiveKeyId` while the previous key stays configured until nothing names it any more; removing a key is what retires it, and it makes anything still encrypted with it undecryptable on purpose. A half-configured section — an active key id with no matching key, a value that is not base64, a key of the wrong length — fails at startup with the setting named. **The section is empty in `appsettings.json`, and that is the shipped state**: with no key configured the API starts and runs completely normally, and only storing or reading a *per-business* Nayax token fails closed. Since issue #520 that is what every Nayax call does, so **an environment that uses Nayax must have a key provisioned** — it is a human step, and it must happen before the existing business's credential is moved into the table by [`migrate-nayax-connection`](#migrating-the-nayax-connection-into-the-business) (issue #519), which refuses with the setting named until it is. Keep the key configured afterwards: removing the key that encrypted a stored token is what makes that token undecryptable. An environment with no Nayax integration at all needs no key and is unaffected. See [docs/architecture.md § Per-business Nayax connection](docs/architecture.md#per-business-nayax-connection-issue-518) and [§ Per-business Nayax credentials](docs/architecture.md#per-business-nayax-credentials-issue-520).

`APPLICATIONINSIGHTS_CONNECTION_STRING` is the Application Insights connection string, a secret that is never committed — see [Observability and error diagnostics](#observability-and-error-diagnostics). Unlike the Nayax base URL it is optional: with the variable absent or blank no telemetry is registered at all and the API starts and runs normally, which is what local development and the automated tests do.

`PlatformAdmin__DirectoryTenantId` and `PlatformAdmin__ObjectId` name the platform super-administrator for the two platform-admin APIs below — [diagnostics](#platform-diagnostics-api) and [businesses](#super-admin-businesses-api). Neither is a secret — an Entra object id is an identifier that grants nothing without a validated token carrying it — so they are ordinary application settings and need no Key Vault reference, but they are a real person's identifiers and are therefore **empty in `appsettings.json` and must never be committed**. Set them with `dotnet user-secrets` locally or application settings in Azure.

`ExternalId__Instance`, `ExternalId__TenantId` and `ExternalId__ClientId` name the one Microsoft Entra External ID tenant whose tokens the API also trusts (issue #710) — see [Customer identity: a second trusted issuer](#customer-identity-a-second-trusted-issuer-issue-710). None of the three is a secret, for the same reason the `PlatformAdmin` pair is not, so they are ordinary application settings with no Key Vault reference; they are nevertheless **empty in `appsettings.json` and must never be committed**. All three empty is the shipped, fail-closed state: the second bearer scheme is not registered, every token from that authority is rejected, and workforce sign-in is unaffected. A section that is half-filled or holds a value that is not what the setting means behaves identically and logs a startup error naming the setting. The values are read once at startup, so filling them in takes effect on the next restart.

`PriceCollector__TenantId` and `PriceCollector__ClientId` name the one scheduled price collector whose app-only token the API trusts (issue #924) — see [The scheduled price collector](#the-scheduled-price-collector-issue-924) below. Neither is a secret, for the same reason the `PlatformAdmin` pair is not, so they are ordinary application settings with no Key Vault reference; they are nevertheless **empty in `appsettings.json` and must never be committed**. Both empty is the shipped, fail-closed state: the collector bearer scheme accepts nothing, the collector policy denies everybody, and `GET /api/price-collector/work` answers `401` to every request while every other endpoint is unaffected. A section that is half-filled or holds a value that is not what the setting means behaves identically and logs a startup error naming the setting. `PriceCollector__RunExpiryHours` is how long an issued run stays open unless the collector reports back; blank, unparseable or outside 1–168 means the owner's default of **6 hours**, logged when it falls back, and it is never read as "no expiry". All three are read once at startup, so filling them in takes effect on the next restart.

`SelfServiceOnboarding__Enabled` switches [self-service business onboarding](#starting-a-business-self-service-onboarding) on (issue #541). It is not a secret, and it is **`false` in `appsettings.json`, which is the shipped state and the only one source control carries**: with it off `POST /api/onboarding/business-applications` answers `404` and `GET /api/me/account-state` reports `onboardingEnabled: false`, so nobody can create a business in a deployment that has not deliberately opened the door. An absent, blank or unparseable value is read as off, and the value is read once at startup, so changing it takes effect on the next restart. Switching it on in production is a human decision with a verified release gate in front of it — [docs/tenant-rollout.md § Enabling self-service onboarding](docs/tenant-rollout.md#enabling-self-service-onboarding-issue-541).

`Subscriptions__EnforcementEnabled` switches [business plan enforcement](#business-plans-free-starter-and-pro) on (issue #966). It is not a secret, and it is **`false` in `appsettings.json`, which is the shipped state and the only one source control carries**: with it off every business is unrestricted and behaves exactly as it did before plans existed, and `GET /api/me/access` reports `entitlement.enforced: false` with no plan at all. An absent, blank or unparseable value is read as off, and the value is read once at startup, so changing it takes effect on the next restart. Switching it on is a human decision with two gates in front of it: the [`grandfather-business-plans`](#grandfathering-existing-businesses-onto-pro) command must have completed first — otherwise the API logs a startup error and keeps treating every business as unrestricted — and the paid plans themselves are switched on only when the whole rollout is released together (issue #965's launch gate).

Uploaded purchase and expense documents are stored outside the API web root, under the content root's `protected-files/` folder, with their metadata in SQLite; they are readable only through the authenticated API endpoints. Do not commit uploaded business documents, local databases, or credentials.

## The scheduled price collector (issue #924)

The shared supplier price catalogue is collected by a scheduled GitHub Actions workflow, and it signs in to this API **without a person and without a stored password**. This section is the human setup that makes that possible; the boundary itself is [docs/architecture.md § The price collector's app-only path](docs/architecture.md#the-price-collectors-app-only-path-issue-924), [§ Collector work list](docs/architecture.md#collector-work-list-issue-924), [§ Collector results](docs/architecture.md#collector-results-issue-937) and [§ Collector product matching](docs/architecture.md#collector-product-matching-issue-941).

```text
GET  /api/price-collector/work                          issues one run and answers the work it is to do
POST /api/price-collector/results                       one batch of what it read off a shelf
POST /api/price-collector/matches                       what it thinks each waiting product is
POST /api/price-collector/work-items/{id}/outcome       finishes one search
POST /api/price-collector/runs/{id}/complete            closes the run
```

**What has shipped.** The authentication path, the work list, the results endpoints (#937) and the matches endpoint (#941). The workflow itself (#929) and the collector prompt and scripts (#926, #927) are separate tasks.

**The shipped state trusts no collector.** `PriceCollector__TenantId` and `PriceCollector__ClientId` are **empty** in `appsettings.json`, which is both the fail-closed state and the rollback lever: with them empty the collector endpoint answers `401` to every request, issues nothing, and no other endpoint is affected in any way. A half-filled or malformed section means the same thing and is logged as a startup error. Neither identifier is a credential — the API validates with Entra's published signing keys and the collector holds no secret either — so they are ordinary application settings with no Key Vault reference, and no real identifier may be committed. Supply them with `dotnet user-secrets` locally or application settings in Azure; see [Configuration and secrets](#configuration-and-secrets).

Human-controlled portal setup this depends on (no agent may perform any of it, and the API deliberately does not check that it was done):

- **Add the app role to the API app registration.** Value exactly `PriceCollector.Run`, with **Applications** as the allowed member type — an application role, not a delegated scope. The API compares the value ordinally, so the spelling has to match.
- **Register the collector application** in the owner's tenant, as its own app registration. It needs **no** client secret, **no** certificate and **no** redirect URI.
- **Add a federated credential** on that registration for GitHub OIDC workload identity federation, scoped to this repository and to the workflow's own environment or branch — that scoping is what stops another repository obtaining the same token. Nothing about it is stored here.
- **Assign the role**: grant the collector application the `PriceCollector.Run` **application** permission on the API registration, and grant admin consent for it. An unassigned application authenticates nowhere: a token without the role is `401`.
- **Set the two App Service settings** to that tenant's GUID and that registration's application id, and restart. Optionally set `PriceCollector__RunExpiryHours`.
- Decide the API registration's accepted access-token version if you have not already; the API pins both audience spellings (`<client-id>` and `api://<client-id>`), so either validates, and reads the calling application from `azp` or `appid` accordingly.

**Why both identifiers are pinned, and not just the role.** The workforce scheme is multi-tenant (`TenantId: common`), so another Entra tenant can consent to this API and assign `PriceCollector.Run` to an application of its own — and Microsoft signs that tenant's tokens with the same key set, so the signature is genuine. Pinning `tid` is what makes that somebody else's token, and pinning `azp`/`appid` is what stops the owner's *other* workloads. The token must also carry **no delegated scope at all**, because Entra puts an assigned application role in a signed-in person's token too: that rule is what stops a person impersonating the collector.

**How much one run is handed.** A run is issued at most 2,000 work items — one product and one supplier each — and each published product carries at most 50 listings and 100 refused master products with it, so a large catalogue cannot produce an unbounded payload. When a run hits the work-item bound it says so (`workItemLimitReached`) and the **next** run is issued the products this one omitted: the products form one rotation and each run starts after the product the last one stopped at, so every tracked product is searched over successive runs rather than only the first stretch of the catalogue. A product whose listings or refusals were omitted says so too, and a waiting product whose refusals were omitted is marked `matchingSuppressed`, which means no master product may be matched to it on that run — a key in that state, or a product with more than 50 listings, is a catalogue entry worth a Super Admin's look. See [docs/architecture.md § Collector work list](docs/architecture.md#collector-work-list-issue-924).

**What the collector can and cannot reach.** One controller, and nothing else in the API. Its request carries no business at all, so every tenant query filter and the central write enforcement stay fully in force, and the only tenant-owned data it may read is the deduplicated product **search text** of the shared catalogue — a product name, a unit label and a folded key, none of which says whose product it is. No response carries a business id, business name, business product id or price. The tables it writes belong to no business, with one reviewed exception: an automatic match (#941) sets the status, master product and match source of the price-tracking mappings that carry the key it matched, and may write nothing else about them — see [docs/architecture.md § Collector product matching](docs/architecture.md#collector-product-matching-issue-941). Reverting is clearing the two settings: no deployment, schema change or data step is involved, and the runs and work items already stored are retained.

## Platform diagnostics API

The tenant boundary fails closed, which is what makes a problem *in* it hard to see: a row assigned to the wrong business, or a child row whose parent belongs to another business, is invisible to the only person who would notice. The platform diagnostics API is one narrowly bounded, read-only, audited path for that investigation and nothing else. See [docs/architecture.md](docs/architecture.md#platform-diagnostics-issue-336) for the complete design.

```text
GET  /api/admin/diagnostics/access   capability signal only; exposes no business data
POST /api/admin/diagnostics/query    { "sql": "..." } -> a bounded, truncation-flagged result
```

- **Who.** Only the Entra `(tid, oid)` pair in `PlatformAdmin__DirectoryTenantId` / `PlatformAdmin__ObjectId`, checked on every request by the named `PlatformDiagnostics` authorization policy. Not a business role, not a `BusinessMembership` row, not an email address, and nothing a request supplies. **With the pair unset — the shipped state — nobody is a platform administrator and both endpoints refuse every caller, including business members.**
- **Where.** The Angular app offers it through the sidebar's top-level **Super Admin** group at `/super-admin/diagnostics` (`/admin/diagnostics` redirects there), shown only while `GET /api/admin/diagnostics/access` confirms the signed-in actor. Entering either URL directly works identically, because the policy above decides access on every request, not the link or the frontend guard in front of the route.
- **Nothing else changes.** The membership requirement is bypassed for the platform-admin endpoints only — these two, and the business ones below — and only after that policy succeeds. The platform administrator still receives `403` from `/api/products` and every other business route, and ordinary members' access is untouched.
- **Read-only and bounded.** One statement, 5 seconds, 500 rows, 1 MiB of serialized response, 16 KiB of submitted SQL. A truncated result says so; it is never presented as complete. No caller can raise a limit.
- **The permitted surface is an allowlist of table *and column* pairs** — identity and foreign-key columns on `Businesses`, `Categories`, `Suppliers`, `Products`, `Receipts`, `ReceiptItems` and `StockAdjustments` — enforced inside SQLite by a read-only connection, `PRAGMA query_only`, a zero attached-database limit and an authorizer callback, so a join, alias, subquery or expression cannot reach outside it. No name, note, amount, quantity or free-text column is on it; `BusinessMemberships` and the `Nayax*`/`Imported*` tables are not on it at all. A new table or column is denied automatically until a reviewed change adds it — including a column named `rowid`, `oid` or `_rowid_`, which SQLite resolves to a declared column of that name when one exists.
- **Audit.** Every query — including a refused one, and one that fails unexpectedly — emits one structured `ILogger` event with the actor `(tid, oid)`, the timestamp, the correlation id, a SHA-256 fingerprint of the normalized query *shape*, the duration, the row count and the outcome. It never contains the statement, a result row or a credential, and there is deliberately no audit table. **The log destination and retention period are platform configuration — Application Insights when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set, the App Service log stream otherwise — set on that resource and not in this repository. A human must verify the destination and its retention period before relying on this audit trail.**
- **It cannot repair anything, by design.** Any future data repair must be a separately reviewed, named maintenance operation with a preview/dry-run step and explicit verification, like `bootstrap-business`, `migrate-documents` and [Costing repair](#costing-repair-admin-page). A repair must never be reachable by submitting SQL.

## Super Admin businesses API

The Super Admin's other responsibility is the one the diagnostics API above deliberately cannot do: **deciding business applications, and switching a business off and on again.** [Self-service onboarding](#starting-a-business-self-service-onboarding) creates a `Pending` business and leaves it waiting, and this is what approves or refuses it — the first platform-admin path that writes anything (issue #544) — plus the lifecycle of a business that has already traded (issue #545). See [docs/architecture.md](docs/architecture.md#super-admin-businesses-api-issue-544) for the complete design.

```text
GET  /api/super-admin/businesses                        every business, pending applications first
GET  /api/super-admin/businesses/pending-count          { "pendingCount": 1 }
POST /api/super-admin/businesses/{businessId}/approve   Pending -> Active, + the enabled supplier
                                                        presets copied into the business
POST /api/super-admin/businesses/{businessId}/reject    { "reason": "..." } (optional) -> Rejected
POST /api/super-admin/businesses/{businessId}/deactivate   Active -> Deactivated, + invites cancelled
POST /api/super-admin/businesses/{businessId}/reactivate    Deactivated -> Active, needs an active Owner
GET  /api/super-admin/businesses/{businessId}/members       while Deactivated only
POST /api/super-admin/businesses/{businessId}/members/{membershipId}/release   while Deactivated only
GET  /api/super-admin/businesses/{businessId}/plan-grant      the manual grant, and its revision
PUT  /api/super-admin/businesses/{businessId}/plan-grant      { plan, endsOnUtc?, reason, revision }
POST /api/super-admin/businesses/{businessId}/plan-grant/end  { reason, revision }
```

- **Who.** The same Entra `(tid, oid)` pair and the same `PlatformDiagnostics` policy as the diagnostics API, checked on every request, plus the ordinary authentication and `access_as_user` scope requirements. **With the pair unset — the shipped state — nobody reaches it at all.** An Owner holds the strongest business role there is and is refused here like anybody else: a business role is not platform authority.
- **What the Super Admin does with it.** See every business — name, status, created/applied/decided dates, contact email, ABN, the state or territory it recorded, how many of its members are active, whether Nayax credentials are stored and how usable they were at that business's last connection test — approve an application so the business starts trading, or refuse it with an optional reason the applicant reads back on their own account-state screen. Refusing also releases the applicant's membership, which is what frees them to apply again.
- **Switching a business off, and on again.** **Deactivate** takes a trading business to `Deactivated` in one transaction, cancels every invite link of it that is still live, so no outstanding link survives, and ends every active [support session](#super-admin-support-sessions) for it; the response says how many invites were cancelled. **Nothing is deleted and no membership is changed** — the business simply stops resolving, so its members get the ordinary `403` on their next request. **Reactivate** brings it back, but only while it still has an active Owner membership: without one the business would trade with nobody able to administer it, so that is a `409` and nothing changes. A reactivation restores only the business — a released or revoked membership stays inactive, and a cancelled invite stays cancelled.
- **Freeing somebody from a business that is off.** A person belongs to one business at a time, so a membership in a deactivated business would otherwise stop them joining anywhere else — and that business has no reachable Owner to release them. While a business is `Deactivated` (and only then) the Super Admin can **list its members** — membership id, display name, role and whether it is active, and nothing else — and **release** one, which marks that membership inactive and frees the person to accept an invite or apply for a business of their own. The usual "a business must keep an active Owner" rule deliberately does not apply here; the reactivation refusal above is what it is replaced by. Both endpoints answer `409` for a business in any other status: a trading business's members are its own Owner's to manage.
- **Giving a business a plan without billing it (issue #982).** The last three routes above are the Super Admin's **manual plan grant**: grandfathering, a trial, a pilot site or a complimentary plan. A grant sets the plan (`Starter` or `Pro` — `Free` is not a grant, because a business with no grant is already on Free), an optional last day in UTC, and a **required reason**, and that is all it can set: there is no payment, price, billing status or subscription field here, the business's paid subscription is neither read to validate a grant nor changed, and the effective plan stays "the higher of the active grant and the active paid plan". Every write carries the grant's current `revision`, which the `GET` reports; a stale one is `409` with "reload it and decide again", so two Super Admin tabs cannot overwrite each other. **End** stops the grant at that moment and deletes nothing — the row keeps its plan, its start and the reason it was granted for — and the business falls back to its paid plan, or Free, on its members' next read of `GET /api/me/access`. Who may use it is the same answer as every route above: the configured Entra pair alone. An Owner, Manager or Operator is refused, a role preview cannot change a grant, and a member who presents a support-session id is still an ordinary member. See [docs/architecture.md § Super Admin sets a business's plan](docs/architecture.md#super-admin-sets-a-businesss-plan-issue-982).
- **It can change a business's status and one membership's activation, give a business being approved its own copies of the enabled supplier presets, decide that business's manual plan grant, and nothing else.** The whole surface is one narrow store: a fixed set of business columns, a membership count, the Nayax yes/no and the connection status stored beside it, one named business's memberships, and the status and activation writes — plus, on an approval alone, the supplier copies described under [New businesses start with the enabled presets](#new-businesses-start-with-the-enabled-presets), written through a separate one-purpose path that can only create that one business's suppliers. No sale, stock, cost, purchase, product, machine, site, document, report or credential value is readable or writable through it, the list takes no filter, sort or search, and no response ever carries a member's email address or Entra id. The plan grant is its **own** narrow store beside that one, over the business's grant row and the append-only record of each decision about it — and nothing else.
- **Only a pending application may be decided.** An already-trading, already-refused or deactivated business is `409`; an unknown id is `404`. Approving also requires that the applicant's membership in the application is still active and that they hold no active membership in any other business — a person belongs to one business at a time — so an approval after they joined somewhere else is `409` and changes nothing. Two decisions arriving at once resolve to one recorded decision and one `409`: the business is re-read inside the write transaction that decides it.
- **Audit.** Every committed decision emits one structured `ILogger` event with the actor `(tid, oid)`, the business id, the old → new status, whether a reason was recorded, the instant and the correlation id; every committed deactivation, reactivation and member release emits one of its own, with the action, the membership id where one applies and how many invites were cancelled. Neither ever contains a name of any kind — not the business's, not the applicant's or released member's — nor a contact address, an Entra identity or the reason's text, and neither has an audit table. A **plan grant is the one platform action that does** (issue #982): each committed grant, change or end writes an append-only row — the actor, the business, the transition, the end instant, the revision, the reason as typed and the instant — in the **same transaction as the grant**, so a granted plan and the record of who gave it commit together or not at all, and nothing can later rewrite that row. The log event emitted beside it carries no reason text and no name at all. A refused action, and one that changed nothing, are audited as nothing. **The log destination and retention period are platform configuration, exactly as for the diagnostics audit above, and a human must verify them before relying on these trails.**
- **The page.** The Angular app offers the Businesses page (issue #547) at `/super-admin/businesses`, shown as **Businesses** in the sidebar's **Super Admin** group (next to **Platform Diagnostics**) only while `GET /api/admin/diagnostics/access` confirms the signed-in actor, exactly like that link; entering the URL directly works identically, because the policy above decides access on every request. The table shows nine of the directory's twelve fields - name, status, applied date, contact email, the recorded state or territory (issue #933, an em dash for a business that has none), ABN, how many members are active, whether Nayax is configured, and the stored Nayax connection status as a coloured dot beside the name (issue #983, see [Each business's Nayax connection status](#each-businesss-nayax-connection-status) below) - never the created/decided instants or the business id itself, which the page keeps only to name the subject of an action - filterable by status with pending applications first, the order the API already returns them in. **Approve** and **Reject** (with an optional reason) are offered for a pending application; **Deactivate** for a trading one; **Reactivate** and **Members** - which lists a deactivated business's memberships and **Release**s one - for a deactivated one; each action is behind a confirmation and shows the API's own result or refusal sentence verbatim, including reactivate's "no active Owner" one. The sidebar's pending-count badge, on both the group and the Businesses link, re-reads `GET .../pending-count` after every action on the page, so it is never more than one action's delay out of date. An action refused with `409` or `404` - the business was decided, changed or removed elsewhere, for example in another tab - also reloads the table and re-reads the badge, alongside the refusal, so the stale row does not keep offering the action that was just refused. **Plan** (issue #982), offered for an `Active` business, opens its own dialog, which reads that business's manual grant through the route above, offers the plan, the optional last day and the required reason, and shows the API's own refusal verbatim - including the stale-revision one; the table itself shows no plan, because the grant is read per business rather than listed. **View as this business**, offered for an `Active` business, is the one action that leaves this page rather than reloading it — see [Super Admin support sessions](#super-admin-support-sessions) for what it starts. Hiding write controls and support-view messages once inside the session is issue #554's separate follow-up.

### Each business's Nayax connection status

The Businesses list used to say only **yes or no** about a business's Nayax credentials, which could not
tell a verified connection from a token that was saved and never tried, or from one whose last test
failed. Since issue #983 the list reports the status each business already **stores**, and the page shows
it as a coloured dot next to the business's name:

| Dot | Status | What it says, in words |
| --- | --- | --- |
| 🟢 Green | `Ready` | Nayax verified at last test |
| 🟠 Orange | `PendingPermissions` | Nayax token saved, not tested yet |
| 🟠 Orange | `NeedsAttention` | Nayax last test failed |
| 🔴 Red | `NotConfigured` | Nayax not set up |

- **The words are always there, not only the colour.** Each sentence above is the dot's tooltip *and* a
  screen-reader label in the same cell, so the two orange states never look like one thing and nobody has
  to tell three hues apart to read the table. A status name the app does not recognise shows a neutral dot
  and "Nayax status unknown" rather than being presented as one of the four.
- **It is the stored result of that business's own last test, never a live check.** Loading the list makes
  no Nayax call, resolves no credentials and decrypts no token, so the page is as fast and as safe as it
  was, and a business whose connection is temporarily unreachable does not change colour because somebody
  opened this page. A business with no connection stored at all reads **Nayax not set up**, which is
  exactly what having nothing stored means.
- **It is the status and nothing else.** No operator ID, no part of a token, no encryption key, no Nayax
  error text, and not even when the test ran. The Super Admin can see which businesses are connected; they
  cannot read or diagnose anybody's credentials from here.
- **Testing a connection is still the business's own job.** This surface has no Test or Re-test action, and
  nothing here changes a status: an Owner tests and re-tests their own connection on their own
  [Connecting Nayax](#connecting-nayax-admin-page) page, exactly as before. Business Owners, Managers and
  Operators see nothing new — the whole Super Admin surface still refuses them.

## Super Admin supplier presets

The Super Admin also keeps **one platform-wide list of suggested suppliers**, which is two things at once: the list **a business approved from now on starts with its own copy of** instead of an empty supplier list (issue #849, child C of #835), and the **supplier master a price check reads** (issue #931, the first task of #817). Keeping the list is issue #848, child B. The shipped list is nine records: Coles, Woolworths, ALDI, Big W, Kmart, Costco and three JB Metro sites. See [New businesses start with the enabled presets](#new-businesses-start-with-the-enabled-presets) below for exactly who gets them, and [docs/architecture.md § Tenant ownership](docs/architecture.md#tenant-ownership-issue-64) for why this is one of the few tables in the schema that belong to no business.

```text
GET  /api/super-admin/supplier-presets          every preset, enabled and disabled, ordered by name
POST /api/super-admin/supplier-presets          { "name", "url", "servesNationwide", "servedStates",
                                                  "pricesIncludeGst", "priceConnector",
                                                  "priceConnectorKey" } -> 201
PUT  /api/super-admin/supplier-presets/{id}     the same, plus "isEnabled": true|false
```

- **It never writes to a business.** This list is a source to copy *from*. Nothing on this surface reads or writes any business's own suppliers: a preset is never a supplier in a business's own list, editing or disabling one changes nothing a business already has, and a business's supplier of the same name is an entirely separate row that business owns outright. A preset added or re-enabled today is **not pushed** into any existing business (issue #935): it is *offered* to the ones it serves, under **Available to add** on their own Suppliers page, and becomes a supplier only when somebody there adds it — see [Suppliers from the platform list](#suppliers-from-the-platform-list).
- **Who.** The same Entra `(tid, oid)` pair and the same `PlatformDiagnostics` policy as the three surfaces above, checked on every request, plus authentication and the `access_as_user` scope. **With the pair unset — the shipped state — nobody can reach it at all.** A business Owner, Admin or member is refused with `403` like anybody else, and an anonymous request is `401`. A [role preview](#previewing-the-app-as-a-role-issues-720-and-721) is refused on both writes as read-only, exactly as on the Businesses surface, while a previewed **read** is unaffected: this controller declares no capability for a preview to narrow. The `X-Support-Session` header is **not acted on here at all** — as on every endpoint of this class — so an ordinary member who sets it is still an ordinary member refused `403`, and an administrator who happens to hold a live [support session](#super-admin-support-sessions) reaches this surface as themselves, writes included, exactly as they still reach the Businesses surface.
- **What a preset is.** A name (required, trimmed, at most 200 characters), an optional website, whether it is still suggested, the three supplier-master facts below, and the instants it was created and last changed. The website must be a complete `http://` or `https://` address of at most 2048 characters; a blank one is stored as "no website" rather than as an empty string, and nothing fetches, resolves or checks the address. That is not a rule of this feature's own: it is [the one a business supplier's own website uses](#a-suppliers-website) (issue #847), reused here so a preset and a supplier cannot disagree about what a valid address is — which matters because the copy below turns the one into the other.
- **What it serves, what its prices mean, and who collects them (issue #931).** Each record says **which states it serves** — Australia nationwide, *or* one or more of NSW, VIC, QLD, SA, WA, TAS, ACT and NT, never both and never neither — whether **its published prices include GST** (yes for the supermarkets, Kmart and BIG W; no for JB Metro, whose prices are ex-GST), and **how those prices are collected**: nothing at all, Varnmill with a key naming the retailer (`coles`, `woolworths`, `aldi`), Kmart, BIG W, or JB Metro. The key is required for Varnmill and refused for every other route and for no route, and an invalid combination is `400` with a sentence saying which rule it broke. Whether prices include GST is a **recorded fact and never inferred** from a name, a connector or an amount, and it is required on every write so an omitted value cannot silently record a price list as ex-GST. A JB Metro record's **website is the site its connector reads**, which is why a new JB Metro site is a new record and not new code. No price is collected, stored or compared by this feature: it records what a later price check needs to know. Each record also carries **how old one of its prices may be and still be compared** (issue #925): 30 hours for a supplier checked daily, and 102 for Big W, which is checked on Thursday and Sunday — each one the longest expected gap between two checks plus a six-hour margin, so a collection run that starts a little later than yesterday's does not make every price look out of date. It is stored on the record rather than decided in code, so a supplier whose schedule changes is an edit to data; it is **not on this page or in this API yet**, and the shipped records were given their two values by the migration that added the column.
- **Names are unique, ignoring case.** "Coles" and "COLES" are the same suggestion, so the second one is refused with `409` and "A supplier preset with this name already exists." The rule is a unique database index over a stored normalised name, not a check made before the insert, so two requests arriving at once leave one preset and one refusal — never two rows and never a server error.
- **There is no delete.** A preset that should stop being suggested is **disabled**, which is reversible and leaves whatever a business already copied from it untouched. Disabled presets stay in the list, because one nobody can see is one nobody can re-enable.
- **The shipped records are inserted once, by migrations.** The original four came with the migration that created the table; ALDI, Kmart and the three JB Metro records came with the one that added the supplier-master columns, which also set those columns on the four that already existed, matching them **by name**. Neither is seeded at startup and neither is declared as EF `HasData`, both of which re-apply themselves: production applies migrations on startup, so either would quietly undo an edit, or switch a deliberately disabled preset back on, at the next deployment. Rename one, give it a website, change what it serves, disable it or delete it directly in the database, and it stays that way — and a record that was **renamed before** the supplier-master migration ran is left entirely alone by it, which shows on the page as a market that is **not set** until somebody fills it in. A record you had **already created yourself** under one of those five names is left alone in the same way: the migration inserts a record only when that name is free, so your record keeps its name, website, enabled state and instants, its new fields start **not set**, and the upgrade cannot fail on the duplicate name. Reversing that migration removes only the records it created itself, never one that was already there.
- **Audit.** Every committed create and edit emits one structured `ILogger` event with the actor `(tid, oid)`, the preset's key, which change it was, whether the preset is suggested afterwards, what it serves, whether its prices include GST, its connector and that connector's key, the instant and the correlation id. It deliberately carries **neither the name nor the website** — the same restraint the two audits above show about a business's name and contact address — and there is no audit table. A refused create or edit is audited as nothing. **The log destination and retention period are platform configuration, exactly as for the audit trails above, and a human must verify them before relying on this trail.**
- **The page.** The Angular app offers Supplier presets at `/super-admin/supplier-presets`, shown as **Supplier Presets** in the sidebar's **Super Admin** group only while `GET /api/admin/diagnostics/access` confirms the signed-in actor, exactly like the two links beside it; entering the URL directly works identically, because the policy above decides access on every request. The table lists every preset — name, website as a link that opens in a new tab, what it serves, whether its prices include GST, which connector collects them, whether it is suggested, and when it last changed — with **Add preset**, **Edit** on each row, and **Disable**/**Enable** behind a confirmation that says plainly that businesses which already have the supplier keep it. The form offers one switch for nationwide and the eight state checkboxes while it is off, the "prices include GST" switch, and the connector with its key shown only for the route that needs one; it checks every rule with the API's own sentences before sending anything, and shows any refusal the API returns — the duplicate-name `409` included — verbatim.

### New businesses start with the enabled presets

When a Super Admin **approves** a business application, that business is created with **one supplier
of its own per enabled preset that serves the state it trades in**, name and website copied, so it
starts trading with a usable supplier list instead of an empty one (issues #849 and #935). It happens
as part of the approval: either the business becomes active with its copies, or the approval fails and
it is still waiting for a decision — never one without the other.

- **Only the records that serve its state** (issue #935). A record marked *Australia nationwide* goes
  to every business; a record that serves particular states and territories goes only to a business
  trading in one of them — so a Canberra business gets JB Metro South Coast & ACT and a Perth one does
  not. A business with **no state recorded** — every business that existed before that field did —
  gets the nationwide records only: its state is never guessed from its time zone, its address or its
  ABN. A record whose market nobody has set yet goes to nobody. What happens **after** the approval —
  when the business changes state, and when the platform adds a supplier — is in
  [Suppliers from the platform list](#suppliers-from-the-platform-list).
- **Who gets them.** Only a business whose `Pending` application is approved, and only the first time
  it is approved. A business that **already exists** gets nothing and is never backfilled: it already
  has the suppliers it uses, and adding the whole suggested list to it would be a change to its own
  data. An application still waiting when this shipped gets its copies when it is approved, because
  it is a new business.
- **Only the name and the website are copied.** What a preset says about the states it serves, whether
  its prices include GST and how they are collected (issue #931) is platform configuration for price
  checks and is **not** copied: a business's own supplier row has no such fields, and giving it one
  would be a separate decision.
- **Who never gets them.** A rejected application, a deactivation, a reactivation, a repeated
  approval attempt, a business created by `bootstrap-business`, and the disposable end-to-end test
  fixture.
- **Disabled presets are not copied**, and a list with nothing enabled — or nothing that serves the
  business's state — approves the business normally, with no suppliers.
- **A copy is the business's own supplier from that moment on, and it stays linked to the list it
  came from** (issue #934). It is listed on the ordinary Suppliers page under the ordinary supplier
  permission, with no contact details and no GST defaults set — a supplier's GST default is always
  something a business configures deliberately, never something copied or inferred. Editing,
  renaming or disabling a preset afterwards changes nothing any business already has, and a business
  editing its copy changes neither the preset nor another business's copy. What the business may
  change about a linked copy is in [Suppliers from the platform list](#suppliers-from-the-platform-list)
  below.

## Super Admin option visibility

The Super Admin also decides whether **one** Admin option is offered to anybody else: **Historical
GST Classification**, the occasional maintenance tool that is only needed until historical purchase
components have been classified. It is one shared, application-wide setting with one endpoint
(issue #873):

```text
PUT /api/super-admin/option-visibility   { "historicalGstClassificationVisible": true|false }
```

- **Who.** The same Entra `(tid, oid)` pair and the same `PlatformDiagnostics` policy as the surfaces
  above, checked on every request, plus authentication and the `access_as_user` scope. **With the pair
  unset — the shipped state — nobody can change it.** An Owner is refused like anybody else, a member
  who presents a support-session id is still an ordinary member, and a refused request changes
  nothing. A role preview is refused, as every write is.
- **Everyone reads it, nobody else writes it.** `GET /api/me/access` reports
  `historicalGstClassificationVisible` beside the capability set, which is how the menu and the Admin
  page know what to offer. That is the one thing a member reads from a platform-wide setting, and it
  is one boolean: it names no business and nobody's data.
- **It is visibility, not permission.** `POST /api/admin/historical-gst-classification/preview` and
  `.../apply` keep requiring `Data.Repair` in both states, and the capability set a member is reported
  is identical in both. Switching the option off hides the entry and the card; switching it on
  restores them only to members whose role already allowed them.
- **The default is shown**, and it is the absence of any stored setting, so every existing
  installation keeps exactly the visibility it has today until a Super Admin switches it. The setting
  is one row (`PlatformOptionVisibility`) that is created the first time it is switched.
- **Audit.** Every committed change emits one structured `ILogger` event with the actor `(tid, oid)`,
  the resulting state, the instant and the correlation id — and nothing about a business or a member,
  because the setting is about neither. A refused change is not audited, because nothing happened.
- **The page.** The switch is on the Admin page beside the tool's own card, offered to a Super Admin
  alone; see [Hiding this option from other users](#hiding-this-option-from-other-users-super-admin).

## Super Admin support sessions

When a business reports a problem nobody can reproduce from the outside, the Super Admin can open a **support session** on it: a time-limited, audited record that one platform administrator may *read* that business's data. The control endpoints below start, report and end one (issue #550); what a request made *inside* one may do is issue #551, below. See [docs/architecture.md](docs/architecture.md#support-sessions-the-store-and-the-control-endpoints-issue-550) and [its request-pipeline half](docs/architecture.md#support-sessions-in-the-request-pipeline-issue-551) for the complete design.

```text
POST /api/super-admin/support-sessions/{businessId}   { "reason": "..." } (required)
                                                      -> { sessionId, businessId, businessName, startedAtUtc, expiresAtUtc }
GET  /api/super-admin/support-sessions/active         the caller's own session, or 204 when they have none
POST /api/super-admin/support-sessions/active/end     ends it, or 204 when there is none to end
```

- **Holding a session grants nothing; presenting it does** (issue #551). A request joins the session by sending its id in the `X-Support-Session` header. Without that header the administrator still receives `403` from `/api/products` and every other business endpoint, exactly as before, and an ordinary member who sets the header is still an ordinary member.
- **What a request in a session may do: read the business, and nothing else.** It resolves that business and sees exactly its data — the same tenant filters every member's request runs under. Every method other than `GET` and `HEAD` is `403` with code `read_only_support_session`, whatever the endpoint; the database refuses every write on top of that, and so does document storage, so a read with a side effect still changes nothing. **Nayax is refused outright**: the business's own operator id and token are never resolved in a support session, so every Nayax-backed screen fails with `403` and code `unavailable_in_support_view` before anything leaves the API. **The home dashboard degrades instead of failing** (issue #552): `GET /api/dashboard/summary` still returns the business's local figures — the week's sales, the catalogue counts and the inventory valuation — with its two fleet-fed cards replaced by `{ "available": false, "reason": "SupportView" }`, and it makes no Nayax call at all. The wording a client shows for that is issue #513's work.
- **What a session may look at.** Dashboard (including its financial figures), pick list, inventory, purchasing, expenses, reports, imports, site commissions, the Nayax integration settings, the data-repair screens, roles and the business's own settings — read-only. It deliberately excludes the **members** page, because members' names and email addresses stay private in v1, and the supplier GST-defaults editor, which only guards an edit. `GET /api/me/access` inside a session reports exactly that list, no role, and a `supportSession` block with the business's name, the expiry and `readOnly: true`, so a client can show unmistakably whose data is on screen.
- **A session that is over is over.** An expired or ended id, an id belonging to another administrator, and a business that has since been deactivated are all `403` with code `support_session_ended` — checked on every request, before any business data is read, and never falling back to the administrator's own membership. **Every request made in a session is audited** with the route template, the method, the status, the business and the session's key; never a path, a query value or anything that was read.
- **Who.** The same Entra `(tid, oid)` pair and the same `PlatformDiagnostics` policy as the two surfaces above, checked on every request, plus authentication and the `access_as_user` scope. **With the pair unset — the shipped state — nobody can start a session at all.** An Owner is refused here like anybody else.
- **How a session is started.** Only for a business whose status is `Active` (anything else is `409`; an unknown business is `404`), and only with a **reason**, which is required, trimmed and at most 500 characters. The start returns an opaque session id, the business's name and the expiry. **One active session per administrator**: starting a second while one is still running is `409` — end the first one instead.
- **Sixty minutes, decided by the server.** That is both the default and the maximum, and the request carries no duration at all, so nobody can ask for a longer look. A session ends when it expires, when the administrator ends it, or when the person holding it stops being the configured platform administrator — a session is only ever visible, usable and endable by the one actor it was granted to, so changing the configured pair makes every session it granted unreachable.
- **The session id is shown once.** It is returned by the start call and never again — the active-session read deliberately does not repeat it — and the database stores only its SHA-256 hash, so a backup or a database copy yields nothing usable. If it is lost, start a new session; that is audited like the first.
- **Switching a business off ends its sessions.** Deactivating a business ends every active support session for it in the same transaction, so no outstanding session survives the door being closed. Reactivating the business does **not** bring one back: a business returning is not a reason to restore somebody else's access to it.
- **Audit, and the privacy disclosure.** Every session start and every session end emits one structured `ILogger` event with the administrator's `(tid, oid)`, the business id, the session's key, **the reason they gave**, the times involved and why it ended. The reason is recorded precisely so that reading another business's data is reviewable after the fact; it is the administrator's own text, and the trail carries no session id, no business name and nobody else's identity. **Businesses must be told this is possible**: v1 discloses support access in the terms and privacy text rather than notifying a business's Owners in the app, which is a documented follow-up on issue #512. **The log destination and retention period are platform configuration, exactly as for the audit trails above, and a human must verify them before relying on this trail.**
- **Starting and ending a support view (issue #553).** On the [Businesses page](#super-admin-businesses-api), **View as this business** — offered only for an `Active` business — opens a dialog with a required reason; submitting it starts the session, forgets the shell's cached business data, re-reads the signed-in capabilities (now the session's read-only set) and lands on the dashboard. From there, and on every other page, a banner reads "Viewing as *business* — read-only — ends at *time*" with its own **End session**, which does the same three things in reverse: the session ends, the cache is forgotten, the capabilities are re-read, and the dashboard is where it lands. The same three things happen for an expiry too — including one the server reports on any call at all (`support_session_ended`), not only the banner's own end action — and for an end made in another open tab. The banner is red (issue #681), and an end the administrator did not make themselves — the expiry, or a call the server refused as `support_session_ended` — also shows a short "your support session has ended" notice, so the return to their own dashboard is never silent. **Signing out ends the session first**, so it is not left running on the server, and presented by other open tabs, for the rest of its hour; and if a different Microsoft account becomes the signed-in one in that tab, the tab forgets the session, because it belongs to the administrator it was granted to and only they can end it.
- **The session id lives in the browser's memory only, for exactly as long as the tab does.** It is never written to `localStorage`, `sessionStorage` or the URL, so there is nothing to find in a saved page, a shared link or a browser history entry. Every open tab of the same browser shares it over a `BroadcastChannel`, which is how starting a session in one tab also puts the banner on every other tab already open, and how ending it — from any tab — clears it everywhere at once. A tab that was not told the id this way — a reload, or one opened after the session already started — still shows the banner and can still end the session, by asking the control API's own `GET .../active` directly, but it cannot itself present the header until another tab's broadcast reaches it; this is `/api/super-admin/support-sessions`'s own deliberate design (the id is returned by the start call and nowhere else, above), not a frontend gap.
- **A response is only trusted for the session it was asked under.** If the session ends, or a different one starts, while a request made under the previous id is still in flight, that response is discarded — not shown, not acted on — whether it would have been a value or a refusal, because by the time it arrives it no longer describes the business now on screen.

### Previewing the app as a role (issues #720 and #721)

A support session answers "what does this business's data look like?". A **role preview** answers the other question a Super Admin gets asked: "what does an Operator actually see?" The Super Admin adds one header to a request — `X-Role-Preview: Owner`, `Manager` or `Operator` — and the API answers it exactly as that role would be answered: the same pages refused with the same bare `403`, and the same financial figures withheld, because withholding them is the API's job and not the screen's. It works in their own business and inside a support session, where the answer is the session's own read-only set narrowed to that role. The app sends that header for them: see **View as** below. See [docs/architecture.md § Super Admin role preview](docs/architecture.md#super-admin-role-preview-issue-720) for the API and [§ Super Admin role preview in the Angular app](docs/architecture.md#super-admin-role-preview-in-the-angular-app-issue-721) for the screen.

- **It can only ever take access away, and it changes nothing.** The preview is the request's own capabilities *intersected* with the previewed role's, so it never grants anything the request did not already hold — previewing an Owner while you are a Manager still answers a Manager. And a previewed request is **read-only**: every method other than `GET` and `HEAD` is refused with `403` and code `read_only_role_preview`, on every endpoint, including the Super Admin's own. Nothing is written, no audit record is created, and the preview is forgotten the moment the request ends — there is nothing to switch off afterwards.
- **It is the Super Admin's alone, and it must name a role exactly.** Anybody else sending the header is refused with `403` and `role_preview_not_permitted` — never quietly served their own access instead — and a value that is not exactly `Owner`, `Manager` or `Operator` (a different case, a number, a role that does not exist, a blank value, or the header sent twice) is refused with `400` and `invalid_role_preview`. A preview also never resolves a business: a Super Admin with no membership and no support session is refused exactly as they are without the header. `GET /api/me/access` reports the previewed capabilities and still reports the Super Admin's own stored role, because the preview changes what they may do rather than who they are.
- **Behind a proxy or gateway.** The API's own CORS policy allows any request header, so nothing in this repository needed changing. If the API is fronted by a proxy or gateway that enumerates allowed request headers, `X-Role-Preview` must be allowed there the same way `X-Support-Session` is — deployed configuration a human verifies.
- **Starting and ending one in the app** (issue #721). The Super Admin's own menu, top right, offers **View as** with the roles other than the one they already hold — all three of them inside a support session, where they hold none. Choosing one reloads the page as that role: the menu, the pages that open and the figures shown are all the API's answers under the header, so the preview shows what the role really sees rather than a mock-up of it, and the **Super Admin** menu group disappears because no business role has it. A banner on every page reads "Viewing as *role* (preview, read-only)" with **Exit preview** beside it; it is a different colour from the red support-session banner, and both are shown when both apply.
- **It is read-only, and the buttons stay where they are.** Write controls the previewed role is offered are deliberately left visible — that is part of what that role sees — so pressing one reaches the API, which refuses it, and the app answers "Previewing as *role* is read-only. Exit the preview to make changes." Nothing is written.
- **It lasts for this tab, until you leave it.** The preview is held in the browser's memory only: reloading the page or opening a new tab starts with no preview, and nothing is stored or shared between tabs. Pressing **Exit preview**, starting or ending a support session, signing out and switching Microsoft account all end it, as does the API refusing the preview itself — which also says "The role preview has ended."

## Database backup and restore

The production database is a single SQLite file, opened through
`ConnectionStrings:DefaultConnection` (default `Data Source=inventory.db`, a path relative to the
API process's working directory). On Azure App Service the only durable, restart-surviving
location is the `/home` mount; a normal deployment extracts the published app under
`/home/site/wwwroot`, so the default relative path resolves there today, but that default is
fragile — an App Service configured with `WEBSITE_RUN_FROM_PACKAGE=1` mounts `wwwroot`
**read-only**, and placing the database inside the deployment target directory is an avoidable
risk either way. See [docs/architecture.md § SQLite operating assumptions and scale
strategy](docs/architecture.md#sqlite-operating-assumptions-and-scale-strategy-issue-53) for the
full persistence assumptions, concurrency/locking limits, monitoring signals, and the measurable,
evidence-based triggers for moving to a server database (Azure SQL or PostgreSQL) — none of which
are met today.

**Backup and restore** uses SQLite's Online Backup API (the same mechanism behind the `sqlite3`
CLI's `.backup` command and `Microsoft.Data.Sqlite`'s `SqliteConnection.BackupDatabase`), which
produces a consistent snapshot without requiring the API process to stop.

The supported way to take a retained snapshot is the API executable's `backup-database` command
(issue #331) — an early CLI mode, like `bootstrap-business` and `migrate-database`, that never
runs during normal startup. It opens the same configured database the API would, requires an
explicit `--output <path>` outside the API's web root/published content, refuses a destination
that already exists or matches the source, runs `PRAGMA integrity_check` on the result itself, and
reports the snapshot's SHA-256 and duration (never the connection string), exiting non-zero on any
failure:

```bash
dotnet InventoryApi.dll backup-database --output /home/data/backups/inventory-<timestamp>.db
```

This is the same command the scheduled backup WebJob calls; routine backups are scheduled, not run
by hand.

The same command's `--upload` mode (issue #332) creates, verifies and uploads a snapshot as one
workflow, so the verified copy ends up off the instance instead of next to the database it
protects:

```bash
dotnet InventoryApi.dll backup-database --upload
```

It stages the snapshot outside `wwwroot`, writes it to a private Azure Blob container under a
`daily/` prefix (and under `monthly/<YYYY-MM>/` on the first successful upload of each UTC calendar
month, never overwriting an existing recovery point), verifies the upload, and removes the local
staged copy either way. Authentication is the App Service's managed identity through
`DefaultAzureCredential`; the only settings are the non-secret `BackupStorage__BlobServiceUri` and
`BackupStorage__ContainerName`, and there is no account key or SAS token to configure. `--output`
and `--upload` are mutually exclusive. The required container, least-privilege
`Storage Blob Data Contributor` assignment and encryption expectations are in
[docs/architecture.md](docs/architecture.md#sqlite-operating-assumptions-and-scale-strategy-issue-53);
no production Azure resource or role assignment is created by this repository.

**The schedule (issue #333)** is a triggered Linux App Service WebJob that ships inside the API's
own `dotnet publish` output, as
`backend/InventoryApi/App_Data/jobs/triggered/database-backup/` (`run.sh` plus a `settings.job`
holding the six-field CRON `0 0 15 * * *`, daily at 15:00 UTC). Because **Deploy Production**
deploys the complete publish directory, the job reaches production with the API and no GitHub
Actions workflow changed. The script holds no backup logic, no database path and no setting: it
locates the published application, runs `dotnet InventoryApi.dll backup-database --upload` from
that directory so the configured connection string resolves exactly as it does for the API, logs
start, completion or failure with the elapsed duration, and exits with the command's own exit code
so a failed backup is a failed WebJob run. Activating it is human work — the App Service plan,
**Always On**, the configured database path and confirming the first run — and the prerequisites
with their verification steps are in [docs/architecture.md § Scheduling the backup with an App
Service
WebJob](docs/architecture.md#scheduling-the-backup-with-an-app-service-webjob-issue-333).

The equivalent manual `sqlite3` CLI sequence remains available where the published `dotnet`
application is not on hand:

```bash
sqlite3 /home/data/inventory.db ".backup '/home/data/backups/inventory-<timestamp>.db'"
sqlite3 /home/data/backups/inventory-<timestamp>.db "PRAGMA integrity_check;"
```

Store the verified backup somewhere other than the App Service's own `/home` mount — which is what
`--upload` does for you. Restoring (`sqlite3 <backup> ".backup '/home/data/inventory.db'"`)
overwrites live data and must be run deliberately, by a human, after stopping the API — never
automatically. Retention/alerting and automated restore remain out of scope for
`backup-database`. The full
step-by-step procedure, including why a plain filesystem copy is unsafe on a live database, is in
[docs/architecture.md](docs/architecture.md#sqlite-operating-assumptions-and-scale-strategy-issue-53).

**Non-destructive local validation.**
`backend/Inventory.IntegrationTests/Operations/SqliteBackupRestoreTests.cs` proves the backup mechanism
itself and, built on top of it, the `backup-database` command's path validation, verification,
hashing, and failure reporting (`DatabaseBackupRunnerTests`) and its argument parsing
(`BackupDatabaseArgumentsTests`). The upload workflow is covered the same way:
`DatabaseBackupUploadRunnerTests` drives create-verify-upload-cleanup end to end and
`AzureBlobBackupUploaderTests` covers the object naming, the monthly recovery point and the
refusals — both against an in-memory stand-in for the container, so no Azure account, credential
or network is involved. `BackupWebJobPackagingTests` covers the schedule: that both WebJob files are
published, that the schedule is the daily 15:00 UTC expression, and — by running the real `run.sh`
against a fake `dotnet` — that it invokes `backup-database --upload` from the application's
directory, names no database path, and propagates the exit code. All of it runs as part of the
normal test suite and only ever touches throwaway files under the OS temp directory, never a
developer's or production `inventory.db`:

```bash
dotnet test backend/InventoryApi/InventoryApi.slnx --filter "FullyQualifiedName~SqliteBackupRestoreTests|FullyQualifiedName~DatabaseBackupRunnerTests|FullyQualifiedName~BackupDatabaseArgumentsTests|FullyQualifiedName~DatabaseBackupUploadRunnerTests|FullyQualifiedName~AzureBlobBackupUploaderTests|FullyQualifiedName~BackupWebJobPackagingTests"
```

To rehearse the `sqlite3` CLI sequence above before relying on it in production, run it against a
disposable local copy, never the live file:

```bash
cp inventory.db /tmp/inventory-check.db
sqlite3 /tmp/inventory-check.db ".backup '/tmp/inventory-check-backup.db'"
sqlite3 /tmp/inventory-check-backup.db "PRAGMA integrity_check;"
rm /tmp/inventory-check.db /tmp/inventory-check-backup.db
```

## Health checks

The API exposes two unauthenticated ASP.NET Core health-check endpoints (issue #164) so Azure App
Service and operators can tell an API process that is merely running apart from one that can
actually serve Inventory App requests:

- **`GET /health/live`** — liveness. Returns `200` whenever the process can answer HTTP requests.
  It runs no dependency checks at all, so it stays healthy through a database, Nayax, Entra ID, or
  Key Vault outage. Use it only to detect a hung or crashed process.
- **`GET /health/ready`** — readiness. Returns `200` only when the API can serve normal requests;
  today that means the database (`AppDbContext`) is reachable, via a health check that calls
  `Database.CanConnectAsync()`. Returns `503` when the database is unreachable. It deliberately
  excludes Nayax and Microsoft Entra ID — an outage in either external system must not make the
  whole Inventory API report unhealthy. Add a dependency to readiness only when it represents
  something that truly prevents the API from serving requests.

Both endpoints allow anonymous access (`AllowAnonymous()` in `Program.cs`) and are unaffected by
`BusinessScopeMiddleware`, which already leaves unauthenticated requests alone.

**Azure App Service configuration.** Set the App Service **Health check** path (Portal: App
Service → Monitoring → Health check; or the `healthCheckPath` site configuration property) to
`/health/ready`, so App Service routes traffic only to instances that can reach the database and
restarts instances that cannot. Do not point Health check at `/health/live` — that would keep an
instance in rotation even while its database connection is down.

## Observability and error diagnostics

The API reports failures to **Azure Application Insights** through the Microsoft-supported Azure
Monitor OpenTelemetry distribution (`Azure.Monitor.OpenTelemetry.AspNetCore`, issue #165), so an
operator can search retained logs, exceptions, requests and dependencies after the fact and
correlate them by trace/operation ID. It is the only instrumentation pipeline in the process: the
classic Application Insights SDK is deliberately not referenced, because two pipelines double the
cost and emit duplicate telemetry that no longer correlates.

Registration lives in one place,
`backend/InventoryApi/Observability/ObservabilityServiceCollectionExtensions.cs`, called from
`Program.cs` before `builder.Build()`. It collects, automatically and with no per-feature code:

- incoming ASP.NET Core requests;
- outgoing `HttpClient` dependencies, which is how every Nayax Lynx call is recorded;
- the SQL dependencies the distribution's `SqlClient` instrumentation supports;
- runtime, HTTP and ASP.NET Core metrics;
- every `Microsoft.Extensions.Logging` log and the exception attached to it.

Application code logs through `ILogger` only. Nothing calls a telemetry client directly, so
telemetry can be switched off — or replaced — in that one file.

**App Service configuration.** Set one application setting (Portal: App Service →
Settings → Environment variables; or `az webapp config appsettings set`):

```text
APPLICATIONINSIGHTS_CONNECTION_STRING=<connection string of the Application Insights resource>
```

Its value is a secret and is never committed to this repository, written into a pull request, an
issue, or a log. The Application Insights resource itself is created and connected by a human in
Azure; no Azure resource is provisioned from repository automation.

**Telemetry is optional, by design.** With the variable absent or blank, nothing OpenTelemetry-related
is registered and the API starts and serves requests exactly as before. Telemetry is diagnostics,
not a dependency the API needs in order to work, so a missing setting must never be a startup
failure. Local development and the whole test suite run that way.

### Error handling and what reaches a log

The four centrally registered exception handlers decide both the caller's response and the log
entry (see
[docs/architecture.md § Domain and application error mapping](docs/architecture.md#domain-and-application-error-mapping)):

| Failure | Response | Logged as |
| --- | --- | --- |
| Expected validation failure (`DomainValidationException`, `InsufficientStockException`) | `400` with the caller-safe message | not logged — it is a normal outcome, not an error |
| Expected business conflict (`DomainConflictException`) | `409` with the caller-safe message | one `Warning` with the trace ID and the caller-safe detail |
| This business's Nayax connection is unusable (`NayaxNotConnectedException`) | `409` with the stable "Nayax is not connected" message, the `nayax_not_connected` code and the connection status | one `Warning` with the error code, the connection status, the request method and path, and the trace ID |
| Nayax refused one feature for lack of permission (`NayaxPermissionNotGrantedException`) | `409` with the stable "Nayax hasn't granted permission for this" message and the `nayax_permission_not_granted` code | one `Warning` with the error code, the refused client operation, the request method and path, and the trace ID |
| Nayax upstream failure (`NayaxUpstreamException`) | `502`, fixed generic detail | one `Error` with the operation, upstream method, relative endpoint and numeric upstream status, plus the request method, path and trace ID |
| Anything else | `500`, fixed generic detail | one `Error` with the exception, the request method and path, and the trace ID |
| Caller cancellation | ASP.NET Core's normal handling | not logged — the caller went away, the server did not fail |

Every response carries a `traceId` and no stack trace, exception type or unexpected exception
message, so the caller can quote an identifier that finds the server-side detail without being
told any of it.

**Safe logging rules.** Telemetry is retained and searchable, so these are hard rules, not
preferences:

- Never log a Nayax token, an `Authorization` header, a cookie, a connection string or any other
  credential. The request's query string and headers are never logged for this reason: the handlers
  add only the request **method** and **path**.
- Never log document contents, customer information or sensitive business data.
- The Nayax upstream handler logs only fields this application composed and deliberately does not
  attach the exception, because its inner transport exception's message is outside this
  application's control and can repeat a request header or an upstream response body.
- The per-query EF Core SQL command log is suppressed in deployed environments; it is the most
  expensive category and the one most likely to carry business data.

### Logging levels

The deployed levels are in `backend/InventoryApi/appsettings.json` (there is no committed
`appsettings.Production.json`), and every log that passes them is exported and retained. The policy
is "every application warning and error, and nothing a framework emits per request":

| Category | Level | Why |
| --- | --- | --- |
| `Default` | `Information` | application code (`InventoryApi.*`, `Inventory.Application.*`, …) |
| `Microsoft`, `Microsoft.AspNetCore` | `Warning` | per-request framework noise |
| `Microsoft.Hosting.Lifetime` | `Information` | the record that a deployment came up |
| `Microsoft.EntityFrameworkCore`, `…Database.Command` | `Warning` | suppresses the per-query SQL log |
| `Microsoft.EntityFrameworkCore.Migrations` | `Information` | Production startup applies pending migrations (issue #201), so "Applying migration …" is an audit record |
| `Microsoft.Identity.Web` | `Warning` | per-request token validation detail |
| `System.Net.Http`, `Azure` | `Warning` | SDK/HttpClient chatter, and it keeps the exporter's own logs out of the exporter |

No category may be set below `Warning`: that would hide a real failure from the only place an
operator can look afterwards. `appsettings.Development.json` raises the framework, EF command and
Identity.Web categories back to `Information` for local debugging, where nothing is exported.
`backend/Inventory.IntegrationTests/Observability/LoggingLevelPolicyTests.cs` enforces all of this.

### KQL troubleshooting queries

Run these in the Application Insights resource (Portal: Application Insights → Monitoring → Logs).

Recent exceptions:

```kusto
exceptions
| where timestamp > ago(24h)
| project timestamp, operation_Id, type, outerMessage, problemId, operation_Name
| order by timestamp desc
```

Error-level traces, with the structured properties the handlers attach:

```kusto
traces
| where timestamp > ago(24h) and severityLevel >= 3     // 3 = Error, 4 = Critical
| project timestamp, operation_Id, message,
          method = tostring(customDimensions.Method),
          path = tostring(customDimensions.Path),
          traceId = tostring(customDimensions.TraceId)
| order by timestamp desc
```

A business's Nayax connection refusing calls (issue #520) — these are `Warning`, not `Error`, so
the query above does not show them. `NayaxErrorCode` is `nayax_not_connected` or
`nayax_permission_not_granted`, and `NayaxConnectionDetail` is the connection status or the refused
client operation:

```kusto
traces
| where timestamp > ago(24h) and message startswith "Nayax connection unavailable"
| project timestamp, operation_Id,
          code = tostring(customDimensions.NayaxErrorCode),
          detail = tostring(customDimensions.NayaxConnectionDetail),
          path = tostring(customDimensions.Path),
          traceId = tostring(customDimensions.TraceId)
| order by timestamp desc
```

Everything recorded for one request, from the `traceId` the caller was given. That value is a W3C
`traceparent` (`00-<trace-id>-<span-id>-<flags>`), and Application Insights indexes its middle
segment as `operation_Id`:

```kusto
let responseTraceId = "<traceId from the ProblemDetails response>";
let operationId = tostring(split(responseTraceId, "-")[1]);
union isfuzzy=true requests, dependencies, traces, exceptions
| where operation_Id == operationId
| project timestamp, itemType, name, message, resultCode, success, duration
| order by timestamp asc
```

### Verifying a change

Automated, as part of `bash scripts/validate.sh`:

```bash
dotnet test backend/InventoryApi/InventoryApi.slnx --filter "FullyQualifiedName~InventoryApi.Tests.Observability|FullyQualifiedName~ExceptionHandlerTests"
```

That covers telemetry registration with a synthetic, non-secret connection string, startup with
the setting absent, the deployed logging levels, the structured `Method`/`Path`/`TraceId`
properties on an unexpected-exception log, the log levels for expected domain failures, and the
rule that no header, query string, token or upstream body reaches a log entry.

By hand, against a running API:

1. Start the API with no `APPLICATIONINSIGHTS_CONNECTION_STRING` and confirm it starts and
   `GET /health/ready` answers `200`.
2. Call an endpoint that fails unexpectedly and confirm the response is a generic `500`
   `ProblemDetails` with a `traceId` and no exception detail, and that the console shows exactly
   one `Error` entry with that trace ID.
3. Call an endpoint that fails validation and confirm nothing is logged at `Error`.

Confirming that telemetry actually arrives in Azure requires the production connection string and
is a human step performed outside this repository: set the App Service setting, restart the app,
and run the KQL queries above.

## Validate a change

Run the repository-level validation from the root:

```bash
# macOS, Linux, or Git Bash
bash scripts/validate.sh

# PowerShell 7
pwsh -File scripts/validate.ps1

# Windows PowerShell
powershell -ExecutionPolicy Bypass -File scripts/validate.ps1
```

`scripts/validate.sh` and `scripts/validate.ps1` are the single local entry point and run the same pipeline:

| # | Step | What it checks | Blocking |
| - | ---- | -------------- | -------- |
| 1 | Test agent eval runner | `node --test scripts/run-agent-evals.test.mjs` | yes |
| 2 | Run agent eval corpus | `node scripts/run-agent-evals.mjs` against `evals/agent/cases/` (see `evals/agent/README.md`) | yes |
| 3 | Restore backend | NuGet restore for `backend/InventoryApi/InventoryApi.slnx` | yes |
| 4 | Verify C# formatting | `dotnet format --verify-no-changes` against the root `.editorconfig` | yes |
| 5 | Build backend | Release build with .NET analyzers, code-style enforcement and warnings-as-errors | yes |
| 6 | Test backend with coverage | xUnit suite plus Coverlet line/branch coverage | yes |
| 7 | Check vulnerable NuGet packages | `dotnet package list --vulnerable --include-transitive` | yes, when a vulnerable package is reported |
| 8 | Install frontend dependencies | `npm ci` against the committed lock file | yes |
| 9 | Lint frontend | `npm run lint` (`ng lint`, ESLint + angular-eslint over TypeScript and templates) | yes, on lint **errors** |
| 10 | Test frontend | `npm run test` (Jest, via `jest-preset-angular`) | yes |
| 11 | Build frontend | Angular production build | yes |
| 12 | Audit frontend dependencies | `npm audit` | no, report only |

### Backend code quality

Quality settings are centralised so every backend project gets them:

- **`Directory.Build.props`** (repository root) enables nullable reference types, .NET analyzers at the latest analysis level, `EnforceCodeStyleInBuild`, and `TreatWarningsAsErrors`. A new warning in application code fails the build.
- **`.editorconfig`** (repository root) holds formatting, naming and diagnostic severities for the whole repository, and is what `dotnet format` enforces. Rules set to `suggestion` are IDE guidance only; only `warning`/`error` rules can fail validation.
- EF Core generated migrations are the one scoped exception. Their all-lowercase generated class names raise `CS8981`, which `.editorconfig` switches off under `[**/Migrations/*.cs]` only — never globally and never through `<NoWarn>` — because an applied migration must not be renamed. `dotnet format` skips the `Migrations` folder for the same reason.
- Coverage is collected on every run (`--collect:"XPlat Code Coverage"`) and written per test project to `backend/Inventory.UnitTests/TestResults/<run-id>/coverage.cobertura.xml` and `backend/Inventory.IntegrationTests/TestResults/<run-id>/coverage.cobertura.xml`, which are git-ignored. There is deliberately **no** minimum-coverage threshold yet; this establishes the baseline.
- A backend test that runs past 90 seconds is treated as hung: `dotnet test --blame-hang` stops the run and writes a full memory dump of the test host and a `Sequence_*.xml` naming the tests in flight next to the coverage output. In CI, a failed validation run uploads them as the `hung-test-diagnostics-<run-id>-<attempt>` artifact (kept 7 days); open the dump with `dotnet-dump analyze` and run `clrstack -all`.
- Architecture tests in `backend/Inventory.IntegrationTests/Architecture/` enforce the Clean Architecture dependency direction (Domain ← Application ← Infrastructure ← InventoryApi) and keep ASP.NET/EF Core/HTTP types out of Domain and Application. `ProjectDependencyDirectionTests` reads the project files; `CleanArchitectureDependencyTests` (NetArchTest) checks the compiled assemblies; `ApiLayerOwnershipTests` keeps `InventoryApi` to the HTTP boundary and composition root, so no business service, financial rule or persistence implementation can return to it.

### End-to-end workflow tests

The browser-level end-to-end suite (issue #46) is **not** part of `scripts/validate.sh`: it drives a real Chromium against a real API, so it is an opt-in command with its own npm project (`frontend/inventory-app/e2e`) and its own dependencies. Keeping Playwright out of `frontend/inventory-app`'s dependency graph is deliberate — `npm ci` runs during repository validation and during the production deployment, and neither may start downloading a browser.

```bash
# once per machine (installs the suite's dependencies and the Chromium build it pins)
npm --prefix frontend/inventory-app run e2e:install

# run the suite headlessly
npm --prefix frontend/inventory-app run e2e

# watch it in a visible browser
npm --prefix frontend/inventory-app run e2e:headed
```

Prerequisites: the .NET SDK and Node/npm you already need to build the repository, plus the shared libraries Chromium needs. On a bare Linux machine install those once with `npm --prefix frontend/inventory-app/e2e run browsers:with-deps` (it uses `sudo apt-get`), or install the distribution's Chromium dependencies by hand. No secret, token, Entra account or Nayax credential is involved, and nothing has to be running before you start: Playwright starts the API and the Angular dev server itself and stops both afterwards.

What a run does, and why it is safe to repeat:

- It starts the API as the dedicated **E2ETest** host on `http://127.0.0.1:5199` against a throwaway SQLite database under `frontend/inventory-app/e2e/.artifacts/` (git-ignored, deleted at the start of every run), and the Angular application on `http://127.0.0.1:4300`. Your own `inventory.db` is never opened.
- That host, and only that host, authenticates the suite's synthetic test actors instead of real Microsoft Entra sign-in, and seeds two synthetic businesses with a small catalogue. See [docs/architecture.md § End-to-end testing authentication](docs/architecture.md#end-to-end-testing-authentication-issue-46) for the scheme and its fail-closed safeguards; the backend suite in `backend/Inventory.IntegrationTests/Auth/` proves it cannot be reached in any other environment, and those tests *are* part of `scripts/validate.sh`.
- It registers no Nayax HTTP client at all, so no run can reach the live Nayax operator account.
- Tests run serially against that one host, each on its own seeded product, and the covered workflows are the reorder → supplier order → receive-as-purchase chain, the positive-magnitude stock correction, purchase create/edit/delete inventory effects, the COGS/profit-unavailable report state, and two-business isolation.

Failure output (screenshots and traces) is written under `frontend/inventory-app/e2e/.artifacts/test-results/`; open a trace with `npm --prefix frontend/inventory-app/e2e exec -- playwright show-trace <path>`.

### Frontend code quality

- `npm run lint` runs `ng lint`, configured through `frontend/inventory-app/eslint.config.js` (ESLint 9 flat config with `angular-eslint` and `typescript-eslint`).
- Rules are chosen to catch defects rather than style: unused variables and imports, `==` vs `===`, unreachable and constant-condition code, Angular template errors, and Angular lifecycle/interface mistakes are **errors**. Accessibility findings, `trackBy`, `any` and stray `console.log` are **warnings** so they are visible without blocking a build.
- `npm run test` runs the Jest suite (`jest-preset-angular`, configured through `frontend/inventory-app/jest.config.js`/`tsconfig.spec.json`/`setup-jest.ts`) once and exits; it is not a watch-mode command. `*.spec.ts` files sit next to the code they test.
- `npm audit` is reported but not enforced. The outstanding high/critical advisories are all in the Angular 19 build toolchain (`@angular-devkit/build-angular` → `vite`, `webpack-dev-server`, `tar`, …) and every published fix requires a major Angular upgrade, which is a separate, deliberate piece of work. Review the printed report; do not run `npm audit fix --force`.

### GitHub Actions runners

The repository variable `INVENTORY_AGENT_SELF_HOSTED_ALL` (Repository Settings → Secrets and variables → Actions → Variables) chooses where every GitHub Actions job runs, Deploy Production included. Exactly `true` runs every job on the self-hosted Linux x64 runners labelled `[self-hosted, linux, x64, inventory-agent]`; `false` or unset runs every job on GitHub-hosted `ubuntu-latest`; any other value (including `TRUE`) makes every job fail in its first step, **Report and validate runner mode**, which also logs the mode and the runner each job got. This is an explicit choice, not a fallback: if the selected runners are offline, jobs wait. Changing the variable affects newly scheduled jobs only; running jobs stay put and queued jobs are not moved, so cancel and re-run them (re-apply the readiness label for an implementation run). GitHub-hosted mode uses GitHub-hosted runner minutes under the account's existing plan and spending limit. `scripts/validate-runner-placement.mjs` fails validation if a workflow selects its runner any other way.

In self-hosted mode, while no runner is online, jobs wait in the queue. To start them, bring the runner VM and its runner service back up and confirm the runner shows **Idle** under Repository Settings → Actions → Runners. The runner host must provide `git`, `curl`, `jq`, `tar`, `gzip`, `unzip`, `/usr/bin/gh`, the Azure CLI and a Docker daemon (GitHub's `ubuntu-latest` image already has them). Jobs install .NET and Node.js themselves. Run **Actions → Inventory agent runner diagnostic** in self-hosted mode to check a runner; in GitHub-hosted mode it checks the hosted runner and says it did not verify your VM.

Choosing the mode is separate from verifying the VM. In self-hosted mode, jobs that hold a credential stay disabled until a human provisions reset or ephemeral runners and sets the repository variable `INVENTORY_RUNNER_ISOLATION_VERIFIED` to `true`. That covers the Claude agents, the SonarCloud and Project sync reads, both production deploy jobs, and every job with a write-capable `GITHUB_TOKEN`: dispatchers, review publication, branch cleanup, the release record and the validation status jobs. Until then those runs fail with an error naming the variable, and pull requests do not receive the required `merge-validation` status, so they cannot merge without an administrator bypass, although the validation job itself still runs. Deploy Production in self-hosted mode also needs `INVENTORY_RUNNER_BASELINE_VERIFIED` set to `true` once the diagnostic has passed on every self-hosted runner. In GitHub-hosted mode neither verification variable is required, because each job gets a fresh GitHub-hosted VM; Deploy Production instead checks its tools on the hosted runner before it changes anything, and keeps its manual start, `main`-only exact commit, environment approval, API-before-frontend order and health checks. See [docs/automation.md § Runner placement](docs/automation.md#runner-placement-issues-645-and-807) for the isolation limits of a persistent runner, recovery and the cost boundaries.

## Important domain rules

- Gross vending sales are not the same as a Nayax payout. Card and cash revenue remain distinct.
- A machine refill is an internal stock transfer, not COGS or an expense.
- Historical sale cost is persisted from internal AVCO when reliable, with transaction-level Nayax product cost as a fallback.
- Missing COGS or profit remains unknown; it is never silently converted to zero.
- Australian financial years run from 1 July to 30 June. Business days, and therefore every report date boundary, follow the business's own configured IANA timezone (`Business.TimeZoneId`, issue #499); the existing business is `Australia/Sydney`. A business whose zone cannot be resolved gets no business dates at all rather than another zone's.
- Purchase amounts are GST-inclusive, and purchase input GST comes from an explicit per-line and per-charge classification, never from an amount. Anything unclassified stays visibly unresolved rather than being treated as GST-free.
- UI reports and CSV/XLSX exports must use the same backend calculations and quality states.

The complete invariants and change rules are in [AGENTS.md](AGENTS.md).

## Purchase entry and GST classification

Purchase amounts are entered **GST-inclusive**. GST is recorded per component, so the **Add Purchase** page
and the **Edit Purchase** page each offer a GST picker with three states — **Taxable**,
**GST-free** and **Not classified** — for every purchased item line and, separately, for the delivery charge
and the package charge. A charge never inherits a line's classification, and there is no single
classification for a whole purchase.

**Edit** on a purchase row opens the Edit Purchase page at `/purchases/<id>/edit`; nothing expands inside the
Purchases table. That address is bookmarkable and survives a page refresh, **Save** stores the changes and
returns to the Purchases list, and **Cancel** returns without saving. Leaving the page by Cancel, by the
browser's Back button or by any other link discards unsaved edits without asking first, which is how every
other form page in the application behaves.

**Entering a line's price.** Stock often arrives in boxes priced per box, so each purchased item line shows
both of its prices side by side — **Unit price ($)** and **Total price ($)** — and you can type into either
one. The other is calculated from that line's purchased unit quantity, so you can check both before saving:
type $32.50 into Total price for 20 units and the Unit price fills in as $1.6250; type $1.50 into Unit price
for 28 units and the Total price fills in as $42.00. The figure you typed **last** is the one the other
follows, so if you change the quantity afterwards, a total you typed is spread over the new quantity while a
unit price you typed is multiplied by it. A line is still saved as its quantity and unit cost, and a total
that does not divide evenly — $100.00 over 300 units, for example — is saved so the line still comes to
exactly the $100.00 you entered, even though the Unit price field shows the rounded $0.3333. Add Purchase
starts each line on the total paid; a line already on a purchase you are editing starts on its recorded unit
price, so correcting only its quantity keeps the cost it was recorded at, and neither receiving a supplier
order nor opening the edit page changes a line's unit cost until you type a price yourself. The purchase's own
**Total Amount**, **Delivery Cost** and **Package Cost** are unaffected and are still entered as plain
amounts.

- Everything starts as **Not classified**, including lines prefilled while receiving a supplier order.
  Nothing is guessed from the product, the supplier or the amount, so a line is classified only when a
  person chooses. **Not classified** contributes no GST and keeps the purchase visibly unresolved for
  bookkeeping review; it does not mean GST-free.
- A charge's picker appears only once that charge has a value. A delivery or package charge left empty (or
  zero) has no classification at all and is never reported as unresolved.
- Editing a purchase shows the stored classifications and sends only the ones you change, so a
  classification that came from a configured product or supplier rule keeps that origin when you edit a
  quantity, a cost, a date or another line. Choosing **Not classified** again is a deliberate change and is
  saved as one.
- A line already on the purchase keeps its product: the edit page shows the product name rather than a
  picker, because a stored line's GST classification, its origin and its restock movement belong to that
  product. To record a different product, **Remove** that line and add the new product as its own line; the
  new line starts **Not classified** and carries none of the removed line's classification or history. You can
  still change a stored line's quantity, unit cost and classification.
- If a save is refused — an unsupported classification, for example — the edit page stays open with everything
  you entered and shows the reason, so a rejected classification never looks like a saved one.
- Each purchase row then shows the API's **Purchase GST (input tax credit)** figure, each component's
  classification, and a warning naming how many components and how much money are still unclassified. Those
  figures are calculated by the API from the saved purchase — the frontend never calculates GST — so they
  update on the Purchases list once your edit is saved.

GST classification is accounting data only. It never changes a purchase's unit cost, the weighted-average
cost, costing quantity or inventory value.

## A purchase without a receipt

A purchase is the record of what you bought; the receipt is paperwork about it. So the **Supporting Document**
field on Add Purchase is optional: leave it empty and the purchase is saved exactly as it would be with a
receipt attached - the same stock, the same cost, the same GST, the same supplier-order handling. A purchase
still needs a **Purchase Title**, because with no file there is no file name for it to fall back to.

The Purchases list says **No document** for a purchase that has none, and offers **Add document** on that row.
Pick the receipt when it turns up and it is added to the purchase that was already recorded; it then opens and
downloads from the list like any other.

- Adding a document **adds** one; it never replaces one. A purchase that already has a document is left alone,
  and if somebody else attaches one between your choosing a file and pressing the button, you are told the
  purchase already has a document rather than having theirs quietly thrown away.
- The receipt itself follows the same rules it always has: an image or a PDF, up to 10 MB. A file that is
  refused leaves the purchase exactly as it was, with no document recorded.
- **Add document** is not offered while a Super Admin is looking at your business in a support session, for
  the same reason Delete is not: a support session can read, never write.

## Receiving a supplier order: keep it open, or complete it

**Receive / Create Purchase** on the Supplier Orders page opens Add Purchase with the order's outstanding
lines prefilled, exactly as before. Suppliers do not always send everything they were ordered, though, and an
order you record a short final delivery against used to stay **Partially received** until you cancelled it
separately — which said the wrong thing, because the goods that *did* arrive were delivered and recorded.

So when you press **Add Purchase** on a purchase that came from a supplier order, and the order would still
be short of something afterwards, the application asks what should happen to the remainder before it saves
anything:

- **Keep order open** — the safe default, and the behaviour saving the purchase has always had. The order
  stays **Partially received**, and the quantities that have not arrived stay on order: they still count
  towards the restock target, so the products do not ask to be ordered again.
- **Complete order** — nothing more is expected. The purchase is saved and the order is closed in the same
  step, with the status **Completed — partially delivered**.
- **Back to purchase** — closes the question and saves nothing. Everything you entered — the document, the
  title, every line, the charges and the classifications — is still on the form.

The dialog lists the affected products with what was ordered, what had already been received, what this
purchase delivers and what would be left outstanding, each in the unit the product is ordered in. Those
figures come from the server, which projects your entered lines against the order's own stored receipts using
the same allocation it will use when it saves — the page does not work them out itself.

**What completing an order does, and what it deliberately does not.**

- It records **only what actually arrived**. The quantities that never came create no stock, no cost, no
  weighted-average change, no GST and no purchase amount of any kind. Nothing on the order is rewritten to
  make it add up, and a line this purchase left out entirely is closed at the zero that really arrived.
- The order keeps its **ordered quantities, its real receipts and its purchase links** as history, with the
  undelivered balance readable beside them, and records who completed it and when. It is **not** relabelled
  **Cancelled** (goods really were delivered) and **not** relabelled **Received** (part of it never came).
- The closed balance stops being **on order**. It no longer counts towards a restock target, so the products
  on it start asking to be ordered again — which is the point: nobody is sending them.
- A completed order accepts **no further receipts**, is no longer listed among the open orders, and cannot be
  cancelled. Reopening one is not supported.
- Because the order was closed on that purchase's receipts, **that purchase can no longer be edited or
  deleted**. Both are refused with a message naming the order, rather than silently reopening it or leaving
  it recording a delivery no purchase holds.

**A fully received order is unchanged.** If your purchase receives everything still outstanding, nothing is
asked and the order becomes **Received** as it always did. A purchase that is not linked to an order is
unchanged too.

**If the order changed while you were deciding** — somebody else recorded another receipt, or cancelled or
completed it — the save is refused, **nothing at all is stored** (not the purchase, not its stock, not the
order's status), and the page asks you to reload the order and review the outstanding quantities with your
entered purchase still on screen. The same applies if the save fails for any other reason: nothing ever
reports an order as completed unless the server stored it.

## Products and categories

The catalogue is the business's own. A business with no sales provider connected creates and
maintains every product and category itself, and a business with one keeps the fields its provider
does not manage.

- **Create a product** with `POST /api/products`, and a **category** with `POST /api/categories`.
  Both answer the created record with its new id. A product needs a name and a **unit label**; a
  category needs a name,
  unique within the business (compared without case, so
  "Drinks" and "drinks" are the same category). Creating a product with an opening quantity records
  the initial restock movement it always did, at the optional initial unit **cost**.
- **Unit Label is required** (issue #895). It is what a quantity of this product is counted in — a
  bag, a can, a bottle — and every product you create or edit by hand has one. **Products → New
  Product** and **Products → Edit Product** mark the field with `*`, refuse to save while it is
  empty or only spaces, and say "Unit label is required." beside it; what is saved is the label
  trimmed. New Product starts with the field **empty** (it used to say `unit`), so you type the
  label the product is counted in; saving without one is refused. **A product from before this rule, with no label stored, opens Edit
  Product with the field empty** rather than with `unit` filled in for you: you choose the label it
  should have, and nothing was changed or filled in behind you. `POST /api/products` refuses a
  missing label with `400` and that same sentence, and saves nothing. `PUT /api/products/{id}`
  refuses one **only when the product's stored label is not blank** — no request can erase a label —
  while a product whose stored label is already blank is still accepted with none, so the Products
  list's **Deactivate** and **Reactivate** (which send the whole product back) keep working for
  those older products. Imported products are unaffected: the catalogue import gives a product it
  creates the `unit` default and leaves a linked product's own label alone, and no label is
  backfilled anywhere. **One feature does require a real label**: supplier price tracking cannot be
  switched on for a product whose label is blank or is still that `unit` default, because the label
  is half of what is searched for at a supplier — see
  [Track supplier prices on a product](#track-supplier-prices-on-a-product).
- **New Product is read down in titled sections** (issue #892). **Products → New Product** groups
  its fields under **Product details** — Name beside SKU, Category beside Unit Label, Description
  across the row below them, and then the compact **Active product** checkbox — **Opening
  inventory** (Opening Stock beside Initial Unit Cost) and **Reordering** (Low Stock Threshold
  beside Restock To, with the same hints and the same "Restock To must be greater than or equal to
  the Low Stock Threshold" message). Each section is one column on a narrow screen and two columns
  from a medium one, and the controls in a row start at the same height, so a field's help text no
  longer pushes the control beside it to the bottom of the row. **GST rule** is the last choice the
  page asks for and the single **Create Product** / **Cancel** row is below it, by reading and by
  Tab alike — so the rule is chosen before the product is created. Nothing else changed: the same
  fields, values, defaults, labels and validation, the same category picker, the same
  support-session restriction, and the same two writes described next. **Editing** a product keeps
  its existing layout, because a provider-managed name and category are locked there.
- **Choose the new product's GST rule while creating it** (issue #829). **Products → New Product**
  offers the same **GST rule** picker the edit page does — **Taxable**, **GST-free** or **No rule**,
  with the same meanings and the same help. The rule is its own resource, so creating a product with
  one is two writes: `POST /api/products`, then `PUT /api/products/{id}/gst-rule` on the id the
  create answered. **No rule** needs no second write, because a new product already carries no rule.
  If the rule write fails the page says exactly that — "The product was created, but its GST rule
  was not saved." — keeps the product that now exists along with the rule you chose, and offers
  **Save GST rule** beside the picker, which retries the rule alone with the choice on screen now.
  **Create Product** is gone from the row below at that point, so there is no way to create a
  second product; a create
  whose rule was not stored is never reported as finished, and you can also leave the page and set
  the rule later by editing the product.
  Choosing the rule here classifies no purchase and is not retroactive — see
  [Product and supplier GST rules](#product-and-supplier-gst-rules).
- **Find a category, or create a missing one, without leaving New Product** (issue #830). The
  **Category** field on **Products → New Product** is a search box over this business's existing
  categories instead of a dropdown: type and the list narrows, ignoring case ("dri" and "DRI" both
  find "Drinks"), and **No category** is still there, because a product's category is optional. What
  is saved is always the category you picked, never the text you typed — dismiss the list with
  Escape and the box goes back to the current selection. Arrow keys move through the list, Enter
  takes the highlighted entry.
  If the name you typed is not one of your categories, the list offers one extra entry,
  **Create category “…”**, labelled with that exact name. Nothing is created by typing it or by
  leaving the field: you have to choose that entry. It creates the category through the same
  `POST /api/categories` the **Categories** page uses, with the same name rules and the same
  permission, and then selects it and adds it to the list — you stay on the form with every other
  field, and the GST rule you chose, exactly as you left them. Pressing it twice while it is working
  creates one category, not two. If it is refused — most often because the name already exists, which
  is what happens when somebody created it in the meantime — the message from the server is shown as
  it worded it, your typed name stays, the list is re-read so you can simply pick the category that
  now exists, and nothing else on the form is changed. If your categories could not be read at all,
  the field says so and offers **Retry**: until that succeeds there is nothing to search and no
  **Create category** entry, because a list nobody could read cannot say whether the name you typed
  is already one of your categories — you can still save the product without a category and set it
  later. **Editing** a product keeps its existing category dropdown, because a category the provider
  manages is locked there.
- **Edit a product** with `PUT /api/products/{id}`, which now accepts its **name and
  category** as well as the SKU, description, unit, low-stock threshold, restock target
  and active flag. The unit label may not be blanked — see **Unit Label is required** above for the
  one case a blank one is still accepted. There is no supplier among them — see
  [A product carries no supplier](#a-product-carries-no-supplier) — and no selling price either —
  see [A product carries no selling price](#a-product-carries-no-selling-price). **Edit a category**
  with `PUT /api/categories/{id}`.
- **Which fields a provider manages.** For a product the provider's catalogue names, its **name and
  category** come from the provider and are refused with a message naming the provider if you try to
  change them — the next catalogue import would overwrite the local value. **Every other local
  field stays editable.** For a category the provider named, its **name** is managed the
  same way and its description stays editable. Every product and category response says which of
  its fields are managed, in `providerManagedFields`, so a screen can lock exactly those inputs.
- **The Products and Categories screens use all of this** (issue #737, with the Products list
  actions as issue #825 left them). **Products → New Product** sits in the All Products
  search/filter toolbar, to the right of search, Category, Status and Reset, and **Categories → New
  Category** creates a record this business maintains itself. (New Product is on All Products only —
  it is not in the shared Products header, so Needs Ordering, which orders products that already
  exist, no longer offers it.) **Needs Ordering → Mark as ordered** sits the same way, in that
  page's own search/filter toolbar to the right of search, Category and Reset (issue #832) — it used
  to sit in its own row below the toolbar. **Import products** sits beside New Product on that same
  toolbar (issue #875) for a member who holds `Imports.Run` while Nayax is usable and no support
  session is active — it opens the catalogue import's own page, **Products → Import products**
  (`/products/import`, issue #888), rather than being a second entry point with its own behaviour.
  **A catalogue read Nayax has refused hides it even while the connection is usable.** A
  `PendingPermissions` connection is usable precisely because the Nayax account may have granted some
  reads and refused others, so the button asks about the catalogue read itself as well: once that
  read is known to be refused there is no import to offer, and the button is left out rather than
  shown disabled. A permission nothing has stated yet — the ordinary state of a fresh session —
  still shows it, because an unread permission is not a refusal; that is the same fail-open
  convention the rest of the shared provider-availability signal uses.
  Editing a product the provider manages shows its name and category read-only with a note naming
  the provider ("Name and category are managed by Nayax and cannot be edited here."); its price and
  every other local field stay editable in the same form. A provider-managed category's name is
  locked the same way in **Categories**, which also gained its own inline create/rename form — the
  "Categories are managed from the product import and are read-only here" notice is gone along with
  the blanket lock the product form used to put on price. **Delete** is offered only where the API
  says it is allowed: the catalogue listing answers `canDelete` for every product, with the
  `deletionRefusal` the delete itself would give when it is `false`, so **Products** renders
  **Delete** only for a product the server would actually delete. For every other product the row
  simply offers no Delete, and **why** is shown once, on that product's **Edit Product** page: a
  light-red notice directly under the page header carrying the server's refusal word for word — the
  provider's ("This product is managed by Nayax and cannot be deleted…") or the history's ("…its
  history refers to it (2 stock movements, 1 sale)…"), or "This product cannot be deleted." when the
  response carries no wording. The notice is informational; it changes nothing on the form and is
  shown for an active and a deactivated product alike. Every **active** product's row offers
  **Deactivate** instead — beside Delete where a delete is possible, and alone where it is not —
  which asks you to confirm ("Deactivate 'Fanta 375ml'? It will be hidden from the working catalogue
  and the reorder alerts, keeps its history, stock and costs, and can be reactivated at any time.") and
  then writes the ordinary product update with `isActive: false`. The **Status** badge deactivates
  through that same confirmation; reactivating a deactivated product stays one click, and a
  deactivated product's row offers **Reactivate** in that same slot instead of Deactivate (issue
  #900), reusing the Status badge's own one-click activation write rather than a new endpoint. A
  failed reactivation leaves the product deactivated and shows the server's own refusal, the same
  way a failed deactivation does - the two controls share the one write and its error reporting.
  That `canDelete` answer is **advisory**: it is as
  fresh as the last fetch, the delete counts again inside its own transaction, and a refusal for a
  product the screen had offered is still shown as the server worded it. A **category** has no such
  answer published and no retirement flag at all, so **Categories** attempts every delete and shows
  the server's refusal, which names how many products use the category. **Products** also has a
  **Status** filter — **Only active** (the default, the working catalogue) or **All**
  (`includeRetired=true`) — so a product just deactivated stays reachable to **Reactivate** instead of
  disappearing from the only list that can flip it back (issue #791) — the Admin costing-repair and
  AVCO-transition tools and the stock-history product filter still call the endpoint without it and
  are unaffected by this change. The **Status** column itself is shown only under **All**: under
  **Only active**, every row would say "Active" anyway, so the column (and a sort on it) is hidden
  instead, and switching back to **Only active** clears a Status sort rather than leaving it pointed
  at a hidden column (issue #828). A **Tracked** column shows which products have supplier prices
  tracked and where each one has got to — see
  [Track supplier prices on a product](#track-supplier-prices-on-a-product).
- **Deleting keeps history.** A product is deleted only while nothing refers to it. If it has stock
  movements, purchase lines, supplier order lines, sales, machine stock counts or costing records,
  the delete is refused with what is in the way and nothing is written — deleting it would erase
  inventory and financial history. **Deactivate it instead**: a deactivated product keeps all of its
  history and can be reactivated at any time. A product the provider manages is not deleted either,
  because the next import would create it again. **The catalogue read says so in advance**:
  `GET /api/products` and `GET /api/products/{id}` answer `canDelete` for each product, and the
  refusal in `deletionRefusal` when it is `false`, so a client offers a delete only where one is
  possible. It is advisory — the delete decides again inside its own transaction, and a client still
  has to show a `409` it did not expect. The reorder listing (`lowStockOnly=true`) and the product
  snapshots nested in other resources answer neither key, and an absent `canDelete` is not
  permission.
- **A deactivated product is out of the product lists.** `GET /api/products` answers the catalogue
  you work from, so a deactivated product is not in it, is not offered in a product picker, and
  stays out of the reorder alerts and the Pick List — it must not be purchased from, counted,
  picked or refilled. Nothing about it is lost: it is still answered by
  `GET /api/products/{id}`, every stock movement, purchase, sale and count that names it is
  untouched, `GET /api/products?includeRetired=true` lists it with the rest of the catalogue, and
  putting `isActive: true` back reactivates it.
- **The Products page passes `includeRetired=true` through its Show retired filter** (issue #737),
  so a deactivated product can be found again and reactivated from the screen. The Admin
  costing-repair and AVCO-transition tools and the stock-history product filter still call the
  endpoint without it and show active products only — picking up `includeRetired` there is unrelated
  follow-up work, not part of this change.
- **Searching All Products or Needs Ordering by name or SKU ignores letter case** (issue #827), so
  "cola", "COLA" and "Cola" all find a product named "Coca Cola".
- **A category products use is not deleted.** The refusal says how many products use it; move them
  to another category first, otherwise they would be left with no category at all.
- **Importing the Nayax catalogue is a review you decide, entry by entry** (issue #739), **and it
  has its own page: Products → Import products** (`/products/import`, issue #888). The sidebar's
  **Inventory → Import products** entry goes there, for every business that may run imports with a
  usable Nayax connection, and so does **Products (All Products) →
  Import products** (issue #875) when the signed-in member may run imports, Nayax is usable, no
  support session is active, and Nayax has not refused this application permission to read the
  catalogue — a refused catalogue read hides that button even though the connection itself is usable
  (`PendingPermissions` is a usable status), while a permission nothing has stated yet still offers
  it. **Admin → Imports → Import products** is a link to the same page, so every entry point opens
  the identical review; the review itself used to be a panel at the bottom of Admin → Imports, and
  closing it now returns you to Products. It is no longer refused for a business
  that keeps its own products (it was, until this change, because the old import recognised a product
  only by Nayax's own id and would have created a duplicate of each of yours), and there is no
  longer a direct import that writes Nayax's catalogue straight through: nothing is written until you
  press Apply, and every row the import writes is a row you decided about. See
  [Connecting Nayax when you already have products](#connecting-nayax-when-you-already-have-products)
  below for the walk-through.
- **Which fields Nayax manages once a row is linked.** A product: its **name** and its **category**.
  A category: its **name**. Those become read-only on the Products and Categories screens and are
  updated automatically by every later review, which lists each such change rather than asking about
  it. Everything else stays yours — the **price** (you decide per product whether to keep yours or
  take Nayax's), the SKU, the **unit/volume**, the low-stock threshold, the restock
  target, the active flag and all of your stock, costing and history. Nayax publishes no unit or
  volume label at all, so the review never proposes one.

## Track supplier prices on a product

You choose which of your products should have their prices checked at the suppliers in the shared
price catalogue, and you can see whether each one has been matched yet. Nothing is checked for a
product you have not switched on.

- **Products → edit a product → Track supplier prices.** One switch. Turning it on asks for this
  product's prices; turning it off withdraws the request. It saves on its own, separately from the
  rest of the product form, because it is not a catalogue field — the same way the **GST rule**
  panel beside it saves.
- **A product is searched for by its own name and unit label** — "Coca-Cola Classic" and "375mL".
  Those two values are copied when you switch tracking on, so what is searched for is what you were
  shown rather than something that changes under you later.
- **A real unit label is required** (see **Unit Label is required** under
  [Products and categories](#products-and-categories)). A product whose label is blank, or is still
  the generic `unit` every imported product starts with, cannot be switched on: the switch is
  unavailable and says so — *"Supplier prices are searched for by the product's name and unit label,
  so this product needs a real unit label such as "375mL" before tracking can be turned on."*
  Give the product its real label on the same form, save, and the switch becomes available. This
  changes nothing about what a product may be saved with; it is only a condition for tracking it.
- **What the status means.** Under the switch, a tracked product shows one of five states, and each
  is an answer rather than a stage:
  - **Awaiting match** — the request is recorded and nothing has matched it yet.
  - **Matched automatically, awaiting check** — the matcher chose a catalogue product and nobody has
    reviewed that choice. The product it chose is named beside it, with its size.
  - **Matched** — the match is settled. The catalogue product's name and size are named beside it.
  - **Not found** — it was looked for and nothing in the catalogue describes it. A real answer, which
    is why it is not searched for again every run.
  - **Paused - this product cannot be searched for** — the product's own name or unit label can no
    longer be searched for, usually because the unit label was replaced by the generic `unit`.
    Nothing is searched for it, it is matched to nothing, and the same sentence about a real unit
    label is shown under it saying how to resume. Give the product a real unit label again and it
    goes back to **awaiting match**.
  Only the catalogue product's **name and size** are ever shown — nothing about which suppliers
  stock it, what it costs, or any other business.
- **Renaming or relabelling the product.** While a product is still **awaiting a match**, editing its
  name or unit label updates what is searched for. For a product that is already **matched**,
  changing its **unit label** puts it back to **awaiting a match** with the new text — a 375 ml can
  that became a 1.25 l bottle is not the same product — while a **name-only** change keeps the match.
  A **not found** product is left as it is.
- **Taking a product's unit label away pauses its tracking; it never prices the old size.** If you
  replace a tracked product's unit label with the generic `unit`, the edit is still saved — tracking
  never stops you editing a product — but its tracking goes to **Paused**: any match it had is
  dropped, so no supplier's price for the old size is shown as this product's, and nothing is
  searched for it in the meantime. Giving it a real unit label again starts it off from the product's
  current name and label; it waits for a match afresh rather than returning to the match it had,
  because that match was made on the size you changed.
- **Turning it off removes only your request.** The shared catalogue of products, supplier listings
  and prices is untouched, and so is every other business's. You can switch it back on at any time.
- **Nobody sees anybody else's choices.** A business cannot see which products another business
  tracks, and the shared catalogue records no business at all — a product is looked up once for
  everybody, which is the whole point of sharing it.
- **Products → All Products** has a **Tracked** column showing which of your products are tracked and
  where each one has got to. It is read-only: the switch itself is on each product's own page, and
  there is no way to switch many products on at once.
- **What the prices look like once a product is matched** is on **Products → Needs Ordering**,
  **Products → All Products** and the Dashboard's **Reorder priorities**, in the **Cheapest
  supplier** column — see
  [The cheapest supplier, on Needs Ordering](#the-cheapest-supplier-on-needs-ordering).
- **New Product** has no switch — there is no product yet to track. The form says so, and the switch
  is on that product's **Edit** page once it exists.
- **Who may use it.** The same permission that edits a product (`Inventory.Operate`); no new
  permission is involved. It is unavailable while you are looking at a business in read-only support
  view, and while a Super Admin is previewing a role, like every other write in those states.
- **It works on every plan, and the prices it leads to are a Starter feature.** The switch, its
  status and the **Tracked** column are available whatever the business's
  [plan](#business-plans-free-starter-and-pro) says: you can turn tracking on and off on Free, and
  the setting is kept if the plan changes — nothing clears it on a downgrade. What a plan decides is
  whether the **prices** are shown (the column below) and whether the daily collection is run for
  your product: a product only Free businesses track is not looked up at the suppliers, while a
  product you and a Starter or Pro business both track still is. Turn the plan back on and the
  prices come back with it, from the request you had already made.

## The cheapest supplier, on Needs Ordering

Once a product's prices are being tracked and the catalogue has matched it, **Products → Needs
Ordering** shows what your own suppliers charge for it. The column is called **Cheapest supplier**,
it sits after the purchase-cost columns, and a row reads like `JB Metro $0.95/unit inc GST
(special)`. These are prices observed on suppliers' own pages as purchasing guidance — they are
**not** costs you have paid, they never change a purchase, a cost, an inventory value or any report,
and what you actually paid stays in the **Last purchase** and **Cheapest purchase** columns beside
them. **Products → All Products** (issue #940) and the Dashboard's **Reorder priorities** carry the
same column, cell and popup — everything below applies to all three.

- **Clicking the cell opens a popup listing every one of your suppliers**, cheapest first — for
  example `JB Metro $0.95/unit (24 pack $22.70, special) · Coles $1.27/unit · Woolworths: no match`.
  Each row shows the pack size and pack price, the unit price **on both GST bases**, whether it is a
  special, what the supplier said about stock, when it was last checked, and a link that opens that
  supplier's own page for the product in a new tab. **Last checked** is shown on your business's own
  clock, in the time zone set in Admin → Business settings, not in whatever zone the computer you
  are using happens to be in; a listing nobody has checked yet says **Never**.
- **The popup is a real dialog, so the page behind it waits.** While it is open, the rest of the page
  cannot be clicked or tabbed into: **Tab** and **Shift+Tab** cycle through the popup's own links and
  buttons and stay there. **Escape**, clicking outside the panel and **Close** all close it, and the
  keyboard goes back to the cell you opened it from.
- **The price you are compared on follows your GST registration.** A GST-registered business is
  shown and compared on **ex-GST** prices, because that is what it really pays for a can; a business
  that is not registered is shown the price it hands over, **inc-GST**. Both figures are in the
  popup either way, and the cheapest supplier is always the smallest number you are shown.
- **Which suppliers take part.** Your own suppliers that came from the platform's supplier list and
  that you have not marked **Not used** (see [Suppliers from the platform
  list](#suppliers-from-the-platform-list)). A supplier you typed in yourself has no catalogue
  listings to compare, and a **Not used** supplier is one you have said you do not buy from.
- **A supplier that is listed with no price is still listed, and says why.** "No match" is a
  supplier that does not list the product at all, "Not checked yet" is a listing nobody has read a
  price from, and an offer that cannot be compared says the one reason — out of stock, the special
  has ended, the pack size is not published, the match is not confirmed, the GST rate is unknown, or
  the last check is too old. A price that is not known is never shown as `$0.00`.
- **The cell itself is an answer, not a blank.** It says **Not tracked** for a product you have not
  switched on, **Awaiting match** while nothing has matched it, **No match found** when it was
  looked for and nothing described it, **No eligible price** when no offer could be compared,
  **Loading** while the prices are being read, and **Unavailable** if that read fails — the rest of
  the page still works, and a Retry sits above the table. When the match was made automatically and
  nobody has checked it, the popup says **Matched automatically**.
- **Who may see it.** The same permission that opens the dashboard (`Dashboard.View`), which every
  role holds — these are suppliers' published prices rather than your own financial figures. The
  column reads as **Unavailable** while a Super Admin is looking at your business in read-only
  support view, which is read-only and asks for nothing.
- **And which plan.** Supplier price comparisons are a **Starter** feature (see
  [Business plans](#business-plans-free-starter-and-pro)). While plans are being enforced, a Free
  business is refused this one read and the column reads as unavailable; everything else on the
  page, including **Last purchase** and **Cheapest purchase**, which are your own purchase history,
  is unaffected. Nothing you have tracked is lost, and the column fills in again as soon as the plan
  includes it.
- **Same column, same answer, everywhere it appears.** One endpoint and one component serve it, so
  the Dashboard's Reorder priorities and **Products → All Products** (issue #940) cannot ever
  disagree with Needs Ordering about which supplier is cheapest. On All Products it sits after the
  **Tracked** column, with one request for the page's products — the same batching, failure
  message and Retry described above, and no sort of its own.

## A supplier's website

A supplier can record an optional **Website** beside its name, contact and address, so you can open
the supplier's own ordering page from the Suppliers list. It is optional everywhere: a supplier with
no website is normal, and editing one you recorded earlier works without touching the field.

- **What is accepted.** A complete web address starting with `http://` or `https://`, up to 2048
  characters. Leading and trailing spaces are trimmed, and leaving the field empty records no
  website at all rather than a blank one. A scheme-less address such as `coles.com.au`, a relative
  path, and anything with another scheme — `javascript:`, `data:`, `file:`, `ftp:`, `mailto:` — are
  refused, with the message shown on the field. The form checks this before saving and the server
  checks it again on every create and update; a refused value changes nothing.
- **It is a link, and only a link.** On the Suppliers list a recorded website opens in a new tab,
  with `rel="noopener noreferrer"`. **The application never visits it.** Nothing fetches, follows,
  previews, scrapes or checks that the address works, and nothing reads a GST status, an ABN or
  anything else out of it. A stored value that is not an `http(s)` address is shown as plain text
  and is never clickable.
- **Where it appears.** `GET /api/suppliers` and `GET /api/suppliers/{id}` return `url`, `null` when
  none is recorded, and `POST`/`PUT /api/suppliers` accept it. It is also part of the nested
  `supplier` object on a purchase, a supplier order and an operating expense, as an additive
  nullable field; nothing else about those payloads changed.
- **One supplier's website cannot be changed**: a supplier that came from the platform's supplier
  list keeps that list's website, as it keeps that list's name. See
  [Suppliers from the platform list](#suppliers-from-the-platform-list).

## Suppliers from the platform list

A business approved from now on starts with one supplier of its own per enabled platform preset that
serves the state it trades in (see
[New businesses start with the enabled presets](#new-businesses-start-with-the-enabled-presets)), and
each of those copies **stays linked** to the list it came from. The Suppliers page marks a linked
supplier with a **Platform list** label.

- **Change your state and the suppliers that serve it are added** (issue #935). When an Owner changes
  the state or territory in Business settings, every supplier on the platform's list that now serves
  you and that you do not already have is added as part of that save. **Nothing is ever removed**: a
  supplier you no longer "match" stays exactly as it is, with its purchase history, because you did
  buy from it — mark it **Not used** if you have stopped. Saving anything else on that page changes
  no supplier at all.
- **A supplier the platform adds later appears under "Available to add"** (issue #935). It is never
  pushed into your business. The Suppliers page shows an **Available to add** section listing the
  platform suppliers that serve your state and that you have not added yet, with their name and
  website; **Add** creates your own copy of one, with that list's name and website, in one click. The
  section is not shown at all when there is nothing left to add. Adding needs the same permission as
  the rest of the Suppliers page, and the action is hidden — and refused — in a read-only support
  session and while previewing another role. Adding the same one twice (two tabs, say) tells you it
  has already been added and leaves one supplier; a supplier that serves another state is not offered
  and cannot be added.

- **Its name and website are the platform's.** Open a linked supplier for editing and the Name and
  Website fields are read-only, with a note saying why. The API refuses a change to either with
  `400` and a message you can read, whatever a client sends.
- **It cannot be deleted.** There is no Delete button on a linked row, and the endpoint refuses the
  delete.
- **Its contact details and GST defaults are yours.** Contact name, phone, email and address save
  normally, and so do the supplier's three
  [GST defaults](#product-and-supplier-gst-rules) — those are your own accounting settings about a
  supplier you buy from, and the link only fixes who the supplier *is*.
- **Mark one you do not use as "Not used".** The Suppliers page has a **Not used** switch on each
  linked row. Switched on, the supplier stops being offered when you record a purchase, place a
  supplier order or order from the Needs Ordering page. Nothing is removed: the row stays on the
  Suppliers page so you can switch it back on, and every purchase, supplier order and expense that
  already names it still shows it. The switch needs the same permission as the rest of the Suppliers
  page, and it is hidden — and refused — in a read-only support session.
- **A supplier you added yourself is unchanged.** Rename it, re-point its website, delete it, exactly
  as before. "Not used" is deliberately not offered for one: you can simply delete it.
- **For the API.** `GET /api/suppliers` and `GET /api/suppliers/{id}` publish `isLinked`, `isHidden`,
  `canRename` and `canDelete` beside the fields they already carried, so a client shows the right
  controls; the server enforces the rules either way. `GET /api/suppliers` leaves the suppliers
  marked "Not used" **out** unless you ask for them with `?includeHidden=true`, so every picker gets
  the right list by default. `PUT /api/suppliers/{id}/not-used` takes `{ "isHidden": true|false }`.
  `GET /api/suppliers/available-presets` lists what you may add — each with `id`, `name` and `url`
  and nothing else — and `POST /api/suppliers/available-presets/{presetId}` adds one, with **no
  body**: its name and website come from the platform's list, and the business is your own. That add
  answers `201` with the new supplier, `409` if you already have it, and `404` for a supplier that
  does not exist, is no longer suggested, or does not serve your state — including when it stopped
  being one of those while your Suppliers page was open, because the add checks all three for itself
  at the moment it writes rather than trusting the list you are looking at. The nested `supplier`
  object on a purchase, supplier order or expense is unchanged.
- **Businesses approved before this** had their existing suppliers linked once, by name: a supplier
  whose name matches a list entry exactly (ignoring case and surrounding spaces) was linked, and
  nothing else about it was touched. Where one business held **two** suppliers matching the same
  entry, **neither** was linked, because there is no way to tell which one you actually buy from —
  those rows stay yours to rename and delete as before. No supplier was created, renamed, hidden or
  deleted by that upgrade.

## A product carries no supplier

A product used to hold one supplier, as a default for ordering. Nothing ever ordered from it: the
supplier of an order is a decision about that order, and a product you buy from whoever is cheapest
this month has no single supplier to record. That field and its stored values are gone.

**What changed.**

- The **product form** has no Supplier dropdown. The **All Products** and **Needs Ordering** pages
  have no "All Suppliers" filter and no Supplier column or sort. The home dashboard's **Reorder
  Alerts** table has no Supplier column.
- **Creating a supplier order** still begins with choosing the supplier, and that choice is still
  required. It now starts **empty** instead of being pre-filled from the selected products, so the
  supplier on the order is always one somebody picked.
- `GET /api/products` and `GET /api/products/{id}` no longer return `supplierId` or `supplier`, and
  neither does the product snapshot nested in a purchase line, a supplier-order line or a machine
  slot. `POST`/`PUT /api/products` no longer accept `supplierId`; sending one is ignored rather than
  refused.

**What did not change.** Suppliers themselves, and everything that records one: the supplier on a
**purchase** and on a **supplier order**, supplier contact details, supplier GST defaults, and
creating, editing and deleting a supplier. Every purchase and order keeps its supplier, its item
unit costs, quantities, totals, GST and receipt attachments exactly as recorded, and no cost,
average cost, inventory value, COGS or reorder figure is recalculated. The per-product **supplier
price comparison** on the product page is unaffected and in fact says more than the old field did:
it is derived from the purchases a product was really bought in, so it shows every supplier it came
from, with the lowest and the most recent unit cost.

**If you relied on the stored product supplier**, it is not recoverable — reverting the release
restores the column empty, not the values. Use the product's supplier price comparison, which reads
the purchase history, to see who a product has actually been bought from.

## A product carries no selling price

A product used to hold a catalogue "Unit Price" — a default list price. Nothing ever calculated
with it: no report, profit, margin, COGS or inventory valuation read it. What it did do was sit
beside the real price the machine charges, with nothing keeping the two in step, so a catalogue
nobody reviewed went quietly stale while the machine kept selling at its own price. That field and
its stored values are gone.

**What changed.**

- The **product form** has no Unit Price input. The **All Products** and **Needs Ordering** pages
  have no Price column or price sort, the home dashboard's **Reorder Alerts** table has no Unit
  Price column, and the **Stock History** page's product summary shows three tiles instead of four.
- **Import products** (Products → Import products) no longer asks you to keep or take a Nayax price. Linking a
  product still replaces its **name and category** with the provider's, and those changes are still
  listed as applied automatically; there is simply no price to decide about any more.
- **A catalogue review you had already started before this release cannot be applied.** It was read
  against a field that no longer exists, so applying it would carry out only part of what you were
  answering. It is refused with "This catalogue review was started before product prices were
  removed, so it cannot be applied. Nothing was changed. Start the review again." — nothing is
  linked, renamed or created — and starting a fresh review works normally.
- `GET /api/products` and `GET /api/products/{id}` no longer return `unitPrice`, and neither does
  the product snapshot nested in a purchase line, a supplier-order line or a machine slot.
  `POST`/`PUT /api/products` no longer accept `unitPrice`; sending one is ignored rather than
  refused.

**What did not change.** Every price and cost that a real event recorded. The **machine's own live
price** (`machinePrice`, from Nayax) and the machine pages' suggested prices, a **purchase item's
unit cost**, a **supplier order line's agreed price**, the amount on a **recorded sale**, and a
product's `averageUnitCost`, costing quantity and inventory value are all exactly as they were.
Purchases, supplier orders, their suppliers, quantities, totals, GST and receipt attachments are
untouched, the initial unit **cost** you may give a new product still records its opening stock
movement, and no cost, average cost, inventory value, COGS, recorded sale or reorder figure is
recalculated or backfilled.

**If you relied on the stored product price**, it is not recoverable — reverting the release
restores the column empty, not the values. The price a product actually sells at is on the machine
it is in, on the Machines pages.

## What you paid, on the dashboard's Reorder Alerts

Since issue #972 this table is the dashboard's **Reorder priorities** (its first five products, with
a link to all of them on Needs Ordering), and its two cost columns are unchanged.

Where the Reorder Alerts table used to carry a selling price, it now carries what you actually paid:
a **Last purchase** column and a **Cheapest purchase** column, each showing the supplier and the unit
cost — for example `Acme · $1.25`. Both come from your recorded purchase history, so you can see
while ordering whether the last price was the best one you have had. The old **Stock after machine
need** column is gone; it was only the stock figure minus the machine-need figure already beside it.

- **A product nothing has ever been bought for shows `—`**, which only ever means "no purchase
  recorded". A purchase with no supplier on it shows **None** as the supplier, and a genuinely free
  or zero-cost delivery shows **$0.00** with its supplier — a real recorded cost, not a blank.
- **While the costs are being read the two columns say "Loading".** If that read fails they say
  **"Unavailable"** and offer **Retry**; the rest of the table and the dashboard still work, because
  every other figure on them is your own data and was not part of that request. Reordering or
  reloading the page never shows one product set's costs against another's.
- **The two columns need the same permission as the dashboard's money figures.** An Operator does not
  see them at all, and nothing is requested on their behalf; a Manager or an Owner sees both.

## Machine restock sync reminder

**Sync Restock never runs by itself.** You open a machine and ask for it, which means forgetting it
is silent: the storage figures drift, and what you see looks like missing product rather than
missing bookkeeping. The home dashboard therefore shows a reminder when that work is waiting.

- **A machine is listed for either of two reasons.** Its last fully successful restock sync is three
  or more business days old — counted as calendar days in your own business time zone, so a
  daylight-saving change never moves the line — or it still has imported restock events from the
  last 14 business days nobody has resolved. Either one alone is enough, and both are shown: a
  recent sync clears only the first.
- **"No recorded sync" means exactly that, not "never synced".** A successful sync has been recorded
  since this feature was added; machines you were already syncing before then start with nothing
  recorded, and nothing guesses a date for them from an event, a sales sync or a stock count. The
  first time you run Sync Restock for a machine, it fills in.
- **A fetch only counts when the whole thing worked.** A sync that failed, or that imported only part
  of what it read, records nothing at all — and one that succeeded and found no new alert counts as
  a success, because an empty answer is an answer. A successful sync is never a promise that all of
  your history has been fetched: Nayax's last-alerts list is a rolling window of recent events.
- **Pending counts are what has been imported in the last 14 days.** They include events needing
  review, events you have not got enough storage stock for, and possible duplicates you have not
  resolved; they exclude ones you have applied or marked as already recorded manually, and events
  reporting a zero or negative quantity — those are machine discrepancies, never restocks, and Sync
  Restock never applies them. Events older than 14 days are not counted, but nothing is deleted:
  clear or move back the From date in Sync Restock and they are still there to resolve. A count of
  zero means nothing imported in that window is outstanding — it cannot promise that Nayax is holding
  no alert nobody has fetched yet, which is why the overdue-sync warning is reported separately.
- **The link opens Sync Restock far enough back.** The Sync Restock dialog opens on the last three
  days by default. A machine's link on the reminder carries the date of its oldest counted pending
  event (`?restockFrom=yyyy-MM-dd`), and when that date is earlier, the dialog opens from it instead,
  so every event the reminder counted is on screen. You can still change or clear the date.
- **It reminds, and does nothing else.** Each row links to the machine and names the **Sync Restock**
  action there. Opening the dashboard, following a link or opening a preview applies nothing,
  resolves no duplicate, changes no inventory and does not mark anything as seen — after you apply or
  reconcile, reloading the dashboard shows what is left. The dashboard read is entirely local: it
  makes no Nayax request, so it costs nothing and cannot fail because Nayax is slow.
- **When it is not shown.** A business with no usable Nayax connection, and machines you created
  yourself rather than ones Nayax names, have no provider restock sync to be behind on. It is hidden
  inside a [support session](#super-admin-support-sessions) too, which is read-only and cannot sync.
  If the reminder itself cannot be loaded it says so — unknown, rather than up to date — and the rest
  of the dashboard keeps working.
- **Who sees it.** The same permission as Sync Restock itself, which every role holds (Operator,
  Manager and Owner). If your Nayax connection has not been confirmed for machine alerts, the
  reminder says the sync may be unavailable instead of sending you to an action that will refuse you.

## Product and supplier GST rules

Purchase amounts are GST-inclusive, and GST is recorded per purchase line and per delivery/package
charge rather than for a whole purchase. Two pages hold the rules that describe what *should* be
classified:

- **Products → edit a product → GST rule.** Choose **Taxable**, **GST-free**, or **No rule**, then
  **Save GST rule**. It saves separately from the rest of the product form, because it is
  bookkeeping configuration rather than a catalogue field.
- **Products → New Product → GST rule** (issue #829). The same picker, for a product that does not
  exist yet: there is no stored rule to read, so it is ready to use immediately and has no Save of
  its own — **Create Product**, in the action row below it (issue #892), saves it with the product,
  as described under
  [Products and categories](#products-and-categories). A **Save GST rule** button appears there only
  if the product was created and its rule was not, to retry that one write.
- **Suppliers → Edit a supplier → GST defaults.** Choose a default for the supplier's **product
  lines** and separate defaults for its **delivery charge** and **package charge**, then **Save GST
  defaults**. The three are independent: a charge never inherits the product-line default.

Both panels read the stored setting first, and the save button stays disabled until it is on screen,
so you are always editing what is actually configured. If that read fails the panel says so and
saving stays disabled: the pickers open on "No rule"/"No default", which are real values that would
replace whatever is stored, so reload the page instead of saving. Opening one product or supplier
and then another also discards the first one's answer, so a slow response can never put one
record's settings on another record's form.

A supplier default applies only where the purchased product has no GST rule of its own, and a
product rule applies only where nobody has classified the purchase line by hand. "No rule" and "No
default" leave a component unclassified, which is not the same as GST-free: an unclassified
component contributes no input GST and stays visibly unresolved in the GST reporting aid.

These settings are explicit configuration, not inference. A supplier being registered for GST does
not classify its products, because a supplier may sell both taxable and GST-free goods. Saving a
rule or a default never changes a purchase that is already recorded; applying rules to purchases
already recorded is the separate, explicit
[Historical GST classification](#historical-gst-classification-admin-page) maintenance action below.

**A rule has no effective date of its own, and it is never retroactive.** It is simply the current
configuration: the rule a product carries is the one the Historical GST classification action can
apply, it reads the rules as they are configured at the moment you press **Apply**, and it only ever
classifies components nobody has classified yet. Recording a purchase does not read a rule at all —
you classify each line and charge on the purchase itself — so a rule chosen while creating a product,
or changed afterwards, classifies nothing by itself and restates nothing that is already recorded.

## Historical GST classification (Admin page)

Purchases recorded before GST classification existed are **Not classified**, and so is anything
nobody has classified since. **Admin → Historical GST Classification** (also in the sidebar's Admin
group) is the only way to classify them from the product and supplier rules above. It is a two-step
action, and nothing is written until the second step.

1. **Preview classification.** This reads only; it writes nothing at all, not even a draft. It
   reports how many purchases and components it examined, how many would become **Taxable**, how
   many **GST-free** and how many stay **Not classified**, the same counts separately for purchased
   items, delivery charges and package charges, the unresolved amount left behind, and the input GST
   the change would make available. Review those numbers: they are the whole point of the step.
2. **Apply classification.** After a confirmation, this writes exactly what the preview showed, as
   one all-or-nothing change.

What it will and will not do:

- It examines only components that are still **Not classified**. A classification you chose by hand
  is never changed, and neither is one an earlier run applied — so running Preview and Apply again
  after an Apply changes nothing.
- A component carries the product's own GST rule when it has one, otherwise the supplier's
  product-line default. A delivery or package charge takes the supplier's matching **delivery** or
  **package** default only; it never inherits a product rule or the product-line default. A
  component no rule covers stays **Not classified**, contributes no input GST, and keeps its purchase
  visibly unresolved in the GST reporting aid — the preview says how many and how much, so you can
  configure the missing rule instead.
- A delivery or package charge that is empty or zero has no classification and is left alone.
- If any relevant purchase or rule changed between your preview and your apply — a purchase added,
  edited or deleted, a component classified, a product rule or supplier default saved — the apply is
  refused, **nothing** is written, and it asks you to preview again. That is deliberate: the figures
  you approved would no longer describe what would be written. Preview again and review the new
  numbers.
- It changes accounting data only. Purchase amounts, unit costs, the weighted-average cost, costing
  quantity, inventory value, physical stock and stock movements are untouched.
- It only ever runs because you pressed Apply. It never runs on startup, on a deployment, during a
  database migration, when a purchase is read, or when you save a rule.

It applies to your own business's purchases only, and only to the purchases of the business you are
signed in to.

### Hiding this option from other users (Super Admin)

Once historical data has been classified, this is a tool nobody needs again, so a **Super Admin**
can stop offering it. The switch is on the Admin page beside this tool's own card: **Hide Historical
GST Classification from other users**, and **Show Historical GST Classification to other users** to
offer it again. It is one shared, application-wide setting, saved on the server — it is not a
per-browser preference, so it survives a reload, a new tab and a different computer, and it stays as
it was until a Super Admin switches it again. Every installation starts with the option **shown**.

- **While it is hidden**, the sidebar's Admin group and the Admin page both leave it out for everybody
  else. A Super Admin still sees it, in both places, with an **Off** pill beside its name — the mark
  that says what you can see is hidden from everybody else.
- **A Super Admin never loses the tool**, and switching it back on restores it only to the people
  whose role already allowed it: it is a visibility switch, not a permission. Historical GST
  Classification still needs the same access it always did (an Owner's, see
  [Who can see and do what](#who-can-see-and-do-what)), the page still works exactly as described
  above, and its address still opens if you go to it directly. Hiding the option hides the way in; it
  does not restrict, change or disable the classification itself.
- **Only a Super Admin can change it.** The server enforces that, so an ordinary member cannot switch
  it even by calling the API directly, and nothing is changed by the attempt. While previewing a
  business role, a Super Admin is shown what that role would see and is not offered the switch.
- **If a save fails**, nothing changes: the switch says the change was not saved, and the setting,
  the pill and the menus stay exactly as the server still has them. Try again.
- **What the switch shows is always an answer the server gave.** A successful save is shown
  immediately — the pill, the card and the switch's own label change to what was stored — and the
  application then re-reads the setting so the sidebar and every other screen follow. If that
  re-read does not get through, what you saved still reads correctly here and you are told that the
  menus may still show the previous state, with **Read this setting again** to try the read once
  more. While the setting is still being read, or when it could not be read at all, the switch says
  so and offers no On/Off action: it never shows an unread setting as shown or hidden.

## Costing repair (Admin page)

The Admin page's **Costing Repair** section is how an operator restores a product's cost history
when it has a fatal missing-opening or unknown-cost costing issue: select the product, enter the
quantity, unit cost, reason and effective date/time (entered and shown in the business's own
timezone, converted to UTC for the API), and **Preview repair** to see the cost position before and after, the
resulting average unit cost, the first previously uncostable sale, the projected position once the
rest of the history replays, and any fatal issues still remaining. **Apply repair** only becomes
available once that preview is shown, and reapplies exactly the previewed proposal; if the
product's cost history changed in the meantime, the apply is refused and asks for a fresh preview.
Changing the selected product discards any preview or history still loading for the previous
product, and a preview can only be applied to the product it was taken for. An effective time that
does not exist in the business's timezone (the hour its clocks skip when daylight saving starts) or
that happens twice (the hour they repeat when it ends) is rejected before preview; enter a time
outside that hour. The product's repair history is shown underneath, newest first.

A costing repair is a human-entered historical correction: it changes the product's historical
cost of goods sold from the effective time onward, and it is never proof that the recorded history
is correct, only an auditable explanation for a specific situation. It never changes physical
stock, machine quantities, or stock adjustments. Use a real purchase or a stock correction instead
when what is actually wrong is physical stock, not costing history - a repair exists only to record
costing value that was never recorded in the first place.

## Connecting Nayax (Admin page)

**Admin → Integrations** (called Nayax Settings until issue #729) is where an Owner connects this
business's own Nayax account. Only an
Owner can reach it, and the connection wizard at the top of the page walks through six steps; the
estimated processing fee rate is configured further down the same page and is unrelated to the
connection.

1. **Operator ID.** In Nayax Core: open **Operator** in the left sidebar, select your operator and
   search; the numeric id is listed under **Company Info** as **Operator Id**. It is not a secret and
   is stored with the connection.
2. **Create a Lynx token.** In Nayax Core, click your username in the top right corner, then
   **Account Settings** › **Security and Login** (the second tab) › **User Tokens** (below Your
   Trusted Devices) › **Add Token**, and choose the **Lynx Token** type. If a Lynx token is already
   listed, use **Show Token** next to it and copy that instead.
3. **Paste the token and press Test connection.** The token field is write-only: it is sent once
   with the test and cleared as soon as the test comes back, and it is never kept in the browser —
   no local storage, no session storage, no URL. Testing stores nothing yet, changes no Nayax data,
   and leaves any connection you already have exactly as it is, so testing a bad token cannot break
   a working one.
4. **Read the checklist.** The server calls each Nayax endpoint this application reads and reports
   every one on its own line: a tick for granted, a cross with the cause for refused, and **Not
   checked** for the machine-scoped lines when the account has no machines yet (or when the machine
   list itself could not be read — the two are shown differently, because they need different
   actions). An untested permission is never shown as a granted one. The restock write call is
   deliberately not tested: a connection test must not change your Nayax data, so it is checked the
   first time a refill is actually pushed.
5. **Ask Nayax for anything refused.** Nayax grants a token's permissions, so when a line is
   refused for lack of a permission the wizard composes a ready-to-copy request naming the operator
   id you tested and every such area with its cause (never the token itself). Raise it in the Nayax
   support portal, <https://nayax.service-now.com/csm>; if that gets no answer,
   <integration-support@nayax.com> is the secondary contact. A line refused for another reason gets
   its own advice instead of a permission request: an invalid or expired token needs a new token, an
   operator id Nayax does not match needs checking, and an unavailable Nayax needs another try. This
   step is skipped when nothing failed.
6. **Save the connection.** Saving stores the operator id and the token the server just tested — the
   page sends back only the server's reference to that test, never the token again. A token that did
   not sign in to Nayax cannot be saved, and a test result older than fifteen minutes has to be run
   again first. Saving with some areas still refused records the connection as **Permissions
   pending**: the features that need those areas keep saying so until Nayax grants them and a
   re-test confirms it.

**Re-testing.** The stored connection is shown above the wizard with its status — Ready, Permissions
pending, Needs attention, or Not connected — and a **Re-test this connection** button that runs the
same read-only checks against the token already stored. Re-test works in every status, including
Needs attention, which is the state it exists to recover from. When the connection was saved before
any machine existed in the Nayax account, the page asks you to **re-test once your machines are set
up**, because those permissions were never verified. If Nayax cannot be reached, the stored status is
left exactly as it was and the page says so rather than concluding anything. If the token was
replaced while a re-test was running, the result is discarded and the page asks you to run the test
again, so the status always describes the token actually stored.

Tests and re-tests share one allowance for the whole business (five runs per ten minutes); past it
the page asks you to wait a few minutes, and no call reaches Nayax.

### Connecting Nayax when you already have products

Once the connection is saved, **Products → Import products** brings Nayax's catalogue in.
It opens a **review**: nothing is written until you press Apply, and every row it writes is a row
you decided about. That is how a business that has been keeping its own products connects Nayax
without ending up with two of everything — matching names are never linked for you, because two
products that happen to share a name are two products.

**Step 1 — categories.** Each Nayax product group is listed. For one nothing of yours is linked to,
you can:

- **Skip for now** — nothing happens to it, and the next review asks again.
- **Link it to one of my categories** — search your categories and choose one. The screen states that
  its name will be replaced by Nayax's name and become read-only here.
- **Create a new category** — a new category with Nayax's name.

Only categories you maintain yourself are offered: one already linked to a Nayax group cannot be
linked to a second one. A group already linked is shown as linked, with any Nayax rename listed as
**applied automatically**.

**Step 2 — products.** Each Nayax product is listed, with the category it will end up in after your
step-1 decisions — or **none**, when its Nayax group is linked to no category of yours, in which case
the product is imported with no category rather than being put in one that merely shares a number.
The same three choices apply, and linking states plainly that **the product's name and category will
be replaced by Nayax's and become read-only here**; only its price is yours to decide:

- **Linking** offers **take the Nayax price** or **keep my own price**, with both prices shown: once
  you choose which of your products to link, the comparison shows that product's own price, because
  that is the price taking Nayax's would replace.
- A **product already linked** whose Nayax price has since moved offers **accept the Nayax price** or
  **skip it and keep my price**, with both prices shown. Its name and category changes are listed as
  applied automatically — Nayax owns those.
- Either price answer is remembered, so that Nayax price is not proposed again until Nayax changes
  it. Leaving both unanswered leaves your price exactly as it is and asks again next review.
- Nothing about a **unit or volume** is ever offered: Nayax publishes no unit/volume label, so yours
  stays yours.
- A linked product can be one you have retired; retired products are offered and marked, because
  linking one is better than creating a duplicate of it.

**Then Apply.** It is one transaction: if any one decision is refused — the row you named was linked
to a different Nayax product in the meantime, say — nothing at all is written and the message says
which entry and why. Applying the same review twice changes nothing. **No sale is touched**: linking
a product does not re-attribute the sales that already carry that Nayax product id, and the screen
reports how many such sales a link would explain. Attributing them is a separate, confirmed Owner-only
step, because it replays that product's cost history.

**The endpoints behind it** (issue #738) are `POST /api/catalogue-review`, which reads Nayax's
catalogue and reports it against your rows without writing a product, category, price or sale, and
`POST /api/catalogue-review/apply`, which carries out your decisions as that one transaction; both
need `Imports.Run`, the capability running an import has always needed. The separate sale attribution
is `POST /api/catalogue-review/unresolved-sales/apply` (Owner only). `POST /api/imports/products`,
the direct catalogue import, no longer exists.

### Reorder alerts without Nayax

Nayax is an optional add-on. A business with no connected Nayax account — or one whose connection
needs attention, or whose Nayax account has not granted the machine permissions — still gets its
reorder alerts and its ordering figures, calculated from the data this application owns (issue
#726):

- **Needs ordering and the reorder-alert list** are answered in **storage-only** mode: storage
  stock, each product's low-stock threshold and restock target, and the quantity already on an
  outstanding supplier order, with the machines' current need counted as **zero**. The formula is
  unchanged — it is the same calculation with one input missing. Both the dashboard's Needs ordering
  card and the reorder-alert list state the mode they were produced in (`StorageOnly` or
  `MachineAware`), and in storage-only mode the reason the machines are missing, so a figure that did
  not account for the machines is never read as one that did.
- **Needs refill** is reported as unavailable with the reason (`NayaxNotConnected` when the
  connection is unusable, `PermissionNotGranted` when Nayax has not granted the machine reads),
  because every figure on that card is a figure about machines. It is never shown as "no machine
  needs refilling", which would be a false statement about a fleet nobody read.
- **Everything else on the dashboard is unaffected**: the week's sales, the inventory valuation and
  the product and storage counts are the business's own data and are reported as usual.
- **A real Nayax outage is still an error.** Only "no usable connection" and "this permission was
  not granted" produce storage-only figures; a Nayax request that fails in transport, or a fleet
  only half read, surfaces as an error rather than as a business whose machines need nothing.

#### The dashboard without a usable provider

The dashboard presents those states (issue #727). Without a usable Nayax connection it is a
storage-management dashboard, and it says what the business has rather than what it is missing:

- **"Sales this week" and "Needs refill" are not shown, and neither is the Site and machine
  performance card.** Every figure on them comes from Nayax, so they are hidden rather than shown
  empty or "Unavailable" — an empty site list would say the business has no sites, which is a
  different claim. **"Needs ordering" and "Inventory" are shown as usual**, and the reorder alerts
  are then the page's main section.
- **The storage-only figures say so.** The "Needs ordering" card and the reorder-alert list each
  carry a label — "Storage stock only: these figures do not include the machines' current
  replenishment need, because Nayax is not connected" — and the list drops its "Machine need"
  column, whose values would otherwise be a column of zeroes stating that the machines need nothing.
  The label is shown for an **empty** list too, because "nothing needs ordering" means something
  different when the machines were never read.
- **No sales synchronization is attempted, and no sync warning is shown.** `POST
  /api/nayax-sales-sync` is a Nayax call that could only be refused, so the page does not make it;
  a call that was never attempted neither failed nor succeeded, so there is nothing to warn about.
  The dashboard reads its summary first and asks it whether the business has a usable provider,
  which is what makes that possible.
- **A permission Nayax has not granted is reported per feature.** With a usable connection whose
  Nayax account has not granted a particular read (`PendingPermissions`), everything else keeps
  working and only the affected card or section says so — "Machine refill status is unavailable
  because Nayax has not granted this application permission to read it" — instead of a message
  about the connection as a whole.
- **A failed request is an error, never an empty result.** A Sites, Machines or reorder-alert
  request that fails says so ("The Sites section could not be loaded. Reload the page to try
  again."). It is never shown as "No sites available yet" or "No reorder alerts. Everything looks
  good!", which are statements about data the page was never given. A genuinely empty answer still
  shows those messages. The sites and the machines are read separately, so the Site and machine
  performance card keeps whichever of the two succeeded and reports only the one that did not.

**Products → Needs Ordering** works the same way in storage-only mode: the full page, with its
filters, sorting and "Mark as ordered", over the storage-only alerts, labelled with the mode and
with the "Machine need" column hidden.

**A connected business sees no change, and neither does a support session.** With a usable
connection and the fleet reads granted, every card, section and column is exactly as before. In a
read-only support session the dashboard keeps its existing behaviour (issues #552 and #661): all
four cards and all three sections, with "Nayax is not available in support view" on the two withheld
cards and in place of the sync notice.

#### Machine and site filters without a usable provider

Issue #728 does the same for every other screen that lists machines or sites, from **one** answer the
whole application shares: the Nayax connection status is read once per sign-in, refreshed when an
Owner saves or re-tests the connection, and used by the "Nayax isn't fully connected" banners and by
the filters and fields below.

- **A filter over a list that cannot exist is not shown.** Without a usable connection, the machine
  filter disappears from the report pages and the Site filter disappears from Site Commissions. No
  request is sent that could only be refused. The Pick List is the one exception
  (see [Recording a pick without a provider](#recording-a-pick-without-a-provider) below): instead of
  disappearing, it opens straight into its manual entry screen and says why the calculated matrix is
  unavailable, because a business can still record what it took out of storage with no provider at
  all. Stock History, the stock adjustment form and the Expenses screen are no longer on this list at
  all: since issue #746 their machine fields and filters are the business's **own** machine records
  and need no provider (see [Choosing a machine on the stock and expense
  screens](#choosing-a-machine-on-the-stock-and-expense-screens) below).
- **A request that *failed* says so where its filter was.** "The machine list could not be loaded, so
  this report cannot be narrowed to one machine", for example. It is never shown as "All machines"
  narrowing nothing, and a business that genuinely has no machines is never reported as an error.
- **Expenses stops mentioning Nayax it does not have.** Without a connection, "Nayax monthly fee" is
  not offered for a **new** expense and the page subtitle drops its Nayax sentence. An expense already
  recorded under that category keeps it — in the table, in the Category filter and on its own edit
  form — because a recorded expense is a fact about the past. Its machine field and its Machine filter
  are not affected: those are the business's own machines.
- **Stock History keeps the two questions apart.** The "Nayax Sync Restock" source filter is about
  movements that are already recorded locally, so it is offered whenever Nayax is part of the business
  at all — including while a stored connection needs attention — and the table keeps labelling such a
  movement either way. Its machine filter is about the business's own machines and does not depend on
  the connection at all.
- **A connection that needs attention is still a connection.** A business whose stored token stopped
  working keeps its Nayax expense category and its Nayax source filter; what it loses is the live
  fleet reads, with the banner asking an Owner to reconnect.
- **One permission Nayax has not granted does not take the rest of the application with it.** When
  Nayax refuses one read for lack of permission, that one feature says so where its control was and
  is not asked for again until the connection is saved or re-tested; everything else the Nayax account
  does allow keeps working. A business whose connection is usable is never assumed to have been
  granted every permission, so nothing is hidden on a guess either: a feature nobody has been refused
  is offered, and its own request reports its own outcome.

**A connected business sees no change here either.** Every filter, field and category is exactly as
before.

#### Choosing a machine on the stock and expense screens

Issue #746 takes the three screens that attribute the business's **own** records to a machine — the
stock adjustment form, Stock History and Operating Expenses — off the live Nayax fleet and onto the
machines the business keeps itself (see [Machines](#machines) below). They worked only with a
connected provider before, even for a business that had recorded its machines, because the only
machine list there was came from the fleet read.

- **The machine field and the machine filter are your own machines, with or without a provider.**
  They list the machines from Machines → *Your machines*, by the name you gave each one, and a
  business with no provider at all gets exactly the same field and filter as one with a usable
  connection. No Nayax request is made for them. What is stored is unchanged — these screens already
  recorded your own machine, not the provider's number — so no movement, expense, report or export
  changes meaning.
- **No provider name or number is shown.** A machine's name is yours and is the only label; the
  machine records publish no provider identifier at all (see [Machines](#machines)).
- **A new record offers the machines that are in service.** A stock adjustment and a **new** expense
  offer only **active** machines, because a retired machine is not somewhere stock is being put or a
  cost is being incurred now.
- **History stays readable, deactivated machines included.** The Stock History filter and the
  Expenses list's own Machine filter list the deactivated machines too, and Stock History's Machine
  column names them, so a movement or a cost recorded against a machine you have since retired is
  still findable and still reads as that machine rather than as a number. An **expense being edited**
  keeps its own machine as the selected value even once that machine has been deactivated, so saving
  the form again cannot quietly detach the cost from the machine that incurred it.
- **The Expenses list narrows to one machine.** Its Machine filter sits beside the date, Category and
  Supplier filters and applies with the same **Apply filters** button, so the table and its totals
  show what one machine cost you. "All machines" asks for no machine at all, which is what you see
  until you choose one.
- **No machines yet is not a machine list that failed.** While the machine read is outstanding the
  field or filter says it is loading; a read that **failed** says so in its place ("The machine list
  could not be loaded…"), and it is never shown as "All machines" narrowing nothing or as a business
  with no machines. A business that has recorded no active machine simply has no machine field, with
  no error.
- **Read-only support sessions are unchanged.** Every write control on these screens is still hidden
  or disabled in a support session, exactly as before.

#### Recording a pick without a provider

Issue #744 gives the Pick List a second, machine-free way to record what a worker took out of
storage, so a business with no usable Nayax connection — or whose account has not granted the
calculated list's permission — still has a way to record a refill instead of just a read-only page
that cannot be used.

- **The calculated matrix is offered only when it can actually work.** With a usable connection and
  Nayax granted both the machine fleet read and each machine's own product read, the page opens on
  calculated matrix, **Plan picks**, with a toggle to **Record refill** (the manual entry screen;
  until issue #996 these were called "Calculated" and "Enter quantities taken") alongside it. Without
  either, the page opens straight into Record refill instead, with
  one sentence saying why the calculated list is unavailable, and the toggle to switch back is not
  offered — there is nothing to switch to.
- **Manual entry needs no machine, no fleet read and no product read.** It lists the business's
  active products with a search box, a category filter and a "Quantity taken" box per row, from the
  same catalogue the calculated view's Products filter already reads. With no machine selected,
  saving sends every entered quantity as one request to the batch refill endpoint
  (`POST /api/pick-list/refills`), recording one
  `MachineRefill` stock movement per product in a single all-or-nothing operation — a guarantee the
  machine-detail page's old per-product Restock action, since removed in favour of this screen (see
  [Record refill from a machine's page](#record-refill-from-a-machines-page) below), never had. A
  malformed "Quantity taken" entry (anything that is not a whole number greater than zero) is reported on its
  own row before anything is sent, and any refusal the server returns is put back on the row it was
  about.
- **The page says which of the two it is.** The line under the title describes the view you are
  actually in: Plan picks is planning only and changes no stock — the quantities you adjust there
  stay in your browser — while Record refill says that nothing is recorded until you click **Record
  refill** and that recording then reduces each product's storage quantity by the quantity taken.
- **A retry never double-deducts.** Each save keeps an exact record of what it last sent. Clicking
  Record refill again with nothing changed resends the identical request under the identical idempotency key,
  which the server answers with the result it already recorded rather than taking the stock twice;
  changing any quantity before retrying sends a new key, because it is a different pick. **While a
  save's outcome could not be confirmed at all** — a dropped connection, a server error — nothing you
  do on screen can send a *different* request: the screen keeps the exact pick it sent and offers
  **Retry save**, which resends precisely that one, so an edited quantity, a removed machine column
  or a reordered selection cannot turn one physical pick into a second deduction. Only once that
  attempt is answered does Record refill start a new pick again. Since issue #996 the quantity boxes,
  the machine selection and Clear are locked while that retry is outstanding, and Retry save is the
  only action offered.
- **An unconfirmed save is not lost by switching views.** A save whose outcome could not be confirmed
  stays reported on the screen until a later attempt settles it, and switching to Plan picks and
  back keeps every quantity you typed — so the retry is still the same request under the same key, not
  a second pick. While a save is actually being sent, the Plan picks button is disabled until it
  finishes, so an outstanding write always lands where you can see what it did.
- **Read-only during a support session.** While a support session is active, manual entry still
  lists the products and their storage quantities, but the quantity boxes and the record button are hidden and the
  screen says why, so nothing can be recorded on the business's behalf. A refusal the server returns
  is reported as a refusal, not as a save that might need retrying.
- **A product list that could not be read is never shown as empty.** While the product list is still
  loading the screen says so, a failed read says the list could not be loaded and offers **Retry**,
  and "No active products match your filters." appears only after a successful read. If refreshing
  storage quantities after a save fails, the screen keeps the last list, says the quantities may be
  out of date and offers **Refresh**.
- **A machine selection** (issues #794 and #904) lets an operator attribute a manual pick to the
  business's own machines, the same local records the stock and expense screens use (see
  [Choosing a machine on the stock and expense screens](#choosing-a-machine-on-the-stock-and-expense-screens)
  above) — with or without a provider. The **Machines** dropdown offers only **active** machines, by
  the name you gave each one, and is shown at all only once the business has at least one active
  machine; a business with none, or whose machine list **failed** to load (which says so in the
  dropdown's place), sees exactly what it always has: the single "No machine" column, saved with no
  machine recorded. The machines you select never reach the calculated matrix's own machine
  selection, which keeps using Nayax's fleet untouched by this one.

#### Entering one pick run across several machines (issue #904)

An operator preparing one run usually visits several machines. Select them all in the Manual Pick
List's **Machines** dropdown (Record refill, since issue #996) and each one gets its own quantity
column, so the whole run is typed once and recorded once.

- **The row reads Product (with its category under it), Storage available, one column per machine,
  then Total quantity taken** (column order since issue #996). The total is that product's own sum
  across the selected machines — what the pick takes off one shelf — and the summary beside the table
  gives the **whole submission**'s total, counting the rows your search or category filter is
  currently hiding, and saying how many units and products those are. A filter hides rows; it never
  changes what is sent.
- **One click of Record refill sends the whole run as a single request** (`POST /api/pick-list/multi-machine-refills`,
  see [Recording one pick across several machines (the API)](#recording-one-pick-across-several-machines-the-api-issue-897)
  below) — never one request per machine. That is the point of it: only one request can weigh each
  product's **total** across the machines you allocated it to against the storage it comes off, so
  six to one machine and six to another is refused as twelve against a shelf of ten rather than
  half-saved. A refusal keeps everything you typed editable and puts the server's own message on each
  cell it was about. Success reports the whole run — how many entries across how many machines — and
  refreshes the storage quantities once.
- **With no machine selected, nothing changes.** The screen is exactly the single "Quantity taken" column
  described above, saved through the existing endpoint as a refill attributed to no machine.
- **Quantities are never silently moved, copied or dropped.** A quantity typed against "No machine"
  is never copied into every machine column — that would invent a pick several times the size of the
  one you typed — so selecting machines asks you to confirm discarding it first, and **Cancel keeps
  everything**, including the selection you had. The same applies in reverse (clearing the machine
  selection) and to **removing one machine column** that holds quantities: you are asked, by name,
  and Cancel changes nothing at all.
- **Nothing moves while a save is being sent.** The machine selection, the column removals and every
  quantity box are locked until the request lands, so what is being answered is what was sent.
- **A wide run stays inside the table.** The matrix scrolls inside its own bounded area, with the
  machine headers staying readable, so many machine columns never widen the page — including on a
  phone. Every quantity box is labelled with both its product and its machine for screen readers.
- **Opening the screen from a machine's page** (the **Record refill** link) preselects exactly that
  machine as the only column, and you can add more beside it.
- **Read-only during a support session**, exactly as before: the quantity boxes and the record
  button are hidden and the screen says why.

#### Plan picks and Record refill (issue #996)

Issue #996 refreshes both Pick List views and renames them after what each is for: **Plan picks**
(the calculated list, which needs a usable Nayax connection) works out what to take, and **Record
refill** (the manual entry screen above) records what was actually taken. No API, stored data or
Nayax call changed; everything below happens in the browser.

**Plan picks**

- **One row per product, one column per machine.** On a desktop the product column and the header
  rows stay in place while the table scrolls, the machine columns are grouped under their site's name
  when your machines have sites (two sites with the same name still get a header each), and a "Scroll sideways" hint appears once the run has more than two
  machines. The MDB code is a sortable column of its own instead of being repeated in every cell.
- **Each suggested pick can be adjusted.** The pencil on a cell opens its quantity: type a whole number of 0
  or more (Enter or Confirm keeps it, Escape or Cancel leaves it as it was). An adjusted cell shows
  "Suggested: N" with **Use suggested** to go back. **0 means skip that machine** for this product;
  a skipped cell has no checkbox. The adjustment lives only in this browser tab: it is never saved,
  never sent to Nayax and never changes stock.
- **Tick each pick as you take it.** Every cell with something to pick has its own checkbox,
  separate from the pencil, and changing a quantity clears that cell's tick because it is a different
  pick now. The summary says "12 of 20 picks completed", the units still to pick, and how many
  products the planned run needs more of than storage holds ("Storage short by N" on the row).
- **Filters keep your work.** Search (by name or MDB), Category, **Only remaining** and **Only
  shortages**, sorting and focusing on one machine never discard an adjustment or a tick. Products and
  Machines are applied with **Apply**; each selected machine is a chip you can remove.
- **Discarding is always asked first.** **Refresh** (or **Retry** after a failed read) re-reads
  exactly the same selection from Nayax and **Reset** clears it; if you have adjusted or ticked
  anything, or typed a quantity you have not yet confirmed, both ask before throwing it away, and so does removing a machine whose cells you changed.
  Cancel changes nothing. The time of the snapshot you are looking at is shown beside Refresh, and an
  answer to an earlier, superseded request is ignored rather than replacing a newer one.
- **On a phone** the matrix becomes one card per product with the machine picks inside it, a
  machine strip to focus on one stop, larger +/− steppers in the editor, and the progress summary
  kept above the bottom bar.

**Record refill**

- **The same workflow, clearer totals.** Storage available, a quantity box per selected machine (or
  the single "Quantity taken" box with no machine), each row's total and the whole submission's total
  across all machines. Rows are marked **Entered**, **Ready to record** or "Storage short by N".
- **A shortage blocks recording.** If any product's total across every machine — rows your filters
  hide included — is more than its storage, the button reads **Resolve shortage to record** and
  nothing is sent until you fix it. Entering less than a Plan picks suggestion is only noted, never
  blocked. (Suggestions appear as "/ N suggested" when the same machine is selected in both views.)
- **Hidden entries are never forgotten.** When a filter hides rows that have quantities, a banner
  says how many, with **Show entered products** and **Show all**. **Clear** asks before emptying
  every box. There is no draft: what you type stays only until you leave the page.
- **An unconfirmed save stays put.** If the outcome of a submission is unknown, a persistent **Retry
  save** resends exactly the same refill under the same idempotency key and the entries stay locked
  until it is answered (see above).
- **Leaving is guarded.** Navigating away from the Pick List, or closing or reloading the tab, while
  Plan picks has adjustments or ticks, Record refill has entered quantities, or a save is outstanding
  or unconfirmed asks you to confirm first.
- **On a phone** Record refill also uses cards, with a machine strip, steppers and the totals and
  record button kept above the bottom bar.

#### Recording one pick across several machines (the API, issue #897)

A pick run usually visits several machines, and until now each machine had to be saved as its own
request. Issue #897 adds one endpoint that records the whole run at once; the Manual Pick List's
machine matrix (above) is what calls it, and `POST /api/pick-list/refills` is unchanged and still
records a refill for no machine at all.

```text
POST /api/pick-list/multi-machine-refills
{
  "idempotencyKey": "<your own string for this submission>",
  "notes": "optional",
  "allocations": [
    { "machineId": 11, "productId": 300, "quantityTaken": 6 },
    { "machineId": 12, "productId": 300, "quantityTaken": 4 }
  ]
}
-> 200 the recorded run: the key, the note, the server's instant, and per allocation the machine,
      the product, the quantity and the id of the stock movement that recorded it, plus
      "replayed": true when this key had already recorded it and nothing new was written
-> 400 a problem document addressed per line (keys "allocations[i]", "idempotencyKey", "notes")
-> 409 the key already recorded a different refill
```

- **All or nothing.** Every allocation is recorded as one `MachineRefill` stock movement attributed
  to its own machine, in one transaction. Any refusal — one bad line, an inactive machine, a product
  that is not yours, not enough stock, a failure part-way — records nothing at all, so a run is never
  partly saved.
- **Storage is checked against each product's total across the run.** Six to one machine and six to
  another is twelve off one shelf: with ten in storage that is refused, and both lines say so, even
  though each would fit on its own. This is the thing several separate requests could not get right.
  The total is added up at full width, so quantities that are each a valid whole number but together
  come to more than two billion are refused by this same rule and the refusal states the real total.
- **Every allocation names a machine**, one of your own active machines (the same local machine
  records the stock and expense screens use). A refill for **no** machine is still
  `POST /api/pick-list/refills`, which is why that endpoint stays.
- **Nothing is deducted twice.** The `idempotencyKey` is your own string for one submission: resend
  the same run under the same key — in any order of allocations — and the server answers with what it
  already recorded. Change anything about the run and it is a different submission, so send a new
  key; reusing a spent key is refused. A key already used by `POST /api/pick-list/refills` is refused
  here too, and vice versa: a recorded refill is never re-read as the other kind.
- **It is an internal transfer, like every refill.** Storage quantity falls by what was taken;
  historical cost, inventory value and profit are untouched, and no cost of goods sold is created.
- **It needs the same permission as any stock write** (`Inventory.Operate`, with Pick List access),
  and it is refused in a read-only support session like every other write.

#### The menu without a usable provider

Issue #729 puts the same answer in the navigation, so the menu says which features need a provider
before a page has to, and issue #868 reshaped what that looks like: no "Needs Nayax" section, Sites
above Machines, Site Commissions back under Reports, and five of the reports showing the same
connection-required page. Issue #869 added two Admin pages to it, and deliberately left a third
out.

- **There is no "Needs Nayax" section.** Issue #868 removed it: the menu is Dashboard, Pick List,
  **Sites**, **Machines** (since issue #740 — see [Machines](#machines) below), **Inventory** (the
  group that used to be called Products — the Products page inside it, its URL and its breadcrumb
  are unchanged), Purchases, Expenses, Reports and Admin, and what an entry needs from Nayax is
  shown on the entry itself rather than by where it sits. Issue #972 put that same order under the
  headings Operations, Inventory & purchasing, Finance & reports and Settings (see [The dashboard
  and the menu](#the-dashboard-and-the-menu)); none of them is about Nayax. **Site Commissions is back under
  Reports**, after Products and before GST.
- **A locked entry is still visible, still clickable.** Without the Nayax connection it needs, an
  entry is shown **locked**, and opening it — or typing its URL — reaches a "needs a provider" page
  with a **Connect** button for a member who may manage integrations. A member whose role may not
  see an entry does not see it locked either: the role rules apply first, so a locked entry is never
  an advertisement for a page that role never had, and a member without the Reports permission still
  reaches the ordinary "access not allowed" page.
- **Sites and Site Commissions need a working connection.** Each exists only for what the live Nayax
  fleet read returns, so either credentials that are missing *or* credentials that have stopped
  working lock them.
- **Five reports need a Nayax connection to exist at all** (issue #868): **Daily**, **Transactions**,
  **Reconciliation**, **Machines** and **Products** are locked while the business has no Nayax
  connection, and show the same connection-required page Site Commissions already showed. Because
  they read **stored** sales and make no live Nayax read, that is the *only* state that locks them:
  a connection whose read Nayax has refused, credentials that have stopped working, and a connection
  status that could not be read all leave these reports open. **Nothing stored is removed** — the
  sales already imported stay in the database and the reports over them open again, unchanged, as
  soon as Nayax is connected.
- **Bookkeeping and GST stay open.** They are never locked, so a business without Nayax still has
  them, and every report that is open shows its own availability states — complete, stale,
  unverified or missing (see
  [docs/architecture.md § Report availability model](docs/architecture.md#report-availability-model-issue-730)).
- **Two Admin pages need a working connection too** (issue #869): **Admin → Site Commission
  Agreements**, whose site picker *is* the live Nayax fleet read, and **Admin → AVCO Transition**
  (the Inventory AVCO Transition Baseline), whose preview reads your machines and then their
  products. Both show the same connection-required page and the same **Connect** action Site
  Commissions does, and — because what is behind them is read from Nayax as the page opens — either
  credentials that are missing *or* credentials that have stopped working lock them. Nothing of the
  page is loaded while it is locked, so no site list, preview, apply or baseline request is sent, and
  everything each page does once it opens is exactly as it was. A
  [support session](#super-admin-support-sessions) follows the **supported** business's own
  connection status and neither adds a lock nor bypasses one: a connected business opens the page in
  the read-only view it already had, and a business with no usable connection reaches the same
  connection-required page a member would.
- **Admin → Nayax Historical Cost Recovery stays available with no Nayax connection.** It makes no
  Nayax call at all: it reads the Nayax cost already **stored** on the sales you imported, so a
  business that has imported history and has since lost (or never had) a connection can still
  recover those costs. Admin → Costing Repair and Admin → Historical GST Classification are the
  business's own data and stay available too.
- **Waiting for one Nayax permission reads differently from having no connection.** With a usable
  connection whose Nayax account has not granted one read (`PendingPermissions`), the entries that
  need that read are locked as *waiting for a Nayax permission* and their page names the Nayax read
  and the endpoint behind it — "Machine list (GET machines)" — which is what a support request to
  Nayax has to ask for. No **Connect** button is offered there: the credentials are working, so
  re-entering them would fix nothing. Entries whose own reads are granted keep working beside them.
  The Nayax permission *key* behind a read is not named, because no published contract states it (see
  [docs/architecture.md § Provider-locked navigation](docs/architecture.md#provider-locked-navigation-issue-729)).
- **The provider's repair and admin tools are hidden, not locked**: Admin → Imports (the Nayax sales
  and settlement imports), Inventory → Import products (the catalogue review's own page, issue #888)
  and Admin → Nayax Sale Timestamp Repair are left out of the menu until the
  connection is usable, because without a provider there is nothing of the provider's to import or
  repair. The costing and GST repairs are the business's own data and stay. **Admin → Nayax Settings
  is now Admin → Integrations** and is always reachable — it is where a connection is made — with the
  page's own heading, the Admin hub card and its breadcrumb renamed with it; its URL is unchanged.
- **An unread connection status locks nothing.** If the status cannot be read, the menu offers every
  entry unlocked and each page's own request reports its own outcome, for the same reason a failed
  probe hides no filter above.
- **The menu updates as soon as the connection does; no page reload is needed.** Save or re-test a
  token on Admin → Integrations and the locked entries unlock and the provider's tools reappear
  there and then; lose the connection, or have Nayax refuse one read, and they lock again with the
  message that fits. The menu, the page and the URL always agree about the same destination.

**A connected business sees no change at all** beyond the renames and the order: every report,
filter, export and navigation path is exactly as it was.

The **Machines** entry itself is never locked — a business keeps its own machine records now (see
the next section), which is a different destination from the machine profitability **report** under
Reports.

## Machines

The machines a business operates are its own records, and they work with or without a sales provider
connected (issue #740). Before it, a machine was never stored at all — every machine list was a read
of the live Nayax fleet — so a business with no provider had no machines.

- **The Machines page is two lists.** *Your machines* is always there: it lists every machine this
  business has recorded, active and inactive, and holds the Add, Rename, Deactivate, Reactivate and
  Delete actions. *Live from Nayax* — the revenue and profit figures, and the links into each machine's
  stock and Sync Restock workflow — appears below it only while the Nayax connection can serve the
  fleet read. With no usable provider the live section is not shown at all, and the page is the local
  machine list alone; if the fleet read fails for some other reason (an outage, a transport failure),
  the page says so in place of that section instead of looking like a business with no machines.
- **Create a machine** with `POST /api/local-machines`, which answers the created machine with its
  new id. A machine needs a name and nothing else. Names are deliberately **not** unique within a
  business: two machines in one corridor may legitimately both be called "Level 3", and nothing
  resolves a machine by its name.
- **Rename one** with `PUT /api/local-machines/{id}`.
- **Deactivate and reactivate**, with `POST /api/local-machines/{id}/deactivate` and
  `.../reactivate`. **This is what a machine with history gets instead of a delete.** Recorded
  transactions, refills, machine counts and expenses refer to a machine, so a machine that stops
  trading keeps all of its history and stays in the list, marked *Inactive*, ready to be put back in
  service.
- **Delete a machine you added by mistake**, with `DELETE /api/local-machines/{id}` — and only one
  that nothing refers to yet. **Delete** appears on a row only where the server says the machine may
  be deleted, asks you to confirm and names the machine, and nothing is sent if you cancel. Every
  other machine says, in the server's own words, what refers to it — "This machine cannot be deleted
  because its history refers to it (2 machine stock counts, 1 operating expense)" — and is
  deactivated instead. What counts as history is a machine stock count, a stock movement (a refill
  included), a pick-list refill or an operating expense; **nothing is ever deleted along with the
  machine**, which is exactly why a machine with any of them is refused. A machine from Nayax is
  never offered Delete.
- **A machine from Nayax is renamed, deactivated and deleted in Nayax, not here.** The list offers no
  Rename, Deactivate or Delete on a row marked "From Nayax", and the API refuses all three with `400`
  and the sentence "This machine comes from Nayax and cannot be renamed here. Nothing was saved." (or
  "deactivated here", or "This machine comes from Nayax and cannot be deleted here. Nothing was
  deleted."). **Reactivate** is still offered, so a Nayax machine deactivated before this rule can be
  put back in service. The machine keeps the name it was given when it was recorded or linked:
  nothing copies a later Nayax rename across.
- **A machine the provider named is marked "From Nayax".** Every machine this database already
  referred to — through a sale, a refill, a machine count, an expense, a stock event, a sale
  timestamp repair or an inventory-cost transition — was given a local record when this change was
  released, carrying the provider's own machine id and the most recent name that had been stored for
  it (or "Machine" and the number, where nothing had named it). That provider id is never something a
  request chooses and is never published: the machine list says only *whether* the provider names a
  machine. Attaching one afterwards is the provider machine review below (issue #742), and nothing
  else in the application does it.
- **Your machines and the Nayax ones are one list.** A Nayax machine that has been linked *is* one of
  your machines, so it is in *Your machines* beside the ones you added, marked "From Nayax". The
  *Live from Nayax* section below is the provider's own view of the fleet — revenue, profit and the
  stock workflows — and shows every Nayax machine whether it is linked or not.
- **Nothing else moved onto these records yet.** Sales, refills, counts and reports still refer to a
  machine by the provider's own number, so no figure, report or export changes in this step; issue
  #741 is where those references are repointed at the local machine.
- **Sites still need Nayax.** A site is known only from the live fleet read, so the Sites page has
  nothing local to fall back on and is locked without a usable connection (it sits immediately
  above Machines in the menu since issue #868). Manual sites are a separate change.
- **In a read-only support view** the machine list is readable and every one of its write controls is
  hidden, like every other write surface in that view.

### Connected machines and the Pro allowance (issue #978)

Pro includes **five connected machines** and bills **A$2 per month** for each one beyond that (see
[Business plans](#business-plans-free-starter-and-pro)). This section is what that count is, where it
comes from, and what it does when it cannot be taken. Nothing in this release shows or bills any of
it: the counts are recorded so that the billing of issue #969 and the plan screens of #970 have dated
evidence to work from, and while plan enforcement is switched off — the shipped state — nothing is
displayed and nothing is charged.

- **A connected machine is a machine on your Nayax account**, whether or not it is linked in this
  application. That is deliberate: Nayax imports sales for every machine on the account either way, so
  the count follows the data you actually receive. A **manual** machine — one you added here, with no
  Nayax id — is never counted.
- **The count comes from the same fleet read the Machines page already makes**, authenticated with your
  own stored Nayax credentials. Every machine the read returns is counted once. Nayax's fleet response
  does carry a `MachineStatusBit`, but the Nayax documentation gives that field a meaning only for
  *creating* a machine, and says nothing about what a read returns for a machine that was removed — so
  this application deliberately does not read it, and counts every machine Nayax returns. Excluding
  inactive machines later is a change in its own right, with the then-confirmed Nayax contract behind
  it.
- **One count per day, as dated evidence.** The first complete fleet read of each UTC day records that
  day's count — the number, the machine ids behind it and when it was taken. The recorded counts are
  **append-only**: a later day is a later record, and nothing edits or deletes one, because the extra
  machines are billed from them. Concurrent page loads and the daily job can never produce two counts
  for one day: they all record through the same insert-if-absent claim, so the first to commit wins and
  the others simply discard what they read.
- **No extra Nayax calls on a page load.** The count is taken from a fleet read that was happening
  anyway. A background job tops it up once a day for businesses nothing read the fleet for — one read
  per business per day, only for businesses on a plan whose machines are counted and with a usable
  connection, and bounded per run. It is a command of the deployed application,
  `snapshot-connected-machines`, scheduled the same way the daily database backup is (see
  [Database backup and restore](#database-backup-and-restore)); **putting it on a schedule is a
  deliberate operator step**, and until it is scheduled the counts come from ordinary use of the
  application.
- **Above the allowance you are billed, never blocked.** Being over five machines never stops you
  linking a machine, importing sales, running a report or using the pick list, and nothing removes a
  link or any history. The entitlement the application reads reports the allowance, the current count,
  the day it was taken and how many machines are over the allowance — as an amount to charge for,
  never as a limit.
- **A count that could not be refreshed stays the count, and says how old it is.** A failed or partial
  fleet read records nothing at all: the last complete count remains, reported as *stale* with its age
  in days. It is never replaced by a zero — a fleet nobody could read is not a fleet with no machines
  in it.
- **No usable connection means "count unavailable".** If Nayax is not connected, or the stored
  credentials need attention, no call is made and no count is reported at all. Reconnecting and the
  next complete fleet read restore it.
- **Changing your Nayax account starts a new count series.** Saving credentials for a **different**
  Nayax operator id begins a new series, and the time of the change is recorded. The previous
  account's counts are kept exactly as they are and are never reported as the new account's, so a day
  you switched on can hold one count per account — the later one being that day's count. Re-saving or
  re-testing credentials for the **same** account changes nothing: it is the same account and the same
  fleet. Until the new account has been counted once, the count reads as unavailable rather than
  showing the account you left.
- **A read that was already under way when you switched accounts is recorded against the account it
  was made with.** Each count is attributed using the credentials that actually fetched the fleet, so
  a fleet read that started on the old account and finished after you connected the new one is kept as
  the old account's count — never relabelled as the new account's, and never taking up the new
  account's count for that day. Your first complete read on the new account still records its own
  count the same day.

### Connecting Nayax when you already have machines (issue #742)

A Nayax machine becomes one of your machines through the **Machines from Nayax** panel on the Machines
page, which reads the Nayax fleet as soon as the page can serve that read. What it does next depends
on one thing only: whether you have a machine of your own that Nayax does not already name.

- **If you have none, the new Nayax machines are simply added**, each as a new machine carrying the
  name Nayax calls it and that machine's Nayax id. The panel lists what it added. This is the ordinary
  case for a business that connected Nayax first: there is no machine of yours any of them could
  already be, so there is nothing to ask.
- **If you have any, nothing is linked and the panel asks you about each one.** For every Nayax
  machine that no machine of yours carries the id of, you choose: *Skip for now*, *It is one of my
  machines* (and pick it, which records the Nayax id on that machine), or *Add it as a new machine*.
  Every row starts on Skip, so a half-answered panel links only what you answered.
- **Nothing is matched by name, ever.** A machine of yours called exactly what Nayax calls one of its
  machines is a different machine until you say otherwise, and a machine whose own id happens to equal
  a Nayax machine number is a coincidence. This is the whole reason the panel asks: name matching is
  how a fleet quietly ends up attached to the wrong machines.
- **Linking records the Nayax id and changes nothing else.** The machine keeps *your* name for it —
  nothing synchronises a machine name — along with all of its history, stock, counts and costs. Only
  machines Nayax does not already name are offered, because a machine cannot be linked to two Nayax
  machines; a deactivated machine *is* offered and marked, since linking a retired machine beats
  duplicating it.
- **One Nayax machine, one of your machines.** Linking two of your machines to the same Nayax machine
  is refused, and so is linking a Nayax machine that one of your machines already carries. One refused
  answer refuses the whole panel: nothing is linked and nothing is added, because a half-applied
  result is the one you cannot reason about afterwards.
- **Until a machine is linked, nothing changes for it.** Its Nayax sales, stock events and reports
  stay under its Nayax machine number exactly as before, and it keeps appearing in *Live from Nayax*
  and the other Nayax pages. Linking is about which machine of yours that number means from now on.
- **With no usable Nayax connection the panel is not shown at all**, and if the fleet read fails it
  says so and claims nothing about your machines. **In a read-only support view it is hidden
  entirely**, like every other write surface there.

## Machine details: Sales and Products

Opening a machine (**Machines → a machine**) shows its most recent Nayax sales first, with the
machine's product table in a second tab (issue #768).

- **Two tabs, Sales then Products, and Sales is selected on arrival at every machine.** They are a
  proper tab list: the arrow keys move between them, `Home` and `End` jump to the ends, and each tab
  names its own panel for a screen reader. Switching tabs makes no new Nayax call — the panel you
  came from keeps what it already read.
- **Sales is Nayax's rolling latest-sales window, not this machine's sales history.** It is whatever
  Nayax currently reports as the machine's most recent transactions, newest first: the endpoint
  behind it guarantees no date range, no row count and no paging, so the page promises none of
  those. For a complete period use the reports, which are built from the sales this application has
  imported.
- **Each sale shows its date and time, the product name Nayax reported, the payment method and the
  amount**, labelled with the currency code Nayax reported — `9.50 AUD`, or `9.50 EUR` for a euro
  sale. **No currency symbol is invented**: the documented Nayax read declares the currency as
  optional and carries no symbol, so a sale Nayax reported without one reads `9.50 (currency not
  reported)` rather than being shown as dollars. Times are shown in your business's own time zone. A
  value Nayax did not report reads **Not reported** rather than being filled in with a zero or a
  guessed date, and **no transaction status is shown**: the documented Nayax endpoint for this read
  declares none, so the page does not invent one. No cost or profit is derived here.
- **Refresh re-reads Nayax, and changes nothing.** It is disabled while a request is in flight. A
  refresh imports no sale, moves no stock, recosts no inventory and applies no stock event — reading
  this tab a hundred times leaves every report, cost and quantity exactly as it was. Importing sales
  is still the separate sales sync.
- **Five different answers, never one that reads as "no sales".** A window Nayax reported as empty
  says so; a business with **no sales provider connected** is told exactly that, as information
  rather than a fault, because inventory and purchasing work fully without one; a connection that
  **needs reconnecting** and a **permission Nayax has not granted** each say what to do about it; and
  a request that **failed** offers Refresh and says plainly that it does not mean the machine has no
  sales.
- **The amount is a financial figure.** A role without `Dashboard.ViewFinancials` — today an
  Operator — still sees every sale's date and time, reported product name and payment method, and
  reads **Not available for your role** where the amount would be. The API withholds the amount and
  its currency, as `null` and never `0`, so this is not a client-side hide; Products stays fully
  usable. A platform administrator in a [support session](#super-admin-support-sessions) sees no
  Nayax data at all here, because a session resolves no Nayax credentials.
- **Products is the same product table as before**, with the same fields and the same financial
  redaction; its per-product Restock inputs are gone — see [Record refill from a machine's
  page](#record-refill-from-a-machines-page) below for what replaced them. The table itself is still
  read the first time you open that tab for a machine — once, not once per thing on screen that reads
  it — so opening a machine does not fetch it and switching back to Products does not fetch it again,
  and **Sync Restock stays its own section above the tabs**. While that read is outstanding the tab
  says it is loading, and a read that **failed** says so and offers a page reload: only a tab that has
  actually been answered reports a product count or tells you the machine has no products.

See [docs/architecture.md § Machine latest sales](docs/architecture.md#machine-latest-sales-issue-768)
for the read boundary, the verified Nayax contract and the authorization rule.

#### Record refill from a machine's page

Issue #744's final phase replaces the Products tab's per-product quantity inputs and Restock buttons
with one **Record refill** link, now that the pick list's manual entry (above) and its optional
machine selector both exist.

- **One link, not a control per product.** The link sits beside the product count in the Products
  tab and opens `/pick-list` in manual entry — [Recording a pick without a
  provider](#recording-a-pick-without-a-provider) above — with this machine already selected as its
  one quantity column, so you can search for whatever you took and save every quantity in the one
  atomic, idempotent request that screen already makes, and add the rest of the run's machines beside
  it if the run continued. It is never shown while a [support
  session](#super-admin-support-sessions) is active, exactly as the Restock inputs it replaced never
  were, because the pick list itself refuses to save during one too.
- **The machine chosen is always this business's own machine record**, never the sales provider's
  own machine number — the same local record the Machines page, Sync Restock and the machine's stock
  and expense figures already use. If that machine is no longer one of your active machines by the
  time the pick list opens — deactivated, for instance, in the moment between opening the two pages —
  the pick list says so plainly next to its machine selection rather than quietly recording the
  refill against no machine at all; you can still choose a machine yourself from the list, or save
  without one. While the pick list is still confirming which machine the link named, it says so and
  Save waits — you can type your quantities straight away, they move into that machine's column the
  moment it is confirmed, and saving becomes available then, so a refill opened from a machine's page
  is never recorded against no machine by accident. Entering a pick from the menu, with no machine
  linked, never waits for this.
- **Sync Restock is unaffected.** It still imports Nayax's own stock-adjustment alerts and still
  recognises a refill you recorded manually — through this link or otherwise — as already accounted
  for, so applying the matching Nayax event afterward still cannot deduct the same storage twice.
- **Stock display is unaffected.** The Stock column, its badge and every other figure on the Products
  tab are exactly as they were; only the write control changed.

## Business settings (Admin page)

**Admin → Business Settings** is where an Owner reads and changes their own business's name,
contact email, ABN, GST registration, time zone and the state or territory it trades in, through
`GET`/`PUT /api/business/settings` (issue #548, and issue #933 for the state). Only an Owner can
reach it — the route, the sidebar entry and the endpoint itself all require `Business.Manage` — and
the six fields are validated with the exact same rules as the business application form (issue
#543): a required name, a single well-formed contact address, an optional but checksum-validated
ABN, a GST registration that must be *stated*, never defaulted to "not registered", and a state or
territory that must be one of **NSW, VIC, QLD, SA, WA, TAS, ACT or NT**.

- **Changing the time zone needs an explicit confirmation.** Saving with a different time zone than
  the one the page was last read with shows a dialog explaining that business days and report date
  boundaries for **all of the business's data — past and future — will be recalculated in the new
  zone**, including that zone's own daylight-saving transitions; nothing stored changes, but a
  figure already shown for one business day may move to an adjacent one. Saving with the time zone
  unchanged needs no confirmation. See [docs/architecture.md § Business settings API](docs/architecture.md#business-settings-api-issue-548)
  for why the re-derivation happens on the next request rather than retroactively rewriting
  anything.
- **A successful save refreshes the shell before showing success.** The page re-fetches
  `GET /api/business/current` and calls the access service's `refresh()` — the same re-read a role
  change already triggers — before reporting the save as done, so the sidebar's business name, the
  active time zone and every date/time already on screen follow the new settings without a reload.
  If the save succeeds but that refresh cannot be read, the page says the settings were saved but
  the page could not refresh, and asks for a reload — it does not report the save as failed, and a
  time zone just saved is not confirmed a second time.
- **The state or territory decides which supplier records a business can be offered** (issue #933,
  under #817), which is why it is recorded rather than worked out: the time zone cannot tell NSW
  from the ACT, and nothing infers a state from a time zone or an address. **Changing it adds the
  platform suppliers the new state serves** and nothing else (issue #935): no supplier is removed,
  hidden or relinked — a supplier you have bought from stays — and no stock, cost or report figure
  moves. See [Suppliers from the platform list](#suppliers-from-the-platform-list).
- **The migrated business is missing its contact email, GST registration and state**, because it
  predates the application form (issue #540, and the state column itself — no existing business has
  one and nothing backfills them). While any of them is unrecorded, a dismissible prompt on the
  Dashboard and on this page asks the Owner to complete them; completing all three is what actually
  clears it, and the prompt on this page disappears as soon as the save that completes them
  succeeds; dismissing it only hides it for the page currently on screen, not everywhere, and it
  returns on the next visit or reload while the business is still incomplete. Nothing else is
  blocked while the state is unset — the business works exactly as before until the Owner records
  one.

## Delivery workflow

Changes are made on feature branches created from `develop` and validated through pull requests that target `develop`. Every pull request to `develop` or `main` runs the validation workflow. A push to `develop` builds and tests the backend without deploying. Production releases are separate pull requests from `develop` to `main`; a merge to `main` makes the code releasable but deploys nothing. A human deploys production by starting the **Deploy Production** workflow from the Actions tab on `main`; it has no inputs and deploys exactly the `main` commit it was dispatched from, with the migration baseline read from the recorded releases. A separate **Deploy Production (advanced recovery)** workflow, which takes a full commit SHA, exists only to deploy an exact earlier `main` commit or to bootstrap the first baseline when no release is recorded. Either one validates that commit, reports the database migrations production startup is expected to apply, deploys the API, checks `/health/ready`, and then deploys the frontend from the same commit (see `docs/automation.md` § Deploy Production). After opening a pull request, an automated engineering agent may update only its feature branch, for at most two permitted repair attempts in response to CI or review failures, and then returns control to a human. It never merges or deploys. An agent may prepare a release pull request only when a human explicitly requests it; a human reviews and merges that pull request, and a human starts Deploy Production.

Tasks intended for an implementation agent use the **Agent task** issue form, and every pull request uses the repository pull request template. Claude is the only agent provider: applying `agent-ready-claude` (the default) to a reviewed issue has Claude Code implement it, a separate read-only Claude invocation check its architecture, Claude fix the findings, and another separate read-only Claude invocation do the final review after exact-SHA validation. Both are recorded as same-provider reviews rather than independent ones. Copilot no longer implements, checks or reviews. The legacy label `agent-ready-full-claude` is still accepted and routes the same way. Model triage always uses Claude Haiku, on every route. All final reviews are comment-only and publish an explicit verdict. The repository owner may request at most two repairs, with `@claude repair` on the agent pull request. Human approval and branch protection remain the merge gate. The full lifecycle, authority model, task labels, risk classification, and retry policy are in [docs/automation.md](docs/automation.md).

Implementation model tiers: append `-low` for a cheap model or `-high` for a stronger model to either readiness label. The existing labels use a brief cheap triage to select low, standard or high automatically, and stop only when the requirements need clarification. Apply exactly one readiness label per issue. Architecture/review/repair models remain unchanged; see [Implementation model tiers](docs/automation.md#implementation-model-tiers) for models, costs, limits and rollout.

Agent branch/PR mutations use a dedicated GitHub App installation token rather than the repository `GITHUB_TOKEN`, so normal `pull_request` validation starts automatically for agent-created and agent-updated PRs instead of waiting for **Approve workflows to run**. Claude never receives the App credential: implementation, architecture and repair agents commit locally, then deterministic workflow steps mint a fresh short-lived token immediately before push/PR creation. One-time repository setup: install a dedicated GitHub App only on this repository with **Contents** and **Pull requests** set to read/write (Metadata read is implicit); set repository variables `AGENT_AUTOMATION_APP_CLIENT_ID` and `AGENT_AUTOMATION_APP_BOT_LOGIN`; store the App private key as repository secret `AGENT_AUTOMATION_APP_PRIVATE_KEY`. Do not grant the App Issues, Actions, Administration, Secrets, Deployments, Environments, or workflow-management permissions.

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to propose a change, and [SECURITY.md](SECURITY.md) for how to report a vulnerability privately.

## License

InventoryApp is **source-available and proprietary, not open source**. This public repository permits viewing and evaluating the source code, but it does not grant permission to copy, modify, redistribute, sublicense, sell, host, deploy, or use InventoryApp (or a substantial portion of it) for commercial or production purposes. Any use beyond viewing and evaluation requires prior written permission from the copyright holder. See [LICENSE](LICENSE) for the controlling terms.
