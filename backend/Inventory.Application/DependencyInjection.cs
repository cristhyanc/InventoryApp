using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class ApplicationServiceCollectionExtensions
{
    // No use cases have been migrated yet; this is the composition entry point future slices will register into.
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        return services;
    }
}
