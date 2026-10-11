using Inventory.Application.Access;
using Inventory.Application.Businesses;
using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Categories;
using Inventory.Application.Commissions;
using Inventory.Application.Costing;
using Inventory.Application.Dashboard;
using Inventory.Application.Expenses;
using Inventory.Application.Gst;
using Inventory.Application.Imports;
using Inventory.Application.InventoryCounting;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.PickList;
using Inventory.Application.PlatformDiagnostics;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reorder;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.SalesSync;
using Inventory.Application.SaleTimestampRepair;
using Inventory.Application.Sites;
using Inventory.Application.Stock;
using Inventory.Application.Suppliers;
using Inventory.Application.SupplierOrders;
using Inventory.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Current-business resolution is per request: it must never be cached across callers.
        services.AddScoped<ICurrentBusinessProvider, CurrentBusinessProvider>();

        // The current business's own name and time zone (issue #499). Per request like everything
        // tenancy-scoped: the answer belongs to one caller's membership.
        services.AddScoped<GetCurrentBusiness>();

        // The signed-in member's role and capabilities (issue #521). Per request for the same
        // reason, and so a role change applies on the next request rather than being cached.
        services.AddScoped<GetCurrentAccess>();

        // The signed-in person's own account state (issue #523). Scoped like the rest of tenancy:
        // the answer belongs to one caller's identity, and a revoked or granted membership must
        // show up on their next request rather than be cached.
        services.AddScoped<GetAccountState>();

        // The shared write path every membership change runs through (issue #522). Scoped, because
        // the transaction it opens is the request's own AppDbContext transaction, which the
        // operation's write has to save inside.
        services.AddScoped<MembershipWriteGuard>();

        // The Nayax credentials every ordinary Nayax call is made with (issue #520), resolved per
        // call from the trusted current business over #518's store. Scoped for the same reason the
        // current business is: a credential belongs to one caller's business and must never be
        // cached across callers. The gate-free re-test read (#506) is a separate registration of a
        // separate port, so nothing that injects the ordinary provider can reach it.
        services.AddScoped<INayaxRequestCredentialProvider, NayaxRequestCredentialProvider>();
        services.AddScoped<INayaxConnectionRetestCredentialProvider, NayaxConnectionRetestCredentialProvider>();

        services.AddScoped<ListNayaxFeeRates>();
        services.AddScoped<SaveNayaxFeeRate>();
        services.AddScoped<GetNayaxProcessingFees>();
        services.AddScoped<IGetNayaxProcessingFees>(sp => sp.GetRequiredService<GetNayaxProcessingFees>());
        services.AddScoped<GetSiteCommissionReport>();
        services.AddScoped<IGetSiteCommissionReport>(sp => sp.GetRequiredService<GetSiteCommissionReport>());
        services.AddScoped<GetSiteCommissionAgreements>();
        services.AddScoped<SaveSiteCommissionAgreement>();
        services.AddScoped<RecordSiteCommissionPayment>();
        services.AddScoped<ListCategories>();
        services.AddScoped<GetCategory>();
        services.AddScoped<ListSuppliers>();
        services.AddScoped<GetSupplier>();
        services.AddScoped<CreateSupplier>();
        services.AddScoped<UpdateSupplier>();
        services.AddScoped<DeleteSupplier>();
        services.AddScoped<GetSupplierGstDefaults>();
        services.AddScoped<SetSupplierGstDefaults>();
        services.AddScoped<ListOperatingExpenses>();
        services.AddScoped<GetOperatingExpense>();
        services.AddScoped<GetOperatingExpenseAttachment>();
        services.AddScoped<CreateOperatingExpense>();
        services.AddScoped<UpdateOperatingExpense>();
        services.AddScoped<DeleteOperatingExpense>();
        services.AddScoped<GetNayaxCatalogReconciliation>();
        services.AddScoped<GetSiteSummaries>();
        services.AddScoped<GetSiteProducts>();
        services.AddScoped<ListMachineDashboard>();
        services.AddScoped<GetMachineDashboard>();
        services.AddScoped<ListMachineProducts>();
        services.AddScoped<SyncMachineStockFromNayax>();
        services.AddScoped<ApplyMachineStockSync>();
        services.AddScoped<ResolveMachineStockDuplicate>();
        services.AddScoped<ResolveMachineStockEventsAsAlreadyRecorded>();
        services.AddScoped<SyncLatestNayaxSales>();
        services.AddScoped<ComputePurchaseTotalValidation>();
        services.AddScoped<ComputePurchaseGstSummary>();
        services.AddScoped<ListPurchases>();
        services.AddScoped<GetPurchase>();
        services.AddScoped<GetPurchaseFile>();
        services.AddScoped<UploadPurchase>();
        services.AddScoped<UpdatePurchase>();
        services.AddScoped<DeletePurchase>();
        services.AddScoped<ListActiveSupplierOrders>();
        services.AddScoped<GetSupplierOrder>();
        services.AddScoped<CreateSupplierOrder>();
        services.AddScoped<CancelSupplierOrder>();
        services.AddScoped<CalculateReorderNeeds>();
        services.AddScoped<ListProducts>();
        services.AddScoped<GetProduct>();
        services.AddScoped<ListLowStockProducts>();
        services.AddScoped<CreateProduct>();
        services.AddScoped<UpdateProduct>();
        services.AddScoped<DeleteProduct>();
        services.AddScoped<GetProductGstRule>();
        services.AddScoped<SetProductGstRule>();
        services.AddScoped<ResolveMachineProductPricing>();
        services.AddScoped<GetPickList>();
        services.AddScoped<RecordInventoryMovement>();
        services.AddScoped<IRecordInventoryMovement>(sp => sp.GetRequiredService<RecordInventoryMovement>());
        services.AddScoped<RebuildProductCost>();
        services.AddScoped<IRebuildProductCost>(sp => sp.GetRequiredService<RebuildProductCost>());
        services.AddScoped<CostSale>();
        services.AddScoped<ICostSale>(sp => sp.GetRequiredService<CostSale>());
        services.AddScoped<CostPendingSales>();
        services.AddScoped<BackfillSaleCosts>();
        services.AddScoped<BackfillNayaxHistoricalSaleCosts>();
        services.AddScoped<PreviewInventoryCostTransition>();
        services.AddScoped<ApplyInventoryCostTransition>();
        services.AddScoped<PreviewAllInventoryCostTransitions>();
        services.AddScoped<ApplyAllInventoryCostTransitions>();
        services.AddScoped<PreviewInventoryCostRepair>();
        services.AddScoped<ApplyInventoryCostRepair>();
        services.AddScoped<GetInventoryCostRepairHistory>();
        services.AddScoped<PreviewNayaxSaleTimestampRepair>();
        services.AddScoped<ApplyNayaxSaleTimestampRepair>();
        services.AddScoped<PreviewHistoricalGstClassification>();
        services.AddScoped<ApplyHistoricalGstClassification>();
        services.AddScoped<ApplyInventoryCount>();
        services.AddScoped<ImportNayaxProductCatalog>();
        services.AddScoped<ImportNayaxSales>();
        services.AddScoped<ImportPendingReimbursementXmlFiles>();
        services.AddScoped<GetStockHistory>();
        services.AddScoped<ListStockHistory>();
        services.AddScoped<GetRestockCostSuggestion>();
        services.AddScoped<IGetRestockCostSuggestion>(sp => sp.GetRequiredService<GetRestockCostSuggestion>());
        services.AddScoped<AdjustStock>();
        services.AddScoped<GetProductPriceComparison>();
        services.AddScoped<GetBookkeepingReport>();
        services.AddScoped<IGetBookkeepingReport>(sp => sp.GetRequiredService<GetBookkeepingReport>());
        services.AddScoped<GetDailyReport>();
        services.AddScoped<GetReconciliationReport>();
        services.AddScoped<GetMachineProfitabilityReport>();
        services.AddScoped<GetProductProfitabilityReport>();
        services.AddScoped<IGetProductProfitabilityReport>(sp => sp.GetRequiredService<GetProductProfitabilityReport>());
        services.AddScoped<GetGstAccountingAid>();
        services.AddScoped<GetDashboardReport>();
        services.AddScoped<GetInventoryValuationSummary>();
        services.AddScoped<GetDashboardSummary>();
        services.AddScoped<GetTransactionSalesReport>();
        services.AddScoped<GetReportExportRows>();

        // The platform-admin diagnostics read (issue #336). The use case is registered here with
        // every other use case; its IDiagnosticsQueryExecutor port is satisfied by
        // AddPlatformDiagnostics() in Inventory.Infrastructure, and its IPlatformDiagnosticsAudit
        // port by the ILogger adapter in InventoryApi, because the audit event carries the
        // request's correlation id.
        services.AddScoped<RunDiagnosticsQuery>();

        return services;
    }
}
