// SUPERSEDED - THIS FILE MUST BE DELETED (issue #187 architecture repair).
//
// The latest-sales synchronization contract is now the Application-owned
// Inventory.Application.SalesSync.ILatestNayaxSalesStore port (persistence/costing) behind the
// Inventory.Application.SalesSync.SyncLatestNayaxSales use case; this legacy service interface has no
// remaining implementation, registration or caller.
//
// It is left behind as an empty file only because the repair agent's sandbox refused every available
// way to delete a nested file (rm, git rm, git mv, git restore, git apply). Delete it together with
// NayaxLatestSalesSyncService.cs - see the deletion command in that file.
