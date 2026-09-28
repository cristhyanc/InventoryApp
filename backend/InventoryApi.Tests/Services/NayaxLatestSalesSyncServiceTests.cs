// SUPERSEDED - THIS FILE MUST BE DELETED (issue #187 architecture repair).
//
// These tests moved, with the responsibility they cover, to
// InventoryApi.Tests/Application/SalesSync/SyncLatestNayaxSalesTests.cs, which exercises
// Inventory.Application.SalesSync.SyncLatestNayaxSales over the real
// InventoryApi.Adapters.Persistence.EfLatestNayaxSalesStore adapter.
//
// This file is left behind empty only because the repair agent's sandbox refused every available way
// to delete a nested file (rm, git rm, git mv, git restore, git apply). Delete it together with the
// two legacy service files - see the deletion command in
// backend/InventoryApi/Services/NayaxLatestSalesSyncService.cs.
