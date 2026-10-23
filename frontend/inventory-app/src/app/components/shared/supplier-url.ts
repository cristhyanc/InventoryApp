/**
 * The supplier website rule is not here.
 *
 * Issue #848 mirrored the server rule in this folder while issue #847 was still open. #847 has
 * since merged (PR #858) with the same rule in `components/suppliers/supplier-url.ts`, beside the
 * `Inventory.Application.Suppliers.SupplierUrlPolicy` it mirrors, and every form and every rendered
 * link - the Suppliers list and the Super Admin presets dialog alike - uses that one helper. A
 * second copy would be a second set of sentences for an operator to disagree with.
 *
 * This module is empty only because the repair that reconciled the two could not delete a file; it
 * is safe and intended to be deleted, together with its spec:
 *     git rm frontend/inventory-app/src/app/components/shared/supplier-url.ts
 *     git rm frontend/inventory-app/src/app/components/shared/supplier-url.spec.ts
 */

export {};
