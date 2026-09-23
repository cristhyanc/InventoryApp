# Tenant ownership rollout (issue #64)

Written for: the human operator performing the rollout, and the reviewer approving it.

This is the procedure for bringing the business/tenant ownership boundary into a live
environment. Every step is performed by a person. Nothing in this procedure is automated, and
nothing in the application performs it on its own.

## What the application does and does not do by itself

| Action | Who |
| --- | --- |
| Apply schema migrations | API startup (`Database.Migrate()`), or a human running `dotnet ef database update` |
| Create the `Business` record | **Human**, via `bootstrap-business` |
| Create `BusinessMembership` rows from the supplied Entra mapping | **Human**, via `bootstrap-business` |
| Assign existing rows to that business | **Human**, via `bootstrap-business --apply` |
| Back up the database | **Human** |
| Deploy | **Human** |

No migration in this repository moves data or assigns ownership. Migrations create tables,
columns and indexes only. The backfill exists exclusively in the `bootstrap-business` command,
which the API never invokes — starting the web host and running the command are mutually
exclusive paths through `Program.cs`.

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

**2. Apply the schema.** Deploy the API, or run `dotnet ef database update` against the target
database. This creates the tenancy tables, the `BusinessId` columns and the audit table. It
assigns no ownership.

**3. Dry run the bootstrap.**

```bash
dotnet run --project backend/InventoryApi -- bootstrap-business --dry-run
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
dotnet run --project backend/InventoryApi -- bootstrap-business --apply
```

`--apply` must be typed explicitly; an invocation with neither flag is a dry run.

**5. Confirm the result.** The command re-prints the same table, now committed. It fails and
rolls back automatically if any row count or financial total moved. Then check by hand:

- sign in as a configured operator and confirm the dashboards and reports show the expected
  figures;
- compare a few known totals against the pre-rollout backup;
- confirm the API's startup log no longer reports unassigned rows;
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
- **Ownership was assigned to the wrong business.** Restore from the backup. Do not attempt to
  reassign rows by hand; ownership is immutable through the application and editing it directly
  bypasses every check in the boundary.

## Not part of this rollout

- **Onboarding a second business.** The Nayax integration still uses one operator account and
  token, so remote identifiers and imports are not yet partitioned. Do not add a second business
  until they are.
- **Database foreign keys from `BusinessId` to `Businesses`.** Deliberately deferred: they can
  only be added once no unassigned rows remain. They belong in a follow-up migration after this
  rollout is verified in production.
- **Document storage.** Issue #39 builds on the ownership key established here.
