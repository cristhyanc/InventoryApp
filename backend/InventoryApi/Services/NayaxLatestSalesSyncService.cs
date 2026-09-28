// SUPERSEDED - THIS FILE MUST BE DELETED (issue #187 architecture repair).
//
// The latest-Nayax-sales synchronization no longer lives in the legacy InventoryApi/Services folder:
// the use case is Inventory.Application.SalesSync.SyncLatestNayaxSales and its EF persistence adapter
// is InventoryApi.Adapters.Persistence.EfLatestNayaxSalesStore. Nothing references this file, and its
// implementation was moved unchanged into that adapter.
//
// It is left behind as an empty file only because the repair agent's sandbox refused every available
// way to delete a nested file (rm, git rm, git mv, git restore, git apply). Delete it, together with
// Services/Interfaces/INayaxLatestSalesSyncService.cs and
// ../../InventoryApi.Tests/Services/NayaxLatestSalesSyncServiceTests.cs, to satisfy
// ProjectDependencyDirectionTests.Only_the_documented_legacy_services_remain_in_InventoryApi_Services:
//
//   git rm backend/InventoryApi/Services/NayaxLatestSalesSyncService.cs \
//          backend/InventoryApi/Services/Interfaces/INayaxLatestSalesSyncService.cs \
//          backend/InventoryApi.Tests/Services/NayaxLatestSalesSyncServiceTests.cs
