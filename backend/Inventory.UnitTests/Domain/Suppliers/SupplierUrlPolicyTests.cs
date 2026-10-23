// The supplier website rule's tests are not in this layer.
//
// They are Application/Suppliers/SupplierUrlPolicyTests.cs, issue #847's own tests for the one
// rule (PR #858), which cover every case this file used to cover: blank as "no website", the
// absolute http(s) acceptances, the refused schemes and relative values, the 2048-character
// boundary measured after trimming, and the length being reported before the shape.
//
// This file holds no code only because the repair that reconciled #848 with #847 could not delete
// a file; it is safe and intended to be deleted:
//     git rm backend/Inventory.UnitTests/Domain/Suppliers/SupplierUrlPolicyTests.cs
