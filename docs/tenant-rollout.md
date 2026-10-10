# Tenant ownership rollout (issue #64)

Written for: the human operator performing the rollout, and the reviewer approving it.

This is the procedure for bringing the business/tenant ownership boundary into a live
environment. It predates issue #201, which restored automatic Production-startup migration once
this rollout was complete (see [Automatic Production-startup migration](#automatic-production-startup-migration-issue-201)
below) — every step below still applies to the bootstrap, which remains entirely human-performed;
only the schema-migration step's "deploy and let startup apply it" alternative is new.

## What the application does and does not do by itself

| Action | Who |
| --- | --- |
| Apply schema migrations | **Application**, automatically at Production startup (issue #201) — or **Human**, via `migrate-database --apply`, for diagnostics or ahead of a deployment window |
| Create the `Business` record | **Human**, via `bootstrap-business` |
| Create `BusinessMembership` rows from the supplied Entra mapping, in the role it names | **Human**, via `bootstrap-business` (see [The member's role](#the-members-role-issue-521)) |
| Assign existing rows to that business | **Human**, via `bootstrap-business --apply` |
| Move the global Nayax operator id and token into that business's record | **Human**, via `migrate-nayax-connection --apply` (issue #519; see [Migrating the Nayax connection](#migrating-the-nayax-connection-into-the-business-issue-519)) |
| Remove the global `NayaxLynx` operator id and token | **Human**, once the per-business record is in use and verified (the client reads it since issue #520; `NayaxLynx__BaseUrl` stays) |
| Back up the database | **Human** |
| Deploy | **Human** |

**For this rollout specifically, apply the schema by hand first (step 2 below) rather than relying
on automatic startup migration.** The tenancy migrations are high risk — the uniqueness one
rebuilds the whole `NayaxSales` table — and this procedure depends on reviewing exactly what step 2
applied before running the bootstrap in step 3 onward. Take the verified backup either way.

Schema migrations never assign tenant ownership or perform the business backfill. That exists
exclusively in the `bootstrap-business` command, which the API never invokes — starting the web
host and running either command are mutually exclusive paths through `Program.cs`.

Schema migrations are not, however, data-free. Several rebuild a table and copy every row into a
new one; the `ScopeUniqueConstraintsByBusiness` migration does this to `NayaxSales` in order to
re-key it. That is why step 1 below asks for a verified backup before step 2 applies anything,
whether applied by hand or by an automatic startup.

## Automatic Production-startup migration (issue #201)

Outside of this rollout procedure, normal Production startup applies pending migrations
automatically rather than refusing to start — the same as Development and `Testing`. A migration
failure still stops the application rather than letting it serve requests against a schema its
code does not match, and the failure is logged with the environment and the pending migration
names. See `DatabaseSchemaStartup` and its concurrency note for why no distributed lock is needed
for the single-instance App Service deployment this application runs on (docs/architecture.md §
SQLite operating assumptions).

Disposable databases keep the same automatic behaviour they always had: Development and `Testing`
migrate automatically, and any other non-Production environment (an ephemeral integration-test or
Staging database, for example) does so only when
`Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` is `true`.

The explicit `migrate-database --dry-run`/`--apply` command remains available and is still the
right tool for diagnostics, for inspecting what a pending deployment will apply, or for applying a
high-risk migration ahead of a deployment window under review — as this rollout's step 2 does.

## Running the commands

The operator commands ship inside the application. How you invoke them depends on what you are
standing in front of.

**A source tree, with the .NET SDK installed** (local development, a build agent):

```bash
dotnet run --project backend/InventoryApi -- migrate-database --dry-run
dotnet run --project backend/InventoryApi -- migrate-database --apply
dotnet run --project backend/InventoryApi -- bootstrap-business --dry-run
dotnet run --project backend/InventoryApi -- bootstrap-business --apply
dotnet run --project backend/InventoryApi -- migrate-nayax-connection --dry-run
dotnet run --project backend/InventoryApi -- migrate-nayax-connection --apply
```

**The deployed application** (Azure App Service, or anywhere the published output runs). The
deployment is `dotnet publish` output: it contains no sources and no SDK, so `dotnet run
--project` does not work there. From the directory holding `InventoryApi.dll`:

```bash
dotnet InventoryApi.dll migrate-database --dry-run
dotnet InventoryApi.dll migrate-database --apply
dotnet InventoryApi.dll bootstrap-business --dry-run
dotnet InventoryApi.dll bootstrap-business --apply
dotnet InventoryApi.dll migrate-nayax-connection --dry-run
dotnet InventoryApi.dll migrate-nayax-connection --apply
```

On App Service, run these from the SSH/console session for the app, where the environment already
holds the connection string and application settings. The rest of this document uses the source
form for brevity; substitute the deployed form when working against a deployed environment.

## The state between schema and bootstrap

After the schema is applied and before the bootstrap is run, every pre-existing row has
`BusinessId = 0`. That is not a valid business key, so:

- every signed-in caller sees **an empty dataset**;
- no data has been lost, changed, or exposed.

This is the intended fail-closed state, but it looks alarming from the outside. The API logs an
error at startup while it persists, naming this document. Plan the window between the two steps
accordingly, or perform them back to back.

## Before you start

1. **A full, restorable backup of the production database.** The backfill is verified and
   transactional, but it is still a one-way ownership change on financial history.
2. **The Entra mapping, from a person.** For each operator who must have access, the validated
   `(tid, oid)` claim pair from their token. Not email, not display name.
3. **Confirmation that the schema migrations have been reviewed** — see `git log` for
   `AddBusinessOwnershipModel`, `AddBusinessOwnershipToTenantOwnedEntities`,
   `ScopeUniqueConstraintsByBusiness` and `AddBusinessBackfillAudit`.

### Where the mapping goes

Never into source control. `appsettings.json` ships the section empty:

```json
"BusinessBootstrap": {
  "BusinessName": "",
  "Members": []
}
```

Supply the real values per environment:

- **Local development:** `dotnet user-secrets`

  ```bash
  cd backend/InventoryApi
  dotnet user-secrets set "BusinessBootstrap:BusinessName" "<business name>"
  dotnet user-secrets set "BusinessBootstrap:Members:0:DirectoryTenantId" "<tid>"
  dotnet user-secrets set "BusinessBootstrap:Members:0:ObjectId" "<oid>"
  dotnet user-secrets set "BusinessBootstrap:Members:0:Role" "Owner"
  ```

- **Azure:** application settings on the App Service, using the same
  `BusinessBootstrap__Members__0__ObjectId` key form. Remove them once the bootstrap is
  complete; they are needed only while the command runs.

With the section empty or malformed the command refuses and changes nothing. It will not invent
an identity, and it will not create a business nobody can sign in to.

#### The member's role (issue #521)

`Role` is optional. Leave it out and the member is created as an `Owner`, which is what this
command has always created and what the migration backfilled every pre-existing membership with —
so an existing configuration behaves exactly as it did before roles existed, and the person running
the bootstrap keeps full access.

The accepted values are `Owner`, `Manager` and `Operator`, matched case-insensitively:

| Role | What it may do |
| --- | --- |
| `Owner` | Everything, including the Nayax integration, data repairs, members, roles and the business record |
| `Manager` | Everything operational, plus the financial figures: dashboard financials, reports, expenses, imports, site commissions and supplier GST defaults |
| `Operator` | Day-to-day work only: dashboard without financial figures, pick list, inventory and purchasing |

Anything else — a misspelling such as `Manger`, a role that does not exist yet such as `Viewer`,
or a bare number such as `40` — refuses the whole run and writes nothing, rather than creating a
member who would then be denied at sign-in. Configuration names a role; it does not supply the
stored value.

Give each member their own `Role` key (`Members:1:Role`, and so on); one entry's role never carries
onto another. Re-running the command never changes the role an existing member already holds, for
the same reason it never resurrects a revoked approval — so a role cannot be corrected by editing
this configuration and running again. Today a role is set when the membership row is created and
changed only by a human directly in the database; the members page that will change one is issue
#524. A signed-in member can see their own role and capabilities at `GET /api/me/access`, and a
change applies on their next request.

## Rollout sequence

Steps 3 and 5 are the two decision points. Do not continue past either without reading the
output.

**1. Back up the database.** Verify the backup restores.

**2. Apply the schema, by hand.** Using the form for your environment (see
[Running the commands](#running-the-commands)), first see what is pending:

```bash
migrate-database --dry-run
```

Read the list, confirm it is what review approved, then apply it:

```bash
migrate-database --apply
```

This creates the tenancy tables, the `BusinessId` columns and the audit table, and assigns no
ownership. Note that applying is not purely additive: `ScopeUniqueConstraintsByBusiness` rebuilds
the `NayaxSales` table and copies every sale row into it, which is why step 1's backup is not
optional.

`--apply` and `--dry-run` are mutually exclusive; passing both, or any other flag, is refused
rather than resolved by precedence.

For this rollout, apply the schema by hand as described above rather than deploying and letting
Production startup migrate automatically (issue #201): the point of this step is the deliberate
review the migration list gets before it runs, and `--dry-run` is where that review happens. A
deploy against a database that still has these migrations pending would now apply them
automatically rather than refuse to start — do not rely on that as a substitute for step 2's review.

**3. Dry run the bootstrap.**

```bash
bootstrap-business --dry-run
```

This performs the whole operation inside a transaction, verifies it, prints the result and then
**rolls it back**. The numbers it prints are measured, not predicted — an apply assigns exactly
what the dry run reported.

Read the output and confirm:

- `Rows assigned` matches roughly what you expect the database to contain;
- every table shows `Left = 0`;
- every financial and inventory total shows `OK = yes`, with identical before and after values.

If anything looks wrong, stop. Nothing has been changed.

**4. Apply the bootstrap.**

```bash
bootstrap-business --apply
```

`--apply` must be typed explicitly; an invocation with neither flag is a dry run.

**5. Confirm the result.** The command re-prints the same table, now committed. It fails and
rolls back automatically if any row count or financial total moved. Then check by hand:

- sign in as a configured operator and confirm the dashboards and reports show the expected
  figures;
- compare a few known totals against the pre-rollout backup;
- confirm the API's startup log now reports tenant ownership as bootstrapped — it requires no
  unassigned rows, **exactly one** business which is **active**, and at least one active
  membership on that active business, so a partial rollout will not report success. "Exactly
  one" is deliberate for this single-business rollout: a second business appearing is a state a
  human should look at, not a healthy steady state, so it stops reporting ready;
- read the `BusinessBackfillAudits` table — one row per table, recording rows assigned and rows
  left unassigned, for the permanent record.

**6. Remove the bootstrap settings** from the environment.

## If something goes wrong

- **The command refuses.** Nothing was written. The message says what to fix.
- **Verification failed.** The transaction was rolled back; the database is as it was. Do not
  re-run until the discrepancy is understood.
- **The run was interrupted.** Re-run it. The backfill only ever touches rows that are still
  unassigned, so a partial run completes rather than double-applying, and the business and its
  memberships are reused rather than duplicated.
- **"the business has no active membership".** Every configured actor matches only a revoked
  membership, so assigning the data would hand it to a business nobody can reach. Nothing was
  assigned. Either reactivate a membership deliberately, or add an actor who should have access
  to `BusinessBootstrap:Members` and run again — the bootstrap will not reactivate a revoked
  approval on your behalf.
- **The API refuses to start with "pending migration(s)" against a non-Production database** (an
  ephemeral integration-test or Staging environment, for example). Run `migrate-database
  --dry-run`, review, then `--apply`, or set `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment`
  for that disposable environment. Production applies pending migrations automatically instead of
  refusing (issue #201) — if Production starts with these tenancy migrations still pending, it
  applies them itself rather than waiting for step 2's manual review, so keep to the sequence above
  rather than relying on that as a substitute for reviewing what `--dry-run` reports.
- **"the business ... is deactivated".** The business that would own the data is inactive, so its
  records would be unreachable whoever is a member. Reactivate it deliberately, then run again.
- **Ownership was assigned to the wrong business.** Restore from the backup. Do not attempt to
  reassign rows by hand; ownership is immutable through the application and editing it directly
  bypasses every check in the boundary.
- **A migration refused to apply with "holds more than one active BusinessMembership".** That is
  the `AddOneActiveMembershipPerIdentity` migration of issue #522 declining to guess which
  business a person stays in. Nothing was changed. See
  [One active membership per identity](#one-active-membership-per-identity-issue-522) below.

## One active membership per identity (issue #522)

A person belongs to one business at a time, and since issue #522 the database enforces it: a
unique index allows at most one **active** `BusinessMembership` per Entra identity
(`(DirectoryTenantId, ObjectId)`), whatever state the owning businesses are in. Revoked rows are
unaffected — any number may exist — so revoking a membership is how somebody moves from one
business to another.

This matters to an operator in exactly one situation: the migration that adds the index checks the
existing data first, and **refuses to apply** if any identity already holds more than one active
membership.

### If the migration aborts

The failure looks like this (one line, wrapped here), and it is the whole of what happened:

```text
Cannot apply AddOneActiveMembershipPerIdentity: at least one identity (DirectoryTenantId,
ObjectId) holds more than one active BusinessMembership, and this migration will not choose
which one to keep. Revoke all but one active membership per person, then run the migration
again. Nothing has been changed. See docs/tenant-rollout.md.
```

**Nothing was changed.** The migration runs in a transaction and aborts before its first schema
change, so no column, no index and no row was touched, and the migration is not recorded as
applied. The application does not start against the new code with the old schema either: startup
migration fails closed (see [Automatic Production-startup migration](#automatic-production-startup-migration-issue-201)),
so the API logs a critical error and refuses to serve requests rather than running against a
schema its code does not match. Expect an outage until the data is resolved, and plan the upgrade
accordingly.

It will not pick a membership for you. Which business a person keeps access to is a decision about
access to financial data: choosing wrongly either strands somebody or shows them another
business's ledger, and neither is a decision a migration may make unattended.

**Resolving it.**

1. Find the conflicting identities. On a copy of the database, or through a read-only connection:

   ```sql
   SELECT "DirectoryTenantId", "ObjectId", COUNT(*) AS "ActiveMemberships"
   FROM "BusinessMemberships"
   WHERE "IsActive" = 1
   GROUP BY "DirectoryTenantId", "ObjectId"
   HAVING COUNT(*) > 1;
   ```

   The result is Entra identifiers, not names: the membership table deliberately stores no email
   address or display name. Match them to people through Entra, with a person who is entitled to.

2. **Ask, then decide.** For each identity, a human decides which single business that person keeps
   — and says so to the person and to the business losing access. Do not infer it from which
   membership is older or which business is busier.

3. Back up the database, verify the backup restores, and revoke every other active membership for
   that identity by setting `IsActive = 0` (leave the rows: they are the membership history, and
   deleting one would lose the record that the access ever existed). There is no
   `StatusChangedAtUtc` to set yet — the aborted migration is the one that adds that column, and
   when it does apply it fills every row from `CreatedAtUtc`.

4. Re-run the upgrade. The migration re-checks and applies normally once no identity has two
   active memberships; the refusal is repeatable, not a one-off, so a half-resolved database stops
   it again rather than applying with the problem still present.

Note that until the duplicate exists the index never fires: the application's single live business
cannot produce this state through any supported path, and no request path creates, revokes or
reactivates a membership today. A database that has it, has it from a hand-written change.

## Migrating the Nayax connection into the business (issue #519)

A separate, later step, and a separate human decision. The ownership rollout above gives the
business its data; this gives it its own Nayax credential. It is **not** part of the sequence above
and must not be run before it: the command refuses until ownership is bootstrapped.

Today the Nayax integration is configured once for the whole application — `NayaxLynx:OperatorId`
and the bearer token (`NayaxLynx:AccessToken`, or the legacy `Nayax:Token` key). Issue #518 added
each business's own encrypted connection record, empty for every business. This command copies the
configured operator id and token into the existing business's record, encrypted, and marks the
connection `Ready`, because that token is the one already authenticating to Nayax in production —
the command never calls Nayax and performs no permission test.

### Before you run it

1. **The token encryption key must be provisioned** for the environment:
   `NayaxTokenProtection:ActiveKeyId` and the matching `NayaxTokenProtection:Keys:<key-id>` entry
   (a base64-encoded 256-bit AES key — see README.md § Configuration and secrets). Both the dry run
   and the apply refuse with the setting named while it is absent, because a credential this
   command cannot encrypt is never stored in some other form.
2. **The ownership bootstrap must be complete** — no unassigned rows, exactly one active business,
   at least one usable membership. This is the same readiness `TenantOwnershipReadiness` reports in
   the startup log.
3. **Nothing else must have stored a credential for that business.** If a different operator id or
   token is already there, the command refuses rather than replacing it.

### Dry run, then apply

```bash
migrate-nayax-connection --dry-run
```

The dry run writes **nothing at all** — no row, no status, no credential revision. It reports the
business it resolved, the configured operator id, what is stored today, and the change an apply
would make. Nothing it prints is a secret: the access token is never printed, never logged, and
never put in a message or an exception, not even as a length or a prefix, and whether the stored
token matches the configured one is reported as a plain yes/no.

Read the output and confirm:

- `Business id` is the business you expect;
- `Configured operator` is the Nayax operator account this business trades under;
- `Change` is `CredentialsStored` (nothing stored yet) or `None` (already migrated).

Then apply:

```bash
migrate-nayax-connection --apply
```

`--apply` must be typed explicitly; an invocation with neither flag is a dry run, and passing both
is refused rather than resolved by precedence. Re-running an applied migration changes nothing at
all — the row is not rewritten, so the stored ciphertext and the credential revision do not move.
Exit code `0` means the run succeeded, including an idempotent re-run; `1` means it did not.

The apply is **one transaction**: the credential and the `Ready` status are committed together, or
neither is. The report says which in as many words — a `Database` line reading `unchanged - nothing
was written` or `changed, as reported below` — so you never have to infer it from the outcome name.

### If an apply fails

Read the `Database` line, and then act on it:

- **`unchanged - nothing was written`** — the apply rolled back; the database is exactly as it was,
  including when the failure happened after the credential had been written inside the transaction.
  Fix the cause the message names, run the dry run to confirm what is stored, and apply again. The
  retry is a first apply, not the repair of a half-finished one.
  - `StatusNotApplied` means something else saved a credential for this business while the command
    was running, so the status write it had prepared no longer matched what was stored. Find out
    what else wrote, confirm which credential is correct, then apply again.
  - `RolledBack` means reading or writing the connection failed outright (the message carries the
    database error).
- **`UNKNOWN - the commit failed`** — the only state the command cannot report, and the only one
  that needs you to look: the database holds either the whole change (credential stored, connection
  `Ready`) or none of it, never a part of it. Run the dry run: `Change: None` means it committed and
  there is nothing left to do; `Change: CredentialsStored` or `Change: StatusMarkedReady` means it
  did not, and you should apply again.

There is no state in which the credential is stored but its status was never written, so "stored
but not Ready" never needs repairing by hand — and the credential table must not be edited by hand
in any case.

### The cutover window

**Run this command before deploying issue #520, not after.** Before that deploy the per-business
record is read by nothing, so running the command early is safe and changes no behaviour. The
deploy is the cutover: from it on, the client reads the business's record instead of the global
configuration, and **while that record is empty every Nayax operation fails closed** — sales sync,
the machine and product catalogue, the dashboard's remote data and the scheduled syncs alike — with
the stable "Nayax is not connected" error (HTTP `409`, code `nayax_not_connected`) rather than an
upstream error. Running the command first makes that window zero. If #520 has already been deployed
and Nayax is failing this way, this command is the fix: run the dry run, confirm it reports
`CredentialsStored`, then apply. There is no fallback to the global settings during the window, by
design, and none to another business's credential ever.

A `NayaxTokenProtection` key must be provisioned before this command (it refuses with the setting
named otherwise), and it must stay provisioned afterwards: since the cutover, every Nayax call
decrypts the stored token, so removing the key takes the integration down.

### Verifying Nayax afterwards

The command's own output is not evidence that Nayax works — it never contacts Nayax. After #520 is
deployed and this command has been applied, verify through the application:

- sign in and open the dashboard; confirm remote machine and sales data loads rather than reporting
  an upstream error;
- run a sales sync for a recent date range and confirm it imports as it did before;
- confirm no `NayaxUpstreamException` entries appear for the period after the cutover (README.md §
  KQL troubleshooting queries), and no `Nayax connection unavailable` warnings either — that is the
  entry the "Nayax is not connected" and "permission not granted" errors log, with the
  `NayaxErrorCode` property naming which one;
- read the connection record's status: it stays `Ready` unless a Nayax `401` moves it to
  `NeedsAttention` (an invalid or expired token), which is also the state to look for if Nayax
  stopped working some time after a successful cutover.

If Nayax fails after the cutover, the credential is the first thing to check — re-run
`migrate-nayax-connection --dry-run` and read what it reports is stored. Do not edit the
credential table by hand; the token is encrypted and its key id is stored with it.

### When to remove the global settings

Only after #520 is deployed and the verification above has passed, and as a deliberate human step:
remove `NayaxLynx__AccessToken` / `Nayax__Token` and `NayaxLynx__OperatorId` from the environment's
configuration. Since the cutover this command is their only reader, so removing them costs the
ability to re-run it and nothing else. **Keep `NayaxLynx__BaseUrl`**: it is still global, and
startup validates it. Until the removal, leave them exactly as they are — this command deliberately
does not remove them, so a rollback of #520 still has a working client. Keep
`NayaxTokenProtection__*` forever: removing the key that encrypted a stored token is what makes that
token undecryptable.

## Not part of this rollout

- **Onboarding a second business.** The Nayax *credential* is now per business: issue #519's
  `migrate-nayax-connection` moves the existing business's credential into its own record, and since
  issue #520 the client reads each business's own with no global fallback. What is still missing is
  the self-service side — a credential reaches the database only through that human-run command, so
  there is no supported way for a second business to connect its own Nayax account (the Owner
  connection wizard and re-test endpoints of issue #506). Do not add a second business until it
  exists.
- **Database foreign keys from `BusinessId` to `Businesses`.** Deliberately deferred, and still
  **required**. This is an outstanding integrity step, not a decision that the constraint is
  unnecessary.

  They cannot be added yet. The checkpoint-2 column defaults to 0, which matches no `Business`,
  so a migration adding these foreign keys would be violated by every not-yet-backfilled row.
  Until the bootstrap has run, the constraint and the data are incompatible.

  **Exact precondition for the follow-up migration**, which must hold in the target database
  *before* it is applied:

  1. **Zero unassigned rows** — no tenant-owned row is left with `BusinessId = 0`.
  2. **Verified Business ownership** — every `BusinessId` in use names a row that exists in
     `Businesses`, and the assignment has been confirmed to be the intended business, not merely
     a non-zero value.

  Both are observable without writing anything. `TenantOwnershipReadiness` reports the unassigned
  row count, the business count and the usable membership count; it is printed at the end of
  `migrate-database` and again in the startup log. A `bootstrap-business` run that reports
  ownership assigned with matching before/after counts and unchanged financial totals is the
  evidence for the second condition, and its `BusinessBackfillAudit` rows are the durable record.

  The migration was **not** written in advance and left pending, deliberately. SQLite cannot add
  a foreign key in place, so EF rebuilds each table — create, copy, drop, rename — across every
  tenant-owned table at once. Carrying an unapplied, unexercised rebuild of the whole financial
  schema in the repository is a larger standing risk than the missing constraint it would add,
  and it could not be tested honestly while the fixtures it would run against have no owners.
  Write it as its own reviewed change, against a database that already satisfies the precondition
  above, with an upgrade test from the prior schema.

  Until then the boundary rests on the query filters and `BusinessOwnershipEnforcer`. That is
  sound rather than merely acceptable: `BusinessId` is never supplied by a caller — it is stamped
  from resolved membership — so a dangling value cannot be injected through the API, and deleting
  a `Business` that still has members is already blocked by `BusinessMembership`'s restricted
  foreign key. The follow-up adds defence in depth, not the only defence.
- **Document storage.** Issue #39 builds on the ownership key established here.
