# DRAFT paragraph: receipt scanning (issue #1202)

**This is a draft, not published legal content.** It holds the proposed Privacy Policy paragraph
for issue #1202, for the owner to review and place into the Privacy Policy under the legal-pages
work of issue #1190. It is not legal advice, carries no effective date, and has not had legal
review. It is not linked from any page and does not change
`frontend/inventory-app/src/app/public/legal/privacy-policy-content.ts`.

## Why this file exists instead of the Privacy Policy page

The Privacy Policy layout (`privacy-policy-content.ts`, issue #1190) is reachable only in a
development build, and every one of its sections intentionally holds nothing but the Stitch
export's own `"[Approved content to be inserted]"` placeholder - see
[docs/design/restockroo-legal-pages.md](../design/restockroo-legal-pages.md). There is no approved
Privacy Policy source in this repository yet, so, per issue #1202's own fallback instruction, the
paragraph is prepared here for the owner to incorporate. Its most likely home is the layout's
section 5, "Automated Indexing & Receipts", alongside section 4, "Sub-processors & Infrastructure",
for naming Anthropic as a processor.

## The paragraph

> When you scan a receipt, Restockroo sends the receipt image or PDF, along with the names and IDs
> of your active products and suppliers, to Anthropic so that it can read the receipt and suggest
> matches. The scan itself does not save the receipt file or a purchase draft in Restockroo; it
> records only that a scan was made, so that your plan's monthly scan allowance can be counted. The
> suggested purchase is shown to you with the scanned receipt attached. Nothing is saved unless you
> choose to save the purchase. If you do, Restockroo stores the purchase as you save it and, unless
> you remove the receipt before saving, the receipt file with it.

The paragraph limits its no-storage statement to Restockroo's own scan step. It makes no promise
about how long Anthropic retains what it receives, and no claim about where Anthropic processes it,
because neither has been verified for this application.

## Differences from the wording proposed in issue #1202

The issue proposed: "When you scan a receipt, Restockroo sends the receipt image or PDF, along with
your active product and supplier names and IDs, to Anthropic to read the receipt and suggest
matches. The scan itself does not save the receipt file or purchase draft in Restockroo. If you
choose to save the purchase or attach the receipt, Restockroo stores what you save."

The first two sentences are kept in substance. Two points were adjusted to match the code:

1. **A scan count is stored.** The scan step writes one thing: a per-business, per-month counter of
   scans used, for the plan's monthly allowance (issue #1164). It holds no receipt, file name,
   product or draft. The paragraph says so, so that "does not save" is not read as "writes
   nothing".
2. **The scanned receipt is attached by default.** Attaching the receipt is not a separate choice
   the person makes after scanning: the Add Purchase page attaches the scanned file to the
   purchase form automatically, and it is uploaded with the purchase on Save unless the person
   removes it first (issue #1166). The paragraph therefore ties storage to saving the purchase, and
   mentions that the receipt can be removed before saving.

## What the code does (verified against `develop` for issue #1202)

| Statement | Evidence |
| --- | --- |
| The receipt file is sent to Anthropic. | `ClaudeReceiptReader.Body` builds one Messages API request whose user message holds the file as a base64 `image` block (photo) or `document` block (PDF). |
| The business's active product and supplier names and IDs are sent with it. | The same request's text block is `ReceiptReadPrompt.Catalogue`, which lists `id: name` for each product and supplier. `EfReceiptScanCatalogueStore` selects only the id and name of products where `IsActive` and suppliers not marked hidden, for the requesting business only. |
| The scan does not save the receipt or a draft. | `ScanReceipt.Handle` reads the upload into memory, sends it once and drops it; its only writing port is `IReceiptScanUsageStore.TryCountScanAsync`, which increments the month's scan count. |
| The scan's log line holds no receipt content. | `LoggingReceiptScanLog.Completed` writes the business id, outcome, duration, page count and token counts only. |
| Saving the purchase stores the receipt unless removed. | `PurchaseUploadComponent.onScanned` sets the scanned file as the form's pending receipt; the receipt field's Remove button clears it before Save. |
| A photo is resized on the device before it is sent. | `PurchaseReceiptScanComponent` passes a chosen photo through `prepareImageForUpload`; the same prepared bytes are the ones attached to the purchase. |
