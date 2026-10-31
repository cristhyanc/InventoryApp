# Hosted testing environment

The hosted testing environment is a separate copy of Restockroo that runs the latest `develop`
commit. **Deploy Testing** (`.github/workflows/deploy-testing.yml`) deploys the API and the frontend
of each push to `develop`. Testing has its own Azure resources, its own SQLite database, its own
document storage, its own GitHub environment and its own Entra deployment identity. It shares no
credential with production. Production still changes only when a human starts **Deploy
Production** from `main`.

This repository creates no Azure resource and holds no identifier or secret for testing. Everything
below is a one-time human setup. Until it is done and `TESTING_DEPLOYMENT_ENABLED` is `true`, every
push to `develop` runs Deploy Testing as a no-op that reports "switched off".

## What a run does

1. **Resolve testing commit** (`contents: read`, `deployments: read`). Refuses any ref other than
   `refs/heads/develop`. Reads the `TESTING_DEPLOYMENT_ENABLED` switch and applies the runner-mode,
   isolation and tool-baseline gates. The testing commit is `github.sha`, the commit that triggered
   the run, which is fixed for the run and for its re-runs. The run checks that the commit is in
   `develop` and is not older than the newest uploaded or healthy testing commit (see
   [Ordering](#ordering-and-concurrency)).
2. **Validate testing commit** (`contents: read`, no environment, no secret). Checks out the exact
   commit, then runs the runner-placement and deployment-workflow contracts and `bash scripts/validate.sh`.
3. **Build testing artifacts** (`contents: read`). Publishes the API and removes
   `App_Data/jobs` from the package, so the production database-backup WebJob never reaches
   testing. Builds the Angular bundle once. Uploads both as `testing-api-<sha>` and
   `testing-frontend-<sha>`.
4. **Deploy test API** (environment `testing`, `contents: read`, `deployments: write`,
   `id-token: write`). The job runs only after validation and build both succeeded. It checks the
   testing variables, refusing the production App Service name and the production API host. It
   repeats the order check, then signs in with the testing identity through OIDC. Next it reads the
   test App Service settings and refuses to deploy unless the database and documents live outside
   the package (see [Test API application settings](#test-api-application-settings)). It compares
   those values without printing them. It then records the upload attempt as a GitHub deployment
   with task `deploy:testing-attempt` and stops if that record cannot be written. Finally it
   deploys `testing-api-<sha>`.
5. **Verify test API health** (no permissions). Polls `GET /health/ready` on `TESTING_API_BASE_URL`
   for up to about 15 minutes, because startup may be applying migrations.
6. **Record testing backend deployment** (`deployments: write`). Runs only after a healthy check. It
   records the commit as a GitHub deployment on environment `testing` with task
   `deploy:testing-backend` and `production_environment: false`.
7. **Deploy test frontend** (environment `testing`, `contents: read`, `id-token: write`). Writes
   `assets/config.json` in the downloaded bundle so it points at `TESTING_API_BASE_URL/api`, and
   refuses the production API host. Signs in with the testing identity and reads the **test** Static
   Web App's deployment token for this run only. It refuses the production Static Web App host,
   masks the token and signs out of Azure. Then it uploads the prebuilt bundle with the app build
   skipped, and checks that `/` answers 200 and that `/assets/config.json` names the test API.
8. **Testing deployment summary** (always, no permissions).

A failed run changes nothing else. It never reverts, merges, relabels, rolls back the test database
or starts another deployment.

### Ordering and concurrency

All runs share the `deploy-testing` concurrency group with `cancel-in-progress: false`, so a
deployment that has started always finishes. GitHub keeps at most one waiting run per group, and a
newer push replaces the waiting run (shown as cancelled), never the running one.

Two kinds of record are kept. An **attempt** record (`deploy:testing-attempt`) is written
immediately before every API upload. A **healthy** record (`deploy:testing-backend`) is written
only after `/health/ready` succeeds. The order check reads the newest record of each kind and
compares each with the run's commit through the GitHub compare API:

- **No record, the same commit, or a newer commit (for both):** the run deploys. Re-deploying the
  same commit is allowed, including one whose upload failed.
- **An older commit** (`behind` either record): the run is refused. This covers re-running an old
  run after a newer commit was deployed, and also after a newer commit was uploaded but then failed
  its health check or its healthy record. Its migrations may already have changed the test
  database, so older code must not be deployed over it. To recover, fix forward: push to `develop`
  or start **Run workflow** on `develop`.
- **A diverged commit:** the run is refused.

The check runs when the run starts and again immediately before Azure sign-in. Runs are serialized,
so no other testing deployment can happen between the check and the upload.

### Deployment records

Records are GitHub deployments on environment `testing` with task `deploy:testing-attempt`
(before each upload) or `deploy:testing-backend` (after each healthy deployment). If a
record ever names a commit that is no longer in `develop` (for example, after history was
rewritten), runs stop with "diverged". To clear it, a human deletes that deployment through the
REST API: first create an `inactive` status for it, then delete it. GitHub also creates its own
`deploy` task records for jobs that use the environment. The order check ignores those.

## One-time Azure setup

Use the same subscription as production or a separate one. Either way, put every test resource in
its own resource group. The names below are examples; choose your own.

### 1. Resource group

Azure portal → **Resource groups** → **Create**. Name it `restockroo-testing`. Use the same region
as production (Australia East).

### 2. Test API App Service (Linux, code)

1. **App Services** → **Create** → **Web App**.
   - Resource group: `restockroo-testing`. Name: for example `restockroo-testing-api`.
   - Publish: **Code**. Runtime stack: **.NET 10**. Operating system: **Linux**.
   - Pricing plan: a **new** Linux plan, not production's. Free F1 is enough for occasional manual
     testing. Use B1 or higher if you want Always On. Use one instance.
2. After creation, open the app → **Settings** → **Identity** → **System assigned** → **On** →
   **Save**. The document storage uses this identity.
3. Leave the publishing settings as Azure created them. The workflow signs in with OIDC and uses
   no publish profile, the same way Deploy Production deploys `vm-manager`.
4. **API** → **CORS**: leave **Allowed Origins empty**. The API adds the test frontend origin itself
   (`Cors__AdditionalAllowedOrigins__0`). An App Service CORS list would take precedence over it and
   drop the response headers the app exposes.
5. Create the database directory once. Open **Development Tools** → **SSH** → **Go** and run
   `mkdir -p /home/data`. SQLite does not create the directory. `/home` is the App Service's
   persistent storage, outside `/home/site/wwwroot`, which each deployment replaces.
6. Note the default domain, for example `https://restockroo-testing-api.azurewebsites.net`. That is
   `TESTING_API_BASE_URL`.

### 3. Test document storage

1. **Storage accounts** → **Create**. Use resource group `restockroo-testing`, a new account (for
   example `restockrootesting`), **Standard**, **LRS**. Under **Networking**, keep public network
   access enabled for selected networks or all networks, as production does. Under **Advanced**,
   disable **Allow enabling anonymous access on individual containers**.
2. Account → **Data storage** → **Containers** → **+ Container**. Name it
   `business-documents-testing` and set the access level to **Private**.
3. Container → **Access control (IAM)** → **Add role assignment**. Choose **Storage Blob Data
   Contributor** and the test App Service's **managed identity** (`restockroo-testing-api`). Scope
   the assignment to the container, not the account.

Do not configure `BackupStorage` for testing. Testing has no scheduled backup.

### 4. Test frontend Static Web App

**Static Web Apps** → **Create**. Use resource group `restockroo-testing`, a name such as
`restockroo-testing-web` (this is `TESTING_STATIC_WEB_APP_NAME`), and the **Free** plan. For
**Deployment source**, choose **Other**, not GitHub, so Azure adds no workflow or secret to this
repository. Note the URL, for example `https://<words>.azurestaticapps.net`. That is the test
frontend origin. A Static Web App created with GitHub as its source stays linked to that portal
workflow and rejects Deploy Testing's upload with "No matching Static Web App was found or the api
key was invalid", even with a fresh token. If that happens, delete it and create it again with
**Other**.

### 5. Test API application settings

Open the test App Service → **Settings** → **Environment variables** → **App settings**. Add the
values below and apply them. The deploy job checks the rows marked **required** and refuses to
deploy without them.

| Setting | Value | Why |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Staging` (**required**) | Not production. Logs and startup name the environment. |
| `Database__AllowAutomaticMigrationUnsafeOutsideDevelopment` | `true` (**required**) | The application's existing mechanism. Startup applies pending EF Core migrations and fails closed if they fail (see `docs/architecture.md`). Without this, a `Staging` app refuses to start with pending migrations. |
| `ConnectionStrings__DefaultConnection` | `Data Source=/home/data/inventory-testing.db` (**required**, must be under `/home/data/`) | Its own SQLite file, outside the deployed package, so every deployment preserves it. |
| `DocumentStorage__Provider` | `AzureBlob` (**required**) | Uploaded receipts live outside the package. |
| `DocumentStorage__BlobServiceUri` | `https://restockrootesting.blob.core.windows.net` (**required**) | The **test** storage account. |
| `DocumentStorage__ContainerName` | `business-documents-testing` (**required**) | The test container. |
| `Cors__AdditionalAllowedOrigins__0` | `https://<words>.azurestaticapps.net` | The test frontend origin: https, host only, no path or trailing slash. An invalid value fails startup. |
| `NayaxLynx__BaseUrl` | `https://qa-lynx.nayax.com` | The Nayax QA sandbox, not the live API. |
| `NayaxLynx__OperatorId` | *(empty)* | Overrides the production operator id in `appsettings.json`. |
| `WEBJOBS_STOPPED` | `1` | Belt and braces: the test package already contains no WebJob. |
| `SelfServiceOnboarding__Enabled` | `true` (optional) | Lets you create a test business by signing up. |
| `PlatformAdmin__DirectoryTenantId`, `PlatformAdmin__ObjectId` | your own Entra ids (optional) | Super Admin pages on testing, for example to approve that business. |
| `PriceCollector__TenantId`, `PriceCollector__ClientId`, `PriceCollector__RunExpiryHours` | see [Collector](#manual-supplier-price-collector-against-testing) | Only when you test the collector. |

**Leave unset**: `NayaxTokenProtection__*`, `BackupStorage__*`, `APPLICATIONINSIGHTS_CONNECTION_STRING`
(or point it at a separate test Application Insights resource), `ExternalId__*` (see below), and
`Subscriptions__EnforcementEnabled`. With no Nayax token key, no business on testing can store Nayax
credentials, so the test API makes no Nayax call. The deploy job warns when a Nayax key or backup
storage is configured. The application has no payment provider. Never copy production
connection strings, keys or storage settings into testing.

The database starts empty. Startup creates the schema from the migrations on the first deployment
and applies new migrations on later ones. Nothing rolls a migration back. To reset testing, stop the
app, delete `/home/data/inventory-testing.db` over SSH, empty the test container, and redeploy.

### 6. Entra sign-in for the test frontend (workforce)

The SPA's client id and API scope are compiled into the frontend
(`frontend/inventory-app/src/app/auth-config.ts`), so the test frontend signs in with the same
**workforce** app registrations as production:

1. **Microsoft Entra ID** → **App registrations** → the SPA registration (client id
   `0d451a7e-c66b-4550-be18-c36dc2753baf`) → **Authentication** → **Single-page application** →
   **Add URI**. Add `https://<words>.azurestaticapps.net/auth`. The sign-out URL is the origin
   itself. **Do not remove or change the production redirect URIs.**
2. The test API keeps the `AzureAd` section of `appsettings.json` unchanged (API client id
   `3dd61784-7289-4c2d-b0aa-3bb586a92004`, scope `access_as_user`). Do not override `AzureAd__*`.
3. A workforce token is therefore valid at both APIs. That is the limit of this design. Each API
   still authorizes only through its own database's memberships, so a production membership grants
   nothing on testing and the reverse. Full isolation needs runtime-configurable SPA client ids and
   is listed as a follow-up.

**Customer identity (External ID / CIAM).** It is off in source control
(`externalIdSettings` empty, `ExternalId__*` empty), so testing offers workforce sign-in only. To test
Google sign-in later, add `https://<words>.azurestaticapps.net/auth` as a SPA redirect URI on the
External ID tenant's SPA registration, and set the same `ExternalId__*` values on the test API that
production uses. Do this only when the compiled `externalIdSettings` are filled in.

### 7. Testing deployment identity (OIDC)

1. **Microsoft Entra ID** → **App registrations** → **New registration**. Name it
   `restockroo-testing-deploy`, single tenant, with no redirect URI. Do **not** create a client
   secret or certificate. This is not production's identity (`VmInventoryApi_CLIENT_ID_4D7F`) and
   not the collector's.
2. The registration → **Certificates & secrets** → **Federated credentials** → **Add credential**:
   - Scenario: **Other issuer**, so the portal does not rewrite the subject.
   - Issuer `https://token.actions.githubusercontent.com`, subject identifier
     `repo:cristhyanc/InventoryApp:environment:testing`, audience `api://AzureADTokenExchange`.
   - Do not use the ID-based subject the portal's GitHub scenario may build
     (`repo:cristhyanc@<owner id>/InventoryApp@<repository id>:environment:testing`). GitHub sends
     the classic subject above, and sign-in then fails with `AADSTS700213: No matching federated
     identity record found`. If the repository ever adopts a customised OIDC subject template,
     match that exactly instead.
3. Minimal role assignments (portal → resource → **Access control (IAM)** → **Add role assignment**
   → select the `restockroo-testing-deploy` service principal):

   | Scope | Role | Used for |
   | --- | --- | --- |
   | The test **App Service** `restockroo-testing-api` | **Website Contributor** | Deploying the package and reading its app settings for the storage check |
   | The test **Static Web App** `restockroo-testing-web` | **Contributor** (or a custom role with `Microsoft.Web/staticSites/read` and `Microsoft.Web/staticSites/listSecrets/action`) | Reading its deployment token for each run |

   Assign nothing on the resource group, the subscription, the storage account or any production
   resource. The identity therefore cannot read production's token or deploy production.

## One-time GitHub setup

1. **Settings** → **Environments** → **New environment** → `testing`.
   - **Deployment branches and tags** → **Selected branches and tags** → add `develop` only.
   - **Required reviewers**: none, so routine deployments run without approval. Leave the wait timer
     off.
   - **Environment variables** (all non-secret identifiers):

     | Variable | Value |
     | --- | --- |
     | `TESTING_AZURE_CLIENT_ID` | client id of `restockroo-testing-deploy` |
     | `TESTING_AZURE_TENANT_ID` | its tenant id |
     | `TESTING_AZURE_SUBSCRIPTION_ID` | the subscription holding `restockroo-testing` |
     | `TESTING_RESOURCE_GROUP` | `restockroo-testing` |
     | `TESTING_API_APP_NAME` | `restockroo-testing-api` |
     | `TESTING_API_BASE_URL` | `https://restockroo-testing-api.azurewebsites.net` (no `/api`) |
     | `TESTING_STATIC_WEB_APP_NAME` | `restockroo-testing-web` |

   - **Environment secrets**: none. The workflow uses no stored secret, and its contract check
     rejects any `secrets.` reference.
2. **Settings** → **Secrets and variables** → **Actions** → **Variables** → **New repository
   variable** `TESTING_DEPLOYMENT_ENABLED` = `true`. Setting it to anything else pauses testing
   deployments without a code change.
3. Leave `VmInventoryApi_Env`, its variables, its reviewers and the Static Web Apps token exactly as
   they are. Leave `INVENTORY_AGENT_SELF_HOSTED_ALL`, `INVENTORY_RUNNER_ISOLATION_VERIFIED` and
   `INVENTORY_RUNNER_BASELINE_VERIFIED` unchanged. In self-hosted mode, the testing deploy jobs wait
   for the same isolation and baseline verification as production.

## First deployment

Actions → **Deploy Testing** → **Run workflow** → branch `develop` → **Run workflow**. Alternatively,
merge any PR into `develop`. Check the summary: validation, build, test API deployment, health,
record and frontend should all show `success`. Then open the test frontend, sign in, and create a
test business (onboarding plus Super Admin approval, or `bootstrap-business` over SSH as in
`docs/tenant-rollout.md`).

Before the human setup above has been done and a run has succeeded, the hosted environment does not
exist. A green "switched off" run proves nothing about it.

## Manual supplier price collector against testing

**Price collector (manual test)** (`.github/workflows/price-collector.yml`) stays manual-only with
no schedule. Its call, item, page, turn, budget and time limits are unchanged. It now accepts a
start from reviewed `develop` as well as `main`, so a human can run it against this test API from
the code that is deployed there. It refuses every other ref and refuses the production API origin
(the one in `frontend/inventory-app/src/assets/config.json`).

The collector authenticates separately from the deployment:

| | Testing deployment | Collector |
| --- | --- | --- |
| GitHub environment | `testing` (develop, no reviewer) | `price-collector` (main and develop, required reviewer) |
| Entra app registration | `restockroo-testing-deploy` | the collector registration from `docs/price-collector-testing.md` |
| Federated subject | `repo:cristhyanc/InventoryApp:environment:testing` | `repo:cristhyanc/InventoryApp:environment:price-collector` |
| Token audience | Azure Resource Manager (deploys resources) | the API application `api://3dd61784-7289-4c2d-b0aa-3bb586a92004/.default`, app role `PriceCollector.Run` |
| Stored secrets | none | `ANTHROPIC_API_KEY`, `APIFY_TOKEN` (environment secrets) |

To point the collector at testing:

1. On the `price-collector` environment, add `develop` to **Deployment branches** next to `main`,
   and keep its required reviewer.
2. Set `PRICE_COLLECTOR_TEST_API_URL` to `TESTING_API_BASE_URL` (no `/api`).
3. On the **test** App Service, set `PriceCollector__TenantId` and `PriceCollector__ClientId` to the
   collector registration's tenant and client ids, and set `PriceCollector__RunExpiryHours` (for
   example `6`). Production's `PriceCollector__*` stay empty, so production still refuses every
   collector token.
4. Run it from **develop** as described in `docs/price-collector-testing.md`. Collector runs and
   testing deployments do not share a concurrency group, so do not merge to `develop` while a
   collector run is issuing or submitting work.

## What the repository verifies, and what it cannot

`scripts/validate-deployment-workflows.mjs` and its tests check the following, running the
workflow's own scripts against fake `gh` and `az` where behaviour matters:

- the triggers: push to `develop` and a dispatch with no inputs, nothing else
- the ref gates, and that every checkout is the resolved commit
- validation, build and health gating, and that a record is written only after health
- the order check: the same commit, a newer one or the first deployment pass, while an older,
  diverged, off-`develop` or unreadable one fails
- the identical re-check before Azure sign-in, and the single non-cancelling concurrency group
- that only the deploy jobs hold OIDC or the environment
- that no stored secret, production environment, identity, token or app name is used, and that no
  other workflow, production included, may use the testing environment or identity
- the storage and environment checks, and that the frontend is pointed at the test API, never
  production
- that the Static Web Apps token is masked before use

`BrowserClientOriginsTests` covers the configurable CORS origin. The collector's ref and
production-host rules are in `scripts/price-collector/workflow.test.mjs` and
`workflow-contract.test.mjs`.

Nothing here proves that the Azure resources, role assignments or federated credential exist, or
that a live deployment succeeds. Those are verified only by a successful run after the human setup.
The first end-to-end deployment succeeded on 31 Oct 2026, after three setup fixes:

| Failure in the run | Cause | Fix |
| --- | --- | --- |
| Azure Login: `AADSTS700213: No matching federated identity record found` | The federated credential used the ID-based subject | Add a credential with the classic subject (step 2 of the identity setup) |
| Deploy test frontend: "Could not read the test Static Web App with the testing deployment identity" | The deploy identity had no role on the Static Web App | Give it **Contributor** on that Static Web App |
| Deploy test frontend: "No matching Static Web App was found or the api key was invalid" | The Static Web App was created with GitHub as its source | Recreate it with deployment source **Other** |
