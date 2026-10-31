# Manual collector test (#929, stage 1)

The actor choices have already been tested. This run tests the **integration**: issue work from a
disposable Restockroo test API, let Claude search and match through the guard, submit each finished
file, and complete the run. It makes real paid supplier calls and writes the **test catalogue**.
It has no production API input and no cron. #926 stays open until real output/cost evidence exists;
#929 stays open for the later Sydney schedule and production rollout.

## One-time human setup

1. Have a disposable HTTPS test API with the latest collector endpoints/migrations and a small set
   of tracked products/supplier presets. Never point this environment at the production database.
   Work issuance is a write; do not run another collector against this same test API concurrently.
   Unselected items from the issued run are completed as `not_searched`, not as missing listings.
2. In the workforce Entra tenant, on the **API** application registration, define the application
   role `PriceCollector.Run`, allowed member type **Applications**. On a separate **collector**
   app registration, assign that application permission on the API and grant admin consent.
   Do not create a client secret, certificate or delegated permission.
3. Add a federated credential to the collector registration:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Audience: `api://AzureADTokenExchange`
   - Subject: `repo:cristhyanc/InventoryApp:environment:price-collector`
   Check the actual subject if the repository has a custom/immutable OIDC subject template; the
   credential must match it exactly. No identifier or token belongs in a commit or public comment.
4. On the **test API**, set `PriceCollector__TenantId` and `PriceCollector__ClientId` to that tenant
   and collector application's GUIDs. Set `PriceCollector__RunExpiryHours` to at least 1 (default 6)
   and restart the test API. Its workforce API audience remains the API application's own id.
5. Create the GitHub environment **price-collector** with a required human reviewer and deployment
   branches limited to **main**. Add environment variables:

   | Variable | Value |
   | --- | --- |
   | `PRICE_COLLECTOR_TEST_API_URL` | Test API origin/base URL, **without** `/api` (the scripts add it) |
   | `PRICE_COLLECTOR_TENANT_ID` | Collector tenant GUID |
   | `PRICE_COLLECTOR_CLIENT_ID` | Collector application/client GUID, not the API's id |
   | `PRICE_COLLECTOR_API_SCOPE` | API application ID URI followed by `/.default`, e.g. `api://<API-application-id>/.default` |

   Add environment **secrets** `ANTHROPIC_API_KEY` and `APIFY_TOKEN`. These are API credentials,
   not a Claude subscription login. No Restockroo password, bearer token or client secret is stored.
6. Set the Apify account monthly usage limit, keep overage disabled, and set the Claude Console
   spend limit. The collector guards calls, items and pages; it does not enforce an Apify dollar cap.
   Keep the existing repository runner-mode and isolation variables unchanged. In self-hosted mode,
   this job is refused unless a human has already verified reset/ephemeral runner isolation.
7. Review and merge the feature PR into `develop`. The owner separately releases reviewed code to
   `main` through the normal process before running it. This workflow deliberately refuses feature
   branches and `develop`; implementing #929 does not authorize a release or a deployment.

## First run

In GitHub → Actions → **Price collector (manual test)** → Run workflow:

1. Select **main**.
2. Confirm that the environment points to a disposable test API and provider limits are set.
3. Start with **3 supplier searches** (maximum 10). This is searches, not 3 products across every
   supplier; the API's due work controls which suppliers are available. Choose representative test
   products/suppliers in the test API when testing another route; the workflow never fabricates ids
   or overrides backend supplier cadence.
4. Enable **include_verified_retailers** if this sample should exercise the previously verified
   Kmart/BIG W actors. This changes only the private run limits, not repository activation flags.
   Otherwise those actors stay blocked and their unanswered work becomes `not_searched`.
5. Leave **simulate_timeout** off and approve the environment job.

The sample allows at most **12 Apify calls**, at most **5 returned items** per call (Dromb still 1),
and **1 internal page**. Claude runs with at most **40 turns**, a **US$1 Claude budget**, and a
**14-minute internal deadline** (16-minute workflow step). The job has 45 minutes, and finalisation
has its own 15-minute step. Supplier calls can cost money even if no matching product is found.
Incomplete searches remain `truncated`; a successful actor run alone is not proof of completeness.

Review the counts/statuses in the Actions summary, then inspect proposed listings, pending prices
and match decisions through the test API's Super Admin pages. Confirm pack size, variant, tax basis
and currency there. No observed supplier price changes purchases, GST accounting or inventory.

## Interruption run and cost evidence

Repeat with **simulate_timeout** enabled. Claude is stopped after 10 seconds; the job is expected
to fail its Claude step while the finalisation step still runs. Any finished files are submitted;
the other work becomes `not_searched`. A fast run may finish before 10 seconds; use representative
work that takes longer to exercise interruption. With no finished files, run completion still runs.
Cancellation of the entire job or its hard timeout can skip finalisation; backend run expiry is
the backstop, not a claim that an `always()` step is guaranteed to run.

Record the run link, search outcomes, counts, duration and actual Apify spend per supplier from the
Apify Console. The Actions summary includes Claude's reported USD cost when available, never a
guessed Apify cost. An interrupted CLI may not emit its final cost record; inspect Claude Console
for that run's usage. Build the 71-product monthly estimate from representative actual supplier
searches and their cadence, not one generic multiplier. Public evidence must contain only counts,
statuses, duration and spend, not product names, prices, raw output or credentials.

The workflow does not upload result files or transcripts. It captures CLI output privately,
publishes only allowlisted counters, and deletes temporary state, CLI files and output in an
`always()` cleanup step. A hard runner loss can skip cleanup; verified ephemeral/reset runners are
still required in self-hosted mode. Do not add debug logging or attach the private directory to a PR.

## Implementation and checks

The trusted `prepare` step exchanges GitHub OIDC for a short-lived API token, issues work once, and
writes a private sample. `read-work.mjs` only reads that snapshot. Claude is launched in a clean
temporary workspace/home with an explicit environment allowlist: no GitHub token, OIDC request
credential, API token, shell startup overrides or inherited user/project Claude settings. The
settings enable only actor execution, literal helper commands and sample-bound write-once files;
the hook denies every other tool and blocks on hook failure. The CLI is pinned to 2.1.316, including
support for `onFailure: block`. The existing atomic Apify guard still reserves each call.

The finalizer obtains a **fresh** API token, copies only this sample's regular result files to a
private submission folder, and delegates independent schema validation, batching, outcome ordering
and completion to `submit-results.mjs`. It never prints raw error bodies or supplier-provided text.
There is no automatic retry loop or production schedule.

```bash
node --test scripts/price-collector/*.test.mjs
node scripts/price-collector/workflow-contract.mjs
node scripts/validate-runner-placement.mjs
node scripts/validate-deployment-workflows.mjs
bash scripts/validate.sh
```

References: [Claude CLI](https://code.claude.com/docs/en/cli-reference),
[hook failure handling](https://code.claude.com/docs/en/hooks#block-the-action-when-a-hook-fails),
[GitHub OIDC](https://docs.github.com/en/actions/reference/security/oidc), and
[Entra federation](https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation-create-trust).
