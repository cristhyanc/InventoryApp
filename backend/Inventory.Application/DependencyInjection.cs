using Inventory.Application.NayaxFeeSettings;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<ListNayaxFeeRates>();
        services.AddScoped<SaveNayaxFeeRate>();

        return services;
    }
}
