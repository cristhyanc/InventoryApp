# DRAFT clause: supplier price comparisons (issue #1183)

**This is a draft, not published legal content.** It holds the exact owner-approved clause text for
issue #1183, for the future legal-page task that authors and publishes the real Terms of Use. It is
not legal advice, carries no effective date, and has not had legal review. It is not linked from any
page and does not change `frontend/inventory-app/src/app/public/legal/terms-of-use-content.ts`.

## Why this file exists instead of the Terms of Use page

The Terms of Use layout (`terms-of-use-content.ts`, issue #1190) is reachable only in a development
build, and every one of its sections intentionally holds nothing but the Stitch export's own
`"[Approved content to be inserted]"` placeholder — see
[docs/design/restockroo-legal-pages.md](../design/restockroo-legal-pages.md). Issue #1190 and its
design note are explicit that authoring the approved legal copy is separate, later work, and issue
#1183 explicitly excludes publishing the full Terms. There is therefore no existing Terms source that
holds real clause text yet, so this clause is drafted here per issue #1183's own fallback
instruction, for that later task to place into whichever section it decides (most likely "Third-Party
Integrations & Supplier Data").

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
