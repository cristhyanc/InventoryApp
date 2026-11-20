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

Out of scope: third-party services this project depends on (Nayax, Microsoft Entra ID, Azure, and Anthropic for receipt scanning) — report issues in those services to their respective owners.

Business data this application sends to third parties is worth stating here, because it is business data rather than a credential. When a Pro business **scans a receipt** (issue #1163), the receipt image or PDF and the names and ids of that business's own products and suppliers are sent to Anthropic to be read. The scan request itself stores only the business's monthly scan count (issue #1164) — a business, a month and a number, with no column that could hold anything about what was scanned — and no receipt text, file name or product name reaches a log; the receipt itself, the reader's answer and the draft are not stored. If the member goes on to save the purchase, the Add Purchase page attaches that same file to the create request and it is stored as the purchase's document unless the member removes it first. The feature is off unless an API key is configured, and a business may scan at most 100 receipts per calendar month of its own time zone. Receipt scanning is not the only feature that sends anything to Anthropic: the scheduled price collector also sends shared-catalogue product names and search text to Anthropic as part of its nightly supplier-price search. The published Privacy Policy §5, "AI and supplier-price collection", is the authority for what each feature sends and stores. See [README.md § Privacy](README.md#privacy-the-personal-data-this-application-stores), [README.md § The scheduled price collector](README.md#the-scheduled-price-collector-issue-924) and [docs/architecture.md § Receipt scanning](docs/architecture.md#receipt-scanning-issue-975).

## Supported versions

InventoryApp does not publish versioned releases; `main` is the only deployed branch (see [docs/automation.md](docs/automation.md)). Security fixes are applied against `develop` and released to `main` through the normal human-controlled release pull request.

## Related documentation

- [`docs/automation.md`](docs/automation.md) — the automated development lifecycle, authority model, and the safeguards that keep repository/deployment secrets out of automated agents' reach.
- [`AGENTS.md`](AGENTS.md) — engineering invariants, including "Security and deployment safeguards".
