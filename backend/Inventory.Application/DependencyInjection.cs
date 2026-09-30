using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Categories;
using Inventory.Application.Expenses;
using Inventory.Application.InventoryCounting;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Machines;
using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.PickList;
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
using Inventory.Application.Sites;
using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Current-business resolution is per request: it must never be cached across callers.
        services.AddScoped<ICurrentBusinessProvider, CurrentBusinessProvider>();

        services.AddScoped<ListNayaxFeeRates>();
        services.AddScoped<SaveNayaxFeeRate>();
        services.AddScoped<ListCategories>();
        services.AddScoped<GetCategory>();
        services.AddScoped<ListSuppliers>();
        services.AddScoped<GetSupplier>();
        services.AddScoped<CreateSupplier>();
        services.AddScoped<UpdateSupplier>();
        services.AddScoped<DeleteSupplier>();
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
        services.AddScoped<SyncMachineStockFromNayax>();
        services.AddScoped<ApplyMachineStockSync>();
        services.AddScoped<ResolveMachineStockDuplicate>();
        services.AddScoped<ResolveMachineStockEventsAsAlreadyRecorded>();
        services.AddScoped<SyncLatestNayaxSales>();
        services.AddScoped<ComputePurchaseTotalValidation>();
        services.AddScoped<CalculateReorderNeeds>();
        services.AddScoped<GetPickList>();
        services.AddScoped<ApplyInventoryCount>();
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
        services.AddScoped<GetTransactionSalesReport>();
        services.AddScoped<GetReportExportRows>();

        return services;
    }
}
