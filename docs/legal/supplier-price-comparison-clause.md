# Supplier price comparison clause (issue #1183, published by issue #1214)

**Current status:** this clause is published. It is the second paragraph of section 7,
"Integrations, prices and AI outputs", of the public Terms of Use at `/terms-of-use`, effective
13 November 2026. The published source is
`frontend/inventory-app/src/app/public/legal/terms-of-use-content.ts`, which is the authority for the
live wording. This file keeps the clause exactly as the owner approved it for issue #1183, and the
related in-app wording. It is not legal advice and has not had legal review. How the page is routed
and published is in [docs/design/restockroo-legal-pages.md](../design/restockroo-legal-pages.md) and
[docs/architecture.md § The Privacy Policy and Terms of Use pages](../architecture.md#the-privacy-policy-and-terms-of-use-pages-issues-1190-and-1214).

## Historical context: why it was drafted here first

When issue #1183 was done, the Terms of Use existed only as issue #1190's layout. That layout was
reachable only in a development build, and each section held only the Stitch export's
`"[Approved content to be inserted]"` placeholder. Issue #1183 explicitly excluded publishing the
full Terms, so, per its own fallback instruction, the clause was recorded in this file for a later
task to place. Issue #1214 was that task. At the owner's request, it published the owner-approved
Terms of Use with this clause as the latest supplier-comparison wording. The clause replaces the
owner draft's two shorter sentences on supplier prices in section 7.

## The clause

> Supplier comparisons are based on recorded offers from supported suppliers and do not cover every
> supplier or guarantee the lowest final purchase cost. Prices, availability and product matches may
> be incomplete, delayed or incorrect, including where automated collection or matching is used.
> Before purchasing, verify the exact product, pack size, GST treatment, location or membership
> conditions, availability, delivery charges and final total directly with the supplier. Nothing in
> these terms excludes, restricts or modifies rights or remedies that cannot lawfully be excluded.

## Related in-app wording

The supplier comparison dialog carries the related owner-approved in-app notice and badge wording
from the same issue — see `cheapest-supplier-format.ts`'s `SUPPLIER_COMPARISON_NOTICE` and
`LOWEST_UNIT_PRICE_EXPLAINER`, and the README's
[The cheapest supplier, on Needs Ordering](../../README.md#the-cheapest-supplier-on-needs-ordering)
section.
