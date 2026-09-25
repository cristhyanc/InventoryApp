# Contributing to InventoryApp

InventoryApp runs a real vending-machine business. Every contribution — human or automated — is expected to follow [AGENTS.md](AGENTS.md) (engineering and safety policy) and [docs/automation.md](docs/automation.md) (automated development lifecycle and authority model). This document is the practical, contributor-facing summary; those two files remain authoritative if anything here is unclear or appears to conflict.

## Before you start

- **Security or privacy issue?** Do not open a public issue or pull request. Follow [SECURITY.md](SECURITY.md) instead.
- **Never commit or paste credentials, access tokens, connection strings, private keys, local databases, uploaded business documents, or customer/business data** into code, an issue, a pull request, a comment, a commit message, or a log. See [AGENTS.md § Security and deployment safeguards](AGENTS.md#security-and-deployment-safeguards).
- Read [AGENTS.md](AGENTS.md) fully before changing anything financial, inventory-related, or touching the Nayax integration, authentication, or database schema — those areas have specific invariants that a generic pull request review will not catch.

## How to contribute

1. **Open an issue first** describing the problem or proposed change, unless one already exists. For a change intended to be picked up by the automated implementation agent, use the **Agent task** issue form (`.github/ISSUE_TEMPLATE/agent-task.yml`), which requires testable acceptance criteria, explicit exclusions, and a documentation-impact decision.
2. **Wait for maintainer triage.** A human confirms the acceptance criteria, risk level, and documentation-impact decision before any work — automated or manual — begins. Submitting an issue does not authorize implementation.
3. **Branch from the latest `develop`.** Never commit directly to `develop` or `main`; both are protected integration/release branches (see [Branches and releases](AGENTS.md#branches-and-releases)).
4. **Make the smallest coherent change** that satisfies the issue's acceptance criteria. Do not mix unrelated cleanup, refactors, or dependency upgrades into the same change.
5. **Add or update tests** appropriate to the change type — see [AGENTS.md § Tests required by change type](AGENTS.md#tests-required-by-change-type). Prioritize authorization/tenant isolation, financial and inventory rules, imports, and API contracts.
6. **Run the complete repository validation before opening a pull request:**

   ```bash
   bash scripts/validate.sh                                        # macOS, Linux, Git Bash, CI
   powershell -ExecutionPolicy Bypass -File scripts/validate.ps1    # Windows PowerShell
   ```

   This single script runs backend restore/format/build/tests with coverage, a NuGet vulnerability check, `npm ci`, Angular ESLint, and the Angular production build. Report the actual result — never claim it passed without running it.
7. **Open a pull request targeting `develop`** using `.github/pull_request_template.md`. Fill in the acceptance criteria checklist, validation evidence, impact table, and the `## Documentation impact` section (`Decision: UPDATED` or `Decision: NOT REQUIRED` with specific evidence — see [AGENTS.md § Documentation impact gate](AGENTS.md#documentation-impact-gate)). CI rejects a missing, placeholder, or generic declaration.
8. **A human reviews and merges.** Only a maintainer approves, merges, or prepares a `develop` → `main` release pull request; no contributor or automated agent may merge, deploy, run production migrations, or touch production data.

## What external contributors cannot do

This repository runs privileged automation (an implementation agent, an independent review agent, and Azure deployment workflows) that must not be reachable from untrusted contributions:

- **Opening a pull request from a fork never grants access to repository or deployment secrets.** The pull-request validation workflow (`validate.yml`) runs the same-repository build/test job with `contents: read` only and no Anthropic or Azure credential, regardless of who opened the PR; that credential-free job is what actually runs `scripts/validate.sh` against the merge result. A fork PR can therefore be built and tested safely, and a maintainer must approve the first workflow run on a new contributor's pull request (GitHub's standard fork-PR protection).
- **A fork PR cannot trigger the implementation, review, or repair agents.** Those workflows require an existing `agent/issue-*` branch that lives in this repository, an issue explicitly labelled `agent-ready` by a maintainer, or a repair comment from the repository owner — none of which an external contributor can produce.
- **A fork PR cannot deploy anything.** The Azure deployment workflows (`vm-manager.yml`, the Static Web Apps workflow) trigger only on a `push` to `main`, which requires an already-merged, maintainer-approved change; they never run against a pull request.
- **No contributor-supplied code runs with write access to this repository or with deployment credentials before a maintainer has reviewed it.**

See [docs/automation.md](docs/automation.md) for the complete authority model.

## Reporting bugs and requesting features

Use a normal GitHub issue for anything that is not a security report. Describe the current behaviour, the expected behaviour, and how to reproduce it (for a bug) or the business outcome you need (for a feature). Do not include real business data, customer data, or credentials in the issue — describe the shape of the problem instead.

## Code of conduct

Be respectful and constructive. Disagreements about design or approach are expected and welcome; personal attacks, harassment, or bad-faith reports are not, and may result in the contributor being blocked from the repository.
