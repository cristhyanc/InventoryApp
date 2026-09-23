# Tenant ownership rollout (issue #64)

Written for: the human operator performing the rollout, and the reviewer approving it.

This is the procedure for bringing the business/tenant ownership boundary into a live
environment. Every step is performed by a person. Nothing in this procedure is automated, and
nothing in the application performs it on its own.

## What the application does and does not do by itself

| Action | Who |
| --- | --- |
| Apply schema migrations | **Human**, via `migrate-database --apply` |
| Create the `Business` record | **Human**, via `bootstrap-business` |
| Create `BusinessMembership` rows from the supplied Entra mapping | **Human**, via `bootstrap-business` |
| Assign existing rows to that business | **Human**, via `bootstrap-business --apply` |
| Back up the database | **Human** |
| Deploy | **Human** |

**Deploying the API does not apply the schema.** In Production, startup applies no migrations at
all, whatever the configuration says: if any are pending it logs a critical error naming them and
refuses to start. That is deliberate — the tenancy migrations are high risk (the uniqueness one
rebuilds the whole `NayaxSales` table), and issue #64 requires them to be applied and verified
under human control. An API serving requests against a schema its code does not match is the
failure this prevents.

Disposable databases keep the convenience. Development and `Testing` migrate automatically, and
any other non-Production environment does so only when
`Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` is `true` — intended for an ephemeral
integration-test or staging database. Production is decided before that setting is read, so no
configuration value can make a Production deployment migrate itself.

Schema migrations never assign tenant ownership or perform the business backfill. That exists
exclusively in the `bootstrap-business` command, which the API never invokes — starting the web
host and running either command are mutually exclusive paths through `Program.cs`.

Schema migrations are not, however, data-free. Several rebuild a table and copy every row into a
new one; the `ScopeUniqueConstraintsByBusiness` migration does this to `NayaxSales` in order to
re-key it. That is another reason production migration stays human-controlled, and why step 2
below asks for a verified backup first.

## Running the commands

Both operator commands ship inside the application. How you invoke them depends on what you are
standing in front of.

**A source tree, with the .NET SDK installed** (local development, a build agent):

```bash
dotnet run --project backend/InventoryApi -- migrate-database --dry-run
dotnet run --project backend/InventoryApi -- migrate-database --apply
dotnet run --project backend/InventoryApi -- bootstrap-business --dry-run
dotnet run --project backend/InventoryApi -- bootstrap-business --apply
```

**The deployed application** (Azure App Service, or anywhere the published output runs). The
deployment is `dotnet publish` output: it contains no sources and no SDK, so `dotnet run
--project` does not work there. From the directory holding `InventoryApi.dll`:

```bash
dotnet InventoryApi.dll migrate-database --dry-run
dotnet InventoryApi.dll migrate-database --apply
dotnet InventoryApi.dll bootstrap-business --dry-run
dotnet InventoryApi.dll bootstrap-business --apply
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
  ```

- **Azure:** application settings on the App Service, using the same
  `BusinessBootstrap__Members__0__ObjectId` key form. Remove them once the bootstrap is
  complete; they are needed only while the command runs.

With the section empty or malformed the command refuses and changes nothing. It will not invent
an identity, and it will not create a business nobody can sign in to.

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

Do **not** deploy the API expecting it to migrate. Until this step completes, a deployed API in a
non-Development environment will refuse to start and log which migrations are pending.

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
- **The API refuses to start with "pending migration(s)".** Step 2 has not been completed against
  that database. Run `migrate-database --dry-run`, review, then `--apply`. Production ignores the
  `Database:AllowAutomaticMigrationUnsafeOutsideDevelopment` setting entirely - there is no
  configuration that makes a Production deployment migrate itself.
- **"the business ... is deactivated".** The business that would own the data is inactive, so its
  records would be unreachable whoever is a member. Reactivate it deliberately, then run again.
- **Ownership was assigned to the wrong business.** Restore from the backup. Do not attempt to
  reassign rows by hand; ownership is immutable through the application and editing it directly
  bypasses every check in the boundary.

## Not part of this rollout

- **Onboarding a second business.** The Nayax integration still uses one operator account and
  token, so remote identifiers and imports are not yet partitioned. Do not add a second business
  until they are.
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
