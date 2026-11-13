# Receipt-scanning paragraph (issue #1202, published by issue #1214)

**Current status:** this paragraph is published. It opens section 5, "AI and supplier-price
collection", of the public Privacy Policy at `/privacy-policy`, effective 13 November 2026. The
published source is `frontend/inventory-app/src/app/public/legal/privacy-policy-content.ts`, which
is the authority for the live wording. This file keeps the paragraph as approved for issue #1202,
how it was derived from the issue's proposal, and the code evidence behind it. It is not legal
advice and has not had legal review. How the page is routed and published is in
[docs/design/restockroo-legal-pages.md](../design/restockroo-legal-pages.md) and
[docs/architecture.md § The Privacy Policy and Terms of Use pages](../architecture.md#the-privacy-policy-and-terms-of-use-pages-issues-1190-and-1214).

## Historical context: why it was drafted here first

When issue #1202 was done, the Privacy Policy existed only as issue #1190's layout. That layout was
reachable only in a development build, and each section held only the Stitch export's
`"[Approved content to be inserted]"` placeholder. There was no approved Privacy Policy text in the
repository, so, per issue #1202's own fallback instruction, the paragraph was prepared in this file
for the owner to place later. Issue #1214 then published the owner-approved Privacy Policy. At the
owner's request, it used this paragraph as the latest receipt-scanning disclosure and put it at the
start of section 5, the policy's AI section.

## The paragraph

> When you scan a receipt, Restockroo sends the receipt image or PDF, along with the names and IDs
> of your active products and suppliers, to Anthropic so that it can read the receipt and suggest
> matches. The scan does not save the receipt file or a purchase draft in Restockroo. Restockroo
> counts each scan against your plan's monthly scan allowance. When a scan succeeds, the receipt is
> attached to the suggested purchase, and it is stored with the purchase when you save it unless
> you remove it first.

The paragraph limits its no-storage statement to Restockroo's own scan step. It makes no promise
about how long Anthropic retains what it receives, and no claim about where Anthropic processes it,
because neither has been verified for this application.

## Differences from the wording proposed in issue #1202

The issue proposed: "When you scan a receipt, Restockroo sends the receipt image or PDF, along with
your active product and supplier names and IDs, to Anthropic to read the receipt and suggest
matches. The scan itself does not save the receipt file or purchase draft in Restockroo. If you
choose to save the purchase or attach the receipt, Restockroo stores what you save."

The first two sentences are kept in substance. Two points were adjusted to match the code:

1. **A scan count is stored.** The scan step stores one thing in the database: a per-business, per-month counter of
   scans used, for the plan's monthly allowance (issue #1164). It holds no receipt, file name,
   product or draft. The paragraph says that scans are counted, and limits "does not save" to the
   receipt file and the purchase draft.
2. **The scanned receipt is attached by default.** Attaching the receipt is not a separate choice
   the person makes after scanning: the Add Purchase page attaches the scanned file to the
   purchase form automatically, and it is uploaded with the purchase on Save unless the person
   removes it first (issue #1166). The paragraph therefore says a successful scan pre-attaches the
   receipt and that it is stored when the purchase is saved unless removed first.

## What the code does (verified against `develop` for issue #1202)

| Statement | Evidence |
| --- | --- |
| The receipt file is sent to Anthropic. | `ClaudeReceiptReader.Body` builds one Messages API request whose user message holds the file as a base64 `image` block (photo) or `document` block (PDF). |
| The business's active product and supplier names and IDs are sent with it. | The same request's text block is `ReceiptReadPrompt.Catalogue`, which lists `id: name` for each product and supplier. `EfReceiptScanCatalogueStore` selects only the id and name of products where `IsActive` and suppliers not marked hidden, for the requesting business only. |
| The scan does not save the receipt or a draft. | `ScanReceipt.Handle` reads the upload into memory, sends it once and drops it; its only writing port is `IReceiptScanUsageStore.TryCountScanAsync`, which increments the month's scan count. |
| The scan's log line holds no receipt content. | `LoggingReceiptScanLog.Completed` writes the business id, outcome, duration, page count and token counts only. |
| Saving the purchase stores the receipt unless removed. | `PurchaseUploadComponent.onScanned` sets the scanned file as the form's pending receipt; the receipt field's Remove button clears it before Save. |
| A photo is resized on the device before it is sent. | `PurchaseReceiptScanComponent` passes a chosen photo through `prepareImageForUpload`; the same prepared bytes are the ones attached to the purchase. |
