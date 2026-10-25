// The supplier website rule is not in this layer.
//
// Issue #848 wrote one here while issue #847 was still open. #847 has since merged (PR #858) with
// the identical rule as Inventory.Application.Suppliers.SupplierUrlPolicy, which every supplier
// and every preset write path now uses - see docs/architecture.md
// "A supplier's optional website (issue #847)". Keeping a second policy of the same name would
// have been the duplication #848's fourth criterion forbids, and it also made
// Inventory.Infrastructure's AppDbContext ambiguous at compile time (CS0104) once both merged.
//
// This file holds no code only because the repair that reconciled the two could not delete a file;
// it is safe and intended to be deleted:
//     git rm backend/Inventory.Domain/Suppliers/SupplierUrlPolicy.cs
// Nothing references it. SupplierUrlRuleSingleSourceOfTruthTests asserts the one rule's one home.
