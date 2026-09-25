# Public release checklist

## Status: **BLOCKED**

This repository is **not** ready to be made public yet. The audit below found a real Nayax API credential and real uploaded business documents reachable in Git history (not the current tracked tree). Making the repository public today would publish those. See [Blocking findings](#blocking-findings) and [Required human remediation before publication](#required-human-remediation-before-publication).

This document is prepared, human-facing guidance. It performs no action by itself: nothing in this repository changes visibility, rotates a credential, rewrites history, or configures a repository setting. A human executes every step below, in order, using the access this repository's automation deliberately does not have.

## How to use this document

1. Read [Blocking findings](#blocking-findings) and resolve every item in [Required human remediation before publication](#required-human-remediation-before-publication).
2. Only after those are resolved, work through [Sequence for changing visibility](#sequence-for-changing-visibility) in order.
3. Re-run the audit commands in [Audit method and how to repeat it](#audit-method-and-how-to-repeat-it) after remediation, before flipping visibility, to confirm nothing new has landed on `develop` or `main` in the meantime.
4. After publication, complete [Post-publication verification](#post-publication-verification).

## Blocking findings

Redacted: no secret value, credential, or business document content appears below — only file paths, commit identifiers, categories, and confidence. All commit identifiers below are reachable from both `develop` and `main` as of this audit (verified with `git merge-base --is-ancestor <commit> develop` / `origin/main`).

| # | Category | Location | Commit(s) | Confidence | Currently in tracked tree? | Required human action |
| - | -------- | -------- | --------- | ---------- | --------------------------- | ---------------------- |
| 1 | External API credential | `backend/InventoryApi/appsettings.json`, `NayaxLynx.AccessToken` | `12670d8` (initial commit), `1d50d70` (replaced with a second literal value); removed from the tracked file in `4642c88` when the token moved to Key Vault | **High** — a real-looking, non-placeholder bearer token value, not a sentinel string | No (moved to Key Vault in `4642c88`; current file has no `AccessToken` key) | **Rotate the Nayax Lynx credential** with Nayax regardless of whether either historical value is still active, before this repository becomes public. Treat both historical values as compromised the moment history becomes public, even though scrubbing history is out of scope for this task. |
| 2 | Uploaded business documents (receipts/expense attachments) | `backend/InventoryApi/wwwroot/receipts/*.pdf`/`*.png`/`*.jpg`, `backend/InventoryApi/wwwroot/expenses/*.png` | 7 commits between the initial commit and a later cleanup (10 binary files total: 6 PDF, 3 PNG, 1 JPG); removed from the tracked tree when the application moved to `protected-files/` storage outside the web root | **High** — real uploaded receipt/expense attachments, not fixtures (filenames are GUIDs matching the application's upload-naming convention) | No (directory no longer exists in the tracked tree; storage moved to the git-ignored `protected-files/` path) | **Decide whether these are sensitive enough to require history scrubbing** (out of scope for this task — see [Explicitly out of scope](#explicitly-out-of-scope-for-this-audit)) or whether the business accepts them remaining in history. If in doubt, treat as sensitive: they are real financial source documents for the business this repository operates. |
| 3 | Azure resource identifiers | `backend/InventoryApi/Properties/PublishProfiles/vm-manager - Zip Deploy.pubxml` (**currently tracked** — see [Repository-owned safeguards applied in this change](#repository-owned-safeguards-applied-in-this-change), removed by this change) and, in history only, `backend/InventoryApi/Properties/serviceDependencies.vm-manager.json.user` (deleted from the tree in an earlier cleanup) | Reachable across the file's full history up to the commit removed by this change | **Medium** — a real Azure subscription ID and resource group/App Service/API Management resource names, not a credential by themselves (they cannot authenticate anything alone) | Yes, until this change (removed below) | **Owner decision**, not a hard blocker: an Azure subscription ID is an identifier, not a secret, but the owner may prefer not to publish it. If it should not be public even in history, note it alongside item 2 for the history-scrub decision. No action is required beyond the removal already made by this change unless the owner also wants history scrubbed. |
| 4 | Local developer path fragment | `backend/InventoryApi/InventoryApi.csproj.user` (history only; not in the current tracked tree) | One historical commit | **Low** | No | No action required. A local Windows file path containing a shortened form of the repository owner's name, consistent with the already-public GitHub username. Not a credential and not independently identifying beyond what the GitHub account already discloses. Recorded for completeness only. |

No other secret-shaped values (private keys, connection strings with embedded passwords, cloud access-key patterns, JWT-shaped strings, additional API tokens) were found in the current tracked tree or in a full-history pickaxe/content search. See [Audit method and how to repeat it](#audit-method-and-how-to-repeat-it) for the exact commands and their [Completeness limitations](#completeness-limitations).

## Required human remediation before publication

These items must be completed by a human before repository visibility changes. No automated agent may perform them (see [AGENTS.md § Security and deployment safeguards](AGENTS.md#security-and-deployment-safeguards) and [docs/automation.md](docs/automation.md)).

1. **Rotate the Nayax Lynx `AccessToken`** (finding 1) with Nayax, and update the value in the deployed environment's Key Vault / configuration. Confirm the old value no longer authenticates before proceeding.
2. **Decide on history scrubbing** for findings 1 and 2 (credential and uploaded documents). Two acceptable outcomes:
   - **Scrub history** (`git filter-repo` or equivalent) before publication, coordinated with anyone who has a local clone (their clones must be re-cloned afterward), then re-verify with the commands in [Audit method](#audit-method-and-how-to-repeat-it). This repository's automation and this task are deliberately not authorized to do this.
   - **Accept exposure of a rotated credential and old business documents in history**, documented as a conscious business decision, because the credential in finding 1 will already have been rotated and is worthless to an attacker, and the documents in finding 2 are judged acceptable to disclose (or the business decides otherwise and requires scrubbing).
3. **Review the Nayax operator identifier** in `backend/InventoryApi/appsettings.json` (`NayaxLynx.OperatorId`) and decide whether it should remain in the tracked file once public. It is a business identifier (which operator account this deployment integrates with), not a credential — see [Identifier classification](#identifier-classification). If the owner prefers it private, move it to Key Vault/configuration the same way the `AccessToken` was moved in commit `4642c88`, as a separate, explicit follow-up change (out of scope for this task).
4. **Confirm no other credential needs rotation** using the [Credential rotation matrix](#credential-rotation-matrix) below.

## Identifier classification

| Identifier | Where | Classification | Reasoning |
| ---------- | ----- | -------------- | --------- |
| `AzureAd.ClientId` (API app registration) | `backend/InventoryApi/appsettings.json` | **Public identifier** | An Entra application (client) ID is not a secret; it is presented to the browser/token endpoint by design and is meant to be public. No client secret is used (see `README.md` § Authentication configuration). |
| SPA client ID, API scope URI | `frontend/inventory-app/src/app/auth-config.ts` | **Public identifier** | Same reasoning: MSAL public-client configuration is inherently client-visible. |
| Deployed API base URL | `frontend/inventory-app/src/assets/config.json` | **Public URL** | Already reachable by anyone using the deployed frontend; publishing the repository adds no exposure. |
| Nayax `OperatorId` | `backend/InventoryApi/appsettings.json` | **Business identifier, owner's call** | Identifies which Nayax operator account this deployment reads from. Cannot authenticate anything by itself, but reveals an operational detail about the business. See [Required human remediation](#required-human-remediation-before-publication) item 3. |
| Azure subscription ID / resource group / resource names | `Properties/PublishProfiles/*.pubxml` (removed by this change), `serviceDependencies*.json.user` (history only) | **Operational identifier, owner's call** | Cannot authenticate anything by itself (Azure RBAC/OIDC trust gates actual access), but the owner may prefer not to publish it. See finding 3. |
| Nayax `AccessToken` | History only (`appsettings.json` at `12670d8`, `1d50d70`) | **Credential — must rotate** | A bearer token is a credential by definition; treat as compromised once history is public regardless of rotation timing. |

## Credential rotation matrix

| Credential | Used by | Rotation trigger | Rotation method | Owner |
| ---------- | ------- | ----------------- | ---------------- | ----- |
| Nayax Lynx `AccessToken` | Backend Nayax integration (`Integrations/Nayax/NayaxLynxClient.cs`) | **Required before publication** (finding 1) | Issue a new token through Nayax's operator portal/support; update the Key Vault secret / deployed configuration; verify with a non-destructive read call | Human (repository owner) |
| `CLAUDE_CODE_OAUTH_TOKEN` | `agent-implement.yml`, `agent-review.yml`, `agent-repair.yml` | Routine hygiene, or if leaked | `claude setup-token` to regenerate; update the repository secret | Human |
| Azure Static Web Apps deployment token (`AZURE_STATIC_WEB_APPS_API_TOKEN_RED_ISLAND_0C128C000`) | `azure-static-web-apps-*.yml` | Routine hygiene, or if leaked; consider replacing with OIDC federation if the Static Web Apps SKU supports it | Regenerate from the Azure Static Web App resource; update the repository secret | Human |
| Azure OIDC federated credential (`VmInventoryApi_CLIENT_ID_4D7F`, `_TENANT_ID_4D7F`, `_SUBSCRIPTION_ID_4D7F`) | `vm-manager.yml` `deploy` job (`azure/login`) | If the federated credential's subject claim (repository/branch) ever needs to change, or on suspected compromise | Update or recreate the federated credential's subject binding in Entra ID app registration; these are repository **variables**, not secrets — no rotation needed merely for going public, but **verify the federated credential's subject is scoped to this exact repository and `main`/`develop` branches** so a fork cannot assume it (see [OIDC trust verification](#oidc-trust-verification)) | Human |
| Key Vault access (used to source the rotated Nayax token and any other deployed secret) | Deployed API runtime, not GitHub Actions | If deployment identity or vault access policy changes | Managed identity / access policy change in Azure, outside this repository | Human |
| Entra client IDs (API and SPA app registrations) | `appsettings.json`, `auth-config.ts` | Not a credential; no rotation needed for publication (see [Identifier classification](#identifier-classification)) | — | — |

No other credential was found in this repository's workflows or tracked configuration.

## GitHub Actions public-repository threat review

Reviewed: `agent-implement.yml`, `agent-review.yml`, `agent-repair.yml`, `validate.yml`, `vm-manager.yml`, `azure-static-web-apps-red-island-0c128c000.yml`. The authoritative behavioural description of the first four is `docs/automation.md`; this section adds the public-repository/fork-PR threat model on top of it and does not restate what that document already proves.

### Fork pull requests cannot reach a secret or write authority

- **`validate.yml`** is the only workflow a fork PR can trigger directly (`pull_request` targeting `develop`/`main`). Its `validate` job (the one that checks out PR code and runs `scripts/validate.sh`) declares only `permissions: contents: read` and receives no `secrets.*` input at all — not even `GITHUB_TOKEN` is passed to a step in that job. Fork PR code therefore builds and tests with no token and no Anthropic/Azure credential available to it. The `context` job that does hold `secrets.GITHUB_TOKEN` never checks out PR code.
- GitHub's own fork-PR protection already withholds repository secrets from a `pull_request` (non-`pull_request_target`) workflow run originating from a fork, and requires a maintainer to approve the first run from a new external contributor. This repository does not use `pull_request_target` anywhere, which is the pattern that would otherwise combine secret access with untrusted checkout.
- **`agent-implement.yml`** triggers only on `issues: labeled` and only proceeds when the added label is `agent-ready` — a label only a human applies (`docs/automation.md` § Task states and labels). An external contributor cannot label an issue `agent-ready`; only a repository collaborator with write access can apply labels at all.
- **`agent-review.yml`**'s event trigger additionally requires `github.event.pull_request.head.repo.full_name == github.repository`, so a fork-headed PR can never satisfy it even if a human applied the `agent-review` label to one; its `workflow_dispatch` path requires the same same-repository, `github-actions[bot]`-authored, exact-SHA checks documented in `docs/automation.md`.
- **`agent-repair.yml`** triggers on `issue_comment: created` repository-wide, but its job `if:` requires `github.event.comment.user.login == github.repository_owner` — an external contributor cannot comment as the repository owner, so this workflow cannot be invoked by a fork PR author or any other outside contributor, regardless of what they write in a comment.
- All three `agent-*` dispatcher jobs (in `agent-implement.yml`, `agent-repair.yml`, and the `dispatch-review` job of `validate.yml`) independently re-verify — via the REST `.user.login` field, not the GraphQL actor spelling — that the target pull request is same-repository, non-draft, targets `develop`, and is authored by `github-actions[bot]` before dispatching trusted `workflow_dispatch` validation or review. A fork PR can never satisfy `github-actions[bot]` authorship.
- Every dispatcher additionally refuses to proceed when the pull request's changed files include anything under `.github/workflows/**`, so an agent PR that (legitimately or otherwise) touched a workflow file cannot receive automatic exact-SHA validation or review; it falls back to requiring manual handling.

### Deployment cannot be reached by a fork PR or by CI

- **`vm-manager.yml`** and **`azure-static-web-apps-red-island-0c128c000.yml`** trigger only on `push` to `main` (the former also builds-and-tests, without deploying, on `push` to `develop`). Neither has a `pull_request` trigger that deploys. A push to `main` only happens when a human merges an already-reviewed release pull request (`AGENTS.md` § Branches and releases); no workflow in this repository pushes to `main` on its own.
- Deployment credentials (the Azure OIDC federated identity variables and the Static Web Apps API token) are consumed only inside these two workflows' `deploy`/`build_and_deploy_job` jobs, which run only on that `push` trigger. No agent workflow (`agent-implement.yml`, `agent-review.yml`, `agent-repair.yml`) requests or has access to them.
- `vm-manager.yml`'s `deploy` job runs against the `VmInventoryApi_Env` GitHub environment; whether that environment has a required reviewer configured is a repository setting, not a workflow file — see [Environment protection](#environment-protection-not-yet-verifiable) below.

### Findings that are not blockers but should be addressed at or shortly after publication

1. **Third-party actions in the deployment workflows are not pinned to a commit SHA.** `vm-manager.yml` and `azure-static-web-apps-red-island-0c128c000.yml` reference `actions/checkout@v4` (and `@v3` in the latter), `actions/setup-dotnet@v4`, `actions/upload-artifact@v4`, `actions/download-artifact@v4`, `azure/login@v2`, and `Azure/static-web-apps-deploy@v1` by mutable tag. The three `agent-*` workflows and `validate.yml` already pin every third-party action to a full commit SHA with the release tag in a comment (`docs/automation.md` § Credentials used by the agent workflows). The deployment workflows should adopt the same practice, because they run with `id-token: write` and real Azure deployment credentials — a compromised or retagged upstream action would have real production impact. **This is a workflow-file change and is deliberately left to a human-reviewed follow-up** (see [Explicitly out of scope](#explicitly-out-of-scope-for-this-audit)): `validate.yml`'s own dispatcher guards refuse automatic validation/review for any pull request touching `.github/workflows/**`, which is this repository's own signal that workflow-file edits need direct human review rather than the automated agent path.
2. **`azure-static-web-apps-red-island-0c128c000.yml` declares no explicit `permissions:` block**, so its jobs run with whatever the repository/organization default `GITHUB_TOKEN` permissions are. Once the repository is public, confirm (or set) the organization/repository default workflow permissions to read-only (see [Repository/organization Actions settings](#repositoryorganization-actions-settings)), and consider adding an explicit least-privilege `permissions:` block to this workflow as a follow-up.
3. **`close_pull_request_job` in the same workflow can never run** (it is conditioned on a `pull_request` event the workflow does not declare a trigger for), confirming `docs/automation.md`'s existing note. Not a security issue, but dead configuration worth removing in a future cleanup — out of scope here.

None of the three items above are edited by this change; they are documented here for a human-reviewed follow-up because editing `.github/workflows/**` is outside what this audit task's automation is positioned to do safely (see next section).

### Explicitly out of scope for this audit

Per the issue's own exclusions, this audit does not: change repository visibility or settings; enable rulesets, branch protection, environments, secrets, variables, collaborators, or Actions policy; rotate or reveal credentials; rewrite, squash, filter, force-push, or delete Git history or remote references; delete or edit historical issues/PRs/comments/releases/workflow logs/attachments; deploy or run production migrations; publish private business data as demonstration data; or make broad/unrelated changes. Everything in this checklist that falls into those categories is written as guidance for a human to execute, not as an action this change performs.

## Sequence for changing visibility

Follow in order. Each step assumes the previous ones are complete. This sequence does not begin until [Required human remediation before publication](#required-human-remediation-before-publication) is fully resolved.

1. **Rotate the Nayax credential** (finding 1) and confirm the old value no longer works.
2. **Decide on history scrubbing** (item 2 of remediation) and execute it now if chosen — before visibility changes, while the repository is still private and no clone outside the owner's control exists.
3. **Review open issues, pull requests, comments, and workflow run logs** for anything that should not become public (draft business discussion, screenshots with real data, attachments). This repository's automation never posts a secret to these surfaces by design (`AGENTS.md` § Security and deployment safeguards), but a human should still skim history before publication, since a public repository publishes its entire issue/PR/comment/attachment history, not just the default branch.
4. **Configure branch protection and rulesets** for `develop` and `main` — see [Branch and ruleset configuration](#branch-and-ruleset-configuration). GitHub reports rulesets unavailable on some private-repo plans; this may only become configurable once the repository is public or the plan changes. If so, configure it **immediately** after changing visibility, before announcing the repository anywhere.
5. **Configure fork-workflow approval and default token permissions** — see [Repository/organization Actions settings](#repositoryorganization-actions-settings).
6. **Verify the Azure OIDC federated credential's trust conditions** — see [OIDC trust verification](#oidc-trust-verification).
7. **Change repository visibility to public** in repository Settings → General → Danger Zone.
8. **Immediately re-verify** branch protection, ruleset, and Actions settings took effect (some GitHub plans apply ruleset availability only after visibility changes, and settings can silently reset on a visibility change on some plans).
9. Complete [Post-publication verification](#post-publication-verification).

## Branch and ruleset configuration

> Repository settings, not files. This section restates and slightly extends `docs/automation.md`'s [Recommended repository protection checklist](automation.md#recommended-repository-protection-checklist) for the public-repository moment specifically; that document remains authoritative.

For both `develop` and `main`:

| Setting | Requirement |
| ------- | ----------- |
| Require a pull request before merging | Yes — no direct pushes, including from the repository owner, ideally (or at minimum, block them for everyone else) |
| Require approvals | At least 1 approving review; require on `main` at minimum, strongly recommended on `develop` too |
| Dismiss stale approvals on new commits | Yes |
| Require conversation resolution before merging | Yes |
| Require status checks to pass | `merge-validation` (every same-repository PR) and, for agent PRs, `agent-validation`; see `docs/automation.md`'s discussion of which to require and why they differ |
| Require branches to be up to date before merging | Recommended, to avoid merging against a stale base |
| Block force pushes | Yes, both branches |
| Block branch deletion | Yes, both branches |
| Restrict who can push | Only the automation identities and the owner should ever push directly, and only through the mechanisms already described in `docs/automation.md` (agent PR branches, not `develop`/`main` directly) |
| Do not allow bypass by automation credentials | `GITHUB_TOKEN` / the agent workflows must not bypass these rules — they should be blocked by them exactly like a human, which is what makes branch protection the final backstop `docs/automation.md` already relies on |

**This is the setting that caused a mechanical blocker for a previous attempt at this issue**: a ruleset that restricts ref (branch) *creation* can reject a feature branch pushed by a non-`github-actions[bot]` identity (for example, an interactively-run Claude Code session authenticated as a different bot/user identity) even though the same ruleset correctly allows `github-actions[bot]`-authored `agent/issue-*` branches from the normal `agent-implement.yml` workflow path. When configuring ref/branch-creation restrictions, confirm they allow exactly the identities this repository's automation actually uses (`github-actions[bot]` via the job-scoped `GITHUB_TOKEN`) and intentionally exclude everything else, rather than accidentally blocking the automation itself.

## Repository/organization Actions settings

- **Fork pull request workflows require approval**: Settings → Actions → General → "Fork pull request workflows" should require approval for first-time contributors at minimum (GitHub's default), and consider requiring approval for all outside collaborators on a business-data repository.
- **Default `GITHUB_TOKEN` permissions**: Settings → Actions → General → "Workflow permissions" should default to **read-only**. Every workflow in this repository that needs write access already declares its own `permissions:` block (`docs/automation.md` documents each one's exact scope), so a restrictive default only affects workflows that omit a `permissions:` block — like `azure-static-web-apps-red-island-0c128c000.yml` today (see [finding 2](#findings-that-are-not-blockers-but-should-be-addressed-at-or-shortly-after-publication)).
- **Do not allow GitHub Actions to create or approve pull requests** (Settings → Actions → General), consistent with this repository's existing design: agent PRs are created by `agent-implement.yml` using its job-scoped token, which is a normal PR-creation call, not GitHub's separate auto-PR-approval feature; that separate feature should stay disabled.
- **Secrets are already withheld from forks by GitHub's platform behaviour** for `pull_request` (not `pull_request_target`) workflows; this repository does not use `pull_request_target` anywhere (verified during this audit), so there is nothing to change here — only to keep true in future workflow edits.

## OIDC trust verification

`vm-manager.yml`'s `deploy` job authenticates to Azure with `azure/login@v2` using `id-token: write` and the federated-credential variables `VmInventoryApi_CLIENT_ID_4D7F`, `VmInventoryApi_TENANT_ID_4D7F`, `VmInventoryApi_SUBSCRIPTION_ID_4D7F`. Before or immediately after publication, a human should confirm in the Entra ID app registration's federated credential configuration that:

- The federated credential's **subject** is scoped to this exact repository (`repo:cristhyanc/InventoryApp:ref:refs/heads/main`, or the equivalent environment-scoped subject if `VmInventoryApi_Env` requires a specific subject pattern) — not a wildcard that any repository or branch could satisfy.
- No federated credential subject matches a pattern a fork or arbitrary branch could produce (for example, a subject scoped to `pull_request` events, which this workflow does not use, but which is worth explicitly ruling out).
- The OIDC audience is the default GitHub Actions audience (`api://AzureADTokenExchange`) unless intentionally configured otherwise.

This verification cannot be performed from within this repository's files; it requires access to the Entra ID app registration, which is Azure/human-controlled configuration outside this repository's authority.

## Environment protection (not yet verifiable)

`docs/automation.md` already notes that whether the `VmInventoryApi_Env` GitHub environment requires a reviewer is a repository setting this document does not verify. Before or at publication, confirm in Settings → Environments → `VmInventoryApi_Env` that a required reviewer (the repository owner) is configured, so a merge to `main` still pauses for explicit approval before the deploy job runs, independent of branch protection on `main` itself.

## Post-publication verification

Immediately after changing visibility to public:

1. Re-check every setting in [Branch and ruleset configuration](#branch-and-ruleset-configuration) and [Repository/organization Actions settings](#repositoryorganization-actions-settings) — some GitHub plans only expose rulesets once a repository is public, and settings have been observed to need re-confirmation after a visibility change on some plans.
2. Open a throwaway test issue and confirm `agent-ready` still behaves as documented (do not merge anything from it) if you want to prove automation still works publicly — optional, and only do this if you are comfortable with a public test artifact.
3. Confirm the deployed API and frontend are unaffected — visibility change does not redeploy anything, but confirm no configuration drifted.
4. Watch the Actions tab for a period after publication for any unexpected workflow run (a sign that a fork-PR or public-issue trigger behaved differently than this review predicted).
5. If history was scrubbed (remediation item 2), confirm every local clone (including the owner's own machines) was re-cloned from the rewritten history, and confirm no stale fork or mirror retains the old history.

## Audit method and how to repeat it

Commands actually run during this audit (read-only; no destructive operation), from the repository root, with the full history available (`git log --all`, not just `HEAD`):

```bash
# Confirm a candidate commit is reachable from the branches that matter
git merge-base --is-ancestor <commit> develop && echo reachable
git merge-base --is-ancestor <commit> origin/main && echo reachable

# List files ever added under a path across all branches
git log --oneline --all --diff-filter=A --name-only -- '<path-glob>'

# Show a file's content at a specific historical commit (read-only)
git show <commit>:<path>

# List currently tracked files matching sensitive extensions/paths
git ls-files -- '*.pdf' '*.png' '*.jpg' '*.jpeg' '*.xlsx' '*.xls' '*.csv' '*.zip' '*.db' '*.sqlite' '*.log' '*.pubxml' '*.user' '*.suo'

# Content search across the current tracked tree for secret-shaped patterns
# (private key headers, common cloud access-key/JWT shapes, common secret field names)
```

Content patterns searched across the current tracked tree: `-----BEGIN ... PRIVATE KEY`, `AKIA[0-9A-Z]{16}` (AWS-shaped access key), `eyJhbGciOi` (JWT-shaped prefix), `AccessToken`, `ApiKey`/`api_key`, `ClientSecret`/`client_secret`, `password`/`Password` assignments, and personal-email domain patterns (`@gmail.com`, `@outlook.com`, `@hotmail.com`). All appsettings/launchSettings/serviceDependencies/publish-profile files were also read directly rather than only pattern-matched.

### Completeness limitations

- No dedicated secret scanner (`gitleaks`, `trufflehog`, `detect-secrets`, GitHub secret scanning) was run as part of this audit; this environment's tool access is a fixed allow-list of `git`/`gh`/build commands with no package manager or arbitrary shell access, so none could be installed. **A human should run a recognized scanner over full history** (for example `gitleaks detect --source . --log-opts="--all"`, or enable GitHub Advanced Security secret scanning with history scan once the repository is public, or before via a private security advisory workflow) before treating this checklist's findings as exhaustive.
- The audit's content search was pattern-based and read specific known-risky file types/paths directly; it is narrower than a purpose-built scanner's entropy analysis and signature database, and could miss a secret that does not match any of the patterns above (for example, a credential embedded in an unusual format inside a binary file, which pattern search over text cannot see).
- Issue/PR/comment/attachment content on GitHub itself (as opposed to Git history) was not exhaustively reviewed line by line; see [Sequence for changing visibility](#sequence-for-changing-visibility) step 3.

## Repository-owned safeguards applied in this change

This pull request (see the linked issue and PR for the exact diff):

- Removes `backend/InventoryApi/Properties/PublishProfiles/vm-manager - Zip Deploy.pubxml` from the tracked tree (finding 3): it is Visual Studio–generated local publish tooling state, redundant with the actual OIDC-based deployment pipeline in `vm-manager.yml`, and its `ResourceId`/`ApiResourceId`/`PublishUrl` fields contain a real Azure subscription ID and resource names that have no reason to be committed.
- Adds `.gitignore` entries so this class of file (and other local/credential-shaped file types not previously excluded) cannot be recommitted — see the `.gitignore` diff in this pull request.
- Adds this document, `SECURITY.md`, and `CONTRIBUTING.md`.

It does not rotate any credential, rewrite history, delete a remote reference, or change a repository setting — all of those remain the human actions described above.
