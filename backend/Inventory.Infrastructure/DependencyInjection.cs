using Inventory.Application.Documents;
using Inventory.Application.Time;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IBusinessCalendar, SydneyBusinessCalendar>();

        return services;
    }

    /// <summary>
    /// Registers the filesystem document-storage adapter (issue #39, checkpoint 1). It is
    /// separate from <see cref="AddInfrastructureServices"/> because only the composition root
    /// knows the host's content and web roots; passing them in here keeps
    /// <c>IWebHostEnvironment</c> out of the Application and Infrastructure layers.
    /// </summary>
    public static IServiceCollection AddFileSystemDocumentStorage(
        this IServiceCollection services,
        FileSystemDocumentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<IDocumentStorage, FileSystemDocumentStorage>();

        return services;
    }
}
