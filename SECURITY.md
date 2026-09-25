# Security policy

InventoryApp manages a real vending-machine business: inventory, sales, Nayax integration data, and financial/GST records. Treat any suspected exposure of credentials, tokens, authentication bypass, cross-tenant data access, or business/customer data as security-sensitive.

## Reporting a vulnerability

**Do not open a public GitHub issue, pull request, or discussion for a suspected vulnerability.** Public issues are visible to everyone once this repository is public, and an issue is exactly the wrong place to describe an exploitable weakness or paste evidence of one.

Report privately instead, using one of these channels:

- **GitHub private vulnerability reporting**: open the repository's **Security** tab and use **Report a vulnerability** ("Security advisories" / private disclosure). This creates a private advisory visible only to the maintainer and reporters, with no public trace until the maintainer chooses to publish it.
- If private reporting is not available on the repository's current plan or visibility state, contact the repository owner (`cristhyanc`) directly through their GitHub profile rather than filing a public issue.

Include, if known: the affected endpoint/component/version, reproduction steps, the potential impact (for example, cross-business data exposure, authentication bypass, or a financial calculation that can be manipulated), and any suggested fix. **Never include an actual secret, token, password, or real business/customer data in the report itself** — describe what you found and where, and let the maintainer verify and rotate it through a private channel.

## Response expectations

This is a small, single-maintainer project without a formal SLA. The maintainer aims to acknowledge a private report and begin triage promptly, and will coordinate on a fix and, if applicable, credential rotation before any public disclosure. There is currently no bug bounty program.

## Scope

In scope: the ASP.NET Core API (`backend/InventoryApi`), the Angular frontend (`frontend/inventory-app`), the GitHub Actions automation in `.github/workflows/`, and repository configuration that affects the security of the above (for example `.gitignore` gaps that could lead to a secret being committed).

Out of scope: third-party services this project depends on (Nayax, Microsoft Entra ID, Azure) — report issues in those services to their respective owners.

## Supported versions

InventoryApp does not publish versioned releases; `main` is the only deployed branch (see [docs/automation.md](docs/automation.md)). Security fixes are applied against `develop` and released to `main` through the normal human-controlled release pull request.

## Related documentation

- [`docs/public-release-checklist.md`](docs/public-release-checklist.md) — the audit and human-controlled sequence for making this repository public, including credential rotation and branch protection.
- [`docs/automation.md`](docs/automation.md) — the automated development lifecycle, authority model, and the safeguards that keep repository/deployment secrets out of automated agents' reach.
- [`AGENTS.md`](AGENTS.md) — engineering invariants, including "Security and deployment safeguards".
