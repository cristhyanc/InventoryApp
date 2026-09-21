using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<ListNayaxFeeRates>();
        services.AddScoped<SaveNayaxFeeRate>();
        services.AddScoped<GetBookkeepingReport>();
        services.AddScoped<GetDailyReport>();
        services.AddScoped<GetReconciliationReport>();
        services.AddScoped<GetMachineProfitabilityReport>();
        services.AddScoped<GetProductProfitabilityReport>();

        return services;
    }
}
