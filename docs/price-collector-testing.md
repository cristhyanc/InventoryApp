# Manual collector test (#929, stage 1)

The actor choices have already been tested. This run tests the **integration**: issue work from a
disposable Restockroo test API, let Claude search and match through the guard, submit each finished
file, and complete the run. It makes real paid supplier calls and writes the **test catalogue**.
It has no cron. A **test** run (the default `target`) never reaches the production API; a
**production** run is a separate, main-only target described in [Production run](#production-run).
#926 stays open until real output/cost evidence exists; #929 stays open for the later Sydney schedule.

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
   branches limited to **main** and **develop** (develop is how the collector is run against the
   hosted testing API; see [Hosted testing environment](testing-environment.md#manual-supplier-price-collector-against-testing)).
   This environment and its federated credential are the collector's own; they are not the
   `testing` environment or the testing deployment identity. Add environment variables:

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
7. Review and merge the feature PR into `develop`. The workflow runs only from reviewed **main** or
   reviewed **develop** and refuses every feature branch, and it refuses the production API origin
   (the one in `frontend/inventory-app/src/assets/config.json`). To test against the hosted testing
   API, run it from `develop` once Deploy Testing has deployed that commit; running it never
   authorizes a release or a production deployment.

## First run

In GitHub → Actions → **Price collector (manual)** → Run workflow:

1. Select **develop** (to test the code the hosted testing API runs) or **main**, and leave
   **target** on `test`.
2. Tick **confirm_test_api** to confirm that the environment points to a disposable test API and
   provider limits are set. Leave **confirm_production_api** off; the gate refuses a test run with it on.
3. Start with **3 supplier searches** (maximum 10). This is searches, not 3 products across every
   supplier; the API's due work controls which suppliers are available. Choose representative test
   products/suppliers in the test API when testing another route; the workflow never fabricates ids
   or overrides the backend's supplier list. Every enabled supplier with a collection route is in
   every run's work, however recently it was checked.
4. Enable **include_verified_retailers** if this sample should exercise the previously verified
   Kmart/BIG W actors. This changes only the private run limits, not repository activation flags.
   Otherwise those actors stay blocked and their unanswered work becomes `not_searched`.
5. Leave **stop_after_minutes** blank and approve the environment job.

The sample allows at most **12 Apify calls**, at most **5 returned items** per call (Dromb still 1),
and **1 internal page**. Claude runs with at most **40 turns**, a **US$1 Claude budget**, and a
**14-minute internal deadline**. The Claude step's 130-minute timeout is only a backstop; the job has
160 minutes, and finalisation has its own 15-minute step. Supplier calls can cost money even if no matching product is found.
Incomplete searches remain `truncated`; a successful actor run alone is not proof of completeness.

The summary also says how many turns Claude took and its result subtype, and one line per tool
with how many calls the hook allowed and denied and the fixed reason codes (for example
`write-wrong-run`, `not-own-dataset`, `not-recorded-string`). An Apify call that fails is counted
with a fixed failure label (`failed-input`, `failed-auth`, `failed-limit`, `failed-rate-limit`,
`failed-not-found`, `failed-timeout`, `failed-interrupt` or `failed-other`).

A row's stock answer never drops it (owner decision, 1 November 2026): `stockStatus` is optional in
`output.schema.json`, and `submit-results.mjs` submits a missing, `null` or unrecognised value as
`unknown`, because suppliers' stock signals are unreliable and ALDI's Varnmill results carry none.
The BIG W name search forces `maxReviewsPerProduct: 1` (the actor's minimum; 0 is refused as invalid
input) with `includeReviews: false`, so no reviews are fetched.

Claude never writes a row's observation time (owner decision, 1 November 2026): it has no clock, and
in run 41259875824 it dated JB Metro rows after the submission, so the API refused them as
`observed in the future`. `submit-results.mjs` sets every row's `observedAtUtc` to the time Claude
wrote that search's file (its modification time, kept when the workflow copies it for submission),
replacing any value in the file. Each run reads prices live, so that is the reading's time.

A single item has a pack of 1 (owner decision, 1 November 2026). Varnmill, Kmart and BIG W publish
no pack field, so `prompt.md` has Claude take the pack from the count in the title (`24 Pack`,
`x 30`, `2 Pack`) and write `1` for a title that names one item size with no count or pack word
(`Kinder Bueno Bar 43g`). Without it a single bar had no per-unit price and could never be the
cheapest. A pack or multipack whose count the title does not state still has no pack quantity.

Kmart and BIG W are capped at **10 results on 1 page** per search, in every run (owner decision,
1 November 2026). Both actors charge per result: full run 41264033898 asked for 50 and 48 and cost
about US$0.14 per Kmart and up to US$0.21 per BIG W search (US$1.33 of the run's US$1.35 Apify
spend, from the Apify Console), while every match they returned was in the top 5 of its search.
Varnmill cost about US$0.02 for 15 searches and keeps its 50-result pages.

BIG W and Kmart are searched **once a week** (owner decision, 1 November 2026): BIG W on Thursdays,
when its catalogues start, and Kmart on Mondays. Their supplier records' price freshness limit is
174 hours (a week plus the six-hour margin), so a price stays comparable until the next week's run.
Choosing which suppliers a run includes on which day is the later scheduled-run part of #929; a
manual run still searches every supplier it is given.

### Run log

The owner decided on 31 October 2026 that a run must not be a black box: the run is logged in
full even though this repository's Actions logs are public. Each run shows:

- **Job summary**: the submission output (every outcome count and every reason the API gave,
  word for word, plus any submission error) and a **Searches** list: for each search, the product
  and supplier, the outcome Claude reported (or that it wrote no file), and every row it wrote
  (title, supplier product id, price, pack, stock, link) with what the API did with that row and why.
- **Finalise step log**: the same, plus every tool call in order: its input as the guard allowed it,
  the hook's decision, and the tool's answer or error, shortened to 600 characters each; and for each
  Claude session, what Claude said at the end.
- **`price-collector-logs` artifact** (kept 7 days): `trace.jsonl` (every call, answer and failure in
  full, up to 20,000 characters per field), `claude-session-<n>.jsonl` (each Claude session's whole
  stream-json transcript), `issued-work.json` (the whole work list the API issued), `result-files/`
  (exactly what Claude wrote), `submission.json` (every API decision) and `summary.md`.

Credentials are never logged: every value of the Claude and Apify keys, the API token and the GitHub
tokens the step holds is masked, and so is any bearer token, JWT, `apify_api_` or `sk-ant-` value,
before a line reaches a log file. On a public repository anyone signed in to GitHub can download the
artifact, so treat it as public.

Review the counts/statuses in the Actions summary, then inspect proposed listings, pending prices
and match decisions through the test API's Super Admin pages. Confirm pack size, variant, tax basis
and currency there. No observed supplier price changes purchases, GST accounting or inventory.

## Full (production-sized) run

Tick **full_run** to see what a real run costs before any schedule is enabled. It is still manual
and still refuses the production API. It ignores **sample_size**, keeps the production limits in
`apify-limits.json` (up to 600 actor calls, full page sizes and up to 3 pages per search, except Kmart and BIG W) and gives
Claude every issued search, at most 576, in batches of about 24 (3 products of 8 searches). Each
batch is a fresh Claude session with 120 turns and a US$1 budget, so no session's context grows with
the whole catalogue and Claude can spend at most US$24 in one run. One product's searches always stay
in the same batch, because its match file can be written only once. The whole Claude phase has 120
minutes; the run stops at the
first batch that does not complete, and every search no batch reached becomes `not_searched`.
Tick **include_verified_retailers** too, or Kmart and BIG W searches become `not_searched`.
A full run covers the whole catalogue (owner decision, 1 November 2026): run 41303824929 used one
product per session and 15 sessions, so it reached only 15 of 71 products (120 of 568 searches) and
the other 56 were never searched. It took about 1.6 minutes and US$0.14 of Claude per product, so
the 71 products need about 115 minutes and US$10.
JB Metro (not an Apify actor) reads up to 5 pages of 20 products per search in a full run, with 2
attempts of 15 seconds per page; a sample reads one page of 5 with one attempt. The guard appends
these caps to every JB Metro call, so Claude cannot widen them.

The summary adds `Claude batches: N of M`, and Claude's cost and turns are totals across batches
(unknown if any batch did not report them). Apify spend is limited only by the Apify account usage
limit, so check it is set before a full run.

When a result file fails validation, the summary shows `skipped <work item>.json: field <path>`
(for example `field file.rows[2].packQuantity`), or `a file-level rule`, never the value or the
validator's message. One skipped file marks the submission failed, but every other file is still
submitted and the run is still completed.

## Production run

A **production** run (owner decision, 1 November 2026) collects real supplier prices into the
production catalogue. It is the same workflow, still manual with no schedule, with **target**
`production`. What keeps it apart from a test run:

- The gate and the credentialed job's own condition refuse it unless it was started from reviewed
  **main**; a test run still refuses the production API origin, from either ref.
- It runs in its **own** GitHub environment, `price-collector-production`, never `price-collector`,
  so it gets only that environment's variables, secrets and federated identity, and its required
  reviewer must approve every run.
- Its URL comes from its own variable, `PRICE_COLLECTOR_PRODUCTION_API_URL`, and
  `workflow.mjs`'s `collectorTarget` accepts only the production API origin there (the one in
  `frontend/inventory-app/src/assets/config.json`, with no path), so it cannot point at another host.
- It needs **confirm_production_api** ticked and **confirm_test_api** off; the target must be
  exactly `test` or `production`, and the scripts refuse anything else rather than picking one.

Running it changes no purchase, GST record or inventory: as in testing, a reading of a listing
nobody has confirmed is held for the Super Admin supplier price review.

### One-time production setup (human)

Do these in order. None of them is done by the workflow, and no id or key belongs in a commit or
public comment.

1. **Release and deploy.** Merge a release PR from `develop` into `main` that carries the collector
   and this workflow, then run **Deploy Production** from `main` so the production API has the
   collector endpoints and migrations (including the BIG W/Kmart weekly freshness change).
2. **Collector identity for production.** Create a **separate** collector app registration for
   production (recommended), so a token from the test environment is never one production accepts.
   On the API app registration the production API uses, assign it the `PriceCollector.Run`
   application role and grant admin consent. No client secret, certificate or delegated permission.
3. **Federated credential** on that production collector registration:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Audience: `api://AzureADTokenExchange`
   - Subject: `repo:cristhyanc/InventoryApp:environment:price-collector-production`
4. **Production App Service settings.** Set `PriceCollector__TenantId` and
   `PriceCollector__ClientId` to the tenant and the **production** collector registration's client
   id, and `PriceCollector__RunExpiryHours` (for example `6`), then restart the API. Until these are
   set, production refuses every collector token.
5. **GitHub environment `price-collector-production`.** Required reviewer: you. Deployment branches:
   **main only** (selected branches, `main`). Environment variables:

   | Variable | Value |
   | --- | --- |
   | `PRICE_COLLECTOR_PRODUCTION_API_URL` | The production API origin, **without** `/api` (the origin of `apiBaseUrl` in `frontend/inventory-app/src/assets/config.json`) |
   | `PRICE_COLLECTOR_TENANT_ID` | Collector tenant GUID |
   | `PRICE_COLLECTOR_CLIENT_ID` | The production collector registration's client GUID |
   | `PRICE_COLLECTOR_API_SCOPE` | The production API application ID URI followed by `/.default` |

   Environment **secrets** `ANTHROPIC_API_KEY` and `APIFY_TOKEN` (the same keys as testing, or
   separate ones to see production spend on its own). Do not set `PRICE_COLLECTOR_TEST_API_URL` here.
6. **Provider limits.** Check the Apify account monthly usage limit and the Claude Console spend
   limit cover production runs as well as testing.
7. **Products to search.** In production, turn on **Track supplier prices** for the products to
   collect, and make sure the supplier presets have their collection routes. A run issues work only
   for tracked products.

### Running it

Actions → **Price collector (manual)** → Run workflow from **main**, **target** `production`, tick
**confirm_production_api**, leave **confirm_test_api** off, choose a sample or **full_run** (tick
**include_verified_retailers** for Kmart and BIG W), and approve the environment job. Then confirm
proposed listings and pending prices in production's Super Admin supplier price review.

## Interruption run and cost evidence

Repeat with **stop_after_minutes** set to a whole number of minutes (1 to 14). Pick a value
shorter than the first run's Claude duration but long enough for at least one search to finish,
for example half of it. Claude is stopped at that point; the job is expected to fail its Claude
step while the finalisation step still runs. The finished files are submitted and the other work
becomes `not_searched`. If no file had finished, run completion still runs; repeat with a longer
value, because #929's evidence needs at least one finished file submitted after the stop.
Cancellation of the entire job or its hard timeout can skip finalisation; backend run expiry is
the backstop, not a claim that an `always()` step is guaranteed to run.

Record the run link, search outcomes, counts, duration and actual Apify spend per supplier from the
Apify Console. The Actions summary includes Claude's reported USD cost when available, never a
guessed Apify cost. An interrupted CLI may not emit its final cost record; inspect Claude Console
for that run's usage. Build the 71-product monthly estimate from representative actual supplier
searches and their cadence, not one generic multiplier. Evidence may name products, queries and
prices (see [Run log](#run-log)), never credentials.

The workflow uploads the masked run log described above, then deletes temporary state, CLI files and
output in an `always()` cleanup step. A hard runner loss can skip cleanup; verified ephemeral/reset
runners are still required in self-hosted mode.

## Implementation and checks

The trusted `prepare` step exchanges GitHub OIDC for a short-lived API token, issues work once, and
writes a private sample. `read-work.mjs` only reads that snapshot. Claude is launched in a clean
temporary workspace/home with an explicit environment allowlist: no GitHub token, OIDC request
credential, API token, shell startup overrides or inherited user/project Claude settings. The
settings enable only actor execution, literal helper commands and sample-bound write-once files;
the hook denies every other tool and blocks on hook failure. The CLI is pinned to 2.1.316, including
support for `onFailure: block`. The existing atomic Apify guard still reserves each call.
An actor call returns only the run's status and storage ids, so the hook also allows the free
`get-dataset-items` (limit capped to the sample page size, at most 3 reads per dataset),
`get-actor-run` (at most 6 polls per run) and `abort-actor-run` tools, only for runs this
collector started. A trusted `PostToolUse` hook records each run and its datasets from the answer
to an allowed actor call in a private ledger that Claude cannot write; the reads are not counted
as actor calls. Every other Apify account tool stays denied. Claude is launched by absolute path with a fixed `PATH`, and a cancelled job's
`SIGINT` stops Claude's process group just as `SIGTERM` does.

The finalizer obtains a **fresh** API token, copies only this sample's regular result files to a
private submission folder, and delegates independent schema validation, batching, outcome ordering
and completion to `submit-results.mjs`. It never prints raw error bodies or supplier-provided text.
There is no automatic retry loop or schedule.

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
