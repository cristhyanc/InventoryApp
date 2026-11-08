# Manual collector test (#929, stage 1)

The actor choices have already been tested. This run tests the **integration**: issue work from a
disposable Restockroo test API, let Claude search and match through the guard, submit each finished
file, and complete the run. It makes real paid supplier calls and writes the **test catalogue**.
A **test** run (the default `target`) never reaches the production API; a **production** run is a
separate, main-only target described in [Production run](#production-run). The same workflow also
has one daily production run, started at 05:00 Sydney time by an Azure Logic App and switched by
a repository variable; see [Daily schedule](#daily-schedule).
#926 stays open until real output/cost evidence exists.

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

In GitHub → Actions → **Price collector** → Run workflow:

1. Select **develop** (to test the code the hosted testing API runs) or **main**, and leave
   **target** on `test`.
2. Tick **confirm_test_api** to confirm that the environment points to a disposable test API and
   provider limits are set. Leave **confirm_production_api** off; the gate refuses a test run with it on.
3. Start with **3 supplier searches** (maximum 10). This is searches, not 3 products across every
   supplier; the API's due work controls which suppliers are available. Choose representative test
   products/suppliers in the test API when testing another route; the workflow never fabricates ids
   or overrides the backend's supplier list. A manual run asks for **all** work, so every supplier
   whose **Search prices** switch is on and that has a collection route is in its work, however
   recently it was checked — see [Pausing one supplier's searches](#pausing-one-suppliers-searches)
   and [Choosing `all` or `due`](#choosing-all-or-due) below.
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

Kmart and BIG W are capped at **5 results on 1 page** per search, in every run (owner decisions,
1 November 2026 for 10 results and 7 November 2026 for 5). Both actors charge per result: full run
41264033898 asked for 50 and 48 and cost about US$0.14 per Kmart and up to US$0.21 per BIG W search
(US$1.33 of the run's US$1.35 Apify spend, from the Apify Console), while every match they returned
was in the top 5 of its search. At 5 results a search costs about US$0.04 on either actor (start fee
plus 5 results), so 70 products cost about US$2.70-2.80 a run. Each actor's `maxItems` is one total
for the whole call, not a per-search limit, so the collector keeps one call per search: putting every
search in one call would either stop after 5 products or, with a larger total, return a full 48-result
page per search and cost more.
Varnmill cost about US$0.02 for 15 searches and keeps its 50-result pages.

BIG W and Kmart are searched **once a week** (owner decision, 1 November 2026): BIG W on Thursdays,
when its catalogues start, and Kmart on Mondays. Their supplier records' price freshness limit is
174 hours (a week plus the six-hour margin), so a price stays comparable until the next week's run.
Which suppliers are searched on which day is each supplier's own search schedule since #1108, and
only a `due` run follows it (see [Choosing `all` or `due`](#choosing-all-or-due)); a manual run with
the default **schedule** `all` still searches every supplier it is given.

### Run log

The owner decided on 31 October 2026 that a run must not be a black box: the run is logged in
full even though this repository's Actions logs are public. Each run shows:

- **Job summary**: the submission output (every outcome count and every reason the API gave,
  word for word, plus any submission error), a **Suppliers** table (#1110) with counts only, one row
  per supplier that had issued work: searches issued, given to Claude, **searched** and **not
  searched** (which add up to issued), the searched outcomes, and unusable files. A search counts
  as searched only when its result file passed the pre-submission check (this run, this sample) and
  the result-file schema with one of the four search outcomes (`complete`, `truncated`,
  `no_matches`, `failed`). No file, a file that says `not_searched`, a refused or malformed file, or
  a search left out of the run counts as not searched; malformed files are also counted as unusable;
  and a **Searches** list: for each search, the product
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

Tick **full_run** to see what a real run costs before the daily schedule is switched on; a
scheduled run is always a full run. It ignores **sample_size**, keeps the production limits in
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
JB Metro (not an Apify actor) reads up to 5 pages of 20 products per search in a full run, with 3
attempts of 25 seconds per page (raised from 2 of 15 after run 42128721044 lost 40 JB Metro searches
to slow answers and GraphQL internal errors); a sample reads one page of 5 with one attempt of 15
seconds. The guard appends these caps to every JB Metro call, so Claude cannot widen them.

The write hook also runs submission's own preparation and schema check on every result and match
file before it is written. A file submission would skip (for example a row with a field that is not
in `output.schema.json`, as two files in run 42128721044 had) is refused with the reason and code
`write-invalid`; a refused write is not recorded, so Claude can write the corrected file once.

The summary adds `Claude batches: N of M`, and Claude's cost and turns are totals across batches
(unknown if any batch did not report them). Apify spend is limited only by the Apify account usage
limit, so check it is set before a full run.

When a result file fails validation, the summary shows `skipped <work item>.json: field <path>`
(for example `field file.rows[2].packQuantity`), or `a file-level rule`, never the value or the
validator's message. One skipped file marks the submission failed, but every other file is still
submitted and the run is still completed.

One bad row no longer costs a whole search (owner decision, 3 November 2026). In production run
41558756584, ten Woolworths files were skipped: eight had a barcode with no `barcodeScope` and two
had a row with no `packPrice` (one an out-of-stock listing). Before it validates a result file,
`submit-results.mjs` now removes a barcode that has no `unit` or `carton` scope and keeps the rest of
that row, and drops a row with no usable price. A `complete` search that lost a row is submitted as
`truncated`, so the API never records that listing as not found. The summary lists each change as
`tidied <work item>.json: ...`, and tidying never marks the submission failed. `prompt.md` also tells
Claude not to write either kind of row in the first place.

Searches use short words (owner decision, 3 November 2026). Supplier searches return only items
that contain every word typed, so in run 41558756584 full names such as `Cheezels Original` or
`M&M's Peanut 46g` found nothing although JB Metro lists `45G CHEEZELS` and Coles lists
`M&M's Peanut Milk Chocolate Snack Bag 46g`. `prompt.md` now has Claude search with the brand and
one or two product words, with no size, and check the size and variant when it picks matches. A
search that returns nothing is tried once more with fewer words; that second call counts towards
the run's Apify call cap (600) and, at Kmart and BIG W, is charged like any other search. JB Metro
search words are written without apostrophes or `&`, because the command guard refuses them.

A JB Metro price includes JB's container deposit levy (issue #1082, owner decision, 3 November
2026). JB Metro charges a per-unit `NSW Levy` at checkout as a Magento fixed product tax that
`price_range.minimum_price.final_price` does not include, so drinks were recorded about 11-14c per
container cheaper than JB actually charges. The owner checked the decision against a JB Metro
invoice: the levy is ex GST, the same basis as `finalPrice`/`regularPrice` (GST is charged on the
item price *and* the levy together, for example $22.00 + $1.10 levy = $23.10 at 10% GST, exactly
$2.31). `jbmetro-lookup.mjs` now requests `fixed_product_taxes` and reports it as `levy` (the summed
amount), `levyLabels`, and `levyStatus` — the word that says what the site answered.

**`levyStatus` decides the row, because a levy nobody could read is not a product without a levy.**
`levy` is `null` for both, and `prompt.md` prices them oppositely:

- `published` — a readable levy. `packPrice` is `finalPrice` plus that `levy` and `regularPackPrice`
  is `regularPrice` plus the same `levy`, with no conversion, and the row's `sourceReference` says
  so.
- `none` — the site published an empty levy list, its answer for a product that carries no levy.
  The row is priced exactly as before this change.
- `unreadable` — the answer carried no levy list at all, or an amount that was missing, unparseable,
  negative or in another currency. **The collector writes no row for that product** and reports that
  search as `truncated` rather than `complete` (never `no_matches`), the same rule it already
  follows for a listing it could not price. It never falls back to the base price, never treats the
  levy as `0` and never adds up only the entries that read: a JB Metro drink priced without a levy
  JB does charge would win the Cheapest supplier comparison on a price that is not real. So a run
  whose JB Metro answers lose the levy field loses those readings and says so, instead of quietly
  recording prices that are 11-14c per container too low.

Shipping stays out of scope: it is charged separately on the invoice and is not part of any
product's price.

## Production run

A **production** run (owner decision, 1 November 2026) collects real supplier prices into the
production catalogue. It is the same workflow, started by hand with **target** `production`, or
by the [daily schedule](#daily-schedule) once that is switched on. What keeps it apart from a test run:

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

Actions → **Price collector** → Run workflow from **main**, **target** `production`, tick
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

## Pausing one supplier's searches

A Super Admin can stop paying for one supplier's searches without hiding that supplier from
businesses (issue #1077, the owner's decision of 3 November 2026). In the app, **Super Admin →
Supplier presets** has a **Search prices** column beside the existing **Suggested** column:

1. Find the supplier's row. The column shows **On** or **Off**, or **No price route** for a record
   nothing collects — Costco, for example — which has no search to start or stop and so offers no
   button.
2. Press **Stop searching** and confirm. From the next run the collector is issued no work for that
   supplier, on any product. Nothing in the workflow, `apify-limits.json` or the collector scripts
   needs changing: the API simply stops putting that supplier in the work list.
3. Press **Start searching** to resume. The next run includes it again — and since #1108 switching it
   back on also makes that supplier's pairs **due again from that instant**, so a `due` run searches
   them even if they were answered earlier the same day. That is deliberate: the reason to switch it
   back on is to get current prices.

What that switch does **not** do, which is the point of having two:

- It does not change what businesses see. **Suggested** is still what decides whether the record is
  copied into a newly approved business and offered under "Available to add", so pausing the paid
  Kmart and BIG W searches leaves both suppliers on offer.
- It removes nothing already collected. That supplier's listings, observed prices and pending
  readings stay exactly as they are, and the Cheapest supplier column keeps reading them until they
  age out by the record's own freshness limit (since #1108 derived from that supplier's search
  schedule — 174 hours for a once-a-week one) — after which the supplier simply has no comparable
  price rather than a wrong one.
- It does not cancel work a run already holds. A run issued before the switch moved can still submit
  its results and complete, because a submitted row is resolved through the work item it names.

Pressing **Disable** on the same page is still the separate decision it always was: it stops the
supplier being suggested to businesses and, since #1077, no longer stops its searches. Which of the
searched suppliers run on which day is each supplier's own **search schedule** since #1108 — see
below.

## Supplier search terms for one master product

Some suppliers name a product differently from the master product (issue #1138). Smith's
**Original** 45g is sold at JB Metro as `45G SMITH'S PLAIN`, so a search for the master product's
own wording finds nothing the collector can accept. A Super Admin can give that one product its own
search wording for one supplier, or for every supplier on one connector, without any global rule.

### Configuring it

In **Super Admin → Master products**, open the product and press **Edit**. Under **Supplier search
terms**:

1. Press **Add supplier search entry** and choose a target: one supplier (for example *JB Metro Penrith*) or a whole
   connector (for example *All JB Metro suppliers*, which covers all three JB Metro sites and no
   other connector).
2. Add up to **two search phrases**, in the order they should be tried. A third is refused by the
   page and by the API, because the collector never searches more than two wordings for one product
   at one supplier. Use **Move up** to reorder and **Remove** to drop one.
3. Add up to five **flavour aliases**: the supplier's other word for the product's flavour.
4. Press **Save**. Pressing **Remove entry** on every entry and saving clears the product's configuration.

Phrases and aliases are trimmed, inner spaces are collapsed, blanks are dropped and a repeat that
differs only by case is dropped. Only one entry per supplier and one per connector is allowed.

### Which entry a work item receives

Each work item stays per supplier preset. It receives exactly one entry, or none: the supplier's own
entry if the product has one, otherwise the entry for that supplier's connector. The two are never
merged, so a Penrith entry replaces the JB Metro connector entry for Penrith as a whole, while
Bankstown and Liverpool keep the connector entry. Products with no entry search exactly as before.

### What the collector does with it

- **Phrases are queries.** The first phrase is searched first. If it returns no products, or no
  result that is a valid match, the next distinct phrase is tried. Phrases are never joined, the
  two-wording limit still holds, and the known-listing fallback, truncated-versus-no-match reporting,
  budgets and guards are unchanged.
- **One file answers both searches** (`configuredPhrasesOutcome` in
  `scripts/price-collector/pagination.mjs`, also `pagination.mjs --configured-phrases`). The rows
  come from the search that found the matches. The item is `truncated` when any search was cut
  short, even if the fallback then found the product: a capped first search with no match and a
  fully read fallback with a match keeps the fallback's rows, reports `truncated`, and carries the
  capped search's `paging`. It is `no_matches` only when every search was read to the end and none
  matched. A failed search ends the item as `failed`; the next phrase is not searched after it.
- **Aliases are evidence, never queries.** An alias only lets a result's flavour word stand for the
  master product's flavour. Brand, product type, individual unit size and variant must still agree,
  the supplier's original title is still reported, and every match still waits for Super Admin
  approval.

### Worked example: Smith's Original 45g at JB Metro

Entry: *All JB Metro suppliers*, phrases `Smiths Plain` then `Smiths`, alias `Plain`.

| Result title | Outcome |
| --- | --- |
| `45G SMITH'S PLAIN` | Eligible: brand and 45g agree, `Plain` is the configured alias for Original |
| `27G SMITH'S PLAIN` | Rejected: the unit size differs |
| `45G SMITH'S SALT & VINEGAR` | Rejected: a different flavour, not an alias |

The automated tests cover only the configuration, the work-list payload and the prompt contract; no
live paid run was made for #1138.

## Choosing `all` or `due`

Since [#1108](https://github.com/cristhyanc/InventoryApp/issues/1108) the work list takes a
`schedule` query value, and **a manual run uses `all`** unless its **schedule** input is set to
`due`:

| Value | What it issues | Who asks for it |
| --- | --- | --- |
| `all`, or nothing at all | every searchable supplier paired with every product the rotation reaches | a manual run, whose **schedule** input defaults to `all` |
| `due` | only the product-and-supplier pairs owed a search on each supplier's own schedule | the [daily schedule](#daily-schedule) of [#1110](https://github.com/cristhyanc/InventoryApp/issues/1110), and a manual run with **schedule** `due` |

`all` is the default and is exactly what this route issued before #1108, so nothing in this document
changes for an ordinary test run. It **deliberately bypasses** the whole due rule, the in-flight
protection included: a manual full run may issue, and pay for, a pair another run answered an hour
ago or is searching right now. That is the point of it — somebody asking for a full run has decided
to re-search everything — so keep the search cap low (step 3 above) when testing against a
production-sized catalogue.

`due` is the one to use when checking the schedules by hand before the daily schedule is switched
on: run the workflow with **schedule** `due`. Three
things about it are worth knowing before reading a run log:

- **It can answer "nothing to do", and that is a success.** When no pair is due the API creates **no
  run at all** and answers an empty work list with `runId: null`. There is nothing to submit results
  for and nothing to finalise, so a workflow that treats a missing run id as a failure is reading it
  wrongly. This workflow ends such a run green with "Nothing due today" in its summary, without
  starting Claude or the finalisation step.
- **A pair already in flight is skipped, and a `due` call waits for the collector holding it.** Work
  issued by a run that is still open and not yet expired is not issued again, whenever that run was
  issued — including a run left open across midnight. While any such work is in flight a `due` call
  issues **nothing at all** and creates no run, even when some other pair is genuinely due: issuing
  would expire that run and record the searches in progress as `not_searched`, so the run a collector
  is working on is left alone instead. Everything deferred that way, its own pairs included, comes
  back on the next `due` call once that run has been finalised or has expired. So if a `due` call
  answers "nothing to do" unexpectedly, check for an open run before checking the schedules. (`all`
  does not wait: it expires the open run, which is part of the bypass above.)
- **A day with nothing scheduled is not the same as a day with nothing due.** The rule is per
  product-and-supplier pair and catches up: a Thursday-only supplier whose Thursday run never reached
  some of its products is still owed those searches on the Friday, and every day after, until
  something answers them.

Anything other than `all` or `due` is refused with `400` and nothing is issued, so a typo in a
workflow file cannot quietly re-search the whole catalogue. A supplier's schedule itself is set in
**Super Admin → Supplier presets**, whose Search schedule column and dialog fields arrived with
[#1109](https://github.com/cristhyanc/InventoryApp/issues/1109); the freshness limit beside it is
derived from that schedule, shown read-only on both the list and the dialog, and is not something to
set by hand.

## Daily schedule

[#1110](https://github.com/cristhyanc/InventoryApp/issues/1110) added one daily production run. It
first used GitHub's own cron (`'17 5 * * *'`, Australia/Sydney), but GitHub starts cron runs when it
has capacity: the first switched-on morning's run came about 5 hours late and the next had not
started 3 hours after its slot. So the owner decided (8 November 2026) that **an Azure Logic App
starts it at 05:00 Sydney time**, and the workflow has no GitHub cron any more. The Logic App calls
the workflow's dispatch API on **main** with one input, `scheduled_run: true`. Such a run is fixed,
whatever else it is given: it runs on **main** against **production**, asks for `due` work only, is
a **full run**, and allows the previously verified Kmart and BIG W actors, because each supplier's
**Search prices** switch and search schedule already decide whether it is searched that day. A
person can tick **scheduled_run** in the Run workflow form to start the same daily run by hand.

**It is switched by a repository variable.** A `scheduled_run` run continues only when
`PRICE_COLLECTOR_SCHEDULE_ENABLED` is exactly `true`. That check is made in the workflow's first
job (**Check run authorization**), which names no environment and holds no secret, API token or
OIDC permission, so a switched-off daily run ends there green, saying "Scheduled collection is
switched off", without requesting `price-collector-production` or its reviewer's approval. Any
other value, or no variable at all, means off. Disabling the Logic App also stops it.

**A missed day is caught up.** If Azure or GitHub fails one morning, the next run does that work,
because `due` is decided per product-and-supplier pair and an owed search stays owed. The workflow
does not check the hour, so a run that starts late still does its work. Runs share the
non-cancelling `price-collector` concurrency group, and the backend never issues a pair that another
open run holds. A day with nothing due ends green with "Nothing due today" and spends nothing.

**Approval.** The `price-collector-production` environment has a required reviewer, so every
switched-on daily run waits for that approval before its credentialed job starts, just like a manual
production run. The workflow does not change environment protection rules. To have the run start
at 05:00 without anyone approving it, the owner removes the required reviewer in GitHub → Settings →
Environments → `price-collector-production` (keep the main-only deployment branch rule); otherwise
it waits until the reviewer approves it.

### Set up the Azure Logic App

Do this after the change is released to **main**. Nothing here goes into the repository.

1. **Create a fine-grained GitHub token for this repository's Actions only.** GitHub → Settings →
   Developer settings → Personal access tokens → **Fine-grained tokens** → Generate new token. Name
   it `Restockroo price collector schedule`, choose an expiry (put a reminder in your calendar before
   it ends), Repository access **Only select repositories** → `cristhyanc/InventoryApp`, and under
   Repository permissions set **Actions** to **Read and write**. Leave every other permission at
   No access. This token cannot read or change code, secrets, variables, environments or settings,
   but repository-level Actions write is broader than starting this one workflow: it can also
   start, cancel, re-run and delete runs of **any** workflow in the repository, and enable or
   disable workflows. Treat it as a credential and keep it only in Key Vault (step 2). Copy it;
   GitHub shows it once, and it must never be pasted into the Logic App itself.
2. **Store the token in Key Vault.** Azure Portal → your Key Vault (or create one in the
   production resource group) → **Objects → Secrets → Generate/Import**. Name it
   `price-collector-dispatch-token` and paste the token as the value. Nothing else holds the token.
3. **Create the Logic App.** Azure Portal → Create a resource → **Logic App** → plan type
   **Consumption** (billed per action, a few cents a month at one run a day). Use the production
   resource group and region, and name it, for example, `restockroo-price-collector-schedule`.
   Then open **Settings → Identity**, turn the **System assigned** identity **On** and save.
   In the Key Vault, give that identity read access to secrets only: with the Azure RBAC
   permission model, **Access control (IAM) → Add role assignment → Key Vault Secrets User** →
   the Logic App's managed identity; with the access-policy model, an access policy with only the
   **Get** secret permission.
4. **Add the trigger.** In the designer choose **Schedule → Recurrence**: Interval `1`, Frequency
   `Day`, Time zone **(UTC+10:00) Canberra, Melbourne, Sydney** (`AUS Eastern Standard Time`, which
   follows daylight saving), At these hours `5`, At these minutes `0`.
5. **Read the token from Key Vault.** Add the **Azure Key Vault → Get secret** action. Create its
   connection with **Managed identity** authentication (the Logic App's system-assigned identity)
   and your vault name, and choose the secret `price-collector-dispatch-token`. Open this action's
   **Settings** and turn on **Secure outputs**, so the secret value is never shown in run history.
6. **Add the call to GitHub.** Add an **HTTP** action after it: Method `POST`, URI
   `https://api.github.com/repos/cristhyanc/InventoryApp/actions/workflows/price-collector.yml/dispatches`,
   Headers `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`,
   `User-Agent: restockroo-price-collector-schedule` and `Authorization` with the value `Bearer `
   followed by the **value** output of the Get secret step, picked from the dynamic content list
   (in code view `Bearer @{body('Get_secret')?['value']}`). Never type the token itself here. Body:

   ```json
   { "ref": "main", "inputs": { "scheduled_run": "true" } }
   ```

   Open the HTTP action's **Settings** and turn on **Secure inputs** and **Secure outputs**, so the
   header that carries the secret never appears in run history either. Save. The Logic App's
   definition (code view) now holds only the reference to the secret, never its value.
7. **Test it once.** With `PRICE_COLLECTOR_SCHEDULE_ENABLED` set to `true`, select **Run trigger →
   Run** in the Logic App. GitHub answers `204 No Content`, and a run named
   **Price collector (daily schedule)** appears under Actions. A failed **Get secret** step means
   the identity has no read access to the secret; from GitHub, a `401` means the token is wrong or
   expired, a `403` that it lacks the Actions permission, and a `422` that `main` does not have this
   workflow change yet. To renew the token, generate a new one and add it as a new version of the
   same Key Vault secret; the Logic App reads the latest version, so nothing else changes.

**Switching it on or off:** in GitHub → Settings → Secrets and variables → Actions → Variables,
`PRICE_COLLECTOR_SCHEDULE_ENABLED` set to `true` is on; deleting it, or any other value, is off.
Before the first morning, check the schedules by hand: run the workflow from **main** with
**target** `production`, **confirm_production_api** ticked, **schedule** `due`, **full_run** ticked
and **include_verified_retailers** ticked, and check that it issues only the suppliers due today, or
ends with "Nothing due today".

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
There is no automatic retry loop; the only automatic start is the [daily schedule](#daily-schedule), started by the Azure Logic App.

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
