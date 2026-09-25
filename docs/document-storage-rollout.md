# Document storage rollout (issue #39)

The canonical human-operated procedure for moving uploaded business documents — purchase
documents and operating-expense attachments — from the application's filesystem storage into a
private Azure Blob container.

**Nothing in this document has been performed against production.** It describes what a human
does, in order, and what each step must show before the next one is allowed to start. Every step
is reversible until the very last section, which is deliberately not part of this rollout.

The architecture behind it — the `IDocumentStorage` port, the two adapters, the tenant-scoped
blob keys, the migration command — is described in
[docs/architecture.md § Document storage](architecture.md#document-storage). This file is the
operational counterpart: what to run, what to read, and when to stop.

Two rules govern the whole procedure:

- **The runtime provider and the migration are separate decisions.** Copying documents into Blob
  storage does not change where the application reads them from, and must not be done in the
  same step.
- **Nothing deletes a source document.** Not the migration, not the runtime switch, not this
  document. Retiring the filesystem copies is later, separate, human-controlled work.

---

## 1. Prerequisites

All of these are human-performed, outside the application, before anything below is run. An
agent does not create Azure resources, assign roles, or change App Service configuration.

| # | Prerequisite | Why it must be true first |
|---|---|---|
| 1 | **A verified, restorable backup** of the SQLite database *and* of `protected-files/` and the legacy `wwwroot/{receipts,expenses}/` folders | The migration does not modify either, but a rollout without a restore point is not a rollout. "Verified" means a restore has actually been tested, not that a file exists. |
| 2 | **Private production container** `business-documents` | The documents are business records. A container with any public access is a data breach waiting for someone to guess a blob name. |
| 3 | **Private development container** `business-documents-dev` | Local work must never write to the production container. Separate containers, not separate prefixes. |
| 4 | **System-assigned managed identity** enabled on the App Service | So the running application authenticates as itself, with no credential stored anywhere. |
| 5 | **`Storage Blob Data Contributor`** granted to that identity, at the narrowest practical scope — preferably the container | Contributor at the container is enough to read, write and delete the application's own documents and nothing else. |
| 6 | **No public/anonymous Blob access** on the account or the containers | Confirm explicitly; see the check in [§ 3](#3-developer-smoke-test-against-business-documents-dev). |

There are no keys, SAS tokens, client secrets or storage connection strings to create. If a step
anywhere asks for one, that step is wrong — authentication is `DefaultAzureCredential`
throughout.

---

## 2. Required configuration

Three non-secret settings, read from App Service application settings in Azure or from
environment variables. They are not committed: `appsettings.json` ships
`DocumentStorage:Provider=FileSystem`, which is what the application has always used.

```text
DocumentStorage__Provider=FileSystem | AzureBlob
DocumentStorage__BlobServiceUri=https://<storage-account>.blob.core.windows.net
DocumentStorage__ContainerName=business-documents          # business-documents-dev for development
```

### The running API and the migration read the same settings

This is the part of the rollout that is easiest to get wrong, so it is stated plainly:

- The **running API** uses `DocumentStorage__Provider` to decide where it serves documents from.
- The **migration command** requires `DocumentStorage__Provider=AzureBlob`, and refuses to run
  against the filesystem default — without that check it would "migrate" every document from the
  filesystem back to the filesystem and report complete success.

They are the same setting name, so setting the persistent App Service value to `AzureBlob` in
order to run the migration would **switch the live application at the same time**, before a single
document had been copied or verified. Every read would go to an empty container and every
existing document would 404.

So the two must be separated. During the whole of this rollout:

| Where | `DocumentStorage__Provider` |
|---|---|
| **Persistent App Service application settings** (the running API) | `FileSystem` — unchanged until [§ 13](#13-do-not-switch-the-runtime-provider-yet) |
| **The `migrate-documents` process only** | `AzureBlob`, supplied as a process-local override for that one command |

`DocumentStorage__BlobServiceUri` and `DocumentStorage__ContainerName` are safe to add to the
persistent App Service settings ahead of time, and doing so is recommended: they are non-secret,
the running API ignores them entirely while its provider is `FileSystem`, and having them already
in place means the migration process only has to override the one setting that matters.

> **Do not use the persistent Provider setting as the migration's temporary switch.** Changing an
> App Service application setting restarts the application. A "set it to AzureBlob, run the
> migration, set it back" sequence therefore means two live restarts with the API serving from an
> unverified — and initially empty — container in between. The override below never touches the
> App Service configuration and never restarts anything.

### Supplying the override to the migration process

The override lives in the environment of the single command invocation. Nothing in the
application needs to change to support this: .NET configuration reads `DocumentStorage__Provider`
from the process environment, which takes precedence over `appsettings.json`.

Bash (App Service SSH, a container shell, or a Linux host):

```bash
DocumentStorage__Provider=AzureBlob dotnet InventoryApi.dll migrate-documents --dry-run
```

The assignment is a prefix on that one command, so it applies only to the migration process and
leaves the shell's own environment untouched.

PowerShell, which has no inline per-command assignment — scope it and put it back:

```powershell
$env:DocumentStorage__Provider = "AzureBlob"
try { dotnet InventoryApi.dll migrate-documents --dry-run } finally { Remove-Item Env:\DocumentStorage__Provider }
```

If `BlobServiceUri` and `ContainerName` are not already in the environment the process inherits,
supply them the same way:

```bash
DocumentStorage__Provider=AzureBlob \
DocumentStorage__BlobServiceUri=https://<storage-account>.blob.core.windows.net \
DocumentStorage__ContainerName=business-documents \
dotnet InventoryApi.dll migrate-documents --dry-run
```

From a source tree the same prefix applies, before `dotnet run`.

**The invariant for the whole of sections 4 to 12:** the running API sees `FileSystem`, the
migration process sees `AzureBlob`, and the persistent setting is changed to `AzureBlob` only
after the migration has been applied and verified ([§ 13](#13-do-not-switch-the-runtime-provider-yet)).

### How the configuration fails

The configuration is validated when the process starts, and the failures are deliberate:

- An unrecognised provider is refused by name.
- `AzureBlob` without `BlobServiceUri` or `ContainerName` is refused, naming the missing setting.
- A `BlobServiceUri` that is not absolute, is not `https`, or carries a query string or embedded
  credentials — what a SAS token or an account key looks like — is refused.
- There is **no fallback** from `AzureBlob` to the filesystem. An application that cannot reach
  its configured storage fails to start rather than quietly writing business documents to a local
  disk nobody is watching.

If the migration is invoked without the override, it refuses immediately and writes nothing,
naming the setting to fix.

---

## 3. Developer smoke test against `business-documents-dev`

Do this first, on a developer machine, against the development container. It proves the
credential chain, the role assignment and the endpoint before any production data is involved.

```bash
# Sign in as an identity that holds Storage Blob Data Contributor on business-documents-dev
az login
az account show --query user.name -o tsv

# Point the local API at the development container. User secrets, never appsettings.
cd backend/InventoryApi
dotnet user-secrets set "DocumentStorage:Provider"       "AzureBlob"
dotnet user-secrets set "DocumentStorage:BlobServiceUri" "https://<storage-account>.blob.core.windows.net"
dotnet user-secrets set "DocumentStorage:ContainerName"  "business-documents-dev"

dotnet run
```

Then, signed in to the application:

1. **Upload** a purchase document and an operating-expense attachment.
2. **Check placement** — both must be under the signed-in business's prefix:
   ```bash
   az storage blob list --account-name <storage-account> --container-name business-documents-dev \
     --auth-mode login --prefix "tenants/" --query "[].name" -o tsv
   ```
3. **Download** both through the application. Confirm the file opens, the content type and
   download file name are unchanged, and the attachment response still carries `Last-Modified`.
4. **Delete** both through the application and confirm the blobs are gone.
5. **Confirm there is no anonymous access**:
   ```bash
   curl -i "https://<storage-account>.blob.core.windows.net/business-documents-dev/tenants/1/purchases/<blob>"
   ```
   This must return `404` / `PublicAccessNotPermitted`, never the document.
6. **Revert** the local configuration: `dotnet user-secrets remove "DocumentStorage:Provider"`
   (and the other two keys), or `dotnet user-secrets clear`.

Do not point a developer machine at `business-documents`.

---

## 4. Production dry run

The dry run writes nothing — not to Blob storage, not to the filesystem, not to the database. It
reads each record, locates its document, computes a SHA-256, looks at the destination, and
reports. Run it with the Azure settings in place **and the runtime still serving documents from
the filesystem** (see [§ 13](#13-do-not-switch-the-runtime-provider-yet)).

From the deployed application (`dotnet publish` output, which has no SDK or sources), with the
provider override applied to this process only — the running API stays on `FileSystem`
([§ 2](#2-required-configuration)):

```bash
DocumentStorage__Provider=AzureBlob dotnet InventoryApi.dll migrate-documents --dry-run
```

From a source tree:

```bash
DocumentStorage__Provider=AzureBlob dotnet run --project backend/InventoryApi -- migrate-documents --dry-run
```

Exactly one of `--dry-run` or `--apply` must be given. A bare `migrate-documents` is refused
rather than assumed to be a dry run, and `--apply --dry-run` together is refused rather than
resolved in favour of the one that writes.

---

## 5. Reading the report

Every candidate record appears exactly once, so the counts always sum to `Candidates`.

| Status | Meaning | What to do |
|---|---|---|
| **Pending** | Dry run only. The source is readable, the destination is free; an apply would copy it. | Nothing. This is the expected state before an apply. |
| **Migrated** | Apply only. Copied, then read back and verified by size and SHA-256 at the destination. | Nothing. |
| **AlreadyPresent** | The destination already holds these exact bytes. | Nothing. This is what a completed migration looks like on a rerun. |
| **DuplicateReference** | Another record names the same document. The bytes are copied once; this record is listed so the shared reference is visible. | Not a migration failure, and it does not affect the exit code. Worth understanding: two records sharing one stored file name is unusual and may indicate a data problem. |
| **MissingSource** | The record names a document that is in neither protected nor legacy storage. | **Investigate.** Nothing was written — an empty blob would be worse than an absent one — and the database was not changed. Either the document was lost before this rollout, or the source folders are not the ones the process can see. |
| **Collision** | Something else is already stored at this document's destination, with different bytes. | **Stop and investigate.** Nothing was overwritten, renamed or deleted. Establish what is already there before re-running. Never resolve this by deleting a blob to "let the migration win". |
| **InvalidBusiness** | The record carries a business id that is not a valid owner (`0`, negative). | **Investigate.** It was not migrated and never will be under a default business — that would hand one business's document to another. Usually it means the tenant ownership backfill (`bootstrap-business`) has not been completed; see [docs/tenant-rollout.md](tenant-rollout.md). |
| **Failed** | This one document's copy could not be read back, or did not match its source. | **Investigate.** The source is untouched and the document is *not* counted as migrated. |

Two more counts are informational and matter for the later fallback decision:

- **Source in protected** — documents found under `protected-files/`.
- **Source in legacy** — documents found only under the web root. While this is above zero, the
  legacy fallback is still load-bearing for something.

**Exit code:** `0` when nothing is unresolved, `1` when any document is `MissingSource`,
`Collision`, `InvalidBusiness` or `Failed`. A dry run obeys the same rule, so an apply can be
gated on a clean dry run. Pending work is not a problem and does not affect the exit code.

**If the command aborts with an Azure error** — container not found, authorization refused,
account disabled, service unavailable — that is a destination failure, not a per-document one,
and it deliberately ends the whole run rather than being reported as a few `Failed` documents.
Fix the cause and run the command again; documents already copied come back as `AlreadyPresent`.

---

## 6. Review the dry run before applying

The dry run is the artefact a human reviews. Do not run `--apply` until:

- [ ] the dry run exited `0`;
- [ ] `Candidates` matches what you expect from the data;
- [ ] `MissingSource`, `Collision`, `InvalidBusiness` and `Failed` are all zero, or each one has
      been individually investigated and explained;
- [ ] `DuplicateReference` entries, if any, are understood;
- [ ] the backup from [§ 1](#1-prerequisites) is confirmed restorable.

An unresolved dry run means the apply is not yet authorised. Investigate first; the apply will
still be there afterwards.

---

## 7. Production apply

Again with the override on this process only; the persistent App Service `Provider` is still
`FileSystem` and the API is still serving documents from the filesystem.

```bash
DocumentStorage__Provider=AzureBlob dotnet InventoryApi.dll migrate-documents --apply
```

For each document: the source is read and fingerprinted, the destination is written only if its
key is free, the written blob is then read back, and only a matching size **and** SHA-256 counts
as `Migrated`. Nothing is overwritten. Nothing is deleted. The database is not written to at all —
the stored file name is the link between record and document, and moving bytes is not a reason to
change it.

The apply is safe to interrupt. If it stops part way, run it again.

---

## 8. Re-run and verify

Run the same command again. This is the verification step, not an optional extra:

```bash
DocumentStorage__Provider=AzureBlob dotnet InventoryApi.dll migrate-documents --apply
```

Expected on a completed migration:

```text
Candidates          : 36
Migrated            : 0
AlreadyPresent      : 35
DuplicateReferences : 0
MissingSource       : 1
Collision           : 0
InvalidBusiness     : 0
Failed              : 0
```

Every document that was `Migrated` on the first run must be `AlreadyPresent` on the second, and
`Migrated` must be `0`. Anything else means the migration is not idempotent in this environment
and must be investigated before going further — in particular, a second run that migrates
documents again would mean the first run's blobs are not where the second run looks.

A `MissingSource` that was present on the first run will still be present; it is not resolved by
re-running.

---

## 9. Verify the blobs directly

Confirm the documents are where the application will look for them, under the tenant prefix of
the business that owns each one:

```text
tenants/{businessId}/purchases/{storedFileName}
tenants/{businessId}/expenses/{storedFileName}
```

```bash
az storage blob list --account-name <storage-account> --container-name business-documents \
  --auth-mode login --prefix "tenants/" --query "[].{name:name, size:properties.contentLength}" -o table
```

Check that:

- every blob is under `tenants/{businessId}/purchases/` or `tenants/{businessId}/expenses/`;
- the business ids present are the real ones from the `Businesses` table, and no document sits
  under a business that should not own it;
- the blob count matches **`Migrated` + `AlreadyPresent`** from the report;
- no container-level or account-level public access has appeared.

Nothing is subtracted for duplicate references. Every candidate record has exactly one status, so
a record reported as `DuplicateReference` was never counted in `Migrated` or `AlreadyPresent` in
the first place — subtracting it again would under-count the blobs that are really there. What
`DuplicateReference` means is that the destination it names is already accounted for by another
record, so it adds no blob of its own:

```text
Candidates          : 2
Migrated            : 1
AlreadyPresent      : 0
DuplicateReferences : 1

Physical destination blobs represented: 1
```

`MissingSource`, `Collision`, `InvalidBusiness` and `Failed` likewise contribute no blob that this
migration wrote — though a `Collision` does mean a blob is sitting at that key, put there by
something other than this run, which is exactly why it has to be investigated rather than counted.

---

## 10. Source documents remain untouched

After a successful migration, `protected-files/` and the legacy `wwwroot/{receipts,expenses}/`
folders contain exactly what they contained before. The migration reads them and nothing else.

This is deliberate: the filesystem copies are the rollback. Until the Blob copies have been
verified *and* the application has run against them successfully for a period the business is
comfortable with, the filesystem is still the authoritative copy.

---

## 11. Do not delete filesystem documents

Not as a cleanup step, not to reclaim space, not to "finish" the migration. The migration command
has no deletion feature and no `--delete-source` flag, and this checkpoint deliberately does not
add one.

---

## 12. Do not remove the legacy `wwwroot` fallback

The filesystem adapter still reads `{WebRoot}/{category}/{storedFileName}` when a document is not
in protected storage. Removing that fallback while any document depends on it would orphan it
silently. The `Source in legacy` count in the migration report is the measurement that decides
when this becomes safe.

---

## 13. Do not switch the runtime provider yet

Copying the documents and serving them from Blob storage are two separate decisions, taken in
that order, with verification in between.

Up to this point every `migrate-documents` invocation has carried its own process-local
`DocumentStorage__Provider=AzureBlob`, and the **persistent App Service application setting has
stayed `FileSystem`** ([§ 2](#2-required-configuration)). This is the step, and the only step,
that changes that persistent setting.

Change the App Service application setting `DocumentStorage__Provider` from `FileSystem` to
`AzureBlob` only after:

- [ ] the apply completed and the verification re-run reported `Migrated: 0`;
- [ ] the blobs were inspected directly ([§ 9](#9-verify-the-blobs-directly));
- [ ] `Collision`, `InvalidBusiness` and `Failed` are all zero;
- [ ] any `MissingSource` is understood and accepted — those documents will 404 when read from
      Blob storage, exactly as they already do from the filesystem;
- [ ] `BlobServiceUri` and `ContainerName` are already present in the App Service settings and
      name the **production** container, not the development one.

Changing the setting restarts the application, so treat it as a deployment: do it in a window
where someone is watching, not alongside the migration itself.

After the switch, smoke-test an authenticated upload, download and delete of both a purchase
document and an operating-expense attachment, and confirm the download still carries its original
content type, file name and `Last-Modified`.

---

## 14. Rollback

The rollback is a configuration change, because the migration changed nothing that would need
undoing:

- the database was never written to — every record still names the same stored file name;
- the filesystem sources are untouched and complete;
- the blobs are additional copies, not moved originals.

So if anything is wrong after the runtime switch, set `DocumentStorage__Provider=FileSystem` and
restart. The application serves documents from `protected-files/` and the legacy web root exactly
as it did before, while the Blob rollout is investigated. Blobs already written can be left in
place; a later re-run of the migration will report them as `AlreadyPresent`.

There is no data-loss path to roll back from, provided [§ 11](#11-do-not-delete-filesystem-documents)
has been observed. That is the whole reason it is a rule.

---

## 15. Later, separate work

Explicitly **not** part of this rollout, and each requiring its own human decision, its own
change and its own verification:

- **Retiring the legacy `wwwroot` fallback** in `FileSystemDocumentStorage`, once the migration
  report has shown `Source in legacy: 0` and the runtime has been on Blob storage long enough to
  be trusted.
- **Deleting the filesystem source documents**, once a verified backup exists, the Blob copies
  have been confirmed, and the business has accepted that the filesystem rollback is being given
  up.
- **Removing `FileSystemDocumentStorage`** altogether, which cannot happen while either of the
  above is outstanding.

Do not bundle any of these with the migration.
