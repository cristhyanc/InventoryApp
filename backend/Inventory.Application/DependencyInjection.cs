using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<ListNayaxFeeRates>();
        services.AddScoped<SaveNayaxFeeRate>();
        services.AddScoped<GetBookkeepingReport>();
        services.AddScoped<IGetBookkeepingReport>(sp => sp.GetRequiredService<GetBookkeepingReport>());
        services.AddScoped<GetDailyReport>();
        services.AddScoped<GetReconciliationReport>();
        services.AddScoped<GetMachineProfitabilityReport>();
        services.AddScoped<GetProductProfitabilityReport>();
        services.AddScoped<IGetProductProfitabilityReport>(sp => sp.GetRequiredService<GetProductProfitabilityReport>());
        services.AddScoped<GetGstAccountingAid>();
        services.AddScoped<GetDashboardReport>();
        services.AddScoped<GetTransactionSalesReport>();
        services.AddScoped<GetReportExportRows>();

        return services;
    }
}
